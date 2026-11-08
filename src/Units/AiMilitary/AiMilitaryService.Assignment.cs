using System;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Tribes;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.Units.AiMilitary.Native;
using APIShared.Units.AiMilitary.Orders;
using APIShared.Units.AiMilitary.Quota;
using APIShared.Units.AiMilitary.Engineering;

namespace APIShared.Units.AiMilitary
{
    internal sealed unsafe partial class AiMilitaryService
    {
        private void OnAssigning(TribeAssignUnitEventArgs args)
        {
            try
            {
                if (args == null || args.Phase != EventHookPhase.Pre || args.UnitId == permittedUnit ||
                    !tracked.TryGetValue(args.UnitId, out var entry) || !entry.Protected ||
                    !TryUnit(entry.Unit, out GameUnit* unit)) return;
                if (GamePlayerManagerAPI.Instance.IsAIPlayer(unit->r_ControllableForPlayerId)) args.SkipOriginalFunction = true;
            }
            catch (Exception error) { Report("assign-guard", error); }
        }

        private bool IsAssignmentAllowed(int unitId, int tribeId)
        {
            if (unitId == permittedUnit) return true;
            SpawnScope scope = capture;
            bool protectedEntry = tracked.TryGetValue(unitId, out var entry) && entry.Protected;
            if (!protectedEntry && (scope == null || scope.Generation != generation || scope.Created.Count == 0)) return true;
            if (!UnitAccess.TryGetById(unitId, out GameUnit* unit, out _)) return false;
            var identity = new AiUnitRef(unitId, unit->r_GlobalId);
            if (scope != null && scope.Generation == generation && AiMilitaryCatalog.IsMilitary(unit->r_UnitChimp) &&
                scope.Created.Contains(identity) && GamePlayerManagerAPI.Instance.IsAIPlayer(unit->r_ControllableForPlayerId)) return false;
            return !protectedEntry || !entry.Unit.Equals(identity) ||
                !GamePlayerManagerAPI.Instance.IsAIPlayer(unit->r_ControllableForPlayerId);
        }

        private bool Request(string owner, AiUnitRef identity, AiMilitaryAssignment assignment,
            Action<AiMilitaryAssignmentResult> completed, bool fresh, bool declaration, out string reason)
            => Request(owner, identity, assignment, completed, fresh, declaration, out reason, out _);

        private bool Request(string owner, AiUnitRef identity, AiMilitaryAssignment assignment,
            Action<AiMilitaryAssignmentResult> completed, bool fresh, bool declaration, out string reason,
            out AiMilitaryAssignmentResult initial)
        {
            reason = string.Empty; initial = null;
            lock (engine)
            {
                if (!ready || worldChanging || GameModes.GameModeHelper.IsRealMultiplayer())
                { reason = "Local mutation is not available during multiplayer or a world transition."; return false; }
                if (!Enum.IsDefined(typeof(AiRecruitmentMode), assignment.RecruitmentMode))
                { reason = "Invalid recruitment mode."; return false; }
                if (!TryUnit(identity, out GameUnit* unit)) { reason = "The unit identity is stale."; return false; }
                if (tracked.TryGetValue(identity.UnitId, out var previous) && previous.Unit.Equals(identity) && previous.Owner != owner)
                { reason = "Another owner holds this unit's assignment."; return false; }
                if (previous != null && previous.Unit.Equals(identity) && previous.Pending)
                { reason = "This exact unit already has a pending assignment."; return false; }
                if (!fresh && !declaration && unit->r_TribeId != 0 && (previous == null || !previous.Protected))
                { reason = "Only a free unit or this owner's protected unit may be assigned."; return false; }
                bool editor = GameModes.GameModeHelper.IsMapEditor();
                var option = AiMilitaryCatalog.Option(unit->r_ControllableForPlayerId, unit->r_UnitChimp, assignment.Role, editor, unit);
                if (!option.IsAvailable) { reason = option.Reason + ": " + option.Detail; return false; }
                var entry = new Tracked(owner, identity, unit->r_ControllableForPlayerId, unit->r_UnitChimp, assignment,
                    fresh, declaration, completed);
                if (previous != null && previous.Unit.Equals(identity))
                { entry.Private = previous.Private; entry.Protected = previous.Protected; }
                tracked[identity.UnitId] = entry;
                if (fresh && AiMilitaryCatalog.IsMilitary(entry.Type)) Quarantine(entry, unit);
                if (declaration)
                {
                    entry.Last = Result(entry, AiMilitaryAssignmentStatus.Declared, AiMilitaryReason.None, string.Empty);
                    initial = entry.Last;
                    Notify(entry, entry.Last);
                    return true;
                }
                entry.Pending = true;
                entry.Last = Result(entry, AiMilitaryAssignmentStatus.Queued, AiMilitaryReason.None, string.Empty);
                initial = entry.Last;
                if (!Wake())
                {
                    entry.Pending = false;
                    entry.Last = Result(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.Unavailable, "The simulation work queue is unavailable.");
                    tracked.Remove(identity.UnitId);
                    reason = "The simulation work queue is unavailable."; return false;
                }
                return true;
            }
        }

        private void Quarantine(Tracked entry, GameUnit* unit)
        {
            entry.Protected = true;
            unit->r_AITribeRole = AiMilitaryNativeBackend.UnmanagedRole;
            unit->r_AITribeRoleRelatedUnknown = 0;
        }

        private void OnSettled(UnitSettledNotification settled)
        {
            try
            {
                if (tracked.TryGetValue(settled.UnitId, out var entry) && entry.Pending &&
                    entry.Unit.GlobalId == unchecked((uint)settled.GlobalId) && entry.Player == settled.PlayerId) Wake();
            }
            catch (Exception error) { Report("settlement", error); }
        }

        private bool Wake()
        {
            if (Interlocked.CompareExchange(ref scheduled, 1, 0) != 0) return true;
            if (Simulation.SimulationWorkPump.Request(process)) return true;
            Volatile.Write(ref scheduled, 0); return false;
        }
        private void Process()
        {
            Volatile.Write(ref scheduled, 0);
            if (!ready || !Monitor.IsEntered(engine)) return;
            // Never invoke a completion from the middle of native creation. A recycled pending
            // slot is retired immediately but reported only at this completed-run safe point.
            if (retired.Count != 0)
            {
                var callbacks = retired.ToArray(); retired.Clear();
                foreach (var entry in callbacks) Notify(entry, entry.Last);
            }
            AiMilitaryEngineeringRuntime.Reconcile();
            long processGeneration = generation;
            var work = new List<Tracked>();
            foreach (var entry in tracked.Values) if (entry.Pending) work.Add(entry);
            foreach (var entry in work)
            {
                // A completion can end the world or submit another request. Never apply a stale
                // snapshot entry after that callback has retired/replaced it.
                if (generation != processGeneration) break;
                if (!entry.Pending || !tracked.TryGetValue(entry.Unit.UnitId, out var current) || !ReferenceEquals(entry, current)) continue;
                if (!TryUnit(entry.Unit, out GameUnit* unit) || unit->r_ControllableForPlayerId != entry.Player || unit->r_UnitChimp != entry.Type)
                { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.StaleIdentity, "Unit identity, owner or type changed."); continue; }
                if (!UnitAccess.IsReallyAlive(unit))
                {
                    if (unit->r_AliveState == AliveState.NeedsInit &&
                        (!Simulation.SimulationTickEvents.TryGetCurrentTick(out int tick) ||
                         !entry.InitializationWait.IsExpired(tick, UnitSettledTracker.MaximumObservedTicks))) continue;
                    Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.InitializationFailed, "The unit did not finish initialization.");
                    continue;
                }
                Apply(entry, unit);
            }
            foreach (var entry in tracked.Values) if (entry.Pending) { Wake(); break; }
        }

        private void Apply(Tracked entry, GameUnit* unit)
        {
            long applyGeneration = generation;
            var option = AiMilitaryCatalog.Option(entry.Player, entry.Type, entry.Assignment.Role, false, unit);
            if (!option.IsAvailable)
            { Finish(entry, AiMilitaryAssignmentStatus.Rejected, option.Reason, option.Detail); return; }
            if (!AiMilitaryCatalog.IsMilitary(entry.Type) || entry.Assignment.Role == AiMilitaryRole.None &&
                !GamePlayerManagerAPI.Instance.IsAIPlayer(entry.Player))
            {
                entry.Protected = false; unit->r_AITribeRole = 0;
                MarkAdditional(entry.Unit, false);
                Finish(entry, AiMilitaryAssignmentStatus.Applied, AiMilitaryReason.None, string.Empty); return;
            }
            if (entry.Assignment.Role == AiMilitaryRole.HarassmentEngineers &&
                !AiMilitaryQuotaRuntime.TryCheckHarassmentEngineerCapacity(entry.Unit, entry.Player,
                    entry.Assignment.RecruitmentMode == AiRecruitmentMode.Additional, out string capacityReason))
            { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.NoAvailableGroup, capacityReason); return; }
            string reason = string.Empty;
            AiNativeGroup group = default;
            AiNativeGroup oldPrivate = entry.Private;
            bool admitted = false;
            int previousPermit = permittedUnit;
            permittedUnit = entry.Unit.UnitId;
            try
            {
                if (entry.Fresh && !backend.TryDetach(entry.Unit.UnitId, entry.Unit.GlobalId, out reason))
                { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.AssignmentFailed, reason); return; }
                if (entry.Assignment.Role == AiMilitaryRole.None)
                {
                    if (!backend.TryCreatePrivateGroup(entry.Player, out group, out reason))
                    { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.NoAvailableGroup, reason); return; }
                }
                else if (!backend.TryResolveGroup(entry.Player, entry.Unit.UnitId,
                    AiMilitaryCatalog.NativeRole(entry.Assignment.Role), -1, out group, out reason))
                { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.NoAvailableGroup, reason); return; }
                AiCurrentOrder order;
                if (entry.Assignment.Role == AiMilitaryRole.None || entry.Assignment.Role == AiMilitaryRole.SiegeEngineers ||
                    entry.Assignment.Role == AiMilitaryRole.HarassmentEngineers)
                    order = new AiCurrentOrder(AiOrderKind.Idle);
                else if (!orders.TryGetCurrent(generation, group, out order, out reason))
                {
                    if (!backend.TryGetInitialOrderForEmptyGroup(entry.Unit.UnitId,
                        AiMilitaryCatalog.NativeRole(entry.Assignment.Role), -1, group, out order, out reason))
                    { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.CurrentOrderUnknown, reason); return; }
                }
                if (!backend.TryAdmit(entry.Unit.UnitId, entry.Unit.GlobalId, group,
                    AiMilitaryCatalog.NativeRole(entry.Assignment.Role), order, out reason))
                { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.AssignmentFailed, reason); return; }
                if (generation != applyGeneration || !IsTracked(entry)) return;
                admitted = true;
                entry.Protected = entry.Assignment.Role == AiMilitaryRole.None;
                entry.Private = entry.Protected ? group : default;
                MarkAdditional(entry.Unit, entry.Assignment.RecruitmentMode == AiRecruitmentMode.Additional);
                entry.Last = Result(entry, AiMilitaryAssignmentStatus.Applied, AiMilitaryReason.None, string.Empty,
                    new AiGroupRef(group.Id, group.GlobalId, group.Owner));
                entry.Pending = false; Notify(entry, entry.Last);
            }
            catch (Exception error) { Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.AssignmentFailed, error.Message); Report("admission", error); }
            finally
            {
                permittedUnit = previousPermit;
                // Only backend-proven allocations can be retired. Existing native groups are never
                // deleted here, even if an external hook changed them during admission.
                if (generation == applyGeneration)
                {
                    try
                    {
                        if (!admitted && group.Id > 0) backend.TryDeleteEmptyGroup(group);
                        if (admitted && oldPrivate.Id > 0 && !oldPrivate.Equals(group)) backend.TryDeleteEmptyGroup(oldPrivate);
                    }
                    catch (Exception error) { Report("empty-group-cleanup", error); }
                }
            }
        }

        private void Finish(Tracked entry, AiMilitaryAssignmentStatus status, AiMilitaryReason reason, string detail)
        {
            if (!IsTracked(entry)) return; // A reentrant load already retired/notified this request.
            entry.Pending = false;
            entry.Last = Result(entry, status, reason, detail);
            if (status == AiMilitaryAssignmentStatus.Rejected && entry.Fresh && TryUnit(entry.Unit, out _))
            {
                MarkAdditional(entry.Unit, false);
                DeleteOwnUnit(entry.Unit);
                entry.Protected = false;
            }
            Notify(entry, entry.Last);
        }
        private bool IsTracked(Tracked entry) => tracked.TryGetValue(entry.Unit.UnitId, out var current) && ReferenceEquals(current, entry);
        private static AiMilitaryAssignmentResult Result(Tracked entry, AiMilitaryAssignmentStatus status,
            AiMilitaryReason reason, string detail, AiGroupRef group = default) =>
            new AiMilitaryAssignmentResult(entry.Unit, entry.Assignment, status, reason, detail, group);
        private void Notify(Tracked entry, AiMilitaryAssignmentResult result)
        {
            var callback = entry.Completed; entry.Completed = null;
            try { callback?.Invoke(result); } catch (Exception error) { Report("completion", error); }
        }
    }
}

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
                entry.Pending = true; Enqueue(entry);
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

        // Pending work index under the engine monitor: Process visits only queued requests, never
        // the whole registry of applied units (mass placement keeps every pump proportional to the
        // pending requests). Every writer of Pending=true enqueues: Request and CompleteNativeLoad
        // (which covers both raw-restore and map-start entries). Stale entries drop out in Process.
        private void Enqueue(Tracked entry)
        {
            if (entry.InWork) return;
            entry.InWork = true; pendingWork.Add(entry);
        }
        private bool RetireFromWork(Tracked entry)
        {
            if (entry.Pending && IsTracked(entry)) return false;
            entry.InWork = false; return true;
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
            // Snapshot: completions can submit new requests or end the world during the loop.
            var work = pendingWork.ToArray();
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
            pendingWork.RemoveAll(retireFromWork);
            if (pendingWork.Count != 0) Wake();
        }

        private string Describe(AiNativeGroup group) =>
            "Target group " + group.Id + "/" + group.GlobalId + " of player " + group.Owner +
            (backend.IsCurrent(group) ? " has " + *(short*)(backend.Tribe(group) + 0x5C) + " members." : " is no longer current.");

        // The owner's AI siege state (3C2E0 phase); -1 when the player record is unavailable.
        private static int SiegeState(int player) =>
            GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(player, out GamePlayerResources* resources) && resources != null
                ? (int)resources->r_AISiegeState : -1;

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
                string appliedDetail = string.Empty;
                bool emptyGroupOrder = false;
                if (entry.Assignment.Role == AiMilitaryRole.None || entry.Assignment.Role == AiMilitaryRole.SiegeEngineers ||
                    entry.Assignment.Role == AiMilitaryRole.HarassmentEngineers)
                    order = new AiCurrentOrder(AiOrderKind.Idle);
                else if (!orders.TryGetCurrent(generation, group, out order, out string ledgerReason))
                {
                    if (!backend.TryGetInitialOrderForEmptyGroup(entry.Unit.UnitId,
                        AiMilitaryCatalog.NativeRole(entry.Assignment.Role), -1, group, out order, out string initialReason))
                    {
                        // An occupied group without an observed order: join exactly like a Vanilla
                        // recruit where the owner's controller provably commands joined members.
                        // Empty or no longer current groups keep their explicit rejection, except
                        // castle defense: its groups always come from the storage slots that 2FE20
                        // visits every pass, and the initial target is absent only where that pass
                        // itself gives no order now (see TryCastleDefenseTarget).
                        int siegeState = SiegeState(entry.Player);
                        if (!AiControllerHandoffPolicy.CommandsJoinedMembers(entry.Assignment.Role, siegeState) ||
                            !backend.IsCurrent(group) || (*(short*)(backend.Tribe(group) + 0x5C) < 1 &&
                            entry.Assignment.Role != AiMilitaryRole.CastleDefense))
                        {
                            Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.CurrentOrderUnknown,
                                ledgerReason + " Empty-group fallback: " + initialReason + " " + Describe(group));
                            return;
                        }
                        order = new AiCurrentOrder(AiOrderKind.Idle);
                        appliedDetail = AiControllerHandoffPolicy.Detail(entry.Assignment.Role, siegeState);
                    }
                    else emptyGroupOrder = true;
                }
                if (!backend.TryAdmit(entry.Unit.UnitId, entry.Unit.GlobalId, group,
                    AiMilitaryCatalog.NativeRole(entry.Assignment.Role), order, out reason))
                {
                    // The recruit's own native route (196280) found no path to the initial or recorded
                    // group order, e.g. a mounted unit on a keep tile or an unconnected wall (castle
                    // defense, 18.11.2026) or a siege diversion horseman in state 7 (in-game 18.11.2026).
                    // Vanilla never routes a recruit when it joins (291F0/294B0 write role and membership
                    // only); the controller of these roles commands the whole group itself (castle
                    // defense every pass, the others per AiControllerHandoffPolicy), where an unroutable
                    // member simply stays. The rolled-back recruit therefore joins like a Vanilla recruit
                    // instead of being rejected. Vetoes and redirects stay rejections.
                    bool rejoin = backend.LastAdmissionRouteRefused &&
                        (entry.Assignment.Role == AiMilitaryRole.CastleDefense ||
                         AiControllerHandoffPolicy.CommandsJoinedMembers(entry.Assignment.Role, SiegeState(entry.Player))) &&
                        generation == applyGeneration && IsTracked(entry);
                    string routeReason = reason;
                    if (!rejoin || !backend.TryAdmit(entry.Unit.UnitId, entry.Unit.GlobalId, group,
                        AiMilitaryCatalog.NativeRole(entry.Assignment.Role), new AiCurrentOrder(AiOrderKind.Idle), out reason))
                    {
                        Finish(entry, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.AssignmentFailed,
                            rejoin ? routeReason + " Vanilla-recruit join failed: " + reason : reason);
                        return;
                    }
                    order = new AiCurrentOrder(AiOrderKind.Idle); emptyGroupOrder = false;
                    appliedDetail = AiControllerHandoffPolicy.AppliedDetail + " The recruit had no route to the group position.";
                }
                if (generation != applyGeneration || !IsTracked(entry)) return;
                // The group was empty and its only member now carries this initial order, so it is
                // the group's current order until an observed group command supersedes it. Later
                // recruits of the same group then receive the same state instead of being rejected.
                if (emptyGroupOrder) orders.RecordAdmissionOrder(generation, group, order);
                admitted = true;
                entry.Protected = entry.Assignment.Role == AiMilitaryRole.None;
                entry.Private = entry.Protected ? group : default;
                MarkAdditional(entry.Unit, entry.Assignment.RecruitmentMode == AiRecruitmentMode.Additional);
                entry.Last = Result(entry, AiMilitaryAssignmentStatus.Applied, AiMilitaryReason.None, appliedDetail,
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

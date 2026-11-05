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
    // A process-owned service. All tracked world state is serialized by EngineMonitor; the additional
    // identity mirror alone is lock-free for native fold callbacks and third-party quota predicates.
    internal sealed unsafe partial class AiMilitaryService
    {
        internal static readonly AiMilitaryService Instance = new AiMilitaryService();
        private readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();
        private readonly List<Tracked> retired = new List<Tracked>();
        private readonly uint[] additional = new uint[10001];
        private readonly Action process;
        private object engine;
        private ManualLogSource log;
        private AiMilitaryNativeBackend backend;
        private AiOrderLedger orders;
        private IDisposable assigning;
        private IMissionLifecycleCapability missions;
        private string failure = "AI military service has not initialized.";
        private volatile bool ready;
        private int scheduled;
        private long generation;
        private bool worldChanging;
        [ThreadStatic] private static SpawnScope capture;
        [ThreadStatic] private static int permittedUnit;
        internal long Generation => generation;

        private AiMilitaryService() { process = Process; }

        internal static bool TryGet(string owner, out IAiMilitaryCapability capability, out NativeCapabilityDiagnostic diagnostic)
        {
            var service = Instance;
            capability = service.ready ? new Binding(service, owner) : null;
            if (capability != null) service.EnableObservation();
            diagnostic = new NativeCapabilityDiagnostic("Units.AiMilitary", service.ready ? NativeCapabilityState.Available :
                NativeCapabilityState.ValidationFailed, string.Empty, service.ready ? string.Empty : service.failure);
            return capability != null;
        }

        internal void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (ready) return;
            log = logger;
            try
            {
                engine = Internal.EngineMonitor.Current ?? throw new InvalidOperationException("Engine monitor unavailable.");
                if (!Simulation.SimulationWorkPump.IsAvailable) throw new InvalidOperationException("Quiescent engine work publisher unavailable.");
                if (!AiMilitaryNativeBackend.TryCreate(context.ModuleHandle, context.Memory, hash, out backend, out string reason))
                    throw new InvalidOperationException(reason);
                ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, "Units.AiMilitary.Calls",
                    AiMilitaryNativeBindings.CallOnlySpans(), message => new InvalidOperationException(message));
                orders = new AiOrderLedger(backend);
                AiMilitaryOrderBroker.Initialize(log, ObserveMove, ObserveTarget, InvalidateIndividualMove, OnCreated,
                    IsAssignmentAllowed, FilterCapturedCreateReturn);
                InitializeNativeObservers(context, hash);
                AiMilitaryQuotaRuntime.Initialize(context, hash, log, IsAdditional, MarkAdditional);
                assigning = TribeR3EventHooks.OnTribeAssignUnit.Observable.Subscribe(OnAssigning);
                if (!UnitSettledEvents.TryRegister(APISharedPlugin.PluginGuid, "ai-military/settled", OnSettled, out reason))
                    throw new InvalidOperationException(reason);
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out missions, out var diagnostic) ||
                    !missions.TryRegisterObserver("ai-military/lifetime", OnStarted, OnEnded, OnInitializing, out diagnostic))
                    throw new InvalidOperationException("AI military lifecycle unavailable: " + diagnostic);
                RegisterPersistence();
                ready = true;
                NativeApiLog.Info(log, "AI_MILITARY_READY: process-owned capture, role admission, native quota accounting and map/save records initialized.");
            }
            catch (Exception error)
            {
                failure = error.Message;
                NativeApiLog.Error(log, "AI_MILITARY_UNAVAILABLE: " + error);
                throw;
            }
        }

        private IReadOnlyList<AiMilitaryOption> Options(int player, eChimps type, bool editor)
        {
            var result = new List<AiMilitaryOption>(AiMilitaryCatalog.Roles.Length);
            lock (engine)
                foreach (var role in AiMilitaryCatalog.Roles) result.Add(AiMilitaryCatalog.Option(player, type, role, editor));
            return result.AsReadOnly();
        }

        internal bool IsAdditional(AiUnitRef unit) => unit.UnitId > 0 && unit.UnitId < additional.Length &&
            unit.GlobalId != 0 && Volatile.Read(ref additional[unit.UnitId]) == unit.GlobalId;
        internal void MarkAdditional(AiUnitRef unit, bool value)
        {
            if (unit.UnitId <= 0 || unit.UnitId >= 10000 || unit.GlobalId == 0) return;
            if (!value && Volatile.Read(ref additional[unit.UnitId]) != unit.GlobalId) return;
            Volatile.Write(ref additional[unit.UnitId], value ? unit.GlobalId : 0);
            AiMilitaryQuotaRuntime.SetAdditional(unit, value);
        }

        private bool Begin(string owner, int player, eChimps type, AiMilitaryAssignment assignment,
            bool editor, bool apply, Action<AiMilitaryAssignmentResult> completed, out IAiMilitarySpawnScope scope, out string reason)
        {
            scope = null; reason = string.Empty;
            if (!ready || worldChanging || !Monitor.IsEntered(engine)) { reason = "AI placement requires the validated engine monitor and an active world."; return false; }
            if (GameModes.GameModeHelper.IsRealMultiplayer()) { reason = "Local army placement is unavailable in multiplayer."; return false; }
            if (editor != GameModes.GameModeHelper.IsMapEditor()) { reason = "Editor mode changed."; return false; }
            if (player < 1 || player > GamePlayerManagerAPI.MAX_PLAYERS ||
                !Enum.IsDefined(typeof(AiRecruitmentMode), assignment.RecruitmentMode))
            { reason = "Invalid player or recruitment mode."; return false; }
            var option = AiMilitaryCatalog.Option(player, type, assignment.Role, editor);
            if (!option.IsAvailable) { reason = option.Reason + ": " + option.Detail; return false; }
            var created = new SpawnScope(this, owner, player, type, assignment, editor, apply, completed, capture);
            capture = created; scope = created;
            return true;
        }

        private void OnCreated(int unitId)
        {
            try
            {
                if (unitId <= 0 || unitId >= 10000 || !UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit->r_GlobalId == 0) return;
                var identity = new AiUnitRef(unitId, unit->r_GlobalId);
                if (tracked.TryGetValue(identity.UnitId, out var old) && !old.Unit.Equals(identity))
                {
                    MarkAdditional(old.Unit, false); tracked.Remove(identity.UnitId);
                    if (old.Pending)
                    {
                        old.Pending = false;
                        old.Last = Result(old, AiMilitaryAssignmentStatus.Rejected, AiMilitaryReason.StaleIdentity,
                            "The pending unit slot was reused by another native identity.");
                        retired.Add(old); Wake();
                    }
                }
                AiMilitaryEngineeringRuntime.OnUnitCreated(identity, unit->r_UnitChimp);
                SpawnScope current = capture;
                if (current == null || current.Generation != generation || current.Player != unit->r_ControllableForPlayerId ||
                    current.Type != unit->r_UnitChimp) return;
                current.Created.Add(identity);
            }
            catch (Exception error) { Report("create", error); }
        }

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
        private long FilterCapturedCreateReturn(long raw, long result)
        {
            SpawnScope scope = capture;
            if (scope == null) return result;
            if (scope.Generation != generation) return result; // A nested new-world load owns a newer ambient receipt.
            if (raw <= 0 || raw >= 10000 || !UnitAccess.TryGetById((int)raw, out GameUnit* unit, out _))
            {
                // A foreign Pre may supply an existing primary unit without invoking Original.
                // Non-primary helper calls remain outside the capture contract.
                if (result > 0 && result < 10000 && UnitAccess.TryGetById((int)result, out GameUnit* supplied, out _) &&
                    supplied->r_UnitChimp == scope.Type && supplied->r_ControllableForPlayerId == scope.Player) return 0;
                return result;
            }
            if (unit->r_UnitChimp != scope.Type || unit->r_ControllableForPlayerId != scope.Player) return result;
            if (raw != result) return 0;
            return scope.Created.Contains(new AiUnitRef((int)raw, unit->r_GlobalId)) ? result : 0;
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
        private static bool TryUnit(AiUnitRef reference, out GameUnit* unit) =>
            UnitAccess.TryGetById(reference.UnitId, out unit, out _) && reference.UnitId < 10000 &&
                reference.GlobalId != 0 && unit->r_GlobalId == reference.GlobalId;
        private static void DeleteOwnUnit(AiUnitRef identity)
        {
            if (!TryUnit(identity, out _)) return;
            // Keep the SDK call behind the public checked-slot boundary too. The preceding stronger
            // identity check is stable under the caller's engine monitor, with no native call between.
            if (APIShared.UnitAccess.TryGetById(identity.UnitId, out _, out _))
                GameUnitManagerAPI.Instance.DeleteUnitSafe(identity.UnitId);
        }
        private void Report(string stage, Exception error)
        {
            try { Internal.RuntimeHealth.Failure(log, "ai-military/" + stage, true, "AI military " + stage + " failed: " + error); } catch { }
        }

        private void ObserveMove(int tribe, int x, int y, short patrol, bool fresh, int moveType, long result)
        {
            if (Volatile.Read(ref observing) != 0 && TryGroup(tribe, out var group))
                orders.ObserveMove(generation, group, x, y, patrol, fresh, moveType, result);
        }
        private void ObserveTarget(int tribe, int command, int arg1, int arg2, int arg3, long result)
        {
            if (Volatile.Read(ref observing) == 0 || !TryGroup(tribe, out var group)) return;
            if (command == 4 && arg1 > 0 && UnitAccess.TryGetById(arg1, out GameUnit* target, out _) &&
                target->r_GlobalId == unchecked((uint)arg2) && UnitAccess.IsReallyAlive(target))
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackUnit,
                    command: command, targetId: arg1, targetGlobalId: unchecked((uint)arg2), extra: arg3), result);
            else if ((command == 9 || command == 36 || command == 38) && arg1 > 0 &&
                GameBuildingManagerAPI.Instance.TryGetBuildingById(arg1, out GameBuilding* building) &&
                building->r_GlobalId == unchecked((uint)arg2) && building->r_AliveState == AliveState.IsAlive)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackBuilding,
                    command: command, targetId: arg1, targetGlobalId: unchecked((uint)arg2), extra: arg3), result);
            else if (command == 5 || command == 22)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.AttackTile,
                    arg1, arg2, command, extra: arg3), result);
            else if (command == 6)
                orders.ObserveAccepted(generation, group, new AiCurrentOrder(AiOrderKind.DigMoat,
                    arg1, arg2, command, extra: arg3), result);
            else orders.ObserveTarget(generation, group, command, arg1, arg2, arg3, result);
        }
        private void ObserveStrategic(int tribe, int action, long result)
        {
            if (Volatile.Read(ref observing) != 0 && TryGroup(tribe, out var group))
                orders.ObserveStrategic(generation, group, action, result);
        }
        private bool TryGroup(int id, out AiNativeGroup group)
        {
            group = default;
            return backend != null && id > 0 && id < AiMilitaryNativeBackend.TribeCapacity && GameTribeManagerAPI.Instance.TryGetTribeById(id, out GameTribe* tribe) &&
                backend.TryGetGroup(id, tribe->r_PlayerIdOwner, out group);
        }
        private void InvalidateIndividualMove(int id)
        {
            if (Volatile.Read(ref observing) != 0 && UnitAccess.TryGetById(id, out GameUnit* unit, out _) &&
                TryGroup(unit->r_TribeId, out var group)) orders.Invalidate(group);
        }

        private sealed class Tracked
        {
            internal Tracked(string owner, AiUnitRef unit, int player, eChimps type, AiMilitaryAssignment assignment,
                bool fresh, bool declaration, Action<AiMilitaryAssignmentResult> completed)
            { Owner = owner; Unit = unit; Player = player; Type = type; Assignment = assignment;
                Fresh = fresh; Declaration = declaration; Completed = completed; }
            internal readonly string Owner;
            internal readonly AiUnitRef Unit;
            internal readonly int Player;
            internal readonly eChimps Type;
            internal readonly AiMilitaryAssignment Assignment;
            internal bool Fresh, Declaration, Pending, Protected;
            internal AiInitializationWaitBudget InitializationWait;
            internal AiNativeGroup Private;
            internal AiMilitaryAssignmentResult Last;
            internal Action<AiMilitaryAssignmentResult> Completed;
        }

        private sealed class SpawnScope : IAiMilitarySpawnScope
        {
            private readonly AiMilitaryService service;
            private readonly string owner;
            internal readonly long Generation;
            internal readonly int Player;
            internal readonly eChimps Type;
            private readonly AiMilitaryAssignment assignment;
            private readonly bool editor, apply;
            private readonly Action<AiMilitaryAssignmentResult> completed;
            private readonly SpawnScope previous;
            private readonly int thread;
            private readonly IDisposable mutation;
            internal readonly List<AiUnitRef> Created = new List<AiUnitRef>();
            private bool complete, disposed;
            internal SpawnScope(AiMilitaryService service, string owner, int player, eChimps type, AiMilitaryAssignment assignment,
                bool editor, bool apply, Action<AiMilitaryAssignmentResult> completed, SpawnScope previous)
            { this.service = service; this.owner = owner; Player = player; Type = type; this.assignment = assignment;
                this.editor = editor; this.apply = apply; this.completed = completed; this.previous = previous;
                Generation = service.generation; thread = Thread.CurrentThread.ManagedThreadId;
                mutation = AiRecruitMutationReceipts.Enter(() => Generation == service.generation); }
            public IReadOnlyList<AiMilitaryAssignmentResult> Complete()
            {
                if (disposed || complete || thread != Thread.CurrentThread.ManagedThreadId || capture != this ||
                    !Monitor.IsEntered(service.engine)) throw new InvalidOperationException("Placement scope order/thread/monitor differs.");
                complete = true;
                var results = new List<AiMilitaryAssignmentResult>(Created.Count);
                foreach (var unit in Created)
                {
                    if (Generation == service.generation && service.Request(owner, unit, assignment, completed, true,
                        editor && !apply, out string reason, out AiMilitaryAssignmentResult initial))
                        results.Add(initial);
                    else
                    {
                        var rejected = new AiMilitaryAssignmentResult(unit, assignment, AiMilitaryAssignmentStatus.Rejected,
                            AiMilitaryReason.AssignmentFailed, "The captured unit could not be submitted.");
                        results.Add(rejected);
                        if (Generation == service.generation) DeleteOwnUnit(unit);
                        try { completed?.Invoke(rejected); } catch (Exception error) { service.Report("capture-completion", error); }
                    }
                }
                return results.AsReadOnly();
            }
            public void Dispose()
            {
                if (disposed) return;
                if (thread != Thread.CurrentThread.ManagedThreadId || capture != this)
                    throw new InvalidOperationException("Placement capture must close on its owning thread in stack order.");
                if (!Monitor.IsEntered(service.engine)) throw new InvalidOperationException("Placement capture must close under the engine monitor.");
                capture = previous; disposed = true;
                try
                {
                    if (!complete && Generation == service.generation)
                        foreach (var unit in Created) DeleteOwnUnit(unit);
                }
                finally { mutation.Dispose(); }
            }
        }

        private sealed class Binding : IAiMilitaryCapability
        {
            private readonly AiMilitaryService service;
            private readonly string owner;
            internal Binding(AiMilitaryService service, string owner) { this.service = service; this.owner = owner; }
            public IReadOnlyList<AiMilitaryOption> GetOptions(int playerId, eChimps type, bool editor) => service.Options(playerId, type, editor);
            public bool TryBeginSpawnScope(int playerId, eChimps type, AiMilitaryAssignment assignment, bool editor, bool apply,
                Action<AiMilitaryAssignmentResult> completed, out IAiMilitarySpawnScope scope, out string reason) =>
                service.Begin(owner, playerId, type, assignment, editor, apply, completed, out scope, out reason);
            public bool RequestAssignment(AiUnitRef unit, AiMilitaryAssignment assignment, Action<AiMilitaryAssignmentResult> completed, out string reason) =>
                service.Request(owner, unit, assignment, completed, false, false, out reason);
            public bool TryDeclareMapAssignment(AiUnitRef unit, AiMilitaryAssignment assignment, out string reason)
            {
                if (!GameModes.GameModeHelper.IsMapEditor()) { reason = "Map declarations require the editor."; return false; }
                return service.Request(owner, unit, assignment, null, false, true, out reason);
            }
            public bool TryGetState(AiUnitRef unit, out AiMilitaryAssignmentResult state)
            {
                lock (service.engine)
                {
                    state = service.tracked.TryGetValue(unit.UnitId, out var entry) && entry.Unit.Equals(unit) &&
                        TryUnit(unit, out GameUnit* current) && current->r_ControllableForPlayerId == entry.Player &&
                        current->r_UnitChimp == entry.Type ? entry.Last : null;
                    return state != null;
                }
            }
            public bool IsExcludedFromRecruitmentQuota(AiUnitRef unit) => service.IsAdditional(unit);
        }
    }
}

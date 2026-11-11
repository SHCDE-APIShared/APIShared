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
        // Exclusive bound of tracked one-based unit IDs: the native unit array has 10000 records
        // (SE GameUnitManager.GameUnitArray; its capacity constant is internal to the extender).
        internal const int UnitIdLimit = 10000;
        private readonly uint[] additional = new uint[UnitIdLimit + 1];
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
            // Observation starts with the first binding, also for a consumer that only asks the quota
            // marker. Starting it at the first assignment instead would lose the group orders issued
            // before that call, and an assignment decides on them; the observers fire per AI group
            // order, not per unit or tick.
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
            if (unit.UnitId <= 0 || unit.UnitId >= UnitIdLimit || unit.GlobalId == 0) return;
            if (!value && Volatile.Read(ref additional[unit.UnitId]) != unit.GlobalId) return;
            Volatile.Write(ref additional[unit.UnitId], value ? unit.GlobalId : 0);
            AiMilitaryQuotaRuntime.SetAdditional(unit, value);
        }

        private static bool TryUnit(AiUnitRef reference, out GameUnit* unit) =>
            UnitAccess.TryGetById(reference.UnitId, out unit, out _) && reference.UnitId < UnitIdLimit &&
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
                service.Request(owner, unit, assignment, completed, fresh: false, declaration: false, out reason);
            public bool TryDeclareMapAssignment(AiUnitRef unit, AiMilitaryAssignment assignment, out string reason)
            {
                if (!GameModes.GameModeHelper.IsMapEditor()) { reason = "Map declarations require the editor."; return false; }
                return service.Request(owner, unit, assignment, null, fresh: false, declaration: true, out reason);
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

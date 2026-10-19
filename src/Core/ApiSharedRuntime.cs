using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using RedBird.Core.Memory;

namespace APIShared
{
    // Process-owned composition root: managed availability is published before native validation.
    // One service failure must not disable independent capabilities; terminal readiness is separate.
    // Capability facades bind owner identity while services retain registrations and permanent hooks.
    internal sealed class ApiSharedRuntime : IApiShared
    {
        internal const string SupportedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";

        private readonly object sync = new object();
        private readonly List<Action<IApiShared>> readyCallbacks = new List<Action<IApiShared>>();
        private NativeApiState state;
        private string binaryHash = string.Empty;
        private GatehouseDistanceOriginService gatehouseDistanceOrigin;
        private GatehouseTimingService gatehouse;
        private UnitHudPresentationService unitHudPresentation;
        private HudExtrasButtonsService hudExtrasButtons;
        private NativeCapabilityDiagnostic hudExtrasDiagnostic; // Set only when its member check failed.
        private AivBuildStepService aivBuildStep;
        private LobbyStateService lobbyState;
        private PlayerDefeatService playerDefeat;
        private MissionLifecycleService missionLifecycle;
        private BriefingGoldPresentationService briefingGoldPresentation;
        private BuildingRepairService buildingRepair;
        internal RepairTooltipViewModel RepairTooltip { get; } = new RepairTooltipViewModel();
        private NativeCapabilityDiagnostic buildingRepairDiagnostic = Pending(NativeCapabilityIds.BuildingRepair);
        private long nativeModuleBase;
        private NativeCapabilityDiagnostic missionLifecycleDiagnostic = Pending(NativeCapabilityIds.MissionLifecycle);
        private NativeCapabilityDiagnostic gatehouseDistanceOriginDiagnostic = Pending(NativeCapabilityIds.GatehouseDistanceOrigin);
        private NativeCapabilityDiagnostic gatehouseDiagnostic = Pending(NativeCapabilityIds.GatehouseTiming);
        private NativeCapabilityDiagnostic unitHudDiagnostic = Pending(NativeCapabilityIds.UnitHudPresentation);
        private NativeCapabilityDiagnostic aivBuildStepDiagnostic = Pending(NativeCapabilityIds.AivBuildStep);
        private NativeCapabilityDiagnostic lobbyStateDiagnostic = Pending(NativeCapabilityIds.LobbyState);
        private NativeCapabilityDiagnostic playerDefeatDiagnostic = Pending(NativeCapabilityIds.PlayerDefeat);
        private NativeCapabilityDiagnostic briefingGoldDiagnostic = Pending(NativeCapabilityIds.BriefingGoldPresentation);
        private ManualLogSource log;

        internal NativeOwnershipRegistry NativeOwnership { get; } = new NativeOwnershipRegistry();

        internal static ApiSharedRuntime ProcessInstance { get; } = new ApiSharedRuntime();

        public NativeApiState State { get { lock (sync) return state; } }

        public void WhenReady(Action<IApiShared> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            lock (sync)
            {
                if (state == NativeApiState.Pending)
                {
                    readyCallbacks.Add(callback);
                    return;
                }
            }
            InvokeReadyCallback(callback);
        }

        private void InvokeReadyCallback(Action<IApiShared> callback)
        {
            try { callback(this); }
            catch (Exception ex)
            {
                // A failing log listener must not defeat consumer error isolation.
                try { NativeApiLog.Error(log, $"APIShared readiness callback failed: build={binaryHash}, error={ex}"); }
                catch { }
            }
        }

        internal void InitializeManaged(ManualLogSource logger)
        {
            lock (sync)
            {
                APIShared.Events.InterceptionDiagnostics.Error = (registration, error) =>
                    NativeApiLog.Error(logger, "Shared event callback failed: " + registration + ": " + error);
                Audio.SoundRuntime.InitializeManaged(logger);
                MarkedUnitSelectionAPI.Initialize(logger);
                Presentation.TroopActionHud.Initialize(logger);
                PlayerPerspectiveAPI.Initialize(logger);
                if (hudExtrasButtons == null && hudExtrasDiagnostic == null &&
                    ManagedMemberContracts.TryValidate(NativeCapabilityIds.HudExtrasButtons, logger, out hudExtrasDiagnostic))
                {
                    hudExtrasButtons = new HudExtrasButtonsService(logger);
                    hudExtrasButtons.Start();
                }
                if (missionLifecycleDiagnostic.State == NativeCapabilityState.Pending)
                    MissionLifecycleService.TryCreate(logger, out missionLifecycle, out missionLifecycleDiagnostic);
                if (briefingGoldDiagnostic.State == NativeCapabilityState.Pending)
                    BriefingGoldPresentationService.TryCreate(
                        logger,
                        out briefingGoldPresentation,
                        out briefingGoldDiagnostic);
                if (playerDefeatDiagnostic.State == NativeCapabilityState.Pending)
                    PlayerDefeatService.TryCreate(
                        logger,
                        missionLifecycle,
                        out playerDefeat,
                        out playerDefeatDiagnostic);
                if (lobbyState != null ||
                    lobbyStateDiagnostic.State != NativeCapabilityState.Pending)
                {
                    return;
                }
                log = logger;
                LobbyStateService.TryCreate(
                    logger,
                    out LobbyStateService created,
                    out NativeCapabilityDiagnostic diagnostic);
                lobbyState = created;
                lobbyStateDiagnostic = diagnostic;
            }
        }

        internal void Initialize(
            long moduleBase,
            ReadOnlySpan<byte> memory,
            string hash,
            INativeMemory nativeMemory,
            ManualLogSource logger,
            GatehouseBuildTarget gateTarget = null,
            bool installUnitHudPresentation = true,
            ScanRegion nativeRegion = null,
            bool installAivBuildStep = false)
        {
            lock (sync)
            {
                if (state != NativeApiState.Pending)
                    return;
                binaryHash = hash ?? string.Empty;
                nativeModuleBase = moduleBase;
                log = logger;
            }
            // Idempotent; the process plugin binds earlier in its own LibraryLoaded step.
            if (nativeMemory != null && memory.Length > 0)
                NativeOwnership.BindModule(moduleBase, memory.Length, nativeMemory.ReadByte);

            NativeApiState terminalState = NativeApiState.Ready;
            try
            {
                if (installUnitHudPresentation)
                {
                    UnitHudPresentationService.TryCreate(
                        binaryHash,
                        moduleBase,
                        memory,
                        log,
                        out unitHudPresentation,
                        out unitHudDiagnostic);
                }
                else
                {
                    unitHudPresentation = null;
                    unitHudDiagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.UnitHudPresentation,
                        NativeCapabilityState.UnsupportedBuild,
                        binaryHash,
                        "Managed HUD hooks are intentionally disabled in the isolated native test harness.");
                }
                var gatehouseOwnership = NativeOwnership;
                var gatehouseMutationSync = new object();
                GatehouseCapabilityResolver.Resolve(
                    binaryHash,
                    moduleBase,
                    memory,
                    nativeMemory,
                    gatehouseOwnership,
                    gatehouseMutationSync,
                    log,
                    gateTarget ?? GatehouseBuildTarget.Supported,
                    nativeRegion,
                    out gatehouseDistanceOrigin,
                    out gatehouseDistanceOriginDiagnostic,
                    out gatehouse,
                    out gatehouseDiagnostic);
                if (installAivBuildStep)
                {
                    AivBuildStepService.TryCreate(
                        binaryHash,
                        moduleBase,
                        memory,
                        nativeRegion,
                        log,
                        NativeOwnership,
                        out aivBuildStep,
                        out aivBuildStepDiagnostic);
                }
                else
                {
                    aivBuildStep = null;
                    aivBuildStepDiagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.AivBuildStep,
                        NativeCapabilityState.UnsupportedBuild,
                        binaryHash,
                        "The AIV build-step hook is intentionally disabled in this isolated test harness.");
                }
            }
            catch (Exception ex)
            {
                // Capability resolvers contain their own error boundaries. Reaching this catch
                // means publication itself failed and the API cannot be trusted globally.
                terminalState = NativeApiState.Unavailable;
                gatehouseDistanceOrigin = null;
                gatehouse = null;
                unitHudPresentation = null;
                aivBuildStep = null;
                gatehouseDistanceOriginDiagnostic = Faulted(NativeCapabilityIds.GatehouseDistanceOrigin, ex.Message);
                gatehouseDiagnostic = Faulted(NativeCapabilityIds.GatehouseTiming, ex.Message);
                unitHudDiagnostic = Faulted(NativeCapabilityIds.UnitHudPresentation, ex.Message);
                aivBuildStepDiagnostic = Faulted(NativeCapabilityIds.AivBuildStep, ex.Message);
                NativeApiLog.Error(log, $"APIShared initialization failed globally: build={binaryHash}, error={ex}");
            }

            Action<IApiShared>[] callbacks;
            lock (sync)
            {
                state = terminalState;
                callbacks = readyCallbacks.ToArray();
                readyCallbacks.Clear();
            }
            NativeApiLog.Debug(log, $"APIShared initialized: state={terminalState}, build={binaryHash}, lobbyState={lobbyStateDiagnostic.State}, playerDefeat={playerDefeatDiagnostic.State}, briefingGold={briefingGoldDiagnostic.State}, gatehouseDistanceOrigin={gatehouseDistanceOriginDiagnostic.State}, gatehouseTiming={gatehouseDiagnostic.State}, unitHudPresentation={unitHudDiagnostic.State}, aivBuildStep={aivBuildStepDiagnostic.State}.");
            foreach (Action<IApiShared> callback in callbacks)
            {
                InvokeReadyCallback(callback);
            }
        }

        // Error boundary of the LibraryLoaded initialization (issue #5). Steps run in order on the
        // caller's thread and are independent: a throw is logged, handed to that step's Failed callback
        // and never stops a later step. One step is expected to call Initialize. If no step published
        // a terminal state, the API publishes Unavailable here, so WhenReady callbacks always complete.
        // Nothing is rethrown to the Script Extender's LibraryLoaded publisher or to Awake.
        internal void RunNativeInitialization(ManualLogSource logger, params NativeInitializationStep[] steps)
        {
            string lastFailure = null;
            foreach (NativeInitializationStep step in steps)
            {
                try { step.Run(); }
                catch (Exception ex)
                {
                    lastFailure = $"Initialization step '{step.Name}' failed: {ex.GetType().Name}: {ex.Message}";
                    try { Internal.RuntimeHealth.Failure(logger, "initialization:" + step.Name, true, $"{lastFailure}; independent steps continue. {ex}"); }
                    catch { }
                    try { step.Failed?.Invoke(lastFailure); }
                    catch { }
                }
            }
            if (State == NativeApiState.Pending)
                PublishInitializationFailure(lastFailure ?? "Native initialization did not publish a terminal state.", logger);
        }

        // A failed repair-tooltip binding leaves the on-demand repair capability Faulted with that reason
        // instead of creating it later without its HUD tooltip.
        internal void FaultBuildingRepair(string reason)
        {
            lock (sync)
            {
                if (buildingRepair == null && buildingRepairDiagnostic.State == NativeCapabilityState.Pending)
                    buildingRepairDiagnostic = Faulted(NativeCapabilityIds.BuildingRepair, reason);
            }
        }

        // Terminal fallback when Initialize was not reached or threw before publishing: the same
        // Unavailable outcome as Initialize's own global failure, with the step's reason.
        private void PublishInitializationFailure(string reason, ManualLogSource logger)
        {
            Action<IApiShared>[] callbacks;
            lock (sync)
            {
                if (state != NativeApiState.Pending)
                    return;
                if (log == null)
                    log = logger;
                gatehouseDistanceOrigin = null;
                gatehouse = null;
                unitHudPresentation = null;
                aivBuildStep = null;
                gatehouseDistanceOriginDiagnostic = Faulted(NativeCapabilityIds.GatehouseDistanceOrigin, reason);
                gatehouseDiagnostic = Faulted(NativeCapabilityIds.GatehouseTiming, reason);
                unitHudDiagnostic = Faulted(NativeCapabilityIds.UnitHudPresentation, reason);
                aivBuildStepDiagnostic = Faulted(NativeCapabilityIds.AivBuildStep, reason);
                state = NativeApiState.Unavailable;
                callbacks = readyCallbacks.ToArray();
                readyCallbacks.Clear();
            }
            try { NativeApiLog.Error(log, $"APIShared initialization failed globally: build={binaryHash}, error={reason}"); }
            catch { }
            foreach (Action<IApiShared> callback in callbacks)
            {
                InvokeReadyCallback(callback);
            }
        }

        public bool TryGetGatehouseDistanceOrigin(
            string ownerGuid,
            out IGatehouseDistanceOriginCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (!ValidateOwner(ownerGuid, NativeCapabilityIds.GatehouseDistanceOrigin, out diagnostic))
                return false;
            lock (sync)
            {
                if (gatehouseDistanceOrigin == null)
                {
                    diagnostic = gatehouseDistanceOriginDiagnostic;
                    return false;
                }
                capability = gatehouseDistanceOrigin.Bind(ownerGuid);
                diagnostic = gatehouseDistanceOriginDiagnostic;
                return true;
            }
        }

        public bool TryGetGatehouseTiming(string ownerGuid, out IGatehouseTimingCapability capability, out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (!ValidateOwner(ownerGuid, NativeCapabilityIds.GatehouseTiming, out diagnostic))
                return false;
            lock (sync)
            {
                if (gatehouse == null)
                {
                    diagnostic = gatehouseDiagnostic;
                    return false;
                }
                capability = gatehouse.Bind(ownerGuid);
                diagnostic = gatehouseDiagnostic;
                return true;
            }
        }

        public bool TryGetUnitHudPresentation(string ownerGuid, out IUnitHudPresentationCapability capability, out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (!ValidateOwner(ownerGuid, NativeCapabilityIds.UnitHudPresentation, out diagnostic))
                return false;
            lock (sync)
            {
                if (unitHudPresentation == null)
                {
                    diagnostic = unitHudDiagnostic;
                    return false;
                }
                capability = unitHudPresentation.Bind(ownerGuid);
                diagnostic = unitHudDiagnostic;
                return true;
            }
        }

        internal bool TryGetHudExtrasButtons(string ownerGuid, out IHudExtrasButtonsCapability capability, out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.HudExtrasButtons, NativeCapabilityState.ValidationFailed,
                    string.Empty, "A nonempty owner GUID is required."); return false;
            }
            lock (sync)
            {
                if (hudExtrasButtons == null && hudExtrasDiagnostic != null) { diagnostic = hudExtrasDiagnostic; return false; }
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.HudExtrasButtons,
                    hudExtrasButtons == null ? NativeCapabilityState.Pending : NativeCapabilityState.Available, string.Empty,
                    hudExtrasButtons == null ? "Managed presentation is not initialized." : "Independent side-HUD presentation is available.");
                if (hudExtrasButtons == null) return false;
                capability = hudExtrasButtons.Bind(ownerGuid); return true;
            }
        }

        public bool TryGetAivBuildStep(
            string ownerGuid,
            out IAivBuildStepCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (!ValidateOwner(ownerGuid, NativeCapabilityIds.AivBuildStep, out diagnostic))
                return false;
            lock (sync)
            {
                if (aivBuildStep == null)
                {
                    diagnostic = aivBuildStepDiagnostic;
                    return false;
                }
                capability = aivBuildStep.Bind(ownerGuid);
                diagnostic = aivBuildStepDiagnostic;
                return true;
            }
        }

        public bool TryGetMissionLifecycle(string ownerGuid, out IMissionLifecycleCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.MissionLifecycle,
                    NativeCapabilityState.ValidationFailed, string.Empty, "A non-empty owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                diagnostic = missionLifecycleDiagnostic;
                if (missionLifecycle == null) return false;
                capability = missionLifecycle.Bind(ownerGuid);
                return true;
            }
        }

        public bool TryGetLobbyState(
            string ownerGuid,
            out ILobbyStateCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.LobbyState,
                    NativeCapabilityState.ValidationFailed,
                    string.Empty,
                    "A non-empty BepInEx owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                if (lobbyState == null)
                {
                    diagnostic = lobbyStateDiagnostic;
                    return false;
                }
                capability = lobbyState.Bind(ownerGuid);
                diagnostic = lobbyStateDiagnostic;
                return true;
            }
        }

        public bool TryGetPlayerDefeat(
            string ownerGuid,
            out IPlayerDefeatCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.PlayerDefeat,
                    NativeCapabilityState.ValidationFailed,
                    string.Empty,
                    "A non-empty BepInEx owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                if (playerDefeat == null)
                {
                    diagnostic = playerDefeatDiagnostic;
                    return false;
                }
                capability = playerDefeat.Bind(ownerGuid);
                diagnostic = playerDefeatDiagnostic;
                return true;
            }
        }

        public bool TryGetBriefingGoldPresentation(
            string ownerGuid,
            out IBriefingGoldPresentationCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.BriefingGoldPresentation,
                    NativeCapabilityState.ValidationFailed,
                    string.Empty,
                    "A non-empty BepInEx owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                diagnostic = briefingGoldDiagnostic;
                if (briefingGoldPresentation == null)
                    return false;
                capability = briefingGoldPresentation.Bind(ownerGuid);
                return true;
            }
        }

        public bool TryGetBuildingRepair(string ownerGuid, out IBuildingRepairCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (!ValidateOwner(ownerGuid, NativeCapabilityIds.BuildingRepair, out diagnostic))
                return false;
            lock (sync)
            {
                if (buildingRepair == null && buildingRepairDiagnostic.State == NativeCapabilityState.Pending)
                {
                    if (state != NativeApiState.Ready)
                        buildingRepairDiagnostic = Faulted(NativeCapabilityIds.BuildingRepair, "APIShared is unavailable.");
                    else
                        BuildingRepairService.TryCreate(binaryHash, nativeModuleBase, log, RepairTooltip, NativeOwnership,
                            out buildingRepair, out buildingRepairDiagnostic);
                }
                diagnostic = buildingRepairDiagnostic;
                if (buildingRepair == null) return false;
                capability = buildingRepair.Bind(ownerGuid);
                return true;
            }
        }

        private bool ValidateOwner(string ownerGuid, string capabilityId, out NativeCapabilityDiagnostic diagnostic)
        {
            lock (sync)
            {
                if (state == NativeApiState.Pending)
                {
                    diagnostic = Pending(capabilityId);
                    return false;
                }
                if (string.IsNullOrWhiteSpace(ownerGuid))
                {
                    diagnostic = new NativeCapabilityDiagnostic(capabilityId, NativeCapabilityState.ValidationFailed, binaryHash, "A non-empty BepInEx owner GUID is required.");
                    return false;
                }
            }
            diagnostic = null;
            return true;
        }

        internal static string ComputeSha256(ReadOnlySpan<byte> bytes)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes.ToArray())).Replace("-", string.Empty);
        }

        private NativeCapabilityDiagnostic Faulted(string capabilityId, string reason) =>
            new NativeCapabilityDiagnostic(capabilityId, NativeCapabilityState.Faulted, binaryHash, reason);
        private static NativeCapabilityDiagnostic Pending(string capabilityId) =>
            new NativeCapabilityDiagnostic(capabilityId, NativeCapabilityState.Pending, string.Empty, "APIShared has not completed native initialization.");
    }

    // One independent step of ApiSharedRuntime.RunNativeInitialization. Failed receives the logged
    // reason when Run throws; its own exceptions are ignored.
    internal sealed class NativeInitializationStep
    {
        internal NativeInitializationStep(string name, Action run, Action<string> failed = null)
        {
            Name = name;
            Run = run ?? throw new ArgumentNullException(nameof(run));
            Failed = failed;
        }

        internal string Name { get; }
        internal Action Run { get; }
        internal Action<string> Failed { get; }
    }
}

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;

namespace APIShared
{
    internal sealed partial class ApiSharedRuntime
    {
        public void WhenReady(Action<IApiShared> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));
            lock (sync)
            {
                if (state == NativeApiState.Pending || deferReadyCallbacks)
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
                    try
                    {
                        var candidate = new HudExtrasButtonsService(logger);
                        candidate.Start();
                        hudExtrasButtons = candidate;
                    }
                    catch (Exception error)
                    {
                        hudExtrasDiagnostic = Faulted(NativeCapabilityIds.HudExtrasButtons, error.Message);
                        NativeApiLog.Error(logger, "Side-HUD frame service unavailable: " + error);
                    }
                }
                if (missionLifecycleDiagnostic.State == NativeCapabilityState.Pending)
                    MissionLifecycleService.TryCreate(logger, out missionLifecycle, out missionLifecycleDiagnostic);
                if (missionPlayStateReadinessDiagnostic.State == NativeCapabilityState.Pending)
                    MissionPlayStateReadinessService.TryCreate(missionLifecycle, logger,
                        out missionPlayStateReadiness, out missionPlayStateReadinessDiagnostic);
                if (coopAiStartImportDiagnostic.State == NativeCapabilityState.Pending)
                {
                    // Plans are bound to lifecycle start attempts; without them nothing could be planned safely.
                    if (missionLifecycle == null)
                        coopAiStartImportDiagnostic = Faulted(NativeCapabilityIds.CoopAiStartImport,
                            "The mission lifecycle is unavailable.");
                    else
                        CoopAiStartImportService.TryCreate(logger, out coopAiStartImport, out coopAiStartImportDiagnostic);
                }
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
                PlayerPerspectiveAPI.BindLifecycle(missionLifecycle);
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
                callbacks = TakeReadyCallbacks();
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
            lock (sync) deferReadyCallbacks = true;
            string lastFailure = null;
            try
            {
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
            finally
            {
                Action<IApiShared>[] callbacks;
                lock (sync)
                {
                    deferReadyCallbacks = false;
                    callbacks = TakeReadyCallbacks();
                }
                foreach (Action<IApiShared> callback in callbacks)
                    InvokeReadyCallback(callback);
            }
        }

        // Caller holds sync. Returns nothing while the state is Pending or delivery is deferred.
        private Action<IApiShared>[] TakeReadyCallbacks()
        {
            if (state == NativeApiState.Pending || deferReadyCallbacks || readyCallbacks.Count == 0)
                return Array.Empty<Action<IApiShared>>();
            Action<IApiShared>[] callbacks = readyCallbacks.ToArray();
            readyCallbacks.Clear();
            return callbacks;
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
                callbacks = TakeReadyCallbacks();
            }
            try { NativeApiLog.Error(log, $"APIShared initialization failed globally: build={binaryHash}, error={reason}"); }
            catch { }
            foreach (Action<IApiShared> callback in callbacks)
            {
                InvokeReadyCallback(callback);
            }
        }
    }
}

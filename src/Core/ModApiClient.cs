using System;

namespace APIShared
{
    /// <summary>Owner-bound access to shared capabilities. Keep this client in your process-owned runtime; it does not own or dispose shared hooks.</summary>
    public sealed class ModApiClient
    {
        private readonly ApiSharedRuntime api;

        internal ModApiClient(string ownerGuid, ApiSharedRuntime api)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid))
                throw new ArgumentException("A non-empty BepInEx plugin GUID is required.", nameof(ownerGuid));
            OwnerGuid = ownerGuid;
            this.api = api ?? throw new ArgumentNullException(nameof(api));
            Network = new Networking.ModNetworkClient(ownerGuid);
            Workshop = new Workshop.WorkshopClient(ownerGuid);
            Ui = new Presentation.UiWorkClient(ownerGuid);
            GoodsWallet = new Economy.GoodsWalletClient(ownerGuid);
        }

        /// <summary>The exact GUID used for acquisitions and owner-local registrations; never a display name.</summary>
        public string OwnerGuid { get; }
        /// <summary>Process-rooted Chore and control-plane packet registration for this owner; registration requires Unity-thread Extender readiness.</summary>
        public Networking.ModNetworkClient Network { get; }
        /// <summary>Owner-bound Workshop upload, receipt-based installation and catalog service. Steam calls require the Unity thread; planning never installs files.</summary>
        public Workshop.WorkshopClient Workshop { get; }
        /// <summary>Owner-bound deferred Unity work and coalesced before-render refreshes; independent of native/network readiness.</summary>
        public Presentation.UiWorkClient Ui { get; }
        /// <summary>Owner-bound native pooled storage, separate virtual goods and coordinated gold exchanges.
        /// Startup registration installs shared capacity owners; no work starts by obtaining this client.</summary>
        public Economy.NativeStorageClient NativeStorage => new Economy.NativeStorageClient(OwnerGuid);
        /// <summary>Owner-bound native-gold and virtual-goods exchanges; acquisition installs no hooks.</summary>
        public Economy.GoodsWalletClient GoodsWallet { get; }
        /// <summary>Explicit, Unity-thread in-game test automation for this owner. Obtaining the client does not start work.</summary>
        public Automation.AutomationClient Automation => new Automation.AutomationClient(this);
        /// <summary>Global publication state, independent of individual capability availability.</summary>
        public NativeApiState State => api.State;

        /// <summary>Runs after global initialization reaches a terminal state. Late calls run synchronously on the caller's thread; early calls use the initialization publisher thread. Exceptions are isolated; no dispatch occurs.</summary>
        public void WhenReady(Action<ModApiClient> callback)
        {
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            api.WhenReady(_ => callback(this));
        }

        /// <summary>Acquires mission initialization/start/end observations and current context for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IMissionLifecycleCapability"/> for operation/thread contracts.</summary>
        public bool TryGetMissionLifecycle(out IMissionLifecycleCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetMissionLifecycle(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires protected, point-in-time native-world observations after API publication.
        /// Cold acquisition validates and retains the actual game engine monitor; failure is isolated in
        /// diagnostic. No hooks are installed. See <see cref="NativeWorldReadClient"/> for required locking,
        /// managed readiness and the distinction between read evidence and mutation/lifetime authority.</summary>
        public bool TryGetNativeWorldReads(out NativeWorldReadClient client, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetNativeWorldReads(OwnerGuid, out client, out diagnostic);

        /// <summary>Acquires first-current-mission PlayState observation, independent of managed Start and loading duration.
        /// Register on Unity and retain the capability; its Current receipt is invalidated at mission end/replacement.</summary>
        public bool TryGetMissionPlayStateReadiness(out IMissionPlayStateReadinessCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetMissionPlayStateReadiness(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires semantic military AI admission, protected placement and map declarations.
        /// Acquisition enables native group-order observation for this process; no unit is changed.
        /// Completion callbacks use their committing thread and never implicitly dispatch UI work.</summary>
        public bool TryGetAiMilitary(out Units.AiMilitary.IAiMilitaryCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            Units.AiMilitary.AiMilitaryService.TryGet(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires exclusive gatehouse distance-origin configuration for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IGatehouseDistanceOriginCapability"/> for operation/thread contracts.</summary>
        public bool TryGetGatehouseDistanceOrigin(out IGatehouseDistanceOriginCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetGatehouseDistanceOrigin(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires exclusive gatehouse timing/distance configuration for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IGatehouseTimingCapability"/> for operation/thread contracts.</summary>
        public bool TryGetGatehouseTiming(out IGatehouseTimingCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetGatehouseTiming(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires shared troop HUD contributions and optional action buttons for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IUnitHudPresentationCapability"/> for operation/thread contracts.</summary>
        public bool TryGetUnitHudPresentation(out IUnitHudPresentationCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetUnitHudPresentation(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires independent side-HUD buttons for this owner; does not require troop selection or native Unit HUD hooks.</summary>
        public bool TryGetHudExtrasButtons(out IHudExtrasButtonsCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetHudExtrasButtons(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires observation around unchanged Vanilla AIV build-step calls for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IAivBuildStepCapability"/> for operation/thread contracts.</summary>
        public bool TryGetAivBuildStep(out IAivBuildStepCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetAivBuildStep(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires immutable lobby-state observations with known-state replay for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="ILobbyStateCapability"/> for operation/thread contracts.</summary>
        public bool TryGetLobbyState(out ILobbyStateCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetLobbyState(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires lord-death and official defeat notifications for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IPlayerDefeatCapability"/> for operation/thread contracts.</summary>
        public bool TryGetPlayerDefeat(out IPlayerDefeatCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetPlayerDefeat(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires ordered briefing-gold presentation adjustments for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IBriefingGoldPresentationCapability"/> for operation/thread contracts.</summary>
        public bool TryGetBriefingGoldPresentation(out IBriefingGoldPresentationCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetBriefingGoldPresentation(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires Coop Trail AI castle and custom-AIC import planning for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="ICoopAiStartImportCapability"/> for timing, thread and failure contracts.</summary>
        public bool TryGetCoopAiStartImport(out ICoopAiStartImportCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetCoopAiStartImport(OwnerGuid, out capability, out diagnostic);

        /// <summary>Acquires the optional native plague standing-height service. No policy is activated by acquisition.</summary>
        public bool TryGetPlagueHeight(out IPlagueHeightCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            PlagueHeightService.TryGet(OwnerGuid, out capability, out diagnostic);

        /// <summary>Reads the effective plague standing-height policy of all owners (the one the native damage
        /// path applies). Any thread; no allocation, lock or callback. Returns false with an inactive policy when
        /// the service is unavailable (then the native path is Vanilla). The value changes only when an owner sets
        /// a policy, which owners do on the main thread before/after simulation, identically on all peers.</summary>
        public bool TryGetPlagueHeightPolicy(out PlagueHeightPolicy policy, out NativeCapabilityDiagnostic diagnostic) =>
            PlagueHeightService.TryGetPolicy(out policy, out diagnostic);

        /// <summary>Acquires building repair quotes and tooltip presentation for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IBuildingRepairCapability"/> for operation/thread contracts.</summary>
        public bool TryGetBuildingRepair(out IBuildingRepairCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetBuildingRepair(OwnerGuid, out capability, out diagnostic);
    }
}

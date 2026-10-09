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
        }

        /// <summary>The exact GUID used for acquisitions and owner-local registrations; never a display name.</summary>
        public string OwnerGuid { get; }
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

        /// <summary>Acquires building repair quotes and tooltip presentation for this owner. On failure inspect diagnostic; other capabilities remain independent. See <see cref="IBuildingRepairCapability"/> for operation/thread contracts.</summary>
        public bool TryGetBuildingRepair(out IBuildingRepairCapability capability, out NativeCapabilityDiagnostic diagnostic) =>
            api.TryGetBuildingRepair(OwnerGuid, out capability, out diagnostic);
    }
}

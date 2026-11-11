using BepInEx.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;

namespace APIShared
{
    internal sealed partial class ApiSharedRuntime
    {
        internal bool TryReadGatehouseClosingRange(out GatehouseDistanceOrigin origin, out int maximumNativeDistance)
        {
            var timing = System.Threading.Volatile.Read(ref gatehouse);
            if (timing == null || System.Threading.Volatile.Read(ref gatehouseDistanceOrigin) == null)
            {
                origin = default;
                maximumNativeDistance = 0;
                return false;
            }

            timing.ReadClosingRange(out origin, out maximumNativeDistance);
            return true;
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

        // Static brokers on the Unit HUD hooks (UnitHudSpriteColours) need the installed service, not a facade.
        internal bool TryGetUnitHudService(out UnitHudPresentationService service, out string reason)
        {
            lock (sync)
            {
                service = unitHudPresentation;
                if (service != null)
                {
                    reason = null;
                    return true;
                }
                reason = state == NativeApiState.Pending
                    ? "APIShared native initialization has not completed; the Unit HUD presentation hooks are not installed yet."
                    : $"Unit HUD presentation is unavailable ({unitHudDiagnostic.State}): {unitHudDiagnostic.Reason}";
                return false;
            }
        }

        internal bool TryGetBuildingPanelExtensions(string ownerGuid, out IBuildingPanelExtensionsCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingPanelExtensions, NativeCapabilityState.ValidationFailed,
                    string.Empty, "A nonempty owner GUID is required."); return false;
            }
            lock (sync)
            {
                if (buildingPanelExtensions == null)
                {
                    diagnostic = buildingPanelExtensionsDiagnostic ?? new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingPanelExtensions,
                        NativeCapabilityState.Pending, string.Empty, "Managed presentation is not initialized.");
                    return false;
                }
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.BuildingPanelExtensions, NativeCapabilityState.Available,
                    string.Empty, "Building panel extensions are available; refresh hooks install with the first registration.");
                capability = buildingPanelExtensions.Bind(ownerGuid); return true;
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

        internal bool TryGetMissionPlayStateReadiness(string ownerGuid, out IMissionPlayStateReadinessCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.MissionPlayStateReadiness,
                    NativeCapabilityState.ValidationFailed, string.Empty, "A non-empty owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                diagnostic = missionPlayStateReadinessDiagnostic;
                if (missionPlayStateReadiness == null) return false;
                capability = missionPlayStateReadiness.Bind(ownerGuid);
                return true;
            }
        }

        internal bool TryGetCoopAiStartImport(string ownerGuid, out ICoopAiStartImportCapability capability,
            out NativeCapabilityDiagnostic diagnostic)
        {
            capability = null;
            // A managed service created in Awake: like the mission lifecycle it needs no native publication.
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.CoopAiStartImport,
                    NativeCapabilityState.ValidationFailed, binaryHash, "A non-empty BepInEx owner GUID is required.");
                return false;
            }
            lock (sync)
            {
                diagnostic = coopAiStartImportDiagnostic;
                if (coopAiStartImport == null) return false;
                capability = coopAiStartImport.Bind(ownerGuid);
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
            // Creating the service under the lock is intended: it runs once, and concurrent first
            // callers must see one service and one diagnostic.
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
    }
}

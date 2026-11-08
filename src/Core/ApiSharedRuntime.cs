using BepInEx.Logging;
using System;
using System.Collections.Generic;
using RedBird.Core.Memory;

namespace APIShared
{
    // Process-owned composition root: managed availability is published before native validation.
    // One service failure must not disable independent capabilities; terminal readiness is separate.
    // Capability facades bind owner identity while services retain registrations and permanent hooks.
    internal sealed partial class ApiSharedRuntime : IApiShared
    {
        internal const string SupportedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";

        private readonly object sync = new object();
        private readonly List<Action<IApiShared>> readyCallbacks = new List<Action<IApiShared>>();
        // True while RunNativeInitialization executes its steps: WhenReady callbacks wait for the
        // last step, so every service initialized after publication accepts their registrations.
        private bool deferReadyCallbacks;
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
        private MissionPlayStateReadinessService missionPlayStateReadiness;
        private NativeCapabilityDiagnostic missionPlayStateReadinessDiagnostic = Pending(NativeCapabilityIds.MissionPlayStateReadiness);
        private BriefingGoldPresentationService briefingGoldPresentation;
        private BuildingRepairService buildingRepair;
        private CoopAiStartImportService coopAiStartImport;
        internal RepairTooltipViewModel RepairTooltip { get; } = new RepairTooltipViewModel();
        // Shared defeat observations (state snapshots) for internal readers such as eliminated-participant views.
        internal PlayerDefeatService PlayerDefeat => playerDefeat;
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
        private NativeCapabilityDiagnostic coopAiStartImportDiagnostic = Pending(NativeCapabilityIds.CoopAiStartImport);
        private ManualLogSource log;

        internal NativeOwnershipRegistry NativeOwnership { get; } = new NativeOwnershipRegistry();

        internal static ApiSharedRuntime ProcessInstance { get; } = new ApiSharedRuntime();
        // For lazily installed services without their own logger; null before managed initialization.
        internal ManualLogSource Logger => System.Threading.Volatile.Read(ref log);

        public NativeApiState State { get { lock (sync) return state; } }

        internal static string ComputeSha256(ReadOnlySpan<byte> bytes)
        {
            return Sha256Hex.Of(bytes.ToArray());
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

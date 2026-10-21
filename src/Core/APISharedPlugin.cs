using BepInEx;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using System;
using System.IO;
using System.Security.Cryptography;

namespace APIShared
{
    /// <summary>BepInEx host for the process-wide APIShared.</summary>
    [BepInDependency(ScriptExtenderGuid, "2.14.0")]
    [BepInDependency("scde.sc2-fog-of-war", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("fixes", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class APISharedPlugin : BaseUnityPlugin
    {
        private const string ScriptExtenderGuid = "000shcdese";
        /// <summary>Stable BepInEx plugin GUID.</summary>
        public const string PluginGuid = "APIShared_Serp";
        /// <summary>Display name of the API plugin.</summary>
        public const string PluginName = "APIShared";
        /// <summary>Current API plugin version.</summary>
        public const string PluginVersion = "0.12.3";

        // Services retain this forwarding logger after startup cleanup (failures via RuntimeHealth; Debug only with diagnostics).
        private BepInEx.Logging.ManualLogSource RuntimeLogger => Internal.DebugLogHelper.ForExternalCode(Logger);

        private void Awake()
        {
            Internal.DebugLogHelper.InitializeDiagnostics(Config, Logger);
            Compatibility.FixesWheatStartup.Initialize(RuntimeLogger);
            Workshop.WorkshopCleanupRuntime.Initialize(RuntimeLogger);
            ScriptExtenderFixes.ScriptExtenderFixesRuntime.Initialize(Config, RuntimeLogger);
            ScriptExtenderFixes.LordMediaResolverRuntime.Initialize(RuntimeLogger);
            APIShared.Internal.UnityMainThreadDispatch.InitializeForCurrentThread();
            Presentation.BuildingTooltipPresentation.Initialize(RuntimeLogger);
            // Logging only: unit-settled SE subscriptions start with the first consumer registration.
            Units.UnitSettledEvents.Initialize(RuntimeLogger);
            Workshop.WorkshopRuntime.Initialize(this, RuntimeLogger);
            ApiSharedRuntime.ProcessInstance.InitializeManaged(RuntimeLogger);
            ModSettings.ModSettingsStartLogRuntime.Initialize(RuntimeLogger);
            // The Script Extender event roots this plugin's native initialization after BepInEx
            // destroys its short-lived manager object. Native process state is never torn down here.
            CrusaderLibrary.Instance.LibraryLoaded += OnLibraryLoaded;
        }

        // Each step is an independent error boundary (ApiSharedRuntime.RunNativeInitialization): a
        // throw is logged and the next step still runs, so one failing service cannot keep the API
        // Pending. The "native API publication" step publishes the terminal state; if it cannot, the
        // boundary publishes Unavailable. WhenReady callbacks therefore always complete.
        private void OnLibraryLoaded(CrusaderLibraryLoadContext context)
        {
            // Startup detours of APIShared and of consumers in this synchronous run skip RedBird's
            // per-patch thread suspension (NativeStartupPatchWindow). Opening never throws.
            NativeStartupPatchWindow.OpenForStartupRun();
            ApiSharedRuntime runtime = ApiSharedRuntime.ProcessInstance;
            string hash = string.Empty;
            try
            {
            runtime.RunNativeInitialization(RuntimeLogger,
                // Binds the image for site registrations before any APIShared or consumer hook claims it.
                new NativeInitializationStep("native site registry", () => runtime.NativeOwnership.BindModule(
                    context.ModuleHandle.ToInt64(), context.Memory.Length, new ProcessNativeMemory().ReadByte)),
                // MainHUD loads before a consumer may request the on-demand repair capability.
                new NativeInitializationStep("repair tooltip binding", () => GameXAMLManagerAPI.Instance.RegisterBinding(
                    "APISharedRepairExtrasHost", runtime.RepairTooltip), runtime.FaultBuildingRepair),
                new NativeInitializationStep("building tooltip bindings", Presentation.BuildingTooltipPresentation.Bind),
                new NativeInitializationStep("savegame settings handler", () => SavegameModSettings.Initialize(RuntimeLogger)),
                new NativeInitializationStep("savegame load dialog", () => Savegames.SaveLoadDialogEvents.Initialize(Config, RuntimeLogger)),
                // An unreadable CrusaderDE.dll leaves the hash empty, so hash-bound services report it unsupported.
                new NativeInitializationStep("CrusaderDE.dll hash", () => hash = ComputeInstalledHash()),
                new NativeInitializationStep("pathfinding tables", () => ScriptExtenderReplacements.PathfindingTables.Initialize(
                    context.ModuleHandle, context.Memory.Length, hash, RuntimeLogger)),
                new NativeInitializationStep("Assassin path", () => AssassinPathAPI.Initialize(
                    context.ModuleHandle, context.Memory, context.Region, hash, RuntimeLogger)),
                new NativeInitializationStep("Assassin attack control", () => AssassinAttackControlAPI.Initialize(
                    context.ModuleHandle, context.Region, hash, RuntimeLogger)),
                new NativeInitializationStep("market prices", () => Economy.MarketPriceNativeRuntime.Initialize(
                    context.ModuleHandle, context.Region, hash, RuntimeLogger)),
                new NativeInitializationStep("audio unit context", () => Audio.SoundRuntime.InitializeNative(
                    context, RuntimeLogger, runtime.NativeOwnership)),
                new NativeInitializationStep("rounded fear popularity", () => Economy.FearPopularityRuntime.Initialize(hash, RuntimeLogger)),
                new NativeInitializationStep("native API publication", () => runtime.Initialize(
                    context.ModuleHandle.ToInt64(),
                    context.Memory,
                    hash,
                    new ProcessNativeMemory(),
                    RuntimeLogger,
                    nativeRegion: context.Region,
                    installAivBuildStep: true)),
                new NativeInitializationStep("popularity display", () => ScriptExtenderFixes.PopularityDisplayRuntime.Initialize(hash, RuntimeLogger)),
                // Managed SE correction; after the Extender has applied its managed detours.
                new NativeInitializationStep("interpolate event reuse", () => ScriptExtenderFixes.InterpolateEventReuseRuntime.Initialize(Config, RuntimeLogger)),
                // One summary of registrations and refusals after this frame's startup handlers.
                new NativeInitializationStep("native site report", () =>
                    NativeSites.NativeSiteRegistry.ScheduleStartupReport(RuntimeLogger)));
            }
            finally { NativeStartupPatchWindow.EndApiSharedHandler(); }
        }

        private static string ComputeInstalledHash()
        {
            string path = Path.Combine(
                Paths.GameRootPath,
                "Stronghold Crusader Definitive Edition_Data",
                "Plugins",
                "x86_64",
                "CrusaderDE.dll");
            using (FileStream stream = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        }
    }
}

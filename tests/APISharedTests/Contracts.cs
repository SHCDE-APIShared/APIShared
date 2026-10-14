using APIShared.ModSettings;
using APIShared;
using CrusaderDE;
using Iced.Intel;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyPublicSurface() => TestPublicSurface();

        private static void TestPublicSurface()
        {
            var imageSlots = (UnitHudImageSlot[])Enum.GetValues(typeof(UnitHudImageSlot));
            Assert(imageSlots.Length >= 7 &&
                imageSlots[0] == UnitHudImageSlot.UIBuildingsO011 &&
                imageSlots[1] == UnitHudImageSlot.UIBuildingsO012 &&
                imageSlots[2] == UnitHudImageSlot.UIButtonsK007 &&
                imageSlots[3] == UnitHudImageSlot.UIButtonsK008 &&
                imageSlots[4] == UnitHudImageSlot.UIButtonsO016 &&
                imageSlots[5] == UnitHudImageSlot.UIButtonsO017 &&
                imageSlots[6] == UnitHudImageSlot.UIButtonsO018,
                "existing unit-HUD image slots retain their ordinal prefix; additional slots are allowed");
            var expected = new HashSet<string>(StringComparer.Ordinal)
            {
                "APIShared.ScriptExtenderReplacements.GamePathingManagerAPI",
                "APIShared.UnitLookupFailure",
                "APIShared.UnitAccess",
                "APIShared.AssassinPathBuilder",
                "APIShared.AssassinPathAPI",
                "APIShared.AssassinTransitionKind",
                "APIShared.AssassinGateTransitionPolicy",
                "APIShared.IEnemyGateClimbRoutePolicySnapshot",
                "APIShared.MissionStartKind",
                "APIShared.MissionMapType",
                "APIShared.MissionLifecycleKind",
                "APIShared.MissionInitializationPhase",
                "APIShared.MissionEndReason",
                "APIShared.MissionContext",
                "APIShared.MissionLifecycleNotification",
                "APIShared.IMissionLifecycleCapability",
                "APIShared.GameModes.GameModeKind",
                "APIShared.GameModes.GameModeLaunchVariant",
                "APIShared.GameModes.GameTrailType",
                "APIShared.GameModes.GameModeSnapshot",
                "APIShared.GameModes.GameModeHelper",
                "APIShared.GameModes.GameplayModAllowedContext",
                "APIShared.GameModes.GameplayModActivationProfile",
                "APIShared.GameModes.GameplayModModePolicy",
                "APIShared.ModSettings.ModSettingsSearchMatcher",
                "APIShared.ModSettings.ModSettingsSearch",
                "APIShared.ModSettings.ModSettingsSearchVisibilityConverter",
                "APIShared.ModSettings.ModSettingsSearchEntry",
                "APIShared.ModSettings.PerPlayerLobbySettingsBuilder",
                "APIShared.ModSettings.PerPlayerLobbySnapshot",
                "APIShared.ModSettings.PresetLocalAttribute",
                "APIShared.ModSettings.PresetLobbyModSettingsViewModel",
                "APIShared.ModSettings.LobbyModSettingsPresetRegistration",
                "APIShared.ModSettings.LobbyModSettingsPresetRegistration+PreparedExternalSettings",
                "APIShared.ModSettings.IModSettingsPresetEndpoint",
                "APIShared.ModSettings.IModSettingsMissionSourceEndpoint",
                "APIShared.ModSettings.IModSettingsWorkingCopyEndpoint",
                "APIShared.ModSettings.ModSettingsWorkingSourceKind",
                "APIShared.ModSettings.ModSettingsWorkingSource",
                "APIShared.ModSettings.IModSettingsWorkingSourceProvider",
                "APIShared.ModSettings.ModSettingsWorkingSourceRegistry",
                "APIShared.ModSettings.PublishedPresetValueMode",
                "APIShared.ModSettings.PresetSaveBulkMode",
                "APIShared.ModSettings.PresetSettingScope",
                "APIShared.ModSettings.PresetSettingDescriptor",
                "APIShared.ModSettings.DynamicPresetSetting",
                "APIShared.ModSettings.RequiresRestartAttribute",
                "APIShared.ModSettings.IModSettingsApplicationBackend", "APIShared.ModSettings.INetworkModSettingsApplicationBackend",
                "APIShared.ModSettings.ModSettingsApplication",
                "APIShared.ModSettings.IDynamicPresetSettingsProvider",
                "APIShared.ModSettings.PresetSaveSelection",
                "APIShared.ModSettings.PresetSaveSettingViewModel",
                "APIShared.ModSettings.ModSettingsPresetSourceKind",
                "APIShared.ModSettings.ModSettingsPresetListEntry",
                "APIShared.ModSettings.ModSettingsPresetSaveTarget",
                "APIShared.ModSettings.PublishedPresetSetting",
                "APIShared.ModSettings.PublishedModSettingsPreset",
                "APIShared.ModSettings.ModSettingsPresetJson",
                "APIShared.GatehouseFootprintCandidate",
                "APIShared.GatehouseDrawbridgeCoupling",
                "APIShared.GatehouseDistanceOrigin",
                "APIShared.GatehouseTimingSettings",
                "APIShared.GatehouseTimingValues",
                "APIShared.IGatehouseDistanceOriginCapability",
                "APIShared.IGatehouseTimingCapability",
                "APIShared.IGatehouseAutomationCapability",
                "APIShared.IUnitHudPresentationCapability",
                "APIShared.HudExtrasButtonContext",
                "APIShared.HudExtrasButtonDefinition",
                "APIShared.IHudExtrasButtonRegistration",
                "APIShared.IHudExtrasButtonsCapability",
                "APIShared.IUnitHudActivationCapability",
                "APIShared.IAivBuildStepCapability",
                "APIShared.ILobbyStateCapability",
                "APIShared.LobbyStateSnapshot",
                "APIShared.LocalSelectionAPI",
                "APIShared.LocalSelectionSnapshot",
                "APIShared.IPlayerDefeatCapability",
                "APIShared.IBriefingGoldPresentationCapability",
                "APIShared.IBuildingRepairCapability",
                "APIShared.BuildingRepairQuote",
                "APIShared.RepairTooltipEntry",
                "APIShared.RepairTooltipViewModel",
                "APIShared.BriefingGoldAdjustmentStage",
                "APIShared.BriefingGoldContext",
                "APIShared.BriefingGoldAdjuster",
                "APIShared.PlayerLordDeathNotification",
                "APIShared.PlayerDefeatNotification",
                "APIShared.IAivBuildStepObserver",
                "APIShared.IAivBuildStepInvocation",
                "APIShared.AivBuildStepContext",
                "APIShared.AivBuildStepCompletion",
                "APIShared.SavegameModSettingsRecord",
                "APIShared.TrailCreatorRule",
                "APIShared.SavegameLoadChoiceState",
                "APIShared.SavegameModSettingsRecordFormatter",
                "APIShared.SavegameModSettings",
                "APIShared.IApiShared",
                "APIShared.NativeSites.NativeSiteRegistry",
                "APIShared.NativeSites.NativeSiteMode",
                "APIShared.NativeSites.NativeSiteOverlapRule",
                "APIShared.NativeSites.NativeSiteRegistrationStatus",
                "APIShared.NativeSites.NativeSiteSpan",
                "APIShared.NativeSites.NativeSiteRegistration",
                "APIShared.NativeSites.NativeSiteRegistrationResult",
                "APIShared.NativeApiState",
                "APIShared.LobbyPreparationOverride",
                "APIShared.ElevatedMoatAiState",
                "APIShared.ElevatedMoatAiCapability",
                "APIShared.NativeCapabilityDiagnostic",
                "APIShared.NativeCapabilityIds",
                "APIShared.NativeCapabilityState",
                "APIShared.UnitHudSurface",
                "APIShared.UnitHudMouseButton",
                "APIShared.UnitHudImageSlot",
                "APIShared.UnitHudTint",
                "APIShared.UnitHudUnitSnapshot",
                "APIShared.UnitHudCategoryMatcher",
                "APIShared.UnitHudCategoryImageResolver",
                "APIShared.UnitHudTextKind",
                "APIShared.UnitHudTextResolver",
                "APIShared.UnitHudTextProfile",
                "APIShared.UnitHudCategoryDefinition",
                "APIShared.UnitHudRecruitmentTicket",
                "APIShared.UnitHudRecruitmentHandler",
                "APIShared.UnitHudCategorySnapshot",
                "APIShared.UnitHudSlotSnapshot",
                "APIShared.UnitHudControlGroupSnapshot",
                "APIShared.UnitHudInteractionContext",
                "APIShared.UnitHudInteractionHandler",
                "APIShared.UnitHudImageOverrideContext",
                "APIShared.UnitHudImageOverrideResolver",
                "APIShared.UnitHudImageOverrideDefinition",
                "APIShared.ApiShared",
                "APIShared.MarkedUnitSelectionSnapshot",
                "APIShared.MarkedUnitSelectionAPI",
                "APIShared.PlayerPerspectiveAPI",
                "APIShared.EnemyGateSearchKind",
                "APIShared.IEnemyGatePathPolicy",
                "APIShared.IEnemyGateRegionPairObserver",
                "APIShared.IEnemyGateAssassinObserver",
                "APIShared.IEnemyGateRoutePolicyProvider",
                "APIShared.IEnemyGateRoutePolicySnapshot",
                "APIShared.EnemyGatePathPolicyBridge",
                "APIShared.IEnemyBridgePathObserver",
                "APIShared.EnemyBridgeDiagnosticBridge", "APIShared.TemporaryGateRouteAcceptanceBridge", "APIShared.ITemporaryGateRouteAcceptanceObserver",
                // BepInEx discovers the plugin type; it is public but is not a consumer service.
                "APIShared.APISharedPlugin"
            };

            MethodInfo referenceLifeHelper = typeof(UnitAccess).GetMethod("IsReallyAlive",
                new[] { typeof(SHCDESE.Interop.GameUnit).MakeByRefType() });
            MethodInfo pointerLifeHelper = typeof(UnitAccess).GetMethod("IsReallyAlive",
                new[] { typeof(SHCDESE.Interop.GameUnit).MakePointerType() });
            Assert(referenceLifeHelper != null && referenceLifeHelper.IsStatic && referenceLifeHelper.ReturnType == typeof(bool) &&
                pointerLifeHelper != null && pointerLifeHelper.IsStatic && pointerLifeHelper.ReturnType == typeof(bool),
                "UnitAccess retains the required reference/pointer bool life helpers; additional overloads are allowed");
            Type[] exported = typeof(IApiShared).Assembly.GetExportedTypes();
            foreach (Type type in exported)
            {
                // Required existing contracts must survive; additive public API extensions are allowed.
                expected.Remove(type.FullName);
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (!IsAuditedGateNativeType(type, method, null))
                        AssertSafePublicType(method.ReturnType, $"{type.FullName}.{method.Name} return type");
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        // Explicit native bridges expose only their audited immediate views.
                        // All other public types and parameters retain the native-type prohibition.
                        if (type == typeof(UnitAccess) && method.Name == "TryGetById" &&
                            parameter.Name == "unit" && parameter.ParameterType.IsByRef &&
                            parameter.ParameterType.GetElementType().IsPointer &&
                            parameter.ParameterType.GetElementType().GetElementType() == typeof(SHCDESE.Interop.GameUnit))
                            continue;
                        if (type == typeof(UnitAccess) && method.Name == "IsReallyAlive" &&
                            method.ReturnType == typeof(bool) && method.GetParameters().Length == 1 &&
                            parameter.Name == "unit" &&
                            parameter.ParameterType == typeof(SHCDESE.Interop.GameUnit).MakePointerType())
                            continue;
                        if ((type == typeof(AssassinPathBuilder) &&
                                (method.Name == "Invoke" || method.Name == "BeginInvoke") ||
                             type == typeof(AssassinPathAPI) && (method.Name == "RunVanillaBuilder" ||
                                method.Name == "TryStageWeightedRoute" || method.Name == "TryGetCurrentWeightedRequest")) &&
                            parameter.Name == "context" && parameter.ParameterType == typeof(IntPtr))
                            continue;
                        if (!IsAuditedGateNativeType(type, method, parameter))
                            AssertSafePublicType(parameter.ParameterType, $"{type.FullName}.{method.Name} parameter {parameter.Name}");
                    }
                }
                if (!typeof(Delegate).IsAssignableFrom(type))
                    foreach (ConstructorInfo constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        foreach (ParameterInfo parameter in constructor.GetParameters())
                            AssertSafePublicType(parameter.ParameterType, $"{type.FullName} constructor parameter {parameter.Name}");
                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    AssertSafePublicType(property.PropertyType, $"{type.FullName}.{property.Name} property type");
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    AssertSafePublicType(field.FieldType, $"{type.FullName}.{field.Name} field type");
            }

            foreach (string missing in expected)
                Assert(false, $"expected exported API type is missing: {missing}");

            var expectedAcquisitionMethods = new HashSet<string>(StringComparer.Ordinal)
            {
                "TryGetGatehouseDistanceOrigin",
                "TryGetGatehouseTiming",
                "TryGetUnitHudPresentation",
                "TryGetAivBuildStep",
                "TryGetLobbyState",
                "TryGetMissionLifecycle",
                "TryGetPlayerDefeat",
                "TryGetBriefingGoldPresentation",
                "TryGetBuildingRepair"
            };
            foreach (MethodInfo method in typeof(IApiShared).GetMethods())
                expectedAcquisitionMethods.Remove(method.Name);
            foreach (string missing in expectedAcquisitionMethods)
                Assert(false, $"expected capability acquisition method is missing: IApiShared.{missing}");

            Assert(NativeCapabilityIds.GatehouseDistanceOrigin == "gatehouse-distance-origin",
                "distance-origin capability ID must remain stable");
            Assert(NativeCapabilityIds.GatehouseTiming == "gatehouse-timing",
                "gatehouse-timing capability ID must remain stable");
            Assert(NativeCapabilityIds.UnitHudPresentation == "unit-hud-presentation",
                "unit-HUD capability ID must remain stable");
            Assert(NativeCapabilityIds.AivBuildStep == "aiv-build-step",
                "AIV build-step capability ID must remain stable");
            Assert(NativeCapabilityIds.LobbyState == "lobby-state",
                "lobby-state capability ID must remain stable");
            Assert(NativeCapabilityIds.PlayerDefeat == "player-defeat",
                "player-defeat capability ID must remain stable");
            Assert(NativeCapabilityIds.BriefingGoldPresentation == "briefing-gold-presentation",
                "briefing-gold capability ID must remain stable");
            Assert(NativeCapabilityIds.BuildingRepair == "building-repair",
                "building-repair capability ID must remain stable");
        }
        // Explicitly audited advanced native API. No namespace-wide pointer exemption.
        private static bool IsAuditedGateNativeType(Type owner, MethodInfo method, ParameterInfo parameter)
        {
            Type actual=parameter==null?method.ReturnType:parameter.ParameterType;
            string name=parameter==null?"return":parameter.Name;
            Type hooks=typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks);
            Type scopes=typeof(APIShared.GateRouting.Native.GateNativeSearchScopes);
            var delegates=new Dictionary<Type,int> {
                {typeof(APIShared.GateRouting.Native.CapturerComparisonCallback),1},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.PathBuilderDelegate),3},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.AttackApproachDelegate),8},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.BuildingApproachDelegate),6},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.BuildingConsumerDelegate),3},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.CursorMoveDelegate),6},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.CandidateSearchDelegate),7},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.DirectTileSearchDelegate),6},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.PclReachabilityDelegate),5},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.CursorPclDecisionDelegate),6},
                {typeof(APIShared.GateRouting.Native.GateRoutingNativeHooks.AiTacticalTargetDelegate),2}
            };
            if(delegates.TryGetValue(owner,out int count) && parameter!=null && parameter.Position==0 &&
                (method.Name=="Invoke"||method.Name=="BeginInvoke"))
            {
                var invoke=owner.GetMethod("Invoke");
                Assert(invoke.GetParameters().Length==count,"audited gate delegate argument count");
                var abi=owner.GetCustomAttribute<System.Runtime.InteropServices.UnmanagedFunctionPointerAttribute>();
                var expected=owner==typeof(APIShared.GateRouting.Native.CapturerComparisonCallback)
                    ?System.Runtime.InteropServices.CallingConvention.Cdecl:System.Runtime.InteropServices.CallingConvention.Winapi;
                Assert(abi!=null&&abi.CallingConvention==expected,"audited gate delegate ABI");
                Assert(actual==typeof(IntPtr),"gate immediate pointer parameter");return true;
            }
            if(owner==hooks && ((method.Name=="InstallMovement"&&(name=="slots"||name=="tactical"))||
                (method.Name=="InstallDescent"&&(name=="activation"||name=="diagnosticActivation"))))
            {Assert(actual==typeof(IntPtr),"permanent gate activation/scope data pointer");return true;}
            if(owner==scopes)
            {
                string key=method.Name+"/"+name;
                var types=new Dictionary<string,Type>{
                    {"Allocate/return",typeof(IntPtr)}, {"Enter/return",typeof(byte).MakePointerType()},
                    {"Enter/slots",typeof(IntPtr)}, {"Enter/mask",typeof(IntPtr)}, {"Enter/previous",typeof(IntPtr).MakeByRefType()},
                    {"Leave/slot",typeof(byte).MakePointerType()}, {"Leave/previous",typeof(IntPtr)},
                    {"EnterTactical/return",typeof(byte).MakePointerType()}, {"EnterTactical/slots",typeof(IntPtr)},
                    {"EnterTactical/mask",typeof(IntPtr)}, {"LeaveTactical/slot",typeof(byte).MakePointerType()}
                };
                if(types.TryGetValue(key,out Type expected)) {Assert(actual==expected,"audited immediate scope signature: "+key);return true;}
            }
            return false;
        }

        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyOwnerBoundClient() => TestOwnerBoundClient();

        private static void TestOwnerBoundClient()
        {
            foreach (string invalid in new[] { null, "", "  " })
            {
                bool rejected = false;
                try { ApiShared.ForMod(invalid); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "owner-bound entry must reject empty GUIDs immediately");
            }
            var runtime = new ApiSharedRuntime();
            var client = new ModApiClient("Foreign.Author.MyMod", runtime);
            Assert(client.OwnerGuid == "Foreign.Author.MyMod" && client.State == NativeApiState.Pending,
                "arbitrary foreign GUID does not require Serps profiles");
            Assert(!client.TryGetGatehouseTiming(out _, out var pending) && pending.State == NativeCapabilityState.Pending,
                "owner-bound client preserves pending capability diagnostics");
            // Publish a managed fixture without installing game hooks or completing native initialization.
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(ApiSharedRuntime).GetField("lobbyState", privateInstance).SetValue(runtime, new LobbyStateService(null));
            typeof(ApiSharedRuntime).GetField("lobbyStateDiagnostic", privateInstance).SetValue(runtime,
                new NativeCapabilityDiagnostic(NativeCapabilityIds.LobbyState, NativeCapabilityState.Available, "", "managed fixture"));
            Assert(client.TryGetLobbyState(out var managed, out var managedDiagnostic) && managed != null &&
                managedDiagnostic.State == NativeCapabilityState.Available && client.State == NativeApiState.Pending,
                "available managed capability must be acquired before global readiness");
            int calls = 0;
            client.WhenReady(_ => throw new InvalidOperationException("test-early"));
            client.WhenReady(c => { Assert(ReferenceEquals(c, client), "callback retains owner-bound client"); calls++; });
            runtime.Initialize(ModuleBase, CreatePeImage(0x4000, true), "UNKNOWN", new FakeMemory(), null, null, false);
            Assert(calls == 1, "early callback failure must not block later consumers");
            client.WhenReady(_ => throw new InvalidOperationException("test-late"));
            client.WhenReady(_ => calls++);
            Assert(calls == 2, "late callbacks are synchronous and equally isolated");
            var failingLogger = new BepInEx.Logging.ManualLogSource("ReadinessLoggerFixture");
            failingLogger.LogEvent += (_, __) => throw new InvalidOperationException("test log listener");
            typeof(ApiSharedRuntime).GetField("log", privateInstance).SetValue(runtime, failingLogger);
            client.WhenReady(_ => throw new InvalidOperationException("test-late-with-failing-log"));
            client.WhenReady(_ => calls++);
            Assert(calls == 3, "failing log listeners must not break callback isolation");
            Assert(!client.TryGetGatehouseTiming(out _, out var unsupported) && unsupported.State == NativeCapabilityState.UnsupportedBuild,
                "global Ready does not imply native service support");
            Assert(typeof(IApiShared).Assembly.GetType("APIShared.UnitCommands.UnitCommandPathAPI") == null,
                "mod-specific command runtime is not shipped in APIShared");
            Assert(!typeof(IApiShared).Assembly.GetExportedTypes().Any(t => t.Namespace == "Shared"),
                "APIShared no longer exports historical Shared contracts");
        }
        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyReadinessAndIndependentCapabilities() => TestReadinessAndIndependentCapabilities();

        private static void TestReadinessAndIndependentCapabilities()
        {
            byte[] image = CreatePeImage(0x4000, true);
            var runtime = new ApiSharedRuntime();
            Assert(!runtime.TryGetGatehouseDistanceOrigin("owner", out _, out NativeCapabilityDiagnostic originPending) &&
                originPending.State == NativeCapabilityState.Pending, "pre-initialization origin query should be Pending");
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic pending) &&
                pending.State == NativeCapabilityState.Pending, "pre-initialization query should be Pending");
            int readyBefore = 0;
            runtime.WhenReady(_ => readyBefore++);
            var memory = new FakeMemory();
            runtime.Initialize(ModuleBase, image, "UNKNOWN", memory, null, null, false);
            Assert(runtime.State == NativeApiState.Ready && readyBefore == 1, "unknown build should still publish Ready");
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic gate) &&
                gate.State == NativeCapabilityState.UnsupportedBuild, "unknown build should disable only gatehouse");
            Assert(!runtime.TryGetGatehouseDistanceOrigin("owner", out _, out NativeCapabilityDiagnostic origin) &&
                origin.State == NativeCapabilityState.UnsupportedBuild, "unknown build should disable distance origin without mutation");
            Assert(memory.OperationCount == 0, "unknown build performs no native operation");
            int readyAfter = 0;
            runtime.WhenReady(_ => readyAfter++);
            Assert(readyAfter == 1, "post-initialization readiness callback should be synchronous");

            runtime = new ApiSharedRuntime();
            runtime.Initialize(0, ReadOnlySpan<byte>.Empty, string.Empty, new FakeMemory(), null, null, false);
            Assert(runtime.State == NativeApiState.Ready, "missing native module is a gate capability error, not a global failure");
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic missingHash) &&
                missingHash.State == NativeCapabilityState.UnsupportedBuild, "missing hash is unsupported for gatehouse");
            Assert(!runtime.TryGetGatehouseDistanceOrigin("owner", out _, out NativeCapabilityDiagnostic missingOriginHash) &&
                missingOriginHash.State == NativeCapabilityState.UnsupportedBuild, "missing hash is unsupported for distance origin");
        }
        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyFailedInitializationStepsStillCompleteReadiness() => TestFailedInitializationSteps();

        // Issue #5: a throwing LibraryLoaded step must not leave the API Pending or skip later steps.
        private static void TestFailedInitializationSteps()
        {
            byte[] image = CreatePeImage(0x4000, true);
            var runtime = new ApiSharedRuntime();
            int early = 0, later = 0, failedCallbacks = 0;
            runtime.WhenReady(_ => early++);
            runtime.RunNativeInitialization(null,
                new NativeInitializationStep("repair tooltip binding",
                    () => throw new InvalidOperationException("binding failed"), runtime.FaultBuildingRepair),
                new NativeInitializationStep("savegame settings handler",
                    () => throw new InvalidOperationException("handler already exists"),
                    _ => { failedCallbacks++; throw new InvalidOperationException("failure handler"); }),
                new NativeInitializationStep("later step", () => later++),
                new NativeInitializationStep("native API publication",
                    () => runtime.Initialize(ModuleBase, image, "UNKNOWN", new FakeMemory(), null, null, false)));
            Assert(later == 1 && failedCallbacks == 1, "a failed step skipped a later independent step");
            Assert(runtime.State == NativeApiState.Ready && early == 1,
                "a failed step before publication left WhenReady pending");
            Assert(!runtime.TryGetBuildingRepair("owner", out _, out NativeCapabilityDiagnostic repair) &&
                repair.State == NativeCapabilityState.Faulted && repair.Reason.Contains("repair tooltip binding") &&
                repair.Reason.Contains("binding failed"), "the failed step's capability did not report Faulted with its reason");
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic gate) &&
                gate.State == NativeCapabilityState.UnsupportedBuild, "a failed step changed an independent capability");
            runtime.RunNativeInitialization(null,
                new NativeInitializationStep("native API publication", () => throw new InvalidOperationException("again")));
            Assert(runtime.State == NativeApiState.Ready && early == 1, "a repeated run replaced the terminal state");

            runtime = new ApiSharedRuntime();
            NativeApiState observed = NativeApiState.Pending;
            runtime.WhenReady(api => observed = api.State);
            var failingLogger = new BepInEx.Logging.ManualLogSource("InitializationBoundaryFixture");
            failingLogger.LogEvent += (_, __) => throw new InvalidOperationException("test log listener");
            runtime.RunNativeInitialization(failingLogger,
                new NativeInitializationStep("native API publication", () => throw new InvalidOperationException("publication failed")));
            Assert(runtime.State == NativeApiState.Unavailable && observed == NativeApiState.Unavailable,
                "a throwing publication did not complete WhenReady with Unavailable");
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic faulted) &&
                faulted.State == NativeCapabilityState.Faulted && faulted.Reason.Contains("publication failed"),
                "native capabilities did not report the publication failure");
            Assert(!runtime.TryGetBuildingRepair("owner", out _, out NativeCapabilityDiagnostic unavailableRepair) &&
                unavailableRepair.State == NativeCapabilityState.Faulted, "on-demand capability ignored Unavailable");
            int late = 0;
            runtime.WhenReady(_ => late++);
            Assert(late == 1, "late callback after a failed initialization was not synchronous");

            runtime = new ApiSharedRuntime();
            runtime.RunNativeInitialization(null, new NativeInitializationStep("no publication", () => { }));
            Assert(runtime.State == NativeApiState.Unavailable, "initialization without publication stayed Pending");
        }
        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyLibraryLoadedStepsRunInsideTheErrorBoundary()
        {
            // Every initialization call of OnLibraryLoaded must sit in a step lambda; a direct call
            // would run outside RunNativeInitialization's error boundary again (issue #5).
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                var plugin = assembly.MainModule.GetType("APIShared.APISharedPlugin");
                var handler = plugin.Methods.Single(m => m.Name == "OnLibraryLoaded");
                // ldftn of a step delegate is not a call; only call and callvirt run code here.
                var calls = handler.Body.Instructions
                    .Where(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Call || i.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt)
                    .Select(i => (Mono.Cecil.MethodReference)i.Operand).ToArray();
                Assert(calls.Count(m => m.Name == "RunNativeInitialization") == 1,
                    "OnLibraryLoaded must run its steps through RunNativeInitialization");
                Assert(!calls.Any(m => m.Name == "Initialize" || m.Name == "InitializeNative" ||
                    m.Name == "Bind" || m.Name == "RegisterBinding" || m.Name == "ComputeInstalledHash"),
                    "an OnLibraryLoaded initialization step runs outside the error boundary");
            }
        }
        [TestMethod]
        [TestCategory("Contracts")]
        public void VerifyOwnership() => TestOwnership();

        private static void TestOwnership()
        {
            var registry = new NativeOwnershipRegistry();
            var first = new[] { new NativeInterval(100, 110) };
            Assert(registry.TryReserve("A", "cap", NativeReservationMode.Exclusive, first, out _), "first reservation");
            Assert(registry.TryReserve("A", "cap", NativeReservationMode.Exclusive, first, out _), "same reservation is idempotent");
            Assert(registry.TryReserve("B", "other", NativeReservationMode.Exclusive,
                new[] { new NativeInterval(110, 120) }, out _), "adjacent half-open intervals do not overlap");
            Assert(!registry.TryReserve("C", "third", NativeReservationMode.Exclusive,
                new[] { new NativeInterval(109, 111) }, out string conflict) && conflict == "A",
                "exclusive overlap identifies the first owner");
        }

    }
}

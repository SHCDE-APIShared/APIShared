using APIShared.ModSettings;
using APIShared.Buildings;
using SHCDESE.Interop;
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
        [TestCategory("NativeContracts")]
        public void VerifyPeValidation() => TestPeValidation();

        private static void TestPeValidation()
        {
            byte[] image = CreatePeImage(0x4000, true);
            NativePeImage pe = NativePeImage.Parse(image);
            Assert(pe.ImageSize == image.Length, "PE image size should parse");
            AssertThrowsState(NativeCapabilityState.ValidationFailed,
                () => pe.RequireExecutableRange(0x200, 1, "header"), "headers are not executable targets");

            byte[] nonExecutable = CreatePeImage(0x4000, false);
            GatehouseBuildTarget catalog = InstallTestGatehouse(nonExecutable);
            var runtime = InitializeRuntime(nonExecutable, catalog, SeedRuntimeMemory(nonExecutable, catalog));
            Assert(!runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic diagnostic) &&
                diagnostic.State == NativeCapabilityState.ValidationFailed, "gatehouse function must be executable");
            Assert(!runtime.TryGetGatehouseDistanceOrigin("owner", out _, out diagnostic) &&
                diagnostic.State == NativeCapabilityState.ValidationFailed, "distance origin also requires an executable gatehouse function");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyFixedCatalogValidation() => TestFixedCatalogValidation();

        private static void TestFixedCatalogValidation()
        {
            byte[] image = CreatePeImage(0x4000, true);
            GatehouseBuildTarget catalog = InstallTestGatehouse(image);
            FakeMemory memory = SeedRuntimeMemory(image, catalog);
            ApiSharedRuntime runtime = InitializeRuntime(image, catalog, memory);
            Assert(runtime.TryGetGatehouseTiming("owner", out _, out NativeCapabilityDiagnostic available) &&
                available.State == NativeCapabilityState.Available && available.Reason.Contains("function SHA-256"),
                "matching fixed catalog should validate with provenance");
            Assert(runtime.TryGetGatehouseDistanceOrigin("origin-owner", out _, out NativeCapabilityDiagnostic originAvailable) &&
                originAvailable.State == NativeCapabilityState.Available && originAvailable.Reason.Contains("function SHA-256"),
                "matching distance-origin catalog should validate with provenance");
            Assert(memory.WriteCount == 0,
                "initialization and capability acquisition must not activate either gatehouse gameplay change");

            FakeMemory preHookedMemory = SeedRuntimeMemory(image, catalog);
            preHookedMemory.SetByte(ModuleBase + catalog.HumanReopenDelayRva + 4, 0xFF);
            runtime = InitializeRuntime(image, catalog, preHookedMemory);
            Assert(runtime.TryGetGatehouseTiming("owner", out var coexistTiming, out _),
                "Fixes Farmer hook after the seven-byte human delay block must coexist before initialization");
            preHookedMemory.SetByte(ModuleBase + catalog.HumanDelayBlockRva + 7, 0xEE);
            Assert(runtime.TryGetGatehouseTiming("owner", out _, out _),
                "Fixes Farmer hook installed after initialization must remain independent");
            var invariants = (IReadOnlyList<NativeByteInvariant>)typeof(GatehouseCapabilityResolver)
                .GetMethod("CreateInstructionInvariants", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { ModuleBase, catalog });
            Assert(!invariants.Any(item => item.Address >= ModuleBase + catalog.HumanDelayBlockRva + 7 &&
                item.Address < ModuleBase + catalog.HumanDelayBlockRva + catalog.HumanDelayBlockBytes.Length),
                "timing invariants exclude the external Farmer block");
            preHookedMemory.SetByte(ModuleBase + catalog.HumanDelayBlockRva, 0x90);
            runtime = InitializeRuntime(image, catalog, preHookedMemory);
            AssertTimingValidationFailure(runtime,"changes inside our owned human block remain fail-closed");
            Assert(runtime.TryGetGatehouseDistanceOrigin("owner", out _, out _),
                "a timing-only live layout mismatch must not disable distance origin");

            byte[] wrongHashImage = (byte[])image.Clone();
            var wrongHashCatalog = CloneCatalog(catalog, functionHash: new string('0', 64));
            runtime = InitializeRuntime(wrongHashImage, wrongHashCatalog, SeedRuntimeMemory(wrongHashImage, wrongHashCatalog));
            AssertBothGateValidationFailures(runtime, "wrong function hash must fail both gatehouse capabilities");

            byte[] wrongOpcode = (byte[])image.Clone();
            wrongOpcode[DecisionRva] ^= 1;
            Copy(wrongOpcode, 0x1500, DecisionBytes); // A decoy must never be used as a fallback.
            GatehouseBuildTarget wrongOpcodeCatalog = CloneCatalog(catalog, functionHash: ApiSharedRuntime.ComputeSha256(
                new ReadOnlySpan<byte>(wrongOpcode, FunctionRva, FunctionSize)));
            runtime = InitializeRuntime(wrongOpcode, wrongOpcodeCatalog, SeedRuntimeMemory(wrongOpcode, wrongOpcodeCatalog));
            AssertTimingValidationFailure(runtime, "wrong timing opcode must fail without accepting a decoy");
            Assert(runtime.TryGetGatehouseDistanceOrigin("owner", out _, out _),
                "a timing-only opcode mismatch must not disable distance-origin capability");

            byte[] wrongImmediate = (byte[])image.Clone();
            WriteInt32(wrongImmediate, DecisionRva + 8, 201);
            GatehouseBuildTarget wrongImmediateCatalog = CloneCatalog(catalog, functionHash: ApiSharedRuntime.ComputeSha256(
                new ReadOnlySpan<byte>(wrongImmediate, FunctionRva, FunctionSize)));
            runtime = InitializeRuntime(wrongImmediate, wrongImmediateCatalog, SeedRuntimeMemory(wrongImmediate, wrongImmediateCatalog));
            AssertTimingValidationFailure(runtime, "wrong Vanilla immediate must fail timing");
            Assert(runtime.TryGetGatehouseDistanceOrigin("owner", out _, out _),
                "a timing immediate mismatch must not disable distance-origin capability");

            byte[] wrongDistance = (byte[])image.Clone();
            wrongDistance[DistanceRva] ^= 1;
            GatehouseBuildTarget wrongDistanceCatalog = CloneCatalog(catalog, functionHash: ApiSharedRuntime.ComputeSha256(
                new ReadOnlySpan<byte>(wrongDistance, FunctionRva, FunctionSize)));
            runtime = InitializeRuntime(wrongDistance, wrongDistanceCatalog, SeedRuntimeMemory(wrongDistance, wrongDistanceCatalog));
            AssertOriginValidationFailure(runtime, "wrong Vanilla distance block must fail distance origin");
            Assert(runtime.TryGetGatehouseTiming("owner", out _, out _),
                "a distance-origin mismatch must not disable gatehouse timing");

            GatehouseBuildTarget outside = CloneCatalog(catalog, aiCloseDistanceRva: FunctionRva - 4);
            runtime = InitializeRuntime(image, outside, SeedRuntimeMemory(image, catalog));
            AssertTimingValidationFailure(runtime, "catalogued immediate outside function must fail timing");
            Assert(runtime.TryGetGatehouseDistanceOrigin("owner", out _, out _),
                "an invalid timing address must not disable distance origin");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyCenteredDistanceSemantics() => TestCenteredDistanceSemantics();

        private static void TestCenteredDistanceSemantics()
        {
            GatehouseBuildTarget supported = GatehouseBuildTarget.Supported;
            Assert(supported.DistanceBlockRva == 0xB7B70 &&
                supported.DistanceBlockRva + supported.VanillaDistanceBlockBytes.Length == 0xB7BBB,
                "distance patch must occupy exactly the post-hook Vanilla arithmetic block");
            var originTarget = new GatehouseDistanceOriginTarget(
                ModuleBase + supported.DistanceBlockRva,
                supported.VanillaDistanceBlockBytes);
            var timingTarget = new GatehouseTimingTarget(
                ModuleBase + supported.AiCloseDistanceRva,
                ModuleBase + supported.AiReopenDelayRva,
                ModuleBase + supported.HumanCloseDistanceRva,
                ModuleBase + supported.HumanReopenDelayRva);
            Assert(originTarget.Intervals.Count == 1 &&
                originTarget.Intervals[0].Start == ModuleBase + 0xB7B70 &&
                originTarget.Intervals[0].End == ModuleBase + 0xB7BBB,
                "distance-origin capability must own exactly [0xB7B70, 0xB7BBB)");
            Assert(timingTarget.Intervals.Count == 4 &&
                timingTarget.Intervals[0].Start == ModuleBase + 0xB7BC3 && timingTarget.Intervals[0].End == ModuleBase + 0xB7BC7 &&
                timingTarget.Intervals[1].Start == ModuleBase + 0xB7BCA && timingTarget.Intervals[1].End == ModuleBase + 0xB7BCE &&
                timingTarget.Intervals[2].Start == ModuleBase + 0xB7BD3 && timingTarget.Intervals[2].End == ModuleBase + 0xB7BD7 &&
                timingTarget.Intervals[3].Start == ModuleBase + 0xB7C35 && timingTarget.Intervals[3].End == ModuleBase + 0xB7C39,
                "timing capability must own exactly the four immediate intervals");
            foreach (NativeInterval timingInterval in timingTarget.Intervals)
                Assert(!originTarget.Intervals[0].Overlaps(timingInterval),
                    "distance-origin and timing intervals must be disjoint");

        }

        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseGeometryAndConservativeRange()
        {
            Assert(BuildingGeometry.TryGetSquareFootprintCenter(408, 348, 7, out int x, out int y) &&
                x == 411 * 8 + 4 && y == 351 * 8 + 4, "the large square gate center is the center of its middle tile");
            Assert(BuildingGeometry.TryGetSquareFootprintCenter(10, 20, 2, out x, out y) &&
                x == 88 && y == 168, "the center of an even-width footprint lies on a tile boundary");
            Assert(!BuildingGeometry.TryGetSquareFootprintCenter(10, 20, 0, out _, out _) &&
                !BuildingGeometry.TryGetSquareFootprintCenter(int.MaxValue, 20, 2, out _, out _),
                "missing or unrepresentable square bounds cannot produce a midpoint");

            var gate = new GameBuilding
            {
                r_TilePositionXBegin = 100, r_TilePositionYBegin = 50, r_OccupyTileGridSize = 7,
                r_BuildingType = eStructs.STRUCT_GATE_MAIN
            };
            var candidate = new GameUnit { r_CurrentWorldPositionX = 1040, r_CurrentWorldPositionY = 400 };
            Assert(!GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in candidate,
                GatehouseDistanceOrigin.VanillaBuildingBegin, 400),
                "a candidate at 30 fields is still eligible for reachability at a configured 50 fields");
            Assert(GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in candidate,
                GatehouseDistanceOrigin.VanillaBuildingBegin, 200),
                "the same candidate is outside the Vanilla maximum");

            candidate.r_CurrentWorldPositionX = 1000;
            Assert(GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in candidate,
                GatehouseDistanceOrigin.VanillaBuildingBegin, 200),
                "equality is outside the strict native threshold from the corner");
            Assert(!GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in candidate,
                GatehouseDistanceOrigin.BuildingBoundsCenter, 200),
                "centering brings the same candidate inside range");
            // Derive the positions from editor tile centers, independently of the geometry helper.
            const int centerX = 103 * 8 + 4, centerY = 53 * 8 + 4;
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1), (-1, -1), (1, 1) })
            {
                foreach (int distance in new[] { 200, 199, 201 })
                {
                    candidate.r_CurrentWorldPositionX = (ushort)(centerX + dx * distance);
                    candidate.r_CurrentWorldPositionY = (ushort)(centerY + dy * distance);
                    Assert(GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in candidate,
                        GatehouseDistanceOrigin.BuildingBoundsCenter, 200) == (distance >= 200),
                        $"centered closing boundary differs at ({dx},{dy}), distance {distance}");
                }
            }
        }

        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyEffectiveGatehouseRangePublication()
        {
            byte[] image = CreatePeImage(0x4000, true);
            GatehouseBuildTarget catalog = InstallTestGatehouse(image);
            var runtime = InitializeRuntime(image, catalog, SeedRuntimeMemory(image, catalog));
            Assert(runtime.TryReadGatehouseClosingRange(out GatehouseDistanceOrigin initialOrigin, out int initialRange) &&
                initialOrigin == GatehouseDistanceOrigin.VanillaBuildingBegin && initialRange == 200,
                "both available services start at the published Vanilla origin and maximum");
            Assert(runtime.TryGetGatehouseTiming("timing", out IGatehouseTimingCapability timing, out _),
                "validated timing capability is available");
            Assert(runtime.TryGetGatehouseDistanceOrigin("origin", out IGatehouseDistanceOriginCapability origin, out _),
                "validated distance origin capability is available");

            Assert(timing.TryApply(new GatehouseTimingSettings(true, 2, 20, 15, 50), out _) &&
                runtime.TryReadGatehouseClosingRange(out GatehouseDistanceOrigin aiOrigin, out int aiRange) &&
                aiOrigin == GatehouseDistanceOrigin.VanillaBuildingBegin && aiRange == 400,
                "the larger AI range is published independently of origin");
            Assert(origin.TryApply(GatehouseDistanceOrigin.BuildingBoundsCenter, out _) &&
                timing.TryApply(new GatehouseTimingSettings(true, 2, 20, 30, 5), out _) &&
                runtime.TryReadGatehouseClosingRange(out GatehouseDistanceOrigin humanOrigin, out int humanRange) &&
                humanOrigin == GatehouseDistanceOrigin.BuildingBoundsCenter && humanRange == 240,
                "the larger human range is read from timing alongside the independently updated origin");
            Assert(timing.TryApply(new GatehouseTimingSettings(false, 0, 0, 0, 0), out _) &&
                runtime.TryReadGatehouseClosingRange(out GatehouseDistanceOrigin restoredOrigin, out int restoredRange) &&
                restoredOrigin == GatehouseDistanceOrigin.BuildingBoundsCenter && restoredRange == 200,
                "disabling custom timing restores Vanilla distances while leaving centering active");

            foreach (int failedSite in new[] { catalog.DistanceBlockRva, catalog.DecisionBlockRva })
            {
                FakeMemory unavailable = SeedRuntimeMemory(image, catalog);
                unavailable.SetByte(ModuleBase + failedSite, 0x90);
                var partiallyValidated = InitializeRuntime(image, catalog, unavailable);
                Assert(!partiallyValidated.TryReadGatehouseClosingRange(out _, out _),
                    "a partial native validation cannot provide a safe range for skipping");
            }

            var gate = new GameBuilding { r_BuildingType = eStructs.STRUCT_GATE_MAIN };
            var distant = new GameUnit { r_CurrentWorldPositionX = 1000, r_CurrentWorldPositionY = 1000 };
            Assert(!GatehouseClosingRules.IsDefinitelyOutsideRange(in gate, in distant),
                "the standalone public query cannot skip a candidate without initialized process state");
        }

        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseDistanceOriginTransaction() => TestGatehouseDistanceOriginTransaction();

        private static void TestGatehouseDistanceOriginTransaction()
        {
            var ownership = new NativeOwnershipRegistry();
            var mutationSync = new object();
            GatehousePermanentRuntimeState state = GatehousePermanentRuntimeState.CreateTestState();
            IGatehouseDistanceOriginCapability origin =
                CreateOriginService(state, ownership, mutationSync).Bind("BugfixesAndQoL_Serp");
            IGatehouseTimingCapability timing =
                CreateGateService(state, ownership, mutationSync).Bind("ExtraFeatures_Serp");

            Assert(!origin.TryApply((GatehouseDistanceOrigin)99, out NativeCapabilityDiagnostic invalid) &&
                invalid.State == NativeCapabilityState.ValidationFailed,
                "unknown distance-origin values fail before logical publication");
            Assert(origin.TryApply(GatehouseDistanceOrigin.BuildingBoundsCenter, out NativeCapabilityDiagnostic centered) &&
                centered.CapabilityId == NativeCapabilityIds.GatehouseDistanceOrigin &&
                state.Origin == GatehouseDistanceOrigin.BuildingBoundsCenter,
                "Bugfixes owner should apply the centered distance origin");
            state.ReadTiming(out int aiDistance, out int aiDelay, out int humanDistance, out int humanDelay);
            Assert(aiDistance == 200 && aiDelay == 1200 && humanDistance == 140 && humanDelay == 100,
                "distance-origin publication leaves all timing values Vanilla");
            Assert(origin.TryApply(GatehouseDistanceOrigin.BuildingBoundsCenter, out _) &&
                state.Origin == GatehouseDistanceOrigin.BuildingBoundsCenter,
                "identical distance-origin apply is idempotent");

            Assert(timing.TryApply(new GatehouseTimingSettings(true, 1, 5, 10, 15), out NativeCapabilityDiagnostic timingApplied) &&
                timingApplied.CapabilityId == NativeCapabilityIds.GatehouseTiming,
                "different owners can reserve the adjacent timing and origin intervals");
            Assert(state.Origin == GatehouseDistanceOrigin.BuildingBoundsCenter,
                "timing publication leaves the independently selected origin unchanged");

            Assert(origin.TryApply(GatehouseDistanceOrigin.VanillaBuildingBegin, out _),
                "distance-origin capability restores Vanilla on explicit request");
            Assert(state.Origin == GatehouseDistanceOrigin.VanillaBuildingBegin,
                "Vanilla distance-origin request changes only the logical gate");
            state.ReadTiming(out aiDistance, out aiDelay, out humanDistance, out humanDelay);
            Assert(aiDistance == 120 && aiDelay == 200 && humanDistance == 80 && humanDelay == 40,
                "restoring the origin does not change customized timing values");

            var conflictRegistry = new NativeOwnershipRegistry();
            GatehouseDistanceOriginService originService =
                CreateOriginService(GatehousePermanentRuntimeState.CreateTestState(), conflictRegistry, new object());
            Assert(originService.Bind("A").TryApply(GatehouseDistanceOrigin.BuildingBoundsCenter, out _),
                "first distance-origin owner applies");
            Assert(!originService.Bind("B").TryApply(GatehouseDistanceOrigin.VanillaBuildingBegin, out NativeCapabilityDiagnostic conflict) &&
                conflict.State == NativeCapabilityState.Conflict && conflict.ConflictOwnerGuid == "A",
                "second distance-origin owner receives conflict diagnostics");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseTransactionAndRounding() => TestGatehouseTransactionAndRounding();

        private static void TestGatehouseTransactionAndRounding()
        {
            GatehousePermanentRuntimeState state = GatehousePermanentRuntimeState.CreateTestState();
            IGatehouseTimingCapability capability = CreateGateService(
                state, new NativeOwnershipRegistry(), new object()).Bind("owner");
            Assert(!capability.TryApply(new GatehouseTimingSettings(true, double.NaN, 0, 5, 5), out NativeCapabilityDiagnostic invalid) &&
                invalid.State == NativeCapabilityState.ValidationFailed, "non-finite gatehouse input should fail");
            AssertThrows<ArgumentOutOfRangeException>(
                () => GatehouseTimingService.ConvertNativeUInt16(8192, 8, "value"), "native UInt16 overflow should fail");

            var rounded = new GatehouseTimingSettings(true, 0.0125, 0.0125, 5.0625, 5.0625);
            Assert(capability.TryApply(rounded, out NativeCapabilityDiagnostic applied) &&
                applied.Reason.Contains("41units") && applied.Reason.Contains("1ticks"),
                "AwayFromZero values and verified native units should be diagnosed");
            state.ReadTiming(out int aiDistance, out int aiDelay, out int humanDistance, out int humanDelay);
            Assert(aiDistance == 41 && aiDelay == 1 && humanDistance == 41 && humanDelay == 1,
                "all four rounded values should be atomically published");
            Assert(capability.TryApply(rounded, out _), "identical apply should be idempotent");
            Assert(capability.TryApply(new GatehouseTimingSettings(false, double.NaN, double.NaN, double.NaN, double.NaN), out _),
                "disabled settings restore Vanilla without validating unused values");
            state.ReadTiming(out aiDistance, out aiDelay, out humanDistance, out humanDelay);
            Assert(aiDistance == 200 && aiDelay == 1200 && humanDistance == 140 && humanDelay == 100,
                "disabled settings atomically publish all Vanilla values");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseRollbackAndPageCleanup() => TestGatehouseRollbackAndPageCleanup();

        private static void TestGatehouseRollbackAndPageCleanup()
        {
            var registry = new NativeOwnershipRegistry();
            GatehousePermanentRuntimeState state = GatehousePermanentRuntimeState.CreateTestState();
            GatehouseTimingService service = CreateGateService(state, registry, new object());
            Assert(service.Bind("A").TryApply(new GatehouseTimingSettings(true, 1, 5, 10, 15), out _), "first owner applies");
            Assert(!service.Bind("B").TryApply(new GatehouseTimingSettings(true, 2, 6, 11, 16), out NativeCapabilityDiagnostic conflict) &&
                conflict.State == NativeCapabilityState.Conflict && conflict.ConflictOwnerGuid == "A",
                "second owner receives conflict diagnostics");
            state.ReadTiming(out int aiDistance, out int aiDelay, out int humanDistance, out int humanDelay);
            Assert(aiDistance == 120 && aiDelay == 200 && humanDistance == 80 && humanDelay == 40,
                "conflicting owner cannot alter the atomically published timing snapshot");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseConcurrentPublication() => TestGatehouseConcurrentPublication();

        private static void TestGatehouseConcurrentPublication()
        {
            GatehousePermanentRuntimeState state = GatehousePermanentRuntimeState.CreateTestState();
            Exception writerFailure = null;
            var writer = new Thread(() =>
            {
                try
                {
                    for (int index = 0; index < 10000; index++)
                    {
                        if ((index & 1) == 0)
                            state.PublishTiming(11, 22, 33, 44);
                        else
                            state.PublishTiming(101, 202, 303, 404);
                    }
                }
                catch (Exception ex)
                {
                    writerFailure = ex;
                }
            });
            writer.Start();
            for (int index = 0; index < 10000; index++)
            {
                state.ReadTiming(out int aiDistance, out int aiDelay, out int humanDistance, out int humanDelay);
                bool vanilla = aiDistance == 200 && aiDelay == 1200 && humanDistance == 140 && humanDelay == 100;
                bool first = aiDistance == 11 && aiDelay == 22 && humanDistance == 33 && humanDelay == 44;
                bool second = aiDistance == 101 && aiDelay == 202 && humanDistance == 303 && humanDelay == 404;
                if (!vanilla && !first && !second)
                {
                    Assert(false, "parallel gatehouse readers must observe one complete immutable timing snapshot");
                    break;
                }
            }
            writer.Join();
            Assert(writerFailure == null, "parallel gatehouse publication must not fail");
        }
        [TestMethod]
        [TestCategory("NativeContracts")]
        public void VerifyGatehouseAssemblerContracts() => TestGatehouseAssemblerContracts();

        private static void TestGatehouseAssemblerContracts()
        {
            GateBridgeAutomationTests.Run(Assert, MapPeImage(File.ReadAllBytes(Path.Combine(
                GetGameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"))));
            const ulong moduleBase = 0x00007FF940F90000;
            const ulong stubAddress = 0x00007FF8D1144000;
            var decisionAssembler = new Assembler(64);
            GatehousePermanentRuntimeState.GenerateDecision(
                decisionAssembler,
                new[] { Instruction.Create(Code.Nopd) },
                0x000001A000002000,
                moduleBase,
                moduleBase + GatehousePermanentRuntimeState.DecisionReturnRva,
                moduleBase + GatehousePermanentRuntimeState.ClosePathRva);
            byte[] decisionStub = AssembleAndDecode(decisionAssembler, stubAddress + 0x1000);
            Assert(decisionStub.Length > 0,
                "gatehouse timing generator assembles with one label per emitted instruction");
        }

    }
}

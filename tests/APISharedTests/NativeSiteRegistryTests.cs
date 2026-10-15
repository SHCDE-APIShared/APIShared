using System;
using System.Collections.Generic;
using System.Linq;
using APIShared;
using APIShared.NativeSites;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MsAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace APISharedTests
{
    // Runtime-side checks of the public native-site registry: APIShared's own site catalog, that every
    // installer claims its sites before publishing hooks, gatehouse capability visibility and the
    // allocation-free facade queries on the .NET Framework host. The overlap matrix, atomicity and
    // concurrency are covered by Core.Tests/NativeSiteRegistryTests on the same implementation.
    public partial class RuntimeTests
    {
        private const string SiteTestOwner = "APISharedTests.NativeSites";

        [TestMethod]
        [TestCategory("NativeSites")]
        public void VerifyApiSharedSiteCatalog()
        {
            var registry = new NativeOwnershipRegistry();
            int feature = 0;
            foreach (KeyValuePair<string, NativeSiteSpan[]> group in ApiSharedNativeSites.All())
            {
                // Distinct owners: any overlap between two APIShared features would be refused here.
                NativeSiteRegistrationResult result = registry.RegisterSites("Feature" + feature++, group.Key, group.Value);
                MsAssert.AreEqual(NativeSiteRegistrationStatus.Accepted, result.Status, group.Key + ": " + result.Reason);
                MsAssert.AreEqual(0, result.SharedWith.Count, group.Key + " shares no byte with another APIShared feature");
            }

            var expectedWriters = new (int Rva, int Length, string Group)[]
            {
                (0x2960, 15, ApiSharedNativeSites.Audio), (0x2990, 15, ApiSharedNativeSites.Audio), (0x1A41E0, 14, ApiSharedNativeSites.Audio),
                (0xB7B70, 75, ApiSharedNativeSites.GatehousePermanent), (0xB7BBB, 37, ApiSharedNativeSites.GatehousePermanent),
                (0xB7A4E, 17, ApiSharedNativeSites.GatehouseAutomation), (0xB79E2, 18, ApiSharedNativeSites.GatehouseAutomation),
                (0x51790, 17, ApiSharedNativeSites.AivBuildStep),
                (0xCEB10, 10, ApiSharedNativeSites.MarketPrices), (0xCEB90, 10, ApiSharedNativeSites.MarketPrices),
                (0xD9C40, 10, ApiSharedNativeSites.AssassinPath), (0xE19F9, 23, ApiSharedNativeSites.AssassinPath),
                (0x16CD70, 10, ApiSharedNativeSites.AssassinAttack),
                (0x1232E0, 6, ApiSharedNativeSites.GateMovement), (0xDC3C0, 6, ApiSharedNativeSites.GateMovement),
                (0x113BC0, 10, ApiSharedNativeSites.GateMovement), (0x8F251, 29, ApiSharedNativeSites.GateMovement),
                (0x8F1BF, 14, ApiSharedNativeSites.GateMovement), (0xDB857, 17, ApiSharedNativeSites.GateMovement),
                (0xE2705, 20, ApiSharedNativeSites.GateComparisons), (0xE3024, 20, ApiSharedNativeSites.GateComparisons),
                (0x185D55, 20, ApiSharedNativeSites.GateDescent)
            };
            foreach (var expected in expectedWriters)
            {
                MsAssert.IsTrue(registry.TryGetWriter(expected.Rva, out NativeSiteRegistration writer), $"writer at 0x{expected.Rva:X}");
                MsAssert.AreEqual((expected.Rva, expected.Length, expected.Group, NativeSiteMode.Exclusive),
                    (writer.Rva, writer.Length, writer.Name, writer.Mode));
            }
            MsAssert.AreEqual(91, registry.Registrations.Count(r => r.Name == ApiSharedNativeSites.Audio && r.Mode == NativeSiteMode.Observe),
                "every decoded audio catalog CALL site is observed");
            foreach (var observed in new[] { (0xB7C32, 7), (0xC5300, 15), (0xCEB1A, 21), (0xCEB9A, 21), (0x122800, 24), (0x185934, 4), (0xD51A0, 17) })
                MsAssert.AreEqual(1, registry.Registrations.Count(r => r.Rva == observed.Item1 && r.Length == observed.Item2 && r.Mode == NativeSiteMode.Observe),
                    $"observed span 0x{observed.Item1:X}+{observed.Item2}");
            MsAssert.AreEqual(0, registry.Registrations.Count(r => r.Mode == NativeSiteMode.ChainableEntry || r.Mode == NativeSiteMode.CallOnly),
                "APIShared's backends refuse occupied entries, so none of its own sites is chainable");
        }

        [TestMethod]
        [TestCategory("NativeSites")]
        public void VerifyEveryInstallerClaimsItsSitesBeforePublishing()
        {
            // (type, method, parameter count or -1, predicate of the first call that publishes or uses the site)
            var installers = new (string Type, string Method, int Parameters, Func<Mono.Cecil.MethodReference, bool> Publishes)[]
            {
                ("APIShared.Audio.SoundRuntime", "Install", -1, m => m.Name == "CreateDetour"),
                ("APIShared.GatehouseCapabilityResolver", "Resolve", -1, m => m.Name == ".ctor" && m.DeclaringType.Name == "GatehousePermanentRuntimeState"),
                ("APIShared.GatehouseAutomationNativeState", "TryPublish", -1, m => m.Name == "Commit"),
                ("APIShared.AivBuildStepService", "TryCreate", 8, m => m.Name == "Commit"),
                ("APIShared.Economy.MarketPriceNativeRuntime", "EnsureInstalled", -1, m => m.Name == "Commit"),
                ("APIShared.AssassinPathAPI", "EnsureInstalled", -1, m => m.Name == "Commit"),
                ("APIShared.AssassinAttackControlAPI", "EnsureInstalled", -1, m => m.Name == "Commit"),
                ("APIShared.AssassinAttackControlAPI", "RegisterGuard", 3, m => m.Name == "GetDelegateForFunctionPointer"),
                ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallMovement", -1, m => m.Name == "InstallMovementHooks"),
                ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallComparisons", -1, m => m.Name == "InstallComparisonHooks"),
                ("APIShared.GateRouting.Native.GateRoutingNativeHooks", "InstallDescent", -1, m => m.Name == "Commit"),
                ("APIShared.BuildingRepairService", "TryCreate", 7, m => m.Name == ".ctor" && m.DeclaringType.Name == "Hook")
            };
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                foreach (var installer in installers)
                {
                    string name = installer.Type + "." + installer.Method;
                    Mono.Cecil.TypeDefinition type = assembly.MainModule.GetType(installer.Type);
                    MsAssert.IsNotNull(type, name);
                    Mono.Cecil.MethodDefinition method = type.Methods.Single(m => m.Name == installer.Method &&
                        (installer.Parameters < 0 || m.Parameters.Count == installer.Parameters));
                    List<Mono.Cecil.MethodReference> calls = CallsInOrder(method, new HashSet<Mono.Cecil.MethodDefinition>());
                    int claim = calls.FindIndex(m => m.DeclaringType.Name == "ApiSharedNativeSites" && m.Name == "Claim");
                    int publish = calls.FindIndex(m => installer.Publishes(m));
                    MsAssert.IsTrue(claim >= 0, name + " claims its native sites");
                    MsAssert.IsTrue(publish >= 0, name + " still contains its publication call");
                    MsAssert.IsTrue(claim < publish, name + " claims before it installs or uses the site");
                    // The release may sit in a rollback helper of the same type (audio's RollbackUnpublished).
                    MsAssert.IsTrue(calls.Any(m => m.DeclaringType.Name == "ApiSharedNativeSites" && m.Name == "Release"),
                        name + " releases its claim when the installation is not published");
                }
            }
        }

        // The method's calls in IL order, with each call into a method of the same type followed in place
        // by that method's own calls (recursively, skipping methods already on the path). An installer can
        // move its claim, publication or release into a helper without leaving this check, as audio's
        // Install did with CreateHooks (#20).
        private static List<Mono.Cecil.MethodReference> CallsInOrder(Mono.Cecil.MethodDefinition method,
            HashSet<Mono.Cecil.MethodDefinition> path)
        {
            path.Add(method);
            var calls = new List<Mono.Cecil.MethodReference>();
            foreach (Mono.Cecil.Cil.Instruction instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code != Mono.Cecil.Cil.Code.Call && instruction.OpCode.Code != Mono.Cecil.Cil.Code.Callvirt &&
                    instruction.OpCode.Code != Mono.Cecil.Cil.Code.Newobj) continue;
                var callee = (Mono.Cecil.MethodReference)instruction.Operand;
                calls.Add(callee);
                if (callee.DeclaringType.FullName != method.DeclaringType.FullName) continue;
                Mono.Cecil.MethodDefinition body = callee.Resolve();
                if (body != null && body.HasBody && !path.Contains(body)) calls.AddRange(CallsInOrder(body, path));
            }
            path.Remove(method);
            return calls;
        }

        [TestMethod]
        [TestCategory("NativeSites")]
        public void VerifyGatehouseReservationsBecomeVisibleSites()
        {
            byte[] image = CreatePeImage(0x4000, true);
            GatehouseBuildTarget catalog = InstallTestGatehouse(image);
            ApiSharedRuntime runtime = InitializeRuntime(image, catalog, SeedRuntimeMemory(image, catalog));
            MsAssert.IsTrue(runtime.NativeOwnership.IsBound, "native initialization binds the image");
            MsAssert.AreEqual(0, runtime.NativeOwnership.Registrations.Count,
                "the isolated harness installs no permanent hooks, so APIShared claims no site");
            MsAssert.IsTrue(runtime.TryGetGatehouseTiming("Consumer.Timing", out IGatehouseTimingCapability timing, out _));
            MsAssert.IsTrue(timing.TryApply(new GatehouseTimingSettings(true, 1, 5, 10, 15), out _));
            var reserved = runtime.NativeOwnership.Registrations.Where(r => r.CapabilityId == NativeCapabilityIds.GatehouseTiming).ToArray();
            CollectionAssert.AreEquivalent(
                new[] { catalog.AiCloseDistanceRva, catalog.AiReopenDelayRva, catalog.HumanCloseDistanceRva, catalog.HumanReopenDelayRva },
                reserved.Select(r => r.Rva).ToArray(), "absolute reservations are published as RVAs of the bound image");
            MsAssert.IsTrue(reserved.All(r => r.OwnerGuid == "Consumer.Timing" && r.Length == 4 && r.Mode == NativeSiteMode.Exclusive));
            MsAssert.IsFalse(runtime.TryGetGatehouseTiming("Consumer.Other", out IGatehouseTimingCapability other, out _) &&
                other.TryApply(new GatehouseTimingSettings(true, 1, 5, 10, 15), out _), "the capability layer still refuses a second owner");
        }

        [TestMethod]
        [TestCategory("NativeSites")]
        public void VerifyPublicFacadeRoundTripAndAllocationFreeQueries()
        {
            const int Base = 0x7F000000;
            try
            {
                NativeSiteRegistrationResult accepted = NativeSiteRegistry.Register(SiteTestOwner, "probe", new[]
                {
                    new NativeSiteSpan(Base, 14, NativeSiteMode.Exclusive),
                    new NativeSiteSpan(Base + 0x40, 31, NativeSiteMode.Observe)
                });
                MsAssert.AreEqual(NativeSiteRegistrationStatus.Accepted, accepted.Status, accepted.Reason);
                NativeSiteRegistrationResult refused = NativeSiteRegistry.Register(SiteTestOwner + ".Other", "probe", Base + 4, 1, NativeSiteMode.Observe);
                MsAssert.AreEqual(NativeSiteRegistrationStatus.Conflict, refused.Status);
                MsAssert.AreSame(accepted.Registrations[0], refused.Conflict);
                MsAssert.IsTrue(NativeSiteRegistry.Refusals.Contains(refused));
                MsAssert.AreEqual(NativeSiteOverlapRule.SameEntryChain,
                    NativeSiteRegistry.GetOverlapRule(NativeSiteMode.ChainableEntry, NativeSiteMode.ChainableEntry));

                var buffer = new NativeSiteRegistration[2];
                long sink = 0;
                Action query = () =>
                {
                    sink += NativeSiteRegistry.Query(Base + 3, 4, buffer);
                    sink += NativeSiteRegistry.Query(Base + 0x50, 1, null);
                    if (NativeSiteRegistry.TryGetWriter(Base + 5, out NativeSiteRegistration writer)) sink += writer.Length;
                    sink += NativeSiteRegistry.Registrations.Count;
                };
                for (int warm = 0; warm < 2000; warm++) query();
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int index = 0; index < 20000; index++) query();
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                TestContext?.WriteLine("NATIVE_SITES_ALLOC facade queries: bytes={0}, calls=80000, sink={1}", allocated, sink);
                MsAssert.AreEqual(0L, allocated, "facade queries allocate nothing after warm-up");
            }
            finally
            {
                MsAssert.AreEqual(2, NativeSiteRegistry.Unregister(SiteTestOwner, "probe"), "test registrations are removed");
            }
        }

        public TestContext TestContext { get; set; }
    }
}

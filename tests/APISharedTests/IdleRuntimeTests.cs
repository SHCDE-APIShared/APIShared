using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class IdleRuntimeTests
    {
        [TestMethod]
        public void PlayerDefeatDoesNotCaptureBeforeDemandAndStartsWithCurrentBaseline()
        {
            int sessions = 0, captures = 0, defeats = 0;
            long? session = 91;
            var observations = new PlayerDefeatObservation[8];
            observations[0] = new PlayerDefeatObservation(true, WinLossState.Loss, false, 0, 0);
            var service = PlayerDefeatService.TestCreate(() => { sessions++; return session; },
                () => { captures++; return observations; });
            for (int tick = 0; tick < 1000; tick++) service.TestTick(tick);
            Assert.AreEqual(0, sessions);
            Assert.AreEqual(0, captures);
            Assert.IsFalse(service.Bind("owner").TryRegisterObserver("invalid", null, null, out _));
            service.TestTick(1000);
            Assert.AreEqual(0, captures, "Invalid registration must not activate observation.");
            Assert.IsTrue(service.Bind("owner").TryRegisterObserver("defeats", null, _ => defeats++, out _));
            service.TestTick(1001);
            Assert.AreEqual(1, captures);
            Assert.AreEqual(0, defeats, "A loss before first demand is baseline state.");
            observations[0] = new PlayerDefeatObservation(true, WinLossState.None, false, 0, 0);
            service.TestTick(1002);
            observations[0] = new PlayerDefeatObservation(true, WinLossState.Loss, false, 0, 0);
            service.TestTick(1003);
            Assert.AreEqual(1, defeats);
            session = null;
            service.TestTick(1004);
            session = 92;
            service.TestTick(1005);
            Assert.AreEqual(1, defeats, "New sessions do not replay loaded loss state.");
        }

        [TestMethod]
        public void LobbyCapturesOnlyOnFirstDemandAndReplaysToLaterObservers()
        {
            int captures = 0;
            var snapshot = new LobbyStateSnapshot(null, null, false, 0, false, "", "");
            var service = new LobbyStateService(null, () => { captures++; return snapshot; });
            var render = (Action)typeof(LobbyStateService).GetMethod("OnBeforeRender",
                BindingFlags.Instance | BindingFlags.NonPublic).CreateDelegate(typeof(Action), service);
            for (int frame = 0; frame < 1000; frame++) render();
            Assert.AreEqual(0, captures, "Idle render callbacks must not even read Unity frame state.");
            Assert.IsFalse(service.Bind("owner").TryRegisterObserver("invalid", null, out _));
            render();
            Assert.AreEqual(0, captures);
            var delivered = new List<LobbyStateSnapshot>();
            Assert.IsTrue(service.Bind("owner").TryRegisterObserver("first", delivered.Add, out _));
            Assert.AreEqual(1, captures);
            Assert.AreSame(snapshot, delivered.Single());
            Assert.IsTrue(service.Bind("owner").TryRegisterObserver("second", delivered.Add, out _));
            Assert.AreEqual(1, captures);
            Assert.AreEqual(2, delivered.Count);
            Assert.AreSame(snapshot, delivered[1]);
        }

        [TestMethod]
        public void AivIdleDispatchPreservesOneOriginalCallResultAndException()
        {
            var service = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            var context = new AivBuildStepContext(0x1234, 2, 3, 0, 1);
            int originals = 0;
            Assert.AreEqual(77, service.DispatchForTest(context, () => { originals++; return 77; }));
            Assert.AreEqual(1, originals);
            var error = new InvalidOperationException("original");
            Assert.AreSame(error, Assert.ThrowsExactly<InvalidOperationException>(() =>
                service.DispatchForTest(context, () => { originals++; throw error; })));
            Assert.AreEqual(2, originals);
        }

        private sealed class DecliningObserver : IAivBuildStepObserver
        {
            internal Action Begin;
            internal int Calls;
            public IAivBuildStepInvocation TryBegin(AivBuildStepContext context)
            { Calls++; Begin?.Invoke(); return null; }
        }

        [TestMethod]
        public void AivRegistrationDuringBeginJoinsNextCallAndDeclinedCallsStillRunVanilla()
        {
            var service = new AivBuildStepService(ApiSharedRuntime.SupportedHash, null);
            var first = new DecliningObserver();
            var second = new DecliningObserver();
            first.Begin = () => service.Bind("second").TryRegisterObserver("id", second, out _);
            service.Bind("first").TryRegisterObserver("id", first, out _);
            var context = new AivBuildStepContext(1, 2, 3, 0, 1);
            int originals = 0;
            Func<int> original = () => ++originals;
            Assert.AreEqual(1, service.DispatchForTest(context, original));
            Assert.AreEqual(0, second.Calls);
            Assert.AreEqual(2, service.DispatchForTest(context, original));
            Assert.AreEqual(2, first.Calls);
            Assert.AreEqual(1, second.Calls);
        }

        [TestMethod]
        public void CompiledIdleEntrypointsCannotAllocatePayloadsOrCapturedDelegates()
        {
            // Verify actual compiler output: a captured lambda can allocate a display
            // class before an early return even when the source appears guarded.
            using (var module = ModuleDefinition.ReadModule(typeof(ApiShared).Assembly.Location))
            {
                var targets = new[] {
                    ("APIShared.AivBuildStepService", "ExecuteBuildStep"),
                    ("APIShared.PlayerDefeatService", "OnTick"),
                    ("APIShared.LobbyStateService", "OnBeforeRender"),
                    ("APIShared.UnitHudPresentationService", "OnBeforeRender"),
                    ("APIShared.UnitHudPresentationService", "HandleVanillaClick"),
                    ("APIShared.MarkedUnitSelectionAPI", "OnTroopSelection"),
                    ("APIShared.Commands.GameActionEvents", "Dispatch"),
                    ("APIShared.Economy.MarketPriceEvents", "Dispatch"),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchView"),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchGui"),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchHud")
                };
                foreach (var target in targets)
                {
                    var method = module.GetTypes().Single(t => t.FullName == target.Item1).Methods.Single(m => m.Name == target.Item2);
                    Assert.IsFalse(method.Body.Instructions.Any(i => i.OpCode == OpCodes.Newobj ||
                        i.OpCode == OpCodes.Newarr || i.OpCode == OpCodes.Box ||
                        i.OpCode == OpCodes.Ldftn || i.OpCode == OpCodes.Ldvirtftn),
                        target.Item1 + "." + target.Item2 + " must reach its idle branch without allocation.");
                }
            }
        }
    }
}

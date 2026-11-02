using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Reflection;
using APIShared;
using APIShared.Buildings;
using APIShared.GameModes;
using APIShared.Simulation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class EnemyProximityPoliciesTests
    {
        private static EnemyProximityRegistration Register(EnemyProximityRegistry registry, string owner, string id,
            EnemyProximityPhase phase, Func<EnemyProximityContext, EnemyProximityDecision> callback, int order = 0)
        {
            Assert.IsTrue(registry.TryRegister(owner, id, phase, callback, out var registration, out var error, order), error);
            registration.SetEnabled(true);
            return registration;
        }

        [TestMethod]
        public void OverridesAlwaysPrecedeLimitsAndSmallerExistingValuesSurvive()
        {
            foreach (int existing in new[] { 0, 1, 2, 3, 15, 30, 100 })
            foreach (bool registerLimitFirst in new[] { false, true })
            {
                var registry = new EnemyProximityRegistry();
                Func<EnemyProximityContext, EnemyProximityDecision> limit = _ => new EnemyProximityDecision(civilianRadius: 3);
                Func<EnemyProximityContext, EnemyProximityDecision> replace = _ => new EnemyProximityDecision(existing, 22);
                if (registerLimitFirst) Register(registry, "z", "peace", EnemyProximityPhase.Limit, limit, -100);
                Register(registry, "a", "settings", EnemyProximityPhase.Override, replace, 100);
                if (!registerLimitFirst) Register(registry, "z", "peace", EnemyProximityPhase.Limit, limit, -100);
                EnemyProximityDecision result = registry.Evaluate(1, 100, 100, 15, 1);
                Assert.AreEqual((int?)Math.Min(3, existing), result.CivilianRadius);
                Assert.AreEqual((int?)22, result.MilitaryRadius);
                Assert.IsFalse(result.Block);
            }
            var standalone = new EnemyProximityRegistry();
            Register(standalone, "unrelated.mod", "cap", EnemyProximityPhase.Limit,
                _ => new EnemyProximityDecision(civilianRadius: 3));
            foreach (int original in new[] { 0, 1, 2, 3, 15, 30, 250, 46340 })
            {
                var result = standalone.Evaluate(1, 100, 100, original, 0);
                Assert.AreEqual(original <= 3 ? null : (int?)3, result.CivilianRadius);
                Assert.IsNull(result.MilitaryRadius);
            }
        }

        [TestMethod]
        public void OrderingIsStableVetoIsStickyAndBadParticipantsCommitNothing()
        {
            var registry = new EnemyProximityRegistry(); var calls = new List<string>();
            Register(registry, "z", "last", EnemyProximityPhase.Override, _ => { calls.Add("z"); return new EnemyProximityDecision(9); });
            Register(registry, "a", "b", EnemyProximityPhase.Override, _ => { calls.Add("ab"); return EnemyProximityDecision.Blocked; });
            Register(registry, "a", "a", EnemyProximityPhase.Override, _ => { calls.Add("aa"); return new EnemyProximityDecision(20); });
            Register(registry, "earlier", "order", EnemyProximityPhase.Override,
                _ => { calls.Add("first"); return EnemyProximityDecision.None; }, -1);
            var throwing = Register(registry, "broken", "throw", EnemyProximityPhase.Limit,
                _ => throw new InvalidOperationException("test boundary failure"));
            var invalid = Register(registry, "broken", "invalid", EnemyProximityPhase.Limit,
                _ => new EnemyProximityDecision(0, 46341));
            Register(registry, "limit", "three", EnemyProximityPhase.Limit, context => {
                Assert.AreEqual(9, context.CivilianRadius);
                Assert.AreEqual(30, context.OriginalRadius); Assert.AreEqual(7, context.Unknown);
                return new EnemyProximityDecision(civilianRadius: 3);
            });
            var result = registry.Evaluate(1, 100, 100, 30, 7);
            CollectionAssert.AreEqual(new[] { "first", "aa", "ab", "z" }, calls);
            Assert.IsTrue(result.Block); Assert.AreEqual((int?)3, result.CivilianRadius);
            Assert.IsNull(result.MilitaryRadius); Assert.AreEqual(1, throwing.CallbackErrorCount); Assert.AreEqual(1, invalid.CallbackErrorCount);

            var atomic = new EnemyProximityRegistry();
            Register(atomic, "bad", "veto", EnemyProximityPhase.Override, _ => new EnemyProximityDecision(-1, block: true));
            Assert.IsFalse(atomic.Evaluate(1, 0, 0, 30, 0).Block, "Invalid category discards its entire contribution.");
        }

        [TestMethod]
        public void IdentitySurvivesLogicalDisableAndRecursiveQuestionsRemainOriginal()
        {
            var registry = new EnemyProximityRegistry(); int calls = 0;
            Assert.IsTrue(registry.TryRegister("owner", "one", EnemyProximityPhase.Override, _ => {
                calls++;
                var inner = registry.Evaluate(1, 0, 0, 15, 0);
                Assert.IsNull(inner.CivilianRadius); Assert.IsNull(inner.MilitaryRadius); Assert.IsFalse(inner.Block);
                return new EnemyProximityDecision(4);
            }, out var registration, out _));
            Assert.IsTrue(registry.HasRegistration("owner", "one")); Assert.IsFalse(registry.HasDemand);
            Assert.IsFalse(registry.TryRegister("owner", "one", EnemyProximityPhase.Limit,
                _ => default, out _, out _));
            registry.Evaluate(1, 0, 0, 15, 0); Assert.AreEqual(0, calls);
            registration.SetEnabled(true);
            Assert.AreEqual((int?)4, registry.Evaluate(1, 0, 0, 15, 0).CivilianRadius);
            Assert.AreEqual(1, calls);
            registration.SetEnabled(false); Assert.IsTrue(registry.HasRegistration("owner", "one"));
            Assert.IsFalse(registry.HasDemand); Assert.IsFalse(registration.IsEnabled);
            registry.Evaluate(1, 0, 0, 15, 0); Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void UnusualRawRadiusKeepsNativeArithmeticButIndependentVetoStillWorks()
        {
            var registry = new EnemyProximityRegistry();
            Register(registry, "cap", "civilian", EnemyProximityPhase.Limit, _ => new EnemyProximityDecision(3));
            foreach (int original in new[] { -1, int.MinValue, 46341, int.MaxValue })
            {
                var result = registry.Evaluate(1, 0, 0, original, 0);
                Assert.IsNull(result.CivilianRadius); Assert.IsNull(result.MilitaryRadius); Assert.IsFalse(result.Block);
            }
            Register(registry, "defense", "delay", EnemyProximityPhase.Override, _ => EnemyProximityDecision.Blocked);
            Assert.IsTrue(registry.Evaluate(1, 0, 0, -1, 0).Block);
            Assert.IsFalse(registry.Evaluate(0, 0, 0, 15, 0).Block, "Invalid player is outside the broker contract.");
        }

        [TestMethod]
        public void ActualEventAdapterRetainsExistingSkipsAndVetoWorksWithoutNativeQuery()
        {
            var queryField = typeof(EnemyProximityPolicies).GetField("query", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(queryField);
            object previousQuery = queryField.GetValue(null);
            var registrations = new List<EnemyProximityRegistration>();
            string owner = "pipeline.fixture." + Guid.NewGuid().ToString("N");
            try
            {
                queryField.SetValue(null, null);
                int calls = 0;
                Func<EnemyProximityContext, EnemyProximityDecision> proposal = _ => {
                    calls++; return new EnemyProximityDecision(civilianRadius: 3);
                };
                var cap = Register(EnemyProximityPolicies.Registry, owner, "cap", EnemyProximityPhase.Limit, proposal);
                registrations.Add(cap);
                var alreadySkipped = new BuildingAllowRepairInProximityEventArgs(EventHookPhase.Pre, 1, 100, 100, 30, 0) {
                    SkipOriginalFunction = true, ReturnValue = 123
                };
                EnemyProximityPolicies.OnQuery(alreadySkipped);
                Assert.AreEqual(123L, alreadySkipped.ReturnValue); Assert.IsTrue(alreadySkipped.SkipOriginalFunction);
                Assert.AreEqual(0, calls, "The broker must not inspect or undo another participant's skip.");

                var noBackend = new BuildingAllowRepairInProximityEventArgs(EventHookPhase.Pre, 1, 100, 100, 30, 0) { ReturnValue = 456 };
                EnemyProximityPolicies.OnQuery(noBackend);
                Assert.IsFalse(noBackend.SkipOriginalFunction); Assert.AreEqual(456L, noBackend.ReturnValue);
                Assert.AreEqual(1, calls);

                var unchanged = new BuildingAllowRepairInProximityEventArgs(EventHookPhase.Pre, 1, 100, 100, 3, 0) { ReturnValue = 789 };
                EnemyProximityPolicies.OnQuery(unchanged);
                Assert.IsFalse(unchanged.SkipOriginalFunction); Assert.AreEqual(789L, unchanged.ReturnValue);

                var veto = Register(EnemyProximityPolicies.Registry, owner, "delay", EnemyProximityPhase.Override,
                    _ => EnemyProximityDecision.Blocked);
                registrations.Add(veto);
                foreach (int rawRadius in new[] { 30, -1, 46341 })
                {
                    var blocked = new BuildingAllowRepairInProximityEventArgs(EventHookPhase.Pre, 1, 100, 100, rawRadius, 0);
                    EnemyProximityPolicies.OnQuery(blocked);
                    Assert.IsTrue(blocked.SkipOriginalFunction); Assert.AreEqual(1L, blocked.ReturnValue);
                }
                var post = new BuildingAllowRepairInProximityEventArgs(EventHookPhase.Post, 1, 100, 100, 30, 0) { ReturnValue = 42 };
                EnemyProximityPolicies.OnQuery(post);
                Assert.IsFalse(post.SkipOriginalFunction); Assert.AreEqual(42L, post.ReturnValue);
            }
            finally
            {
                foreach (var registration in registrations) registration.SetEnabled(false);
                queryField.SetValue(null, previousQuery);
                GC.KeepAlive(previousQuery);
            }
        }

        [TestMethod]
        public void NativePredicateRetainsOriginalFiltersCategoriesAndAbi()
        {
            byte[] file = File.ReadAllBytes(Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll"));
            using (var hash = SHA256.Create())
                Assert.AreEqual(ApiSharedRuntime.SupportedHash, BitConverter.ToString(hash.ComputeHash(file)).Replace("-", ""));
            byte[] image = MapImage(file);
            Assert.AreEqual(PeaceTimeState.FlagRva, PeaceTimeState.ValidateAnchor(image));
            image[PeaceTimeState.FlagAnchorRva] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => PeaceTimeState.ValidateAnchor(image));
            image[PeaceTimeState.FlagAnchorRva] ^= 1;
            EnemyProximityNativeTests.Run(image, (condition, message) => Assert.IsTrue(condition, message));
        }

        private static byte[] MapImage(byte[] file)
        {
            int pe = BitConverter.ToInt32(file, 0x3c), count = BitConverter.ToUInt16(file, pe + 6);
            int optional = pe + 24, table = optional + BitConverter.ToUInt16(file, pe + 20);
            var image = new byte[BitConverter.ToInt32(file, optional + 56)];
            Buffer.BlockCopy(file, 0, image, 0, Math.Min(BitConverter.ToInt32(file, optional + 60), file.Length));
            for (int i = 0; i < count; i++)
            {
                int h = table + i * 40, size = BitConverter.ToInt32(file, h + 16), raw = BitConverter.ToInt32(file, h + 20);
                if (size > 0) Buffer.BlockCopy(file, raw, image, BitConverter.ToInt32(file, h + 12), Math.Min(size, file.Length - raw));
            }
            return image;
        }

        [TestMethod]
        public void PeaceFlagRequiresReadyMissionAndRejectsLoadReplacementAndReadFailure()
        {
            var state = new MissionLifecycleState(error => Assert.Fail(error.ToString()));
            var seed = new MissionContext(0, MissionStartKind.NewGame, default(GameModeSnapshot), null, "peace fixture", false);
            byte flag = 1;
            var reader = new PeaceTimeReader(() => state.Current, () => state.NativeClock, () => flag);
            Assert.IsFalse(reader.TryRead(out _));
            long id = state.Begin(seed);
            state.BeginNativeLoad(id); Assert.IsFalse(reader.TryRead(out _));
            state.CompleteNativeLoad(id, true); Assert.IsFalse(reader.TryRead(out _), "Native-loaded is not yet a fully started mission.");
            state.Ready(id, state.Context);
            Assert.IsTrue(reader.TryRead(out bool active)); Assert.IsTrue(active);
            // Native expiry or restored saves change the same flag. No tick/duration heuristic exists.
            flag = 0; Assert.IsTrue(reader.TryRead(out active)); Assert.IsFalse(active);
            flag = 1; Assert.IsTrue(reader.TryRead(out active)); Assert.IsTrue(active);
            var failing = new PeaceTimeReader(() => state.Current, () => state.NativeClock, () => throw new InvalidOperationException());
            Assert.IsFalse(failing.TryRead(out _));
            var replacing = new PeaceTimeReader(() => state.Current, () => state.NativeClock, () => {
                state.BeginNativeLoad(id); state.CompleteNativeLoad(id, true); return 1;
            });
            Assert.IsFalse(replacing.TryRead(out _));
            state.BeginNativeLoad(id); state.CompleteNativeLoad(id, false);
            Assert.IsFalse(reader.TryRead(out _));
            state.End(MissionEndReason.Unloaded); Assert.IsFalse(reader.TryRead(out _));
        }
    }
}

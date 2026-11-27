using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class SoundSourceEventsTests
    {
        public TestContext TestContext { get; set; }
        private static SoundSourceArgs Kill(int sound = 240) => new SoundSourceArgs(sound, 5, 6, false, SoundSituation.MeleeKill,
            SituationEvidence.NativeAudited, "MeleeKill.Source01", 12, 3456u, 25, 34, 7890u);
        private static SoundSourceEvents.Registration Entry(string owner, string id, SoundSourceHandler pre, SoundRegistration handle = null, int order = 0)
            => new SoundSourceEvents.Registration(owner, id, pre, order, handle ?? new SoundRegistration());

        [TestMethod]
        public void ArgsCarryAttributionAndBoundReplacement()
        {
            var args = Kill();
            Assert.AreEqual(240, args.OriginalSoundId); Assert.AreEqual(240, args.SoundId);
            Assert.AreEqual(12, args.UnitId); Assert.AreEqual(3456u, args.UnitGlobalId); Assert.AreEqual(25, args.UnitType);
            Assert.AreEqual(34, args.TargetUnitId); Assert.AreEqual(7890u, args.TargetGlobalId);
            Assert.AreEqual(SoundPlaybackKind.Spatial, args.Kind); Assert.IsFalse(args.SituationConfirmed);
            Assert.IsFalse(args.TryReplaceSound(-1)); Assert.IsFalse(args.TryReplaceSound(SoundSourceArgs.MaxEffectId + 1)); Assert.AreEqual(240, args.SoundId);
            Assert.IsTrue(args.TryReplaceSound(118)); Assert.AreEqual(118, args.SoundId); Assert.AreEqual(240, args.OriginalSoundId);
            var soldier = new SoundSourceArgs(180, 1, 2, true, SoundSituation.ArabianIdleSwordDraw, SituationEvidence.InGameConfirmed, "Unit75.Source04", 9, 1u, 75, null, null);
            Assert.IsNull(soldier.TargetUnitId); Assert.IsNull(soldier.TargetGlobalId); Assert.IsTrue(soldier.SituationConfirmed);
            Assert.AreEqual(SoundPlaybackKind.SpatialUnattenuated, soldier.Kind);
        }

        [TestMethod]
        public void OrderedCallbacksLastReplacementWinsAndFailuresRollBack()
        {
            var order = new List<string>(); long failures = SoundSourceEvents.CallbackFailures;
            var disabled = new SoundRegistration(); disabled.SetEnabled(false);
            var snapshot = new[] {
                Entry("a", "first", (ref SoundSourceArgs a) => { order.Add("first"); a.TryReplaceSound(118); }),
                Entry("a", "throws", (ref SoundSourceArgs a) => { order.Add("throws"); a.TryReplaceSound(999); throw new InvalidOperationException("fixture"); }),
                Entry("b", "off", (ref SoundSourceArgs a) => order.Add("off"), disabled),
                Entry("c", "last", (ref SoundSourceArgs a) => { order.Add("last:" + a.SoundId); a.TryReplaceSound(153); }) };
            var args = Kill();
            SoundSourceEvents.Dispatch(ref args, snapshot);
            CollectionAssert.AreEqual(new[] { "first", "throws", "last:118" }, order);
            Assert.AreEqual(153, args.SoundId); Assert.AreEqual(failures + 1, SoundSourceEvents.CallbackFailures);
        }

        [TestMethod]
        public void NestedDispatchIsNotPublishedAgain()
        {
            int inner = 0;
            SoundSourceEvents.Registration[] snapshot = null;
            snapshot = new[] { Entry("n", "outer", (ref SoundSourceArgs a) => {
                inner++; var nested = Kill(241); SoundSourceEvents.Dispatch(ref nested, snapshot); a.TryReplaceSound(nested.SoundId);
            }) };
            var args = Kill();
            SoundSourceEvents.Dispatch(ref args, snapshot);
            Assert.AreEqual(1, inner); Assert.AreEqual(241, args.SoundId);
        }

        [TestMethod]
        public void PublicRegistrationValidatesOrdersAndOwnsDemand()
        {
            string owner = "source." + Guid.NewGuid();
            int before = SoundSourceEvents.Demand.ActiveCount;
            SoundRegistration first = null, second = null;
            try
            {
                Assert.IsFalse(SoundSourceEvents.TryRegister("", "x", (ref SoundSourceArgs a) => { }, out _, out _));
                Assert.IsFalse(SoundSourceEvents.TryRegister(owner, "x", null, out _, out _));
                var order = new List<string>();
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "z", (ref SoundSourceArgs a) => order.Add("z"), out first, out string reason));
                Assert.AreEqual(string.Empty, reason);
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "a", (ref SoundSourceArgs a) => order.Add("a"), out second, out _, order: 1));
                Assert.IsFalse(SoundSourceEvents.TryRegister(owner, "z", (ref SoundSourceArgs a) => { }, out _, out reason));
                Assert.AreEqual(before + 2, SoundSourceEvents.Demand.ActiveCount);
                var args = Kill(); SoundSourceEvents.Dispatch(ref args);
                CollectionAssert.AreEqual(new[] { "z", "a" }, order);
                first.SetEnabled(false); Assert.AreEqual(before + 1, SoundSourceEvents.Demand.ActiveCount);
                order.Clear(); SoundSourceEvents.Dispatch(ref args); CollectionAssert.AreEqual(new[] { "a" }, order);
            }
            finally { first?.SetEnabled(false); second?.SetEnabled(false); }
            Assert.AreEqual(before, SoundSourceEvents.Demand.ActiveCount);
        }

        [TestMethod]
        public void NativeDemandBytesFollowEveryOwnedTransition()
        {
            var status = typeof(SoundRuntime).GetField("playbackStatus", BindingFlags.Static | BindingFlags.NonPublic); var old = status.GetValue(null);
            bool wasReady = SoundContextPolicy.Active; SoundRegistration source = null, playback = null;
            try
            {
                Assert.AreEqual(0, SoundEvents.Demand.ActiveCount); Assert.AreEqual(0, SoundSourceEvents.Demand.ActiveCount);
                SoundContextPolicy.SetReady(true);
                Assert.AreEqual(0, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue);
                string owner = "bytes." + Guid.NewGuid();
                Assert.IsTrue(SoundSourceEvents.TryRegister(owner, "source", (ref SoundSourceArgs a) => { }, out source, out _));
                Assert.AreEqual(1, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue, "source events never need the emitter");
                status.SetValue(null, "Ready");
                Assert.IsTrue(SoundEvents.TryRegister(owner, "playback", a => { }, null, out playback, out _));
                Assert.AreEqual(1, SoundNativeDemand.WrapperValue); Assert.AreEqual(1, SoundNativeDemand.EmitterValue);
                SoundContextPolicy.SetReady(false);
                Assert.AreEqual(0, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue, "inactive context closes both guards");
                SoundContextPolicy.SetReady(true);
                source.SetEnabled(false);
                Assert.AreEqual(1, SoundNativeDemand.WrapperValue); Assert.AreEqual(1, SoundNativeDemand.EmitterValue);
                playback.SetEnabled(false);
                Assert.AreEqual(0, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue);
                // Concurrent transitions: every enabled handle sees a raised byte; the final state is exact.
                var handles = new[] { source, playback };
                Parallel.For(0, 2, index => {
                    for (int i = 0; i < 2000; i++) {
                        handles[index].SetEnabled(true);
                        if (SoundNativeDemand.WrapperValue == 0) throw new InvalidOperationException("False zero for an enabled handle.");
                        handles[index].SetEnabled(false);
                    }
                });
                Assert.AreEqual(0, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue);
                source.SetEnabled(true);
                Assert.AreEqual(1, SoundNativeDemand.WrapperValue); Assert.AreEqual(0, SoundNativeDemand.EmitterValue);
            }
            finally
            {
                source?.SetEnabled(false); playback?.SetEnabled(false); status.SetValue(null, old); SoundContextPolicy.SetReady(wasReady);
            }
            Assert.AreEqual(0, SoundNativeDemand.WrapperValue);
        }

        [TestMethod]
        public void WarmDispatchAllocatesNothing()
        {
            foreach (int count in new[] { 1, 16 })
            {
                var snapshot = new SoundSourceEvents.Registration[count];
                for (int i = 0; i < count; i++) snapshot[i] = Entry("bench", i.ToString(), Replace);
                const int iterations = 20000;
                for (int i = 0; i < 2000; i++) { var warm = Kill(); SoundSourceEvents.Dispatch(ref warm, snapshot); }
                var times = new long[9]; long bytes = 0;
                for (int sample = 0; sample < times.Length; sample++)
                {
                    long startBytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < iterations; i++) { var args = Kill(); SoundSourceEvents.Dispatch(ref args, snapshot); }
                    times[sample] = Stopwatch.GetTimestamp() - start; bytes = GC.GetAllocatedBytesForCurrentThread() - startBytes;
                    Assert.AreEqual(0L, bytes, "Source dispatch allocates after warm-up.");
                }
                Array.Sort(times);
                TestContext.WriteLine("SOURCE_DISPATCH_BENCH registrations={0},bytesPerCall=0,nsPerCallMin={1:F1},nsPerCallMedian={2:F1}", count,
                    times[0] * (1e9 / Stopwatch.Frequency) / iterations, times[times.Length / 2] * (1e9 / Stopwatch.Frequency) / iterations);
            }
        }
        private static void Replace(ref SoundSourceArgs args) { if (args.SoundId == 240) args.TryReplaceSound(118); }

        [TestMethod]
        public void ManagedDormantPathsAllocateNothingBeforeTheOriginal()
        {
            // IL of the production callbacks: no allocation before the first original call, and the
            // first ThreadStatic access follows the demand check.
            foreach (string name in new[] { "OnWrapper", "OnEmit" })
                using (var definition = new DynamicMethodDefinition(typeof(SoundRuntime).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)))
                {
                    bool demand = false, original = false;
                    foreach (var instruction in definition.Definition.Body.Instructions)
                    {
                        if (instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box)
                            Assert.Fail(name + " allocates before its dormant original: " + instruction);
                        if (instruction.Operand is Mono.Cecil.MethodReference method && method.Name == "get_HasObservers") demand = true;
                        if (instruction.Operand is Mono.Cecil.FieldReference field && (field.Name == "scope" || field.Name == "scopeConsumed"))
                            Assert.IsTrue(demand, name + " reads ThreadStatic scope before its demand check.");
                        if (instruction.Operand is Mono.Cecil.MethodReference invoke && invoke.Name == "Invoke" && invoke.DeclaringType.Name.Contains("Wrapper") ||
                            instruction.Operand is Mono.Cecil.MethodReference emit && emit.Name == "Invoke" && emit.DeclaringType.Name.Contains("Emit"))
                        { original = true; break; }
                    }
                    Assert.IsTrue(demand && original, name + " dormant shape");
                }
            using (var definition = new DynamicMethodDefinition(typeof(SoundSourceEvents).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(SoundSourceArgs).MakeByRefType(), typeof(SoundSourceEvents.Registration[]), typeof(int) }, null)))
            {
                var handlers = new HashSet<Instruction>();
                foreach (var handler in definition.Definition.Body.ExceptionHandlers)
                    if (handler.HandlerType == ExceptionHandlerType.Catch)
                        for (var instruction = handler.HandlerStart; instruction != null && instruction != handler.HandlerEnd; instruction = instruction.Next) handlers.Add(instruction);
                foreach (var instruction in definition.Definition.Body.Instructions)
                    if (!handlers.Contains(instruction) && (instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box))
                        Assert.Fail("Source dispatch allocates outside failure handling: " + instruction);
            }
        }
    }
}

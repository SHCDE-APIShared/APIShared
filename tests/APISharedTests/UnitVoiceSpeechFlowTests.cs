using System;
using System.Reflection;
using System.Threading.Tasks;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class UnitVoiceSpeechFlowTests
    {
        [TestMethod]
        public void ActualDormantAcceptedLoadGuardAllocatesNoFrameOrExecutionContext()
        {
            var flags = BindingFlags.Static | BindingFlags.NonPublic;
            var status = typeof(UnitVoiceSpeechPipeline).GetField("status", flags);
            var original = typeof(UnitVoiceSpeechPipeline).GetField("originalStart", flags);
            object oldStatus = status.GetValue(null), oldOriginal = original.GetValue(null); int calls = 0;
            UnitVoiceSpeechPipeline.LoadStart recorder = (manager, channel, folder, name, units, pause) => calls++;
            try
            {
                Assert.IsFalse(UnitVoiceSpeechFlow.HasAmbientRequest);
                status.SetValue(null, "Ready"); original.SetValue(null, recorder);
                for (int i = 0; i < 2000; i++) UnitVoiceSpeechPipeline.Start(null, 1, "troops", "voice.wav", true, false);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 10000; i++) UnitVoiceSpeechPipeline.Start(null, 1, "troops", "voice.wav", true, false);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0L, bytes); Assert.AreEqual(12000, calls); Assert.IsFalse(UnitVoiceSpeechFlow.HasAmbientRequest);
            }
            finally { status.SetValue(null, oldStatus); original.SetValue(null, oldOriginal); }
        }
        [TestMethod]
        public async Task DistinctAcceptedRequestsKeepTheirGainAcrossDelayedOutOfOrderAwait()
        {
            var manager = new object(); var a = new TaskCompletionSource<bool>(); var b = new TaskCompletionSource<bool>();
            Task<float> first, second;
            using (UnitVoiceSpeechFlow.BeginRequest(manager, 1, 0.25f))
            using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, true)) first = After(manager, 1, a.Task);
            using (UnitVoiceSpeechFlow.BeginRequest(manager, 1, 2f))
            using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, true)) second = After(manager, 1, b.Task);
            Assert.AreEqual(1f, UnitVoiceSpeechFlow.Gain(manager, 1));
            b.SetResult(true); Assert.AreEqual(2f, await second);
            a.SetResult(true); Assert.AreEqual(0.25f, await first);
        }

        [TestMethod]
        public async Task NestedNormalSpeechCannotInheritAnAlreadyClaimedUnitRequest()
        {
            var manager = new object(); var done = new TaskCompletionSource<bool>(); Task<float> normal;
            using (var request = UnitVoiceSpeechFlow.BeginRequest(manager, 1, 0.25f))
            using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, true))
            {
                Assert.IsTrue(UnitVoiceSpeechFlow.HasAmbientRequest);
                Assert.IsTrue(request.Request.WasAccepted); Assert.AreEqual(true, request.Request.AcceptedUnits);
                using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, false)) normal = After(manager, 1, done.Task);
                Assert.AreEqual(0.25f, UnitVoiceSpeechFlow.Gain(manager, 1));
            }
            done.SetResult(true); Assert.AreEqual(1f, await normal);
        }

        [TestMethod]
        public void MismatchedManagerOrChannelCannotClaimOrReadAnotherRequestsGain()
        {
            var manager = new object(); var other = new object();
            using (var request = UnitVoiceSpeechFlow.BeginRequest(manager, 1, 0f))
            {
                using (UnitVoiceSpeechFlow.BeginAccepted(other, 1, true)) Assert.AreEqual(1f, UnitVoiceSpeechFlow.Gain(other, 1));
                using (UnitVoiceSpeechFlow.BeginAccepted(manager, 2, true)) Assert.AreEqual(1f, UnitVoiceSpeechFlow.Gain(manager, 2));
                Assert.IsFalse(request.Request.WasAccepted);
                using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, false))
                {
                    Assert.AreEqual(0f, UnitVoiceSpeechFlow.Gain(manager, 1));
                    Assert.AreEqual(1f, UnitVoiceSpeechFlow.Gain(other, 1));
                    Assert.AreEqual(false, request.Request.AcceptedUnits);
                }
            }
        }

        [TestMethod]
        public void ScopeExceptionRestoresPreviousRequestAndFrame()
        {
            var manager = new object();
            using (UnitVoiceSpeechFlow.BeginRequest(manager, 2, 0.5f))
            using (UnitVoiceSpeechFlow.BeginAccepted(manager, 2, true))
            {
                try
                {
                    using (UnitVoiceSpeechFlow.BeginRequest(manager, 1, 3f))
                    using (UnitVoiceSpeechFlow.BeginAccepted(manager, 1, true)) throw new InvalidOperationException();
                }
                catch (InvalidOperationException) { }
                Assert.AreEqual(0.5f, UnitVoiceSpeechFlow.Gain(manager, 2));
            }
            Assert.AreEqual(1f, UnitVoiceSpeechFlow.Gain(manager, 2));
            Assert.IsTrue(UnitVoiceSpeechFlow.VerifyAsyncFlow());
        }
        private static async Task<float> After(object manager, int channel, Task gate)
        { await gate.ConfigureAwait(false); return UnitVoiceSpeechFlow.Gain(manager, channel); }
    }
}

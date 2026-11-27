using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Audio;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class UnitVoiceResourceTests
    {
        private static UnitVoicePcmData Samples() => new UnitVoicePcmData(2, 1, 44100, new[] { 0.25f, -0.25f });

        [TestMethod]
        public void PreparationIsDeferredAndPcmDoesNotExposeMutableSamples()
        {
            var store = new UnitVoiceResourceStore(); var queue = new Queue<Action>(); int decoded = 0, complete = 0;
            Assert.IsTrue(store.TryPrepare("third.party", "voice", "Voices/a.wav", false, 1f,
                () => { decoded++; return Samples(); }, queue.Enqueue, handle => { complete++; Assert.IsTrue(handle.IsReady); }, out var value, out var reason), reason);
            Assert.AreEqual(UnitVoiceResourceState.Pending, value.State); Assert.AreEqual(0, decoded);
            queue.Dequeue()();
            Assert.AreEqual(1, decoded); Assert.AreEqual(1, complete); Assert.AreEqual(UnitVoiceResourceState.Ready, value.State);
            Assert.AreEqual(8L, value.DecodedByteCount); Assert.AreEqual(2, value.SamplesPerChannel); Assert.AreEqual(44100, value.Frequency);
            var first = value.ReadSamples().CopySamples(); first[0] = 0;
            Assert.AreEqual(0.25f, value.ReadSamples().CopySamples()[0]);
            Assert.IsFalse(store.TryPrepare("third.party", "voice", "Voices/a.wav", false, 1f, Samples, queue.Enqueue, null, out _, out _));
        }

        [TestMethod]
        public void MapBoundaryInvalidatesPendingAndReadyHandlesWithoutRetargetingThem()
        {
            var store = new UnitVoiceResourceStore(); var queue = new Queue<Action>(); int decoded = 0;
            Func<UnitVoicePcmData> decode = () => { decoded++; return Samples(); };
            Assert.IsTrue(store.TryPrepare("owner", "map", "voice.wav", true, 1f, decode, queue.Enqueue, null, out var old, out _));
            store.InvalidateMapResources();
            Assert.AreEqual(UnitVoiceResourceState.Invalidated, old.State); Assert.IsFalse(old.IsReady);
            Assert.IsTrue(store.TryPrepare("owner", "map", "voice.wav", true, 1f, decode, queue.Enqueue, null, out var current, out _));
            Assert.AreNotSame(old, current);
            queue.Dequeue()(); Assert.AreEqual(0, decoded); Assert.IsFalse(old.IsReady);
            queue.Dequeue()(); Assert.AreEqual(1, decoded); Assert.IsTrue(current.IsReady);
            Assert.AreEqual(0L, old.DecodedByteCount);
            store.InvalidateMapResources(); Assert.AreEqual(UnitVoiceResourceState.Invalidated, current.State); Assert.AreEqual(0L, current.DecodedByteCount);
        }

        [TestMethod]
        public void MapChangeDuringDecodeCannotPublishOldSamplesAndModResourcesRemainReady()
        {
            var store = new UnitVoiceResourceStore();
            Assert.IsTrue(store.TryPrepare("owner", "mod", "voice.ogg", false, 1f, Samples, action => action(), null, out var mod, out _));
            Assert.IsTrue(store.TryPrepare("owner", "map", "voice.wav", true, 1f,
                () => { store.InvalidateMapResources(); return Samples(); }, action => action(), null, out var map, out _));
            Assert.IsFalse(map.IsReady); Assert.AreEqual(UnitVoiceResourceState.Invalidated, map.State); Assert.IsTrue(mod.IsReady);
        }

        [TestMethod]
        public void AMapChangingBetweenAdmissionAndRegistrationCannotRetargetPreparation()
        {
            var store = new UnitVoiceResourceStore(); int observed = store.Generation;
            store.InvalidateMapResources();
            Assert.IsFalse(store.TryPrepare("owner", "map", "a.wav", true, 1f,
                () => throw new Exception("must not decode"), action => action(), null, out _, out _, observed));
        }

        [TestMethod]
        public void FailedDecodeAndCompletionAreIsolated()
        {
            var store = new UnitVoiceResourceStore();
            Assert.IsTrue(store.TryPrepare("owner", "bad", "a.wav", false, 1f,
                () => throw new InvalidDataException("broken"), action => action(), handle => throw new Exception("callback"), out var failed, out _));
            Assert.AreEqual(UnitVoiceResourceState.Failed, failed.State); Assert.IsFalse(failed.IsReady); Assert.AreEqual("broken", failed.Diagnostic);
            Assert.AreEqual(1L, store.CallbackFailures);
            Assert.IsTrue(store.TryPrepare("other.owner", "bad", "a.wav", false, 1f, Samples, action => action(), null, out var unaffected, out _));
            Assert.IsTrue(unaffected.IsReady);
        }

        // A failed preparation must not block its owner-ID: a missing or broken file can be fixed and prepared again.
        [TestMethod]
        public void AFailedRegistrationCanBePreparedAgainButAPendingOrReadyOneCannot()
        {
            var store = new UnitVoiceResourceStore();
            Assert.IsTrue(store.TryPrepare("owner", "voice", "a.wav", false, 1f,
                () => throw new InvalidDataException("broken"), action => action(), null, out var failed, out _));
            Assert.AreEqual(UnitVoiceResourceState.Failed, failed.State);
            Assert.IsTrue(store.TryPrepare("owner", "voice", "a.wav", false, 1f, Samples, action => action(), null, out var retried, out string reason), reason);
            Assert.IsTrue(retried.IsReady);
            Assert.AreEqual(UnitVoiceResourceState.Failed, failed.State, "The old handle keeps its own failure.");
            Assert.IsFalse(store.TryPrepare("owner", "voice", "a.wav", false, 1f, Samples, action => action(), null, out _, out _), "Ready blocks.");
            Assert.IsTrue(store.TryPrepare("owner", "queued", "a.wav", false, 1f, Samples, action => { }, null, out var pending, out _));
            Assert.AreEqual(UnitVoiceResourceState.Pending, pending.State);
            Assert.IsFalse(store.TryPrepare("owner", "queued", "a.wav", false, 1f, Samples, action => action(), null, out _, out _), "Pending blocks.");
        }

        [TestMethod]
        public void AFailedOldQueueCannotRemoveTheNextMapsRegistration()
        {
            var store = new UnitVoiceResourceStore(); UnitVoiceResourceHandle current = null;
            Assert.IsFalse(store.TryPrepare("owner", "map", "a.wav", true, 1f, Samples, action =>
            {
                store.InvalidateMapResources();
                Assert.IsTrue(store.TryPrepare("owner", "map", "a.wav", true, 1f, Samples, work => work(), null, out current, out _));
                throw new InvalidOperationException("old queue failed");
            }, null, out _, out _));
            Assert.IsTrue(current.IsReady);
            Assert.IsFalse(store.TryPrepare("owner", "map", "a.wav", true, 1f, Samples, work => work(), null, out _, out _));
        }

        [TestMethod]
        public void PathsAndGainsRejectInvalidInputBeforeDecoding()
        {
            foreach (string path in new[] { "../a.wav", "/a.wav", "C:/a.wav", "a//b.wav", "a/./b.wav", "a/../b.wav", "a\0.wav" })
                Assert.IsFalse(UnitVoiceResourceStore.SafePath(path), path);
            foreach (float gain in new[] { -1f, float.NaN, float.PositiveInfinity })
            {
                var store = new UnitVoiceResourceStore();
                Assert.IsFalse(store.TryPrepare("owner", "id", "voice.wav", false, gain, () => throw new Exception("must not decode"), action => action(), null, out _, out _));
            }
        }

        [TestMethod]
        public void PreparedPcmAcceptsOnlyTheDeclaredMonoOrStereoChannelContract()
        {
            Assert.AreEqual(1, new UnitVoicePcmData(1, 1, 48000, new[] { 0f }).Channels);
            Assert.AreEqual(2, new UnitVoicePcmData(1, 2, 48000, new[] { 0f, 0f }).Channels);
            Assert.ThrowsExactly<ArgumentException>(() => new UnitVoicePcmData(1, 3, 48000, new[] { 0f, 0f, 0f }));
            Assert.ThrowsExactly<ArgumentException>(() => new UnitVoicePcmData(1, 0, 48000, Array.Empty<float>()));
        }

        [TestMethod]
        public void RiffValidationRejectsNonAdvancingChunksAndTruncatedFramesBeforeTheDecoder()
        {
            var valid = Wave(); UnitVoiceEncodedAudio.Validate(valid);
            var negative = (byte[])valid.Clone(); for (int i = 16; i < 20; i++) negative[i] = 255;
            Assert.ThrowsExactly<ArgumentException>(() => UnitVoiceEncodedAudio.Validate(negative));
            var truncated = (byte[])valid.Clone(); truncated[40] = 100;
            Assert.ThrowsExactly<ArgumentException>(() => UnitVoiceEncodedAudio.Validate(truncated));
            var partialFrame = (byte[])valid.Clone(); partialFrame[22] = 2; partialFrame[28] = 16; partialFrame[29] = 177; partialFrame[30] = 2; partialFrame[32] = 4;
            Assert.ThrowsExactly<ArgumentException>(() => UnitVoiceEncodedAudio.Validate(partialFrame));
            UnitVoiceEncodedAudio.Validate(new byte[] { (byte)'O', (byte)'g', (byte)'g', (byte)'S' }); // Vorbis validation remains the decoder's responsibility.
        }
        private static byte[] Wave()
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); writer.Write(38);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200); writer.Write((short)2); writer.Write((short)16);
                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); writer.Write(2); writer.Write((short)1);
                return stream.ToArray();
            }
        }
    }
}

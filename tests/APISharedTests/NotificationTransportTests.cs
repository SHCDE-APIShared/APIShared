using System;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class NotificationTransportTests
    {
        [TestMethod]
        public void ValidatesDistinctOwnershipForRepeatedHeadersAndMedia()
        {
            const int count = 7;
            const int section = -3;
            const int text = 11;
            var words = new short[count * LordMessageGroups.Words];
            Header(words, 0, section, text, true, true);
            Speech(words, 1);
            Video(words, 2, -1); // Old-video stop belongs to neither presentation.
            Video(words, 3, 0);
            Header(words, 4, section, text, true, true);
            Speech(words, 5);
            Video(words, 6, 0);

            var first = new NotificationEmission(5, 0, 4, section, text, "First variant", true, true, true);
            var second = new NotificationEmission(6, 4, 7, section, text, "Second variant", true, true, true);
            Assert.IsTrue(NotificationTransport.TryValidateRecord(words, count, first, out var a));
            Assert.IsTrue(NotificationTransport.TryValidateRecord(words, count, second, out var b));
            Assert.AreEqual(5UL, a.Ticket);
            Assert.AreEqual(6UL, b.Ticket);
            Assert.AreEqual("First variant", a.Subtitle);
            Assert.AreEqual("Second variant", b.Subtitle);
            Assert.AreEqual(1, a.AudioIndex);
            Assert.AreEqual(3, a.VideoIndex);
            Assert.AreEqual(5, b.AudioIndex);
            Assert.AreEqual(6, b.VideoIndex);
            Assert.IsFalse(NotificationTransport.TryValidateRecord(words, count,
                new NotificationEmission(5, 0, 5, section, text, null, false, true, true), out _),
                "An extra header invalidates the exact emission range.");
        }

        [TestMethod]
        public void ValidatesAnnouncementsAndSilentOrMissingTextWithoutInventingMedia()
        {
            var words = new short[3 * LordMessageGroups.Words];
            Header(words, 0, 0, 0, true, false);
            Speech(words, 1);
            Header(words, 2, 201, 11, false, false);
            Assert.IsTrue(NotificationTransport.TryValidateRecord(words, 3,
                new NotificationEmission(5, 0, 2, 0, 0, null, false, true, false), out var announcement));
            Assert.AreEqual(1, announcement.AudioIndex);
            Assert.AreEqual(-1, announcement.VideoIndex);
            Assert.IsFalse(announcement.SubtitleKnown);

            var silent = new NotificationEmission(6, 2, 3, 201, 11, null, true, false, false);
            Assert.IsTrue(NotificationTransport.TryValidateRecord(words, 3, silent, out var genericText));
            Assert.IsTrue(genericText.SubtitleKnown);
            Assert.IsNull(genericText.Subtitle);
            Assert.AreEqual(-1, genericText.AudioIndex);
            Assert.AreEqual(-1, genericText.VideoIndex);
            Assert.IsFalse(NotificationTransport.TryValidateRecord(words, 2, silent, out _),
                "A staged record from a later buffer cannot bind to this buffer.");
        }

        [TestMethod]
        [DataRow(1)]
        [DataRow(2)]
        public void LaterUnrelatedDispatchAndVideoStopRetireMediaOwnership(int laterChannel)
        {
            const int count = 5;
            var words = new short[count * LordMessageGroups.Words];
            Header(words, 0, -3, 11, true, true);
            Speech(words, 1);
            Video(words, 2, 0);
            Speech(words, 3, (short)laterChannel);
            Video(words, 4, -1);
            var emission = new NotificationEmission(8, 0, 3, -3, 11, "authored", true, true, true, 9);
            Assert.IsTrue(NotificationTransport.TryValidateRecord(words, count, emission, out var original));
            var records = new[] { original };
            NotificationTransport.LimitMediaOwnership(words, count, new List<NotificationEmission> { emission }, records);
            Assert.AreEqual(-1, records[0].AudioIndex);
            Assert.AreEqual(-1, records[0].VideoIndex);
            Assert.IsTrue(records[0].HasVideo);
            Assert.AreEqual("authored", records[0].Subtitle);
            Assert.AreEqual(9, records[0].Epoch);
        }

        [TestMethod]
        public void TickEditAcceptsTheInstalledRunAndGeneratesTheBoundaries()
        {
            var target = typeof(EngineInterface).GetMethod(nameof(EngineInterface.run),
                BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(bool) }, null);
            Assert.IsNotNull(target);
            using (var method = new DynamicMethodDefinition(target))
            {
                using (var il = new ILContext(method.Definition))
                    NotificationTransport.InstallTickEdit(il);
                Assert.IsNotNull(method.Generate());
            }
        }

        [TestMethod]
        public void CompletionWithoutASuccessorRetiresTheRenderedSkipAvailability()
        {
            const BindingFlags fields = BindingFlags.Static | BindingFlags.NonPublic;
            var type = typeof(NotificationNativeRuntime);
            var manager = type.GetField("manager", fields);
            var currentEpoch = type.GetField("currentEpoch", fields);
            var active = type.GetField("activePresentation", fields);
            var priorManager = manager.GetValue(null);
            var priorEpoch = currentEpoch.GetValue(null);
            var priorActive = active.GetValue(null);
            IntPtr memory = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                manager.SetValue(null, memory);
                currentEpoch.SetValue(null, NotificationNativeRuntime.Epoch);
                active.SetValue(null, 1);
                Marshal.WriteInt32(memory, 1);
                NotificationNativeRuntime.ReconcileActivePresentation();
                Assert.IsTrue(NotificationNativeRuntime.HasActivePresentation);

                Marshal.WriteInt32(memory, 0);
                NotificationNativeRuntime.ReconcileActivePresentation();
                Assert.IsFalse(NotificationNativeRuntime.HasActivePresentation);
            }
            finally
            {
                active.SetValue(null, priorActive);
                currentEpoch.SetValue(null, priorEpoch);
                manager.SetValue(null, priorManager);
                Marshal.FreeHGlobal(memory);
            }
        }

        private static void Header(short[] words, int index, int selector, int line, bool audio, bool video)
        {
            int start = index * LordMessageGroups.Words;
            words[start] = 20;
            words[start + 11] = (short)selector;
            words[start + 12] = (short)line;
            words[start + 13] = (short)(audio ? 1 : 0);
            words[start + 14] = (short)(video ? 1 : 0);
        }

        private static void Speech(short[] words, int index, short channel = 1)
        {
            int start = index * LordMessageGroups.Words;
            words[start] = 18;
            words[start + 11] = 100;
            words[start + 12] = channel;
        }

        private static void Video(short[] words, int index, short mode)
        {
            int start = index * LordMessageGroups.Words;
            words[start] = 21;
            words[start + 3] = mode;
        }
    }
}

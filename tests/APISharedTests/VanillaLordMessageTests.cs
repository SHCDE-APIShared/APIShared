using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop.Enums;
using System;
using System.IO;
using System.Security.Cryptography;
using AILords = SHCDESE.Interop.AILords;

namespace APISharedTests
{
    [TestClass]
    public sealed class VanillaLordMessageTests
    {
        [TestMethod]
        public void CanonicalNativeCatalogResolvesEveryAuthoredReferenceWithoutGameCalls()
        {
            byte[] image = InstalledImage(out ulong imageBase);
            var anchors = NativeLordMessageCatalog.Resolve(image, NativeLordMessageCatalog.NativeHash);
            Assert.AreEqual("reference-rva", anchors.Method);
            var catalog = NativeLordMessageCatalog.Read(image, imageBase, anchors);
            foreach (AILords lord in new[] { AILords.SK_RAT, AILords.SK_SNAKE, AILords.SK_PIG, AILords.SK_WOLF })
            {
                var request = catalog[(int)AILordMessageType.RequestGoods * NativeLordMessageCatalog.Slots + (int)lord];
                Assert.AreEqual("fx\\speech\\all_req_01.wav", request.AudioPath);
                Assert.AreEqual("bad_soldier_taunt.webm", request.VideoPath);
                Assert.AreEqual(lord, request.Lord);
                Assert.AreEqual(AILordMessageType.RequestGoods, request.MessageType);
                Assert.AreEqual(global::Enums.eTextSections.TEXT_SKIRMISH_SPEECH, request.TextSection);
            }
            string[] voices = { "rt_taunt_01.wav", "sn_taunt_01.wav", "pg_taunt_03.wav", "wf_taunt_01.wav" };
            string[] videos = { "rt_taunt2.webm", "sn_taunt1.webm", "pg_taunt1.webm", "wf_taunt1.webm" };
            for (int lord = (int)AILords.SK_RAT; lord <= (int)AILords.SK_WOLF; lord++)
            {
                var donor = catalog[(int)AILordMessageType.Taunt1 * NativeLordMessageCatalog.Slots + lord];
                Assert.AreEqual("fx\\speech\\" + voices[lord - (int)AILords.SK_RAT], donor.AudioPath);
                Assert.AreEqual(videos[lord - (int)AILords.SK_RAT], donor.VideoPath);
            }
            for (int category = (int)AILordMessageType.Taunt1; category <= (int)AILordMessageType.WillAttackEnemy; category++)
                for (int lord = (int)AILords.SK_RAT; lord <= (int)AILords.SK_DLC4B; lord++)
                {
                    var message = catalog[category * NativeLordMessageCatalog.Slots + lord];
                    Assert.IsNotNull(message);
                    // 34 is the audited native localization stride, independently checked against
                    // both the wrapper's IMUL 22h and generic branch's byte-pointer stride.
                    Assert.AreEqual((lord - (int)AILords.SK_RAT) * NativeLordMessageCatalog.Categories + category, message.TextIndex);
                }
        }

        [TestMethod]
        public void UnknownIdentityUsesUniqueAnchorsAndValidatedCacheAndIgnoresCorruptCache()
        {
            byte[] image = InstalledImage(out ulong imageBase);
            string cache = Path.GetTempFileName();
            try
            {
                string unknown = new string('0', 64);
                var scanned = NativeLordMessageCatalog.Resolve(image, unknown, cache);
                Assert.AreEqual("pattern-fallback", scanned.Method);
                Assert.IsNotNull(NativeLordMessageCatalog.Read(image, imageBase, scanned));
                var cached = NativeLordMessageCatalog.Resolve(image, unknown, cache);
                Assert.AreEqual("validated-cache", cached.Method);
                Assert.AreEqual(scanned.BuiltIn, cached.BuiltIn);
                File.WriteAllBytes(cache, new byte[] { 1, 2, 3 });
                Assert.AreEqual("pattern-fallback", NativeLordMessageCatalog.Resolve(image, unknown, cache).Method);
            }
            finally { File.Delete(cache); }
        }

        [TestMethod]
        public void IndependentTablesModuleBaseAndPointerBoundsAreRequired()
        {
            byte[] image = InstalledImage(out ulong imageBase);
            var anchors = NativeLordMessageCatalog.Resolve(image, NativeLordMessageCatalog.NativeHash);
            // Both addressing forms must agree, even though the table displacement is a pattern wildcard.
            int offset = anchors.BuiltIn + 36;
            byte original = image[offset]; image[offset] ^= 8;
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeLordMessageCatalog.Read(image, imageBase, anchors));
            image[offset] = original;
            offset = anchors.Base + 17;
            original = image[offset]; image[offset] ^= 1;
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeLordMessageCatalog.Read(image, imageBase, anchors));
            image[offset] = original;
            int audioTable = BitConverter.ToInt32(image, anchors.Generic + 26);
            int entry = audioTable + ((int)AILordMessageType.Taunt1 * NativeLordMessageCatalog.Slots + (int)AILords.SK_RAT) * sizeof(ulong);
            byte[] pointer = new byte[sizeof(ulong)]; Buffer.BlockCopy(image, entry, pointer, 0, pointer.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(imageBase + (ulong)image.Length), 0, image, entry, sizeof(ulong));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeLordMessageCatalog.Read(image, imageBase, anchors));
            Buffer.BlockCopy(pointer, 0, image, entry, pointer.Length);
            // A bounded pointer into .text is still not an accepted string source.
            Buffer.BlockCopy(BitConverter.GetBytes(imageBase + (ulong)anchors.Base), 0, image, entry, sizeof(ulong));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeLordMessageCatalog.Read(image, imageBase, anchors));
        }

        [TestMethod]
        public void OnlyBuiltInLordsAndFullMessagesAreValidReferences()
        {
            Assert.IsTrue(VanillaLordMessages.ValidReference((int)AILords.SK_RAT, (int)AILordMessageType.Taunt1));
            Assert.IsTrue(VanillaLordMessages.ValidReference((int)AILords.SK_DLC4B, (int)AILordMessageType.WillAttackEnemy));
            foreach (AILords lord in new[] { AILords.SK_NULL, AILords.SK_X1, AILords.SK_TEMP, (AILords)uint.MaxValue })
            {
                Assert.IsFalse(VanillaLordMessages.TryGet(lord, AILordMessageType.Taunt1, out var message, out string reason));
                Assert.IsNull(message); Assert.IsFalse(string.IsNullOrEmpty(reason));
            }
            foreach (AILordMessageType type in new[] { AILordMessageType.IncomingMessage, AILordMessageType.Nickname1, (AILordMessageType)(-1) })
                Assert.IsFalse(VanillaLordMessages.TryGet(AILords.SK_RAT, type, out _, out _));
        }

        private static byte[] InstalledImage(out ulong imageBase)
        {
            string path = Path.Combine(InstalledTestEnvironment.GameDirectory(),
                "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            byte[] file = File.ReadAllBytes(path);
            using (var sha = SHA256.Create())
                Assert.AreEqual(NativeLordMessageCatalog.NativeHash, BitConverter.ToString(sha.ComputeHash(file)).Replace("-", string.Empty),
                    "The exact source-media expectations belong to the audited native image; re-audit them after a game update.");
            int pe = BitConverter.ToInt32(file, 0x3C), optional = pe + 24;
            imageBase = BitConverter.ToUInt64(file, optional + 24);
            int imageSize = BitConverter.ToInt32(file, optional + 56), headerSize = BitConverter.ToInt32(file, optional + 60);
            var image = new byte[imageSize]; Buffer.BlockCopy(file, 0, image, 0, headerSize);
            int sectionCount = BitConverter.ToUInt16(file, pe + 6), sections = optional + BitConverter.ToUInt16(file, pe + 20);
            for (int i = 0; i < sectionCount; i++)
            {
                int section = sections + i * 40, size = BitConverter.ToInt32(file, section + 16);
                if (size > 0) Buffer.BlockCopy(file, BitConverter.ToInt32(file, section + 20), image,
                    BitConverter.ToInt32(file, section + 12), size);
            }
            return image;
        }
    }
}

using System;
using System.IO;
using APIShared.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class FixesWheatAnalysisTests
    {
        private const ulong Stub = 0x180400000, Flags = 0x26746026040, Continuation = 0x18003F0DC;
        private const int Home = 0x68;

        private static FixesWheatAnalysis.WheatStub Analyze(byte[] bytes, ulong flags = Flags, ulong continuation = Continuation,
            int home = Home) => FixesWheatAnalysis.AnalyzeStub(bytes, Stub, flags, FixesWheatTestStub.Original, continuation, home);

        [TestMethod]
        public void InstalledSellerFrameIsDerivedFromUnwindDataAndPrologCode()
        {
            string game = Environment.GetEnvironmentVariable("SHCDE_GAME_DIR") ??
                File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim();
            string path = Path.Combine(game, "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            var image = new FixesWheatAnalysis.PeImage(File.ReadAllBytes(path));
            byte[] atSite = image.Read(FixesWheatTestStub.InstalledSiteRva, FixesWheatTestStub.Original.Length);
            // An observation of the installed game: another build moves the site, which Fixes then resolves.
            if (!System.Linq.Enumerable.SequenceEqual(atSite, FixesWheatTestStub.Original)) { Assert.Inconclusive("Different game build."); return; }
            var frame = FixesWheatAnalysis.AnalyzeSeller(image, FixesWheatTestStub.InstalledSiteRva, FixesWheatTestStub.Original);
            Assert.AreEqual(0x3EE10u, frame.FunctionBegin);
            Assert.AreEqual(0x58, frame.Allocation);
            Assert.AreEqual(Home, frame.PlayerHome);
            Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatAnalysis.AnalyzeSeller(image,
                FixesWheatTestStub.InstalledSiteRva + 1, FixesWheatTestStub.Original), "Disk bytes must match the snapshot");
        }

        [TestMethod]
        public void SyntheticSellerFrameAndHomeOverwriteAreDerivedFromCode()
        {
            var frame = FixesWheatAnalysis.AnalyzeSeller(new FixesWheatAnalysis.PeImage(SyntheticSeller(false)),
                SyntheticSite, FixesWheatTestStub.Original);
            Assert.AreEqual(0x1000u, frame.FunctionBegin);
            Assert.AreEqual(0x58, frame.Allocation);
            Assert.AreEqual(Home, frame.PlayerHome);
            Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatAnalysis.AnalyzeSeller(
                new FixesWheatAnalysis.PeImage(SyntheticSeller(true)), SyntheticSite, FixesWheatTestStub.Original));
        }

        [TestMethod]
        public void WrongPlayerLoadIsCorrectedByExactlyOneByteAndThenAccepted()
        {
            byte[] actual = FixesWheatTestStub.Audited(Flags, Continuation, 0x50);
            byte[] before = (byte[])actual.Clone();
            var stub = Analyze(actual);
            Assert.AreEqual(75, stub.Length);
            Assert.AreEqual(FixesWheatTestStub.OperandOffset, stub.OperandOffset);
            Assert.AreEqual(0x10, stub.PushedBytes);
            Assert.AreEqual((byte)0x78, stub.Required);
            int writes = 0;
            Action<int, byte> write = (offset, value) => { actual[offset] = value; writes++; };
            Assert.IsTrue(FixesWheatAnalysis.Correct(() => (byte[])actual.Clone(), write, bytes => Analyze(bytes)));
            for (int i = 0; i < actual.Length; i++)
                Assert.AreEqual(i == FixesWheatTestStub.OperandOffset ? (byte)0x78 : before[i], actual[i], "byte " + i);
            Assert.IsFalse(FixesWheatAnalysis.Correct(() => (byte[])actual.Clone(), write, bytes => Analyze(bytes)));
            Assert.AreEqual(1, writes);
            // Any other wrong slot is corrected too; the requirement comes from the frame, not from a known value.
            byte[] other = FixesWheatTestStub.Audited(Flags, Continuation, 0x40);
            Assert.IsTrue(FixesWheatAnalysis.Correct(() => (byte[])other.Clone(), (o, v) => other[o] = v, bytes => Analyze(bytes)));
            Assert.AreEqual((byte)0x78, other[FixesWheatTestStub.OperandOffset]);
        }

        [TestMethod]
        public void StubThatDoesNotMatchItsFlagsReturnOrOriginalIsRefusedBeforeWrite()
        {
            byte[] stub = FixesWheatTestStub.Audited(Flags, Continuation, 0x50);
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(stub, flags: Flags + 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(stub, continuation: Continuation + 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(stub, home: 0x7F));
            byte[] original = (byte[])stub.Clone();
            original[0x2A + 3] ^= 1; // displaced copy differs from the backend snapshot
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(original));
            byte[] unbalanced = (byte[])stub.Clone();
            unbalanced[0x29] = 0x90; // pop rdx removed
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(unbalanced));
            byte[] index = (byte[])stub.Clone();
            index[0x1C] = 0x08; // cmp byte [r8+rcx]: the load no longer indexes the flags
            Assert.ThrowsExactly<InvalidOperationException>(() => Analyze(index));
            bool wrote = false;
            Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatAnalysis.Correct(() => index,
                (_, __) => wrote = true, bytes => Analyze(bytes)));
            Assert.IsFalse(wrote);
        }

        [TestMethod]
        public void DeniedWriteAndUnexpectedPostWriteBytesNeverReportSuccess()
        {
            byte[] actual = FixesWheatTestStub.Audited(Flags, Continuation, 0x50);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => FixesWheatAnalysis.Correct(() => actual,
                (_, __) => throw new UnauthorizedAccessException(), bytes => Analyze(bytes)));
            Assert.AreEqual((byte)0x50, actual[FixesWheatTestStub.OperandOffset]);
            Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatAnalysis.Correct(() => (byte[])actual.Clone(),
                (offset, value) => { actual[offset] = value; actual[0] ^= 1; }, bytes => Analyze(bytes)));
            Assert.AreEqual((byte)0x78, actual[FixesWheatTestStub.OperandOffset]); // No rollback of the published stub.
        }

        private const uint SyntheticSite = 0x1010;

        // Minimal PE32+: one seller with `mov [rsp+10h],edx; sub rsp,58h`, the displaced instructions at
        // 0x1010 and an optional later store into the player home slot [rsp+68h].
        private static byte[] SyntheticSeller(bool overwriteHome)
        {
            byte[] file = new byte[0x800];
            file[0] = (byte)'M'; file[1] = (byte)'Z';
            Put(file, 0x3C, 0x40);
            Put(file, 0x40, 0x4550);
            PutShort(file, 0x44, 0x8664);
            PutShort(file, 0x46, 2);
            PutShort(file, 0x54, 240);
            int optional = 0x58;
            PutShort(file, optional, 0x20B);
            Put(file, optional + 56, 0x3000);
            Put(file, optional + 108, 16);
            Put(file, optional + 112 + 24, 0x2000);
            Put(file, optional + 112 + 28, 12);
            int sections = optional + 240;
            Section(file, sections, ".text", 0x1000, 0x200, 0x400, 0x60000020);
            Section(file, sections + 40, ".pdata", 0x2000, 0x200, 0x600, 0x40000040);
            byte[] code = FixesWheatTestStub.Hex("89542410" + "4883EC58" + "9090909090909090");
            byte[] tail = FixesWheatTestStub.Hex((overwriteHome ? "4889442468" : "488B442468") + "4883C458C3");
            int length = code.Length + FixesWheatTestStub.Original.Length + tail.Length;
            Array.Copy(code, 0, file, 0x400, code.Length);
            Array.Copy(FixesWheatTestStub.Original, 0, file, 0x400 + code.Length, FixesWheatTestStub.Original.Length);
            Array.Copy(tail, 0, file, 0x400 + code.Length + FixesWheatTestStub.Original.Length, tail.Length);
            Put(file, 0x600, 0x1000);
            Put(file, 0x604, 0x1000 + length);
            Put(file, 0x608, 0x2100);
            // UNWIND_INFO v1: prolog 8 bytes, one code UWOP_ALLOC_SMALL (0x58 = 10 * 8 + 8) at offset 8.
            file[0x700] = 1; file[0x701] = 8; file[0x702] = 1; file[0x703] = 0;
            file[0x704] = 8; file[0x705] = (10 << 4) | 2;
            return file;
        }

        private static void Section(byte[] file, int header, string name, int rva, int size, int raw, uint characteristics)
        {
            for (int i = 0; i < name.Length; i++) file[header + i] = (byte)name[i];
            Put(file, header + 8, size);
            Put(file, header + 12, rva);
            Put(file, header + 16, size);
            Put(file, header + 20, raw);
            Array.Copy(BitConverter.GetBytes(characteristics), 0, file, header + 36, 4);
        }

        private static void Put(byte[] file, int offset, int value) => Array.Copy(BitConverter.GetBytes(value), 0, file, offset, 4);
        private static void PutShort(byte[] file, int offset, int value) => Array.Copy(BitConverter.GetBytes((ushort)value), 0, file, offset, 2);
    }
}

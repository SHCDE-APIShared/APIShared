using APIShared.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class FixesWheatContractTests
{
    private const ulong Flags = 0x26746026040, Return = 0x7FFC3A95F0DC;

    [TestMethod]
    public void ChangesExactlyOneByteAndSecondAttemptDoesNotWrite()
    {
        byte[] before = FixesWheatContract.ExpectedStub(Flags, Return, 0x50);
        byte[] actual = (byte[])before.Clone();
        int writes = 0;
        Action<int, byte> write = (offset, value) => { actual[offset] = value; writes++; };
        Assert.IsTrue(FixesWheatContract.Correct(() => (byte[])actual.Clone(), write, Flags, Return));
        for (int i = 0; i < actual.Length; i++)
            Assert.AreEqual(i == 12 ? (byte)0x78 : before[i], actual[i], "byte " + i);
        Assert.IsFalse(FixesWheatContract.Correct(() => (byte[])actual.Clone(), write, Flags, Return));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public void EveryOtherStubByteAndWrongReturnAreRejectedBeforeWrite()
    {
        for (int i = 0; i < FixesWheatContract.StubLength; i++)
        {
            byte[] actual = FixesWheatContract.ExpectedStub(Flags, Return, 0x50);
            actual[i] ^= 1;
            bool wrote = false;
            Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatContract.Correct(
                () => actual, (_, _) => wrote = true, Flags, Return));
            Assert.IsFalse(wrote, "byte " + i);
        }
        Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatContract.ValidateStub(
            FixesWheatContract.ExpectedStub(Flags, Return + 1, 0x50), Flags, Return));
    }

    [TestMethod]
    public void DeniedWriteAndUnexpectedPostWriteBytesNeverReportSuccess()
    {
        byte[] actual = FixesWheatContract.ExpectedStub(Flags, Return, 0x50);
        Assert.ThrowsExactly<UnauthorizedAccessException>(() => FixesWheatContract.Correct(
            () => actual, (_, _) => throw new UnauthorizedAccessException(), Flags, Return));
        Assert.AreEqual((byte)0x50, actual[12]);
        Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatContract.Correct(
            () => (byte[])actual.Clone(), (offset, value) => { actual[offset] = value; actual[0] ^= 1; }, Flags, Return));
        Assert.AreEqual((byte)0x78, actual[12]); // No rollback of the published stub.
    }

    [TestMethod]
    public void CorrectedLoadUsesPlayerSlotRegardlessOfSavedRdiAndFlags()
    {
        // Stack fixture at the first two pushes, before TestNativeFlag's extra push.
        // This verifies addressing, not execution of the full native stub (backend test pending).
        byte[] stub = FixesWheatContract.ExpectedStub(Flags, Return, 0x50);
        FixesWheatContract.Correct(() => (byte[])stub.Clone(), (i, b) => stub[i] = b, Flags, Return);
        foreach (ulong rdi in new ulong[] { 0, 1, 8, 9, 0x7FFC3E96C950, ulong.MaxValue })
        for (int player = 1; player <= 8; player++)
        foreach (bool enabled in new[] { false, true })
        {
            byte[] stack = new byte[0x80];
            Array.Copy(BitConverter.GetBytes(rdi), 0, stack, 0x50, 8);
            Array.Copy(BitConverter.GetBytes(player), 0, stack, 0x78, 4);
            uint index = BitConverter.ToUInt32(stack, stub[12]);
            byte[] flags = new byte[9]; flags[player] = enabled ? (byte)1 : (byte)0;
            Assert.AreEqual((uint)player, index);
            Assert.AreEqual(enabled, flags[index] != 0);
        }
    }

    [TestMethod]
    public void LateMissingOrReorderedStartupFramesAreRejected()
    {
        Assert.IsTrue(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(false, false, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, true, true, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, false, 2, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, -1, 5, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, -1, 6, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, -1, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 6, 5, true, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, false, true));
        Assert.IsFalse(FixesWheatContract.IsStartup(true, false, true, 2, 5, 6, true, false));
    }
}

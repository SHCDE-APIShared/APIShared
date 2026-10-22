using System;

namespace APISharedTests
{
    // Test data only: the stub Fixes 1.26.5 emits through RedBird, and the installed game's displaced
    // instructions. Production derives every value from the actual code instead.
    internal static class FixesWheatTestStub
    {
        internal const uint InstalledSiteRva = 0x3F0C9;
        internal const int OperandOffset = 12;
        internal static readonly byte[] Original = Hex("458B9AC4000000498D0438488D2C8500000000");
        private static readonly byte[] Template = Hex(
            "5241504883FF09751E8B542450415049B8000000000000000041803C100041587405B90600000041585A458B9AC4000000498D0438488D2C8500000000FF25000000000000000000000000");

        internal static byte[] Audited(ulong flags, ulong continuation, byte displacement)
        {
            byte[] result = (byte[])Template.Clone();
            Array.Copy(BitConverter.GetBytes(flags), 0, result, 17, 8);
            Array.Copy(BitConverter.GetBytes(continuation), 0, result, 67, 8);
            result[OperandOffset] = displacement;
            return result;
        }

        internal static byte[] Hex(string text)
        {
            byte[] bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}

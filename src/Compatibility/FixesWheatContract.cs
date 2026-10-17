using System;

namespace APIShared.Compatibility
{
    // Complete machine contract, not a pattern search. Immediates 9/6 are Fixes'
    // eGoods.STORED_RAW_WHEAT / AISellCategory.Resource in the audited binary.
    internal static class FixesWheatContract
    {
        internal const int SellerRva = 0x3EE10;
        internal const int SiteRva = 0x3F0C9;
        internal const int DisplacedLength = 19;
        internal const int OperandOffset = 12;
        internal const int StubLength = 75;
        internal const byte CorrectOffset = 0x10 + 0x58 + 8 + 8;
        internal const string NativeHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal static readonly byte[] Prolog = Hex("8954241048894C24084883EC58");
        internal static readonly byte[] Original = Hex("458B9AC4000000498D0438488D2C8500000000");
        private static readonly byte[] Template = Hex(
            "5241504883FF09751E8B542450415049B8000000000000000041803C100041587405B90600000041585A458B9AC4000000498D0438488D2C8500000000FF25000000000000000000000000");

        internal static bool IsStartup(bool initialized, bool loaded, bool ownInstanceAbsent,
            int awakeFrame, int chainFrame, int entryFrame, bool directorAbsent, bool mapAbsent)
            => initialized && !loaded && ownInstanceAbsent && awakeFrame >= 0 &&
                chainFrame > awakeFrame && entryFrame > chainFrame && directorAbsent && mapAbsent;

        internal static byte[] ExpectedStub(ulong flags, ulong continuation, byte offset)
        {
            if (flags == 0 || continuation == 0 || (offset != 0x50 && offset != CorrectOffset))
                throw new InvalidOperationException("Invalid wheat stub operands");
            byte[] result = (byte[])Template.Clone();
            Array.Copy(BitConverter.GetBytes(flags), 0, result, 17, 8);
            Array.Copy(BitConverter.GetBytes(continuation), 0, result, 67, 8);
            result[OperandOffset] = offset;
            return result;
        }

        internal static byte[] ExpectedEntry(ulong stub)
        {
            byte[] result = new byte[DisplacedLength];
            result[0] = 0xFF; result[1] = 0x25;
            Array.Copy(BitConverter.GetBytes(stub), 0, result, 6, 8);
            for (int i = 14; i < result.Length; i++) result[i] = 0x90;
            return result;
        }

        internal static bool ValidateStub(byte[] actual, ulong flags, ulong continuation)
        {
            if (actual == null || actual.Length != StubLength)
                throw new InvalidOperationException("Wrong wheat stub length");
            RequireEqual(actual, ExpectedStub(flags, continuation, actual[OperandOffset]), "complete wheat stub");
            return actual[OperandOffset] == CorrectOffset;
        }

        // The writer owns page protection and cache synchronization. No rollback is allowed.
        internal static bool Correct(Func<byte[]> read, Action<int, byte> write, ulong flags, ulong continuation)
        {
            byte[] before = read();
            if (ValidateStub(before, flags, continuation)) return false;
            write(OperandOffset, CorrectOffset);
            byte[] expected = (byte[])before.Clone();
            expected[OperandOffset] = CorrectOffset;
            byte[] after = read();
            RequireEqual(after, expected, "post-write bytes (only player displacement may change)");
            ValidateStub(after, flags, continuation);
            return true;
        }

        internal static void RequireEqual(byte[] actual, byte[] expected, string description)
        {
            if (actual == null || actual.Length != expected.Length)
                throw new InvalidOperationException(description + ": length mismatch");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i])
                    throw new InvalidOperationException(description + ": mismatch at +0x" + i.ToString("X"));
        }

        private static byte[] Hex(string text)
        {
            byte[] bytes = new byte[text.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(text.Substring(i * 2, 2), 16);
            return bytes;
        }
    }
}

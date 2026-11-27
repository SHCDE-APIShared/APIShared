using System;
using System.IO;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

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

        // Independent snapshot of the v1.29.1 foreign emitter, not the production repair builder.
        // The installed-emitter test separately executes a private copy from the real Fixes DLL.
        internal static byte[] Division(ulong flags, ulong continuation)
        {
            var a = new Assembler(64);
            Label vanilla = a.CreateLabel();
            a.push(rax); a.push(rcx); a.push(rdx); a.push(r8);
            a.cmp(rdi, 9); a.jne(vanilla);
            a.mov(rax, r9); a.mov(rcx, 22588); a.xor(rdx, rdx); a.div(rcx);
            a.push(r8); a.mov(r8, flags); a.cmp(__byte_ptr[r8 + rdx], 0); a.pop(r8);
            a.jz(vanilla); a.mov(ecx, 6); a.Label(ref vanilla);
            a.pop(r8); a.pop(rdx); a.pop(rcx); a.pop(rax);
            using (var stream = new MemoryStream())
            {
                a.Assemble(new StreamCodeWriter(stream), 0x180400000);
                byte[] prefix = stream.ToArray();
                byte[] body = new byte[prefix.Length + Original.Length];
                Array.Copy(prefix, body, prefix.Length);
                Array.Copy(Original, 0, body, prefix.Length, Original.Length);
                return Complete(body, continuation);
            }
        }

        internal static byte[] Complete(byte[] body, ulong continuation)
        {
            byte[] result = new byte[body.Length + 14];
            Array.Copy(body, result, body.Length);
            result[body.Length] = 0xFF;
            result[body.Length + 1] = 0x25;
            Array.Copy(BitConverter.GetBytes(continuation), 0, result, body.Length + 6, 8);
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

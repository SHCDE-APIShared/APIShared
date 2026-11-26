using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Pure generator and arithmetic of the corpse lifetime sites (see CorpseLifetimeNative). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native bytes.
    internal static class CorpseLifetimeNativeAssembly
    {
        // Vanilla's constant in the fading branch (AI state 0x6E) of the rabbit (0x157810), camel (0x158020)
        // and chicken (0x162CB0) updates (hash FBCB9319): `mov ecx, 0x20`. CL caps the signed fade byte
        // (+0x986, cmp/cmovg) and CX is the signed threshold of the fading counter (+0x920, jle): above it the
        // update writes alive state 3 and Vanilla's unit pass deletes the corpse.
        internal const int VanillaThreshold = 0x20;
        // Above Vanilla's threshold APIShared keeps the low byte at 8: the same constant would otherwise cap the
        // fade byte at an arbitrary (possibly negative) value. 8 of 32 fade steps keeps a long-lived corpse
        // visible, as ImprovedHunters' former value 1800 (0x708) did.
        internal const int LongLifetimeFadeCap = 8;
        // Signed 16-bit counter compare; the largest encodable threshold with low byte 8 is 0x7F08.
        internal const int MaximumThreshold = 0x7F08;
        // Public limit (HuntablePrey.MaximumCorpseLifetime): encodable for every supported step.
        internal const int MaximumLifetimeTicks = 16000;

        // Map ticks of the fading state -> native threshold. step: counter increments per update (rabbit and
        // camel 1, chicken 2). A request at or below Vanilla keeps Vanilla's constant (minimum semantics).
        internal static int EncodeThreshold(int ticks, int step)
        {
            if (step < 1 || step > 2) throw new ArgumentOutOfRangeException(nameof(step));
            long counter = (long)Math.Max(0, ticks) * step;
            if (counter <= VanillaThreshold) return VanillaThreshold;
            long blocks = (counter - LongLifetimeFadeCap + 255) / 256;
            long threshold = blocks * 256 + LongLifetimeFadeCap;
            if (threshold > MaximumThreshold) throw new ArgumentOutOfRangeException(nameof(ticks), "The corpse lifetime exceeds the native counter.");
            return (int)threshold;
        }

        // Replays the span and loads ECX from the threshold cell instead of the constant. RCX is written by the
        // replaced `mov ecx, imm32` anyway (dead before it), and mov changes no flag, so the following
        // cmp/cmovg/jle see exactly Vanilla's operands for a cell holding 0x20.
        internal static void EmitThreshold(Assembler a, ReadOnlySpan<Instruction> original, ulong cell)
        {
            int loads = 0;
            foreach (var decoded in original)
            {
                if (decoded.Code == Code.Mov_r32_imm32 && decoded.Op0Register == Register.ECX && decoded.Immediate32 == VanillaThreshold)
                {
                    a.mov(rcx, cell);
                    a.mov(ecx, __dword_ptr[rcx]);
                    loads++;
                    continue;
                }
                var instruction = decoded;
                instruction.IP = 0;
                a.AddInstruction(instruction);
            }
            if (loads != 1) throw new InvalidOperationException("The corpse fading span must load Vanilla's threshold exactly once.");
        }
    }
}

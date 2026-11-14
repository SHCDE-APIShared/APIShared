using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Pure generator of the chicken target bridge (see ChickenPopulationRuntime). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native bytes.
    internal static class ChickenPopulationNativeAssembly
    {
        // Player record field compared with the target: Vanilla's count of the player's chickens.
        internal const int CountOffset = 0x2048;

        // Span [0xD2AB4,0xD2AC5) of 0xD29E0, three instructions:
        //   cmp dword ptr [rip+0x3593525], 0   (0x3665FE0: Vanilla's "100 chickens" switch)
        //   cmovne eax, r13d                   (R13D = 100)
        //   cmp eax, dword ptr [rdi+0x2048]    (flags read by jle 0xD2BA7 and its jge)
        // EAX is the food-based target, EBX the player (1..8, loop at 0xD2C03), RDI the player record. The
        // stub replays the first two, so EAX is Vanilla's final target, and with demand calls the managed
        // callback(player, target, count), whose result replaces EAX before the replayed compare. RCX, RDX,
        // R8 to R11 and XMM0..5 are saved around the call (R9D = 0x6E and R10D = 10 stay live in the loop);
        // nonvolatile registers are the callee's ABI duty. The flags at the span start are dead and the final
        // compare sets them again; no RedBird context flag is involved.
        internal static void EmitTarget(Assembler a, ReadOnlySpan<Instruction> original, ulong demand, ulong callback)
        {
            if (original.Length != 3) throw new InvalidOperationException("The chicken target span must be three instructions.");
            if (callback == 0) throw new InvalidOperationException("The chicken target bridge requires a rooted callback.");
            Label skip = a.CreateLabel("chickenVanilla");
            Replay(a, original.Slice(0, 2));
            a.push(rax);
            a.mov(rax, demand);
            a.cmp(__dword_ptr[rax], 0);
            a.pop(rax);
            a.je(skip);
            a.push(rcx); a.push(rdx); a.push(r8); a.push(r9); a.push(r10); a.push(r11);
            a.mov(r11, rsp);
            a.and(rsp, -16);
            a.sub(rsp, 144);
            a.mov(__qword_ptr[rsp + 128], r11);
            a.movdqu(__xmmword_ptr[rsp + 32], xmm0); a.movdqu(__xmmword_ptr[rsp + 48], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 64], xmm2); a.movdqu(__xmmword_ptr[rsp + 80], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 96], xmm4); a.movdqu(__xmmword_ptr[rsp + 112], xmm5);
            a.mov(ecx, ebx);
            a.mov(edx, eax);
            a.mov(r8d, __dword_ptr[rdi + CountOffset]);
            a.mov(rax, callback);
            a.call(rax);
            a.movdqu(xmm0, __xmmword_ptr[rsp + 32]); a.movdqu(xmm1, __xmmword_ptr[rsp + 48]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 64]); a.movdqu(xmm3, __xmmword_ptr[rsp + 80]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 96]); a.movdqu(xmm5, __xmmword_ptr[rsp + 112]);
            a.mov(rsp, __qword_ptr[rsp + 128]);
            a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rdx); a.pop(rcx);
            a.Label(ref skip);
            Replay(a, original.Slice(2, 1));
        }

        private static void Replay(Assembler a, ReadOnlySpan<Instruction> instructions)
        {
            foreach (var decoded in instructions)
            {
                var instruction = decoded;
                instruction.IP = 0;
                a.AddInstruction(instruction);
            }
        }
    }
}

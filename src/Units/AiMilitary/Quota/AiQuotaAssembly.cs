using System;
using System.IO;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Units.AiMilitary.Quota
{
    internal static class AiQuotaAssembly
    {
        internal const int ContextSize = 240;
        private static readonly AssemblerRegister64[] Gprs =
            { rax, rbx, rcx, rdx, rsi, rdi, rbp, rsp, r8, r9, r10, r11, r12, r13, r14, r15 };
        // The shared context bridge saves every GPR, actual flags and volatile XMM0..5.
        // The managed callback may change only its documented result register/status bits.
        internal static void Bridge(Assembler a, int index, ulong callback)
        {
            a.lea(rsp, __qword_ptr[rsp - ContextSize]);
            for (int i = 0; i < Gprs.Length; i++) if (i != 7) a.mov(__qword_ptr[rsp + i * 8], Gprs[i]);
            a.lea(rax, __qword_ptr[rsp + ContextSize]); a.mov(__qword_ptr[rsp + 56], rax);
            a.pushfq(); a.pop(rax); a.mov(__qword_ptr[rsp + 128], rax);
            a.movdqu(__xmmword_ptr[rsp + 144], xmm0); a.movdqu(__xmmword_ptr[rsp + 160], xmm1);
            a.movdqu(__xmmword_ptr[rsp + 176], xmm2); a.movdqu(__xmmword_ptr[rsp + 192], xmm3);
            a.movdqu(__xmmword_ptr[rsp + 208], xmm4); a.movdqu(__xmmword_ptr[rsp + 224], xmm5);
            a.mov(rdx, rsp); a.mov(ecx, index); a.mov(rax, rsp);
            a.and(rsp, -16); a.sub(rsp, 48); a.mov(__qword_ptr[rsp + 32], rax);
            a.mov(rax, callback); a.call(rax); a.mov(rsp, __qword_ptr[rsp + 32]);
            a.movdqu(xmm0, __xmmword_ptr[rsp + 144]); a.movdqu(xmm1, __xmmword_ptr[rsp + 160]);
            a.movdqu(xmm2, __xmmword_ptr[rsp + 176]); a.movdqu(xmm3, __xmmword_ptr[rsp + 192]);
            a.movdqu(xmm4, __xmmword_ptr[rsp + 208]); a.movdqu(xmm5, __xmmword_ptr[rsp + 224]);
            a.push(__qword_ptr[rsp + 128]); a.popfq();
            for (int i = Gprs.Length - 1; i >= 0; i--) if (i != 7) a.mov(Gprs[i], __qword_ptr[rsp + i * 8]);
            a.lea(rsp, __qword_ptr[rsp + ContextSize]);
        }
        internal static void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            int index, ulong image, ulong state, ulong callback)
        {
            if (index < 0 || index >= AiQuotaNativeContract.Sites.Length ||
                back != image + (uint)AiQuotaNativeContract.Sites[index].Rva + (uint)AiQuotaNativeContract.Sites[index].Bytes.Length)
                throw new InvalidOperationException("Unexpected AI quota continuation.");
            if (index == 0)
            {
                // Detect a natural load-time fold even before prepared markers have
                // enabled demand. This counter has no managed transition/allocation.
                a.pushfq(); a.push(rax); a.mov(rax, state); a.inc(__dword_ptr[rax + 4]); a.pop(rax); a.popfq();
            }
            if (index == 22)
            {
                // Reconstruct this entire block in both modes: its original short
                // branches land inside the owned span and cannot be replayed verbatim.
                a.imul(r14, rsi, 0x688); a.cmp(__dword_ptr[r14 + rcx + 0x34], eax);
                var valid = a.CreateLabel(); var recruit = a.CreateLabel(); var physical = a.CreateLabel(); var compared = a.CreateLabel();
                a.je(valid); a.imul(rbx, rdx, AiQuotaNativeContract.PlayerStride); a.jmp(recruit);
                a.Label(ref valid); a.pushfq(); a.push(rax); a.mov(rax, state); a.cmp(__dword_ptr[rax], 0); a.je(physical);
                a.pop(rax); a.popfq(); Bridge(a, index, callback); a.jmp(compared);
                a.Label(ref physical); a.pop(rax); a.popfq(); a.cmp(__word_ptr[r14 + rcx + 0x5C], (short)2);
                a.Label(ref compared); a.jge(image + 0x2EECC);
                a.Label(ref recruit); a.mov(r14, image + 0x7CC6720); a.AddUnrestrictedJmp(back); return;
            }
            var vanilla = a.CreateLabel();
            a.pushfq(); a.push(rax); a.mov(rax, state); a.cmp(__dword_ptr[rax], 0); a.je(vanilla);
            a.pop(rax); a.popfq();
            if (index == 1)
            {
                // R11 points at the original raw unit record. Reject ordinary units in
                // native code before entering managed code; the slot/GID mirror changes
                // synchronously with the core marker under the engine monitor.
                var skip = a.CreateLabel();
                a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx);
                a.mov(rax, image + 0x9302C4); a.mov(ecx, __dword_ptr[rax]);
                a.cmp(ecx, 1); a.jl(skip); a.cmp(ecx, 9999); a.jg(skip);
                a.mov(rax, state + 64); a.mov(edx, __dword_ptr[rax + rcx * 4]);
                a.test(edx, edx); a.je(skip); a.cmp(edx, __dword_ptr[r11 + 0x6F0]); a.jne(skip);
                a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
                Bridge(a, index, callback); Replay(a, original); a.AddUnrestrictedJmp(back);
                a.Label(ref skip); a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
                Replay(a, original); a.AddUnrestrictedJmp(back);
            }
            else
            {
                for (int i = 0; i < original.Length; i++)
                {
                    // The first span resets the fold. The second reader's CMP is
                    // preceded by real register setup, which must execute first.
                    if (index == 0 && i == 0) Bridge(a, index, callback);
                    bool replace = index == 3 ? i == 5 : index >= 2 && i == 0;
                    if (replace) Bridge(a, index, callback); else Replay(a, original.Slice(i, 1));
                }
                a.AddUnrestrictedJmp(back);
            }
            a.Label(ref vanilla); a.pop(rax); a.popfq(); Replay(a, original); a.AddUnrestrictedJmp(back);
        }
        private static void Replay(Assembler a, ReadOnlySpan<Instruction> code)
        { foreach (var decoded in code) { var instruction = decoded; instruction.IP = 0; a.AddInstruction(instruction); } }
        internal static byte[] Expected(int index, ulong image, ulong state, ulong callback, ulong address)
        {
            var site = AiQuotaNativeContract.Sites[index]; var assembler = new Assembler(64);
            ulong start = image + (uint)site.Rva, back = start + (uint)site.Bytes.Length;
            Generate(assembler, AiQuotaNativeContract.Decode(site.Bytes, start), back, index, image, state, callback);
            assembler.AddUnrestrictedJmp(back);
            using (var stream = new MemoryStream()) { assembler.Assemble(new StreamCodeWriter(stream), address); return stream.ToArray(); }
        }
    }
}

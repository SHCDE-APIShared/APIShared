using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    // Pure generator shared with native execution tests. No managed callback in the CRC loop.
    internal static class WorkerProductionStateAssembly
    {
        internal static void GenerateChecksum(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            ulong rows, ulong control, ulong unitManager)
        {
            if (original.Length != 3 || original[0].Length != 7 || original[1].Code != Code.Add_r32_rm32 || original[2].Length != 7)
                throw new InvalidOperationException("Production checksum displaced instructions differ.");
            Add(a, original[0]); Add(a, original[1]);
            var done = a.CreateLabel(); var stale = a.CreateLabel();
            a.pushfq(); a.push(rax); a.push(r8); a.push(r9); a.push(r10);
            a.mov(rax, control); a.cmp(__dword_ptr[rax + 4], 0); a.je(done);
            a.cmp(__dword_ptr[rax], (int)WorkerProductionWorldMode.Extended); a.jne(done);
            a.cmp(r12d, 1); a.jl(done); a.cmp(r12d, WorkerProductionStateStore.RowCapacity); a.jge(done);
            a.mov(rax, r12); a.shl(rax, 5); a.mov(r8, rows); a.add(r8, rax);
            a.cmp(__dword_ptr[r8], 0); a.je(done);
            a.imul(rax, r12, WorkerProductionBonusNativeDefinition.UnitStride); a.mov(r9, unitManager); a.add(rax, r9);
            a.mov(r9d, __dword_ptr[rax + 0x6F0]); a.cmp(__dword_ptr[r8], r9d); a.jne(stale);
            a.mov(r10d, __dword_ptr[r8 + 4]);
            a.movzx(r9d, __word_ptr[rax + 0x6E6]); a.cmp(__word_ptr[r8 + 4], r9w);
            var sameKind = a.CreateLabel(); a.je(sameKind);
            // Preserve per-unit credit across profession changes. Hash a normalized
            // virtual row (new kind, no old batch flags) without mutating an observation.
            a.cmp(__qword_ptr[r8 + 8], 0); a.je(done); a.mov(r10d, r9d);
            a.Label(ref sameKind);
            a.xor(edi, unchecked((int)2166136261U));
            for (int n = 0; n < WorkerProductionStateStore.RowSize; n++)
            {
                if (n >= 4 && n < 8)
                { a.mov(r9d, r10d); if (n != 4) a.shr(r9d, (byte)((n - 4) * 8)); a.and(r9d, 255); }
                else a.movzx(r9d, __byte_ptr[r8 + n]);
                a.xor(edi, r9d); a.imul(edi, edi, 16777619);
            }
            a.jmp(done);
            a.Label(ref stale); a.mov(__qword_ptr[r8], 0); a.mov(__qword_ptr[r8 + 8], 0);
            a.mov(__qword_ptr[r8 + 16], 0); a.mov(__qword_ptr[r8 + 24], 0);
            a.Label(ref done); a.pop(r10); a.pop(r9); a.pop(r8); a.pop(rax); a.popfq();
            Add(a, original[2]); a.AddUnrestrictedJmp(back);
        }
        private static void Add(Assembler a, Instruction instruction) { instruction.IP = 0; a.AddInstruction(instruction); }
    }
}

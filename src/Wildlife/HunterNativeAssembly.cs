using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Pure generators of the two Hunter stubs (see HunterNativeSites). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native bytes.
    internal static class HunterNativeAssembly
    {
        internal const int UnitCapacity = 10000;
        internal const int EntrySize = 16; // +0 Hunter global, +4 corpse global, +8 corpse type, +12 valid
        internal const int NativeGlobalOffset = 0x6F0, NativeTypeOffset = 0x6E6;

        // Query entry 0x18AF50(rcx = unit manager, edx = querying unit id): RAX, R10 and the flags are
        // free at a function entry. [rsp] is the caller's return address before the replayed pushes.
        internal static void EmitQueryEntry(Assembler a, ReadOnlySpan<Instruction> original, ulong slot)
        {
            a.mov(rax, slot);
            a.mov(__dword_ptr[rax], edx);
            a.mov(r10, __qword_ptr[rsp]);
            a.mov(__qword_ptr[rax + 8], r10);
            Economy.WorkerProductionBonusNativeAssembly.Replay(a, original);
        }

        // Corpse arrival 0x1306AE in HunterUpdate: RBX = Hunter id, RDI = Hunter id * 0x490, R14 = unit
        // manager, RCX = corpse id * 0x490, EAX = corpse global (just compared with the Hunter's target).
        // R10/R11 are saved; the flags are dead at entry because the replayed cmp sets them again.
        internal static void EmitCorpseArrival(Assembler a, ReadOnlySpan<Instruction> original, ulong demand, ulong table)
        {
            var skip = a.CreateLabel();
            a.push(r10); a.push(r11);
            a.mov(r10, demand);
            a.cmp(__byte_ptr[r10], 0); a.je(skip);
            a.cmp(ebx, 1); a.jl(skip); a.cmp(ebx, UnitCapacity - 1); a.jg(skip);
            a.movsxd(r10, ebx); a.shl(r10, 4);
            a.mov(r11, table); a.add(r10, r11);
            a.mov(r11d, __dword_ptr[rdi + r14 + NativeGlobalOffset]); a.mov(__dword_ptr[r10], r11d);
            a.mov(__dword_ptr[r10 + 4], eax);
            a.movzx(r11d, __word_ptr[rcx + r14 + NativeTypeOffset]); a.mov(__dword_ptr[r10 + 8], r11d);
            a.mov(__dword_ptr[r10 + 12], 1);
            a.Label(ref skip);
            a.pop(r11); a.pop(r10);
            Economy.WorkerProductionBonusNativeAssembly.Replay(a, original);
        }
    }
}

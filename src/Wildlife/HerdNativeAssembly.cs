using System;
using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Pure generator of the herd flight gate (see HerdRuntime.Flight). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native bytes.
    internal static class HerdNativeAssembly
    {
        // Herd records of the tribe manager (Extender GameTribeManagerAPI: 4500 slots, id 0 reserved).
        internal const int HerdCapacity = 4500;
        // Sector grid 0x3395E10 (hash FBCB9319): 80 by 80 sectors of 10 by 10 tiles, stride 0xA0.
        internal const int SectorSize = 10, SectorsPerSide = 80, SectorStride = 0xA0;
        // +0x28 lions, hyenas and crocodiles; +0x48 non-animals of every player and deer and goats of fleeing
        // herds (filled by 0x126810). The deer and goat updates read the sum of both as their threat.
        internal const int SectorPredatorsOffset = 0x28, SectorUnitsOffset = 0x48;

        // Deer (0x124F20) and goat (0x125330) update, 18 bytes, both byte-identical:
        //   mov edi,[rax+rcx+0x28]; add edi,[rax+rcx+0x48]; mov r15d,[rax+rcx+0x40]; mov r12d,[rax+rcx+0x44]
        // RBP is the herd id (movsxd rbp, edx at the function entry, below 0x1194 by the dispatcher 0x117520).
        // EDI is read once afterwards (0x124F71: not zero starts flight) and written again before any other
        // read. RAX and RCX are written again on both paths behind the span before they are read; the flags
        // are dead (mov, then cmp edx). A herd whose byte in calmHerds is set sees no threat.
        internal static void EmitFlightGate(Assembler a, ReadOnlySpan<Instruction> original, ulong calmHerds)
        {
            if (original.Length != 4) throw new InvalidOperationException("The herd threat read must be four instructions.");
            Label done = a.CreateLabel("herdFlightDone");
            Economy.WorkerProductionBonusNativeAssembly.Replay(a, original);
            a.mov(rax, calmHerds);
            a.cmp(__byte_ptr[rax + rbp], 0);
            a.je(done);
            a.xor(edi, edi);
            a.Label(ref done);
            a.nop();
        }
    }
}

using System;
using Iced.Intel;
using RedBird.X64.Extensions;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Buildings
{
    // Pure generator of the build-menu availability wrapper (see BuildMenuAvailabilityRuntime). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native function.
    internal static class BuildMenuAvailabilityAssembly
    {
        // One decision byte per mapper ID 0..511 (eMappers ends at 440).
        internal const int MapperCapacity = 512;
        internal const byte Allow = 1, Veto = 2;
        // The final branch of 0xFFC70 (0xFFF92): mappers below 0x1B8 read the mission's signed availability
        // word at record + 0x22C + mapper * 2.
        internal const int MissionTableMappers = 0x1B8, MissionTableOffset = 0x22C;
        private const int Frame = 0x38;

        // Entry of uint 0xFFC70(record rcx, mapper edx), a leaf without stack use whose result the callers
        // test for > 0 (managed EngineInterface.IsMapperAvailable) or != 0 (0xC2030, 0x19D960). The displaced
        // entry (lea eax,[rdx-0xB2]; mov r8,rcx; cmp eax,1; jbe 0xFFC92) only reads RCX/RDX. At an entry RAX,
        // R8 and R10 are free and no flag is live. Decision 0 (and every mapper outside the table) runs the
        // original function unchanged. Veto returns 0 without running Vanilla, which has no side effects.
        // Allow calls a copy of the original entry; a result above 0 stays, otherwise the result is 1 unless
        // the mission's own availability word for that mapper is 0 or below.
        internal static void EmitEntry(Assembler a, ReadOnlySpan<Instruction> original, ulong back, ulong table)
        {
            if (original.Length != 4) throw new InvalidOperationException("The mapper availability entry must be four instructions.");
            Label vanilla = a.CreateLabel("mapperVanilla"), veto = a.CreateLabel("mapperVeto"), copy = a.CreateLabel("mapperCopy");
            Label done = a.CreateLabel("mapperDone"), force = a.CreateLabel("mapperForce");
            a.mov(r10d, edx);
            a.cmp(r10d, MapperCapacity);
            a.jae(vanilla);
            a.mov(rax, table);
            a.movzx(eax, __byte_ptr[rax + r10]);
            a.test(eax, eax);
            a.je(vanilla);
            a.cmp(eax, Veto);
            a.je(veto);
            // Entry RSP is 8 mod 16, so the call into the copy sees the alignment of an ordinary call.
            a.sub(rsp, Frame);
            a.mov(__qword_ptr[rsp + 0x20], rcx);
            a.mov(__qword_ptr[rsp + 0x28], r10);
            a.call(copy);
            a.test(eax, eax);
            a.jg(done);
            a.mov(r10, __qword_ptr[rsp + 0x28]);
            a.cmp(r10d, MissionTableMappers);
            a.jae(force);
            a.mov(rcx, __qword_ptr[rsp + 0x20]);
            a.movsx(r8d, __word_ptr[rcx + r10 * 2 + MissionTableOffset]);
            a.test(r8d, r8d);
            a.jle(done);
            a.Label(ref force);
            a.mov(eax, 1);
            a.Label(ref done);
            a.add(rsp, Frame);
            a.ret();
            a.Label(ref veto);
            a.xor(eax, eax);
            a.ret();
            a.Label(ref copy);
            Replay(a, original);
            a.AddUnrestrictedJmp(back);
            a.Label(ref vanilla);
            // The backend appends its own continuation jump to back after this replay.
            Replay(a, original);
        }

        private static void Replay(Assembler a, ReadOnlySpan<Instruction> original)
        {
            foreach (var decoded in original)
            {
                var instruction = decoded;
                instruction.IP = 0;
                a.AddInstruction(instruction);
            }
        }
    }
}

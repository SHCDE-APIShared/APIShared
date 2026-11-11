using System;
using Iced.Intel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Wildlife
{
    // Pure generators of the Hunter stubs (see HunterNativeSites and HunterOwnerGates). Executed by
    // tests/WorkerProductionBonus.NativeTests against the audited native bytes. Native field offsets are
    // relative to manager + id * 0x490 (public GameUnit + 0x65C).
    internal static class HunterNativeAssembly
    {
        internal const int UnitCapacity = 10000;
        internal const int TypeCount = ushort.MaxValue + 1;
        internal const int EntrySize = 16; // +0 Hunter global, +4 corpse global, +8 corpse type, +12 valid
        internal const int NativeGlobalOffset = 0x6F0, NativeTypeOffset = 0x6E6, NativeAliveOffset = 0x6E4, NativeOwnerOffset = 0x6EE;
        internal const int NativeDeadOffset = 0x8F8, NativeHeightOffset = 0x712, NativeStandingHeightOffset = 0x714;
        internal const byte RemoveCorpseFlag = 1;
        // The query walks RBX over unit field +0x8F8 (public +0x29C), so these are field deltas.
        private const int QueryCandidateOwnerDelta = NativeOwnerOffset - NativeDeadOffset;
        private const int QueryCandidateTypeDelta = NativeTypeOffset - NativeDeadOffset;

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
        // A type flagged in corpseFlags is marked for deletion (alive state 3), Vanilla's own end path: the
        // unit pass then removes it through 0x1869C0. The butchering Hunter (state 2) never reads it again.
        internal static void EmitCorpseArrival(Assembler a, ReadOnlySpan<Instruction> original, ulong demand, ulong table, ulong corpseFlags)
        {
            var keep = a.CreateLabel();
            var skip = a.CreateLabel();
            a.push(r10); a.push(r11);
            a.movzx(r11d, __word_ptr[rcx + r14 + NativeTypeOffset]);
            a.mov(r10, corpseFlags);
            a.test(__byte_ptr[r10 + r11], RemoveCorpseFlag); a.je(keep);
            a.mov(__word_ptr[rcx + r14 + NativeAliveOffset], (short)AliveState.MarkedForDeletion);
            a.Label(ref keep);
            a.mov(r10, demand);
            a.cmp(__byte_ptr[r10], 0); a.je(skip);
            a.cmp(ebx, 1); a.jl(skip); a.cmp(ebx, UnitCapacity - 1); a.jg(skip);
            a.movsxd(r10, ebx); a.shl(r10, 4);
            a.push(r11); // the corpse type
            a.mov(r11, table); a.add(r10, r11);
            a.mov(r11d, __dword_ptr[rdi + r14 + NativeGlobalOffset]); a.mov(__dword_ptr[r10], r11d);
            a.mov(__dword_ptr[r10 + 4], eax);
            a.pop(r11); a.mov(__dword_ptr[r10 + 8], r11d);
            a.mov(__dword_ptr[r10 + 12], 1);
            a.Label(ref skip);
            a.pop(r11); a.pop(r10);
            Economy.WorkerProductionBonusNativeAssembly.Replay(a, original);
        }

        // Query owner check [0x18AFD8,0x18AFE6): cmp word [rbx-0x20A],0; jne reject. An owned candidate of
        // a type flagged in ownTypes passes when it belongs to the querying Hunter's player ([r13+0x6EE]).
        // Free: RAX, R8-R11, RDI; flags dead. Owner equality, not team: allied animals stay refused.
        internal static void EmitOwnerQuery(Assembler a, ReadOnlySpan<Instruction> original, ulong ownTypes, ulong reject)
        {
            Require(original, 2, "Hunter query owner check");
            var pass = a.CreateLabel();
            var refuse = a.CreateLabel();
            a.AddInstruction(Clean(original[0]));
            a.je(pass);
            a.movzx(eax, __word_ptr[rbx + QueryCandidateTypeDelta]);
            a.mov(r11, ownTypes);
            a.cmp(__byte_ptr[r11 + rax], 0); a.je(refuse);
            a.movzx(eax, __word_ptr[rbx + QueryCandidateOwnerDelta]);
            a.cmp(__word_ptr[r13 + NativeOwnerOffset], ax); a.je(pass);
            a.Label(ref refuse);
            a.mov(rax, reject); a.jmp(rax);
            a.Label(ref pass);
            a.nop();
        }

        // Explicit attack team check [0x18EBDA,0x18EBE8): cmp team[target],team[attacker]; je reject. Same
        // team passes only for a Hunter (R12D) attacking a flagged type (target at R8+RBP) owned by his own
        // player (RCX target owner == R13 attacker owner). Free: RAX, R11; flags dead.
        internal static void EmitOwnerAttack(Assembler a, ReadOnlySpan<Instruction> original, ulong ownTypes, ulong reject)
        {
            Require(original, 2, "explicit attack team check");
            var pass = a.CreateLabel();
            var refuse = a.CreateLabel();
            a.AddInstruction(Clean(original[0]));
            a.jne(pass);
            a.cmp(r12d, (int)eChimps.CHIMP_TYPE_HUNTER); a.jne(refuse);
            a.cmp(rcx, r13); a.jne(refuse);
            a.movzx(eax, __word_ptr[r8 + rbp + NativeTypeOffset]);
            a.mov(r11, ownTypes);
            a.cmp(__byte_ptr[r11 + rax], 0); a.jne(pass);
            a.Label(ref refuse);
            a.mov(rax, reject); a.jmp(rax);
            a.Label(ref pass);
            a.nop();
        }

        // Arrow hit team check [0x192AA7,0x192AB5): cmp team..., eax; movzx r11d,[rsp+0x78]; the JNE behind
        // reads ZF. Same team continues as a hit only for a Hunter's arrow (type 1, source unit a Hunter) on a
        // flagged type (R12 victim type) of the same player (R8W projectile owner == R10W victim owner). The
        // hit then repeats Vanilla's dead and height gates of 0x192B00 and continues behind the "under
        // attack" warning (0x192B93), which equal owners must not trigger; the skipped attacker record is a
        // no-op for equal owners. R11 holds the image base until the replayed movzx; RDI is saved around
        // the table read (POP keeps the flags). Every other case restores ZF = 1 for Vanilla's JNE.
        internal static void EmitOwnerArrow(Assembler a, ReadOnlySpan<Instruction> original, ulong ownTypes,
            ulong continueHit, ulong miss, int projectileSource, int projectileHeight)
        {
            Require(original, 2, "arrow team check");
            var sameTeam = a.CreateLabel();
            var resume = a.CreateLabel();
            var missed = a.CreateLabel();
            a.AddInstruction(Clean(original[0]));
            a.jne(resume);
            a.cmp(cx, 1); a.jne(sameTeam);
            a.cmp(r8w, r10w); a.jne(sameTeam);
            a.push(rdi);
            a.mov(rdi, ownTypes);
            a.movzx(eax, r12w);
            a.cmp(__byte_ptr[rdi + rax], 0);
            a.pop(rdi);
            a.je(sameTeam);
            a.movsx(rax, __word_ptr[r11 + r13 + projectileSource]);
            a.imul(rax, rax, 0x490);
            a.cmp(__word_ptr[r15 + rax + NativeTypeOffset], (int)eChimps.CHIMP_TYPE_HUNTER); a.jne(sameTeam);

            a.cmp(__word_ptr[rsi + r15 + NativeDeadOffset], 0); a.jne(missed);
            a.movsx(eax, __word_ptr[rsi + r15 + NativeHeightOffset]);
            a.movsx(r9d, __word_ptr[rsi + r15 + NativeStandingHeightOffset]);
            a.add(r9d, eax);
            a.movsx(ecx, __word_ptr[r11 + r13 + projectileHeight]);
            a.cmp(r9d, ecx); a.jg(missed);
            a.lea(eax, __[r9 + rdx]);
            a.cmp(eax, ecx); a.jl(missed);
            a.AddInstruction(Clean(original[1]));
            a.mov(rax, continueHit); a.jmp(rax);

            a.Label(ref missed);
            a.mov(rax, miss); a.jmp(rax);

            a.Label(ref sameTeam);
            a.cmp(rax, rax);
            a.Label(ref resume);
            a.AddInstruction(Clean(original[1]));
        }

        private static void Require(ReadOnlySpan<Instruction> original, int count, string site)
        {
            if (original.Length != count)
                throw new InvalidOperationException("Unexpected displaced instructions at the " + site + ": " + original.Length + ".");
        }

        // A fresh block-encoder identity for a replayed instruction (none of these is RIP-relative).
        private static Instruction Clean(Instruction instruction) { instruction.IP = 0; return instruction; }
    }
}

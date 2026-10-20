using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared
{
    internal static class AssassinEndpointEmitter
    {
        internal static void Emit(Assembler assembler, ReadOnlySpan<Instruction> original,
            ulong module, ulong flagsAddress, int site)
        {
            if (site < 0 || site > 3 || original.Length < 2 ||
                original[0].Mnemonic != Mnemonic.Jne)
                throw new InvalidOperationException("Unexpected Assassin endpoint instructions.");
            Instruction[] replay = original.CloneInstructionsWithoutIP();
            Label vanilla = assembler.CreateLabel("vanilla");
            Label accept = assembler.CreateLabel("accept");
            Label reject = assembler.CreateLabel("reject");
            Label done = assembler.CreateLabel("done");
            assembler.pushfq();
            assembler.push(rax);
            assembler.push(r10);
            assembler.push(r11);
            assembler.mov(r11, flagsAddress);
            assembler.mov(r10d, __dword_ptr[r11]);
            assembler.test(r10d, site >= 2 ? 3 : 2);
            assembler.je(vanilla);
            // Weighted reconstruction already used this broad skip. Search remains gate-specific.
            if (site >= 2)
            {
                assembler.test(r10d, 1);
                assembler.jne(accept);
            }
            // Native start/neighbor tile registers, audited independently at each site.
            assembler.mov(r10, site == 1 ? r13 : site == 3 ? rcx : r8);
            assembler.cmp(r10, AssassinPathAPI.TileCount);
            assembler.jae(reject);
            assembler.mov(r11, module + AssassinPathAPI.BuildingGridRva);
            assembler.movzx(eax, __word_ptr[r11 + r10 * 2]);
            assembler.test(eax, eax);
            assembler.je(accept);
            assembler.cmp(eax, 3999);
            assembler.ja(reject);
            assembler.mov(r11, module + AssassinPathAPI.TileFlagsRva);
            assembler.test(__dword_ptr[r11 + r10 * 4], (int)TilePropertyFlag.IsWall);
            assembler.je(reject);
            assembler.imul(eax, eax, AssassinPathAPI.BuildingStride);
            assembler.mov(r11, module + AssassinPathAPI.BuildingAliveBaseRva);
            assembler.cmp(__word_ptr[r11 + rax], (int)AliveState.None);
            assembler.je(reject);
            assembler.cmp(__word_ptr[r11 + rax], (int)AliveState.MarkedForDeletion);
            assembler.je(reject);
            assembler.mov(r11, module + AssassinPathAPI.BuildingTypeBaseRva);
            assembler.movzx(eax, __word_ptr[r11 + rax]);
            assembler.cmp(eax, (int)eStructs.STRUCT_GATE_MAIN);
            assembler.je(accept);
            assembler.cmp(eax, (int)eStructs.STRUCT_GATE_INNER);
            assembler.jne(reject);
            assembler.Label(ref accept);
            Restore(assembler);
            // Drop only the building rejection branch. Every subsequent wall test is replayed.
            for (int index = 1; index < replay.Length; index++) assembler.AddInstruction(replay[index]);
            assembler.jmp(done);
            assembler.Label(ref reject);
            Restore(assembler);
            assembler.jmp(original[0].NearBranchTarget);
            assembler.Label(ref vanilla);
            Restore(assembler);
            foreach (Instruction instruction in replay) assembler.AddInstruction(instruction);
            assembler.Label(ref done);
            assembler.nop();
        }

        /// <summary>
        /// E1640 mode 3 (Assassin reconstruction, E19C0) walks target -> start and accepts an ordinary
        /// step only by the current tile's bit toward the next tile. D9C40 built the same edge from the
        /// start side (D9EA6: the nearer tile's bit toward this one), and the physical step DCE60 accepts
        /// either endpoint's bit. Across a one-way connection (an own gatehouse exit onto its drawbridge)
        /// every reconstruction therefore failed and ran E1640's full-map repair (DAA50, E49D0, C3E50,
        /// DE6A0) once per unit. While policy flag 1 or 2 is set, the neighbor's bit back toward the
        /// current tile is accepted as well. Stamp and distance checks at E1A1F stay unchanged.
        /// </summary>
        internal static void EmitReverseConnection(Assembler assembler, ReadOnlySpan<Instruction> original,
            ulong module, ulong flagsAddress)
        {
            // test byte [r14+r15+312620], al / jne E1A1F / test r12b,1 (flags for the jne at E19CE).
            if (original.Length != 3 || original[0].Mnemonic != Mnemonic.Test || original[1].Mnemonic != Mnemonic.Jne ||
                original[2].Mnemonic != Mnemonic.Test || original[2].Op0Register != Register.R12L)
                throw new InvalidOperationException("Unexpected Assassin reconstruction instructions.");
            Instruction[] replay = original.CloneInstructionsWithoutIP();
            ulong acceptTarget = original[1].NearBranchTarget;
            Label accept = assembler.CreateLabel("accept");
            Label restore = assembler.CreateLabel("restore");
            Label tail = assembler.CreateLabel("tail");
            assembler.AddInstruction(replay[0]);
            assembler.jne(accept);
            // Live here: al (current connections), rdx (row y), r8d (current tile), r14 (direction),
            // r9d/r10d/r11w/r12d/r13d/rdi and r15 (image base). Only rax/rcx/rdx are used, all restored.
            assembler.push(rax);
            assembler.push(rcx);
            assembler.push(rdx);
            assembler.mov(rcx, flagsAddress);
            assembler.test(__dword_ptr[rcx], 3);
            assembler.je(restore);
            assembler.lea(rax, __qword_ptr[r14 + rdx * 8]);
            assembler.mov(rcx, module + AssassinPathNativeDefinition.NeighborOffsetsRva);
            assembler.mov(eax, __dword_ptr[rcx + rax * 4]);
            assembler.add(eax, r8d);
            assembler.cmp(eax, AssassinPathAPI.TileCount);
            assembler.jae(restore);
            assembler.mov(rcx, module + AssassinPathNativeDefinition.ConnectionsRva);
            assembler.movzx(eax, __byte_ptr[rcx + rax]);
            assembler.lea(rcx, __qword_ptr[r14 + 4]);
            assembler.and(ecx, 7);
            assembler.mov(rdx, module + AssassinPathNativeDefinition.DirectionMasksRva);
            assembler.test(__byte_ptr[rdx + rcx], al);
            // pop leaves the flags of this test intact.
            assembler.pop(rdx);
            assembler.pop(rcx);
            assembler.pop(rax);
            assembler.jne(accept);
            assembler.jmp(tail);
            assembler.Label(ref restore);
            assembler.pop(rdx);
            assembler.pop(rcx);
            assembler.pop(rax);
            assembler.jmp(tail);
            assembler.Label(ref accept);
            assembler.jmp(acceptTarget);
            assembler.Label(ref tail);
            assembler.AddInstruction(replay[2]);
        }

        private static void Restore(Assembler assembler)
        {
            assembler.pop(r11);
            assembler.pop(r10);
            assembler.pop(rax);
            assembler.popfq();
        }
    }
}

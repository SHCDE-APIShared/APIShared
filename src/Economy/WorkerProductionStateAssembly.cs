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
            ulong rows, ulong control, ulong unitManager) =>
            GenerateChecksum(a, original, back, rows, control, unitManager, 0, 0, 0);

        internal static void GenerateChecksum(Assembler a, ReadOnlySpan<Instruction> original, ulong back,
            ulong rows, ulong control, ulong unitManager, ulong weaponRows, ulong weaponDemand, ulong weaponKinds)
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
            if (weaponRows != 0 && weaponDemand != 0 && weaponKinds != 0)
                FoldWeaponRow(a, r12, edi, weaponRows, weaponDemand, 20, 10000,
                    unitManager, WorkerProductionBonusNativeDefinition.UnitStride, 0x6F0, weaponKinds, 0x6E6);
            Add(a, original[2]); a.AddUnrestrictedJmp(back);
        }

        internal static void GenerateBuildingChecksum(Assembler a, ReadOnlySpan<Instruction> original,
            ulong back, ulong rows, ulong demand, ulong buildingManager)
        {
            if (original.Length != 3 || original[0].Length != 7 ||
                original[1].Code != Code.Add_r32_rm32 || original[2].Length != 7)
                throw new InvalidOperationException("Weapon workshop building checksum instructions differ.");

            Add(a, original[0]); Add(a, original[1]);
            FoldWeaponRow(a, rbp, edx, rows, demand, 16, 4000, buildingManager, 0x32C, 0x134, 0, 0);
            Add(a, original[2]); a.AddUnrestrictedJmp(back);
        }

        // The canonical wire row begins with its one-based slot and native GID. An empty
        // or recycled row contributes nothing. Preserve Vanilla flags and scratch registers.
        private static void FoldWeaponRow(Assembler a, AssemblerRegister64 slot,
            AssemblerRegister32 accumulator, ulong rows, ulong demand, int rowSize, int capacity,
            ulong manager, int nativeStride, int nativeGlobalIdOffset, ulong expectedKinds, int nativeKindOffset)
        {
            var done = a.CreateLabel();
            var restore = a.CreateLabel();
            a.pushfq(); a.push(rax);
            a.mov(rax, demand); a.cmp(__byte_ptr[rax], 0); a.je(restore);
            a.cmp(slot, 1); a.jl(restore); a.cmp(slot, capacity); a.jge(restore);
            a.push(r8); a.push(r9); a.push(r10);
            a.imul(rax, slot, rowSize); a.mov(r8, rows); a.add(r8, rax);
            a.movzx(r10d, __word_ptr[r8]); a.cmp(r10, slot); a.jne(done);
            a.mov(r10d, __dword_ptr[r8 + 2]); a.test(r10d, r10d); a.je(done);
            a.imul(rax, slot, nativeStride); a.mov(r9, manager); a.add(rax, r9);
            a.cmp(r10d, __dword_ptr[rax + nativeGlobalIdOffset]); a.jne(done);
            if (expectedKinds != 0)
            {
                a.mov(r9, expectedKinds); a.movzx(r10d, __word_ptr[r9 + slot * 2]);
                a.cmp(r10w, __word_ptr[rax + nativeKindOffset]); a.jne(done);
            }
            a.xor(accumulator, unchecked((int)2166136261U));
            for (int index = 0; index < rowSize; index++)
            {
                a.movzx(r9d, __byte_ptr[r8 + index]);
                a.xor(accumulator, r9d); a.imul(accumulator, accumulator, 16777619);
            }
            a.Label(ref done); a.pop(r10); a.pop(r9); a.pop(r8);
            a.Label(ref restore); a.pop(rax); a.popfq();
        }

        private static void Add(Assembler a, Instruction instruction) { instruction.IP = 0; a.AddInstruction(instruction); }
    }
}

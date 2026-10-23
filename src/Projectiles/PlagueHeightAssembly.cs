using System;
using System.IO;
using Iced.Intel;
using RedBird.X64.Extensions;
using SHCDESE.Interop.Enums;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared
{
    // All offsets below are hash-bound native machine contracts, not publicized members.
    internal static class PlagueHeightAssembly
    {
        internal const int Tolerance = 20, VisualOffset = 20;
        internal const int ProjectileBase = 0x2F7A680, HeightGrid = 0x4DDD350;
        internal const int PropertiesGrid = 0x48F71B0, BuildingGrid = 0x4B6AA50;
        internal const int BuildingManager = 0x64CCBB0, BuildingHeight = 0x148;
        internal static readonly int[] Sites = { 0x9FDB8, 0x192809, 0x9CB6F, 0x9CC3D };
        internal static readonly string[] OriginalHex = {
            "4863C24869C8E800000048634419600FB6843050D3DD046683C0146689441950",
            "4A6384062C8B7E0641F78480B0718F04000100100F85AD010000",
            "418BCC2BC88BC19933C22BC283F8787D1D6642837C2B3C027514",
            "418BCC2BC88BC19933C22BC283F8787D1C6642837C333C027513"
        };

        // Windows x64 leaf-style helper: ECX=tile, EAX=standing plane or -1.
        // C07C0 changes only RAX/RCX/RDX and flags; no SIMD state is touched.
        internal static void GenerateSurface(Assembler a, ulong module, int tiles, int buildings)
        {
            var invalid = a.CreateLabel(); var done = a.CreateLabel();
            var stairs = a.CreateLabel(); var wall = a.CreateLabel(); var building = a.CreateLabel();
            a.push(rbx); a.sub(rsp, 32); // alignment and shadow space for native C07C0
            a.cmp(ecx, tiles); a.jae(invalid);
            a.mov(r10, module);
            a.movzx(ebx, __byte_ptr[r10 + rcx + HeightGrid]);
            a.mov(r8d, __dword_ptr[r10 + rcx * 4 + PropertiesGrid]);
            a.test(r8d, (int)TilePropertyFlag.IsStairs); a.jnz(stairs);
            a.test(r8d, (int)TilePropertyFlag.IsWall); a.jnz(wall);
            a.test(r8d, (int)TilePropertyFlag.IsBuilding); a.jnz(building);
            a.test(r8d, (int)TilePropertyFlag.IsElevated); a.jz(done);
            a.movsx(edx, __word_ptr[r10 + rcx * 2 + BuildingGrid]);
            a.cmp(edx, 1); a.jl(invalid); a.cmp(edx, buildings); a.jg(invalid);
            a.imul(rax, rdx, 0x32C);
            a.movzx(ebx, __word_ptr[r10 + rax + BuildingManager + BuildingHeight]);
            a.lea(rcx, __[r10 + BuildingManager]);
            a.AddUnrestrictedCall(module + 0xC07C0);
            a.add(ebx, eax); a.jmp(done);
            a.Label(ref wall);
            a.movsx(edx, __word_ptr[r10 + rcx * 2 + BuildingGrid]);
            a.test(edx, edx); a.jz(done);
            a.cmp(edx, 1); a.jl(invalid); a.cmp(edx, buildings); a.jg(invalid);
            a.add(ebx, 40); a.jmp(done);
            a.Label(ref stairs); a.add(ebx, 12); a.jmp(done);
            a.Label(ref building); a.add(ebx, 2); a.jmp(done);
            a.Label(ref invalid); a.mov(ebx, -1);
            a.Label(ref done); a.mov(eax, ebx); a.add(rsp, 32); a.pop(rbx); a.ret();
        }

        internal static void GenerateHook(Assembler a, ReadOnlySpan<Instruction> original,
            ulong back, int index, ulong module, ulong state, ulong surface)
        {
            if (back != module + (uint)Sites[index] + (uint)(index == 0 ? 32 : 26))
                throw new InvalidOperationException("Unexpected hook continuation.");
            var vanilla = a.CreateLabel();
            a.pushfq(); a.push(rax); a.mov(rax, state); a.cmp(__qword_ptr[rax + 64], 0);
            a.je(vanilla); a.pop(rax); a.popfq();
            if (index == 0) GenerateUpdate(a, original, back, state, surface, ref vanilla);
            else if (index == 1) GenerateDamage(a, original, back, module, state, surface);
            else
            {
                // Keep Vanilla's arithmetic/register outputs and its alive gate,
                // but leave the final height decision to the disease damage branch.
                for (int i = 0; i < 6; i++) Replay(a, original[i]);
                Replay(a, original[8]); Replay(a, original[9]);
                a.AddUnrestrictedJmp(back);
            }
            a.Label(ref vanilla); a.pop(rax); a.popfq();
            foreach (var i in original) Replay(a, i);
            a.AddUnrestrictedJmp(back);
        }

        private static void Save(Assembler a)
        {
            a.pushfq(); a.push(rax); a.push(rcx); a.push(rdx);
            a.push(r8); a.push(r9); a.push(r10); a.push(r11); a.sub(rsp, 32);
        }
        private static void Restore(Assembler a)
        {
            a.add(rsp, 32); a.pop(r11); a.pop(r10); a.pop(r9); a.pop(r8);
            a.pop(rdx); a.pop(rcx); a.pop(rax); a.popfq();
        }
        private static void GenerateUpdate(Assembler a, ReadOnlySpan<Instruction> original,
            ulong back, ulong state, ulong surface, ref Label vanilla)
        {
            var fallback = a.CreateLabel();
            Save(a);
            a.movsxd(rax, edx); a.imul(rax, rax, 0xE8);
            a.mov(ecx, __dword_ptr[rbx + rax + 0x60]);
            a.AddUnrestrictedCall(surface); a.test(eax, eax); a.js(fallback);
            a.add(eax, VisualOffset);
            // Saved EDX still carries the slot. Never reuse a clobbered pointer/index.
            a.movsxd(rdx, __dword_ptr[rsp + 64]); a.imul(rcx, rdx, 0xE8);
            a.mov(__word_ptr[rbx + rcx + 0x50], ax);
            a.mov(__qword_ptr[rsp + 80], rax); a.mov(__qword_ptr[rsp + 72], rcx);
            a.mov(r11, state); a.mov(__qword_ptr[r11 + 32], 1);
            Restore(a); a.AddUnrestrictedJmp(back);
            a.Label(ref fallback); a.mov(r11, state); a.mov(__qword_ptr[r11 + 56], 1); Restore(a);
            foreach (var i in original) Replay(a, i);
            a.AddUnrestrictedJmp(back);
        }
        private static void GenerateDamage(Assembler a, ReadOnlySpan<Instruction> original,
            ulong back, ulong module, ulong state, ulong surface)
        {
            var reject = a.CreateLabel(); var invalid = a.CreateLabel(); var vanilla = a.CreateLabel();
            Save(a);
            a.mov(rax, module + ProjectileBase);
            a.mov(ecx, __dword_ptr[rax + r13 + 0x60]);
            a.mov(__dword_ptr[rsp], ecx); // private shadow-space scratch; native helper does not write it
            a.AddUnrestrictedCall(surface);
            a.test(eax, eax); a.js(invalid); a.mov(r8d, eax);
            // Classify the target, never the source cloud. Building identity wins over wall flags.
            a.mov(ecx, __dword_ptr[rsi + r15 + 0x72C]);
            a.cmp(ecx, 320800); a.jae(invalid);
            a.mov(r10, module); a.mov(r11, state); a.mov(r9, __qword_ptr[r11 + 64]);
            a.movsx(edx, __word_ptr[r10 + rcx * 2 + BuildingGrid]);
            var surfaceFlags = a.CreateLabel(); var ground = a.CreateLabel(); var chosen = a.CreateLabel();
            var categories = new[] { a.CreateLabel(), a.CreateLabel(), a.CreateLabel(), a.CreateLabel() };
            a.test(edx, edx); a.jz(surfaceFlags); a.cmp(edx, 1); a.jl(invalid);
            a.cmp(edx, 3999); a.jg(invalid); a.imul(rdx, rdx, 0x32C);
            a.movzx(edx, __word_ptr[r10 + rdx + BuildingManager + 0x12E]);
            foreach (var entry in BuildingCategories)
            { a.cmp(edx, (int)entry.Type); a.je(categories[entry.Lane]); }
            a.Label(ref surfaceFlags);
            a.mov(edx, __dword_ptr[r10 + rcx * 4 + PropertiesGrid]);
            a.test(edx, (int)(TilePropertyFlag.IsWall | TilePropertyFlag.IsStairs)); a.jnz(categories[0]);
            // Unknown elevated surfaces must retain their original exclusions.
            a.test(edx, (int)TilePropertyFlag.IsElevated); a.jnz(vanilla); a.jmp(ground);
            for (int lane = 0; lane < 4; lane++)
            {
                a.Label(ref categories[lane]); a.mov(rdx, r9);
                if (lane != 0) a.shr(rdx, (byte)(lane * 16));
                a.and(edx, 0xFFFF); a.jmp(chosen);
            }
            a.Label(ref ground); a.mov(edx, r9d); a.and(edx, 0xFFFF);
            for (int lane = 1; lane < 4; lane++)
            { a.shr(r9, 16); a.mov(eax, r9d); a.and(eax, 0xFFFF); a.cmp(edx, eax); a.cmovb(edx, eax); }
            a.Label(ref chosen); a.dec(edx); a.js(vanilla);
            a.movsx(ecx, __word_ptr[rsi + r15 + 0x712]);
            a.movsx(eax, __word_ptr[rsi + r15 + 0x714]); a.add(ecx, eax); a.sub(ecx, r8d);
            a.neg(edx); a.cmp(ecx, edx); a.jl(reject);
            a.neg(edx); a.cmp(ecx, edx); a.jg(reject);
            RecordComparison(a, state, true); Restore(a);
            a.AddUnrestrictedJmp(module + 0x192861);
            a.Label(ref invalid); a.mov(r11, state); a.mov(__qword_ptr[r11 + 56], 1);
            Restore(a); a.AddUnrestrictedJmp(module + 0x1929D0);
            a.Label(ref reject); RecordComparison(a, state, false); Restore(a);
            a.AddUnrestrictedJmp(module + 0x1929D0);
            a.Label(ref vanilla); Restore(a);
            foreach (var instruction in original) Replay(a, instruction);
            a.AddUnrestrictedJmp(back);
        }

        // Only capture the first reached comparison; no unbounded diagnostic counter writes.
        private static void RecordComparison(Assembler a, ulong state, bool accepted)
        {
            var done = a.CreateLabel(); a.mov(r11, state);
            a.cmp(__qword_ptr[r11 + 8], 0); a.jne(done);
            a.mov(edx, __dword_ptr[rsp]); a.mov(__dword_ptr[r11 + 40], edx);
            a.mov(__dword_ptr[r11 + 44], r8d); a.add(ecx, r8d); a.mov(__dword_ptr[r11 + 48], ecx);
            a.mov(__qword_ptr[r11 + (accepted ? 16 : 24)], 1); a.mov(__qword_ptr[r11 + 8], 1);
            a.Label(ref done); a.nop();
        }

        // Actual type identity takes precedence; editor prototypes and collapsed variants are
        // included so a still-linked platform cannot silently become a different category.
        internal static readonly (eStructs Type, int Lane)[] BuildingCategories = {
            (eStructs.STRUCT_KEEP_ONE,3), (eStructs.STRUCT_KEEP_TWO,3), (eStructs.STRUCT_KEEP_THREE,3),
            (eStructs.STRUCT_KEEP_FOUR,3), (eStructs.STRUCT_KEEP_FIVE,3),
            (eStructs.STRUCT_KEEPDOOR_LEFT,3), (eStructs.STRUCT_KEEPDOOR_RIGHT,3), (eStructs.STRUCT_KEEPDOOR,3),
            (eStructs.STRUCT_GATE_MAIN,1), (eStructs.STRUCT_GATE_INNER,1), (eStructs.STRUCT_GATE_WOOD,1),
            (eStructs.STRUCT_GATE_POSTERN,1), (eStructs.STRUCT_GATEHOUSE,1),
            (eStructs.STRUCT_GATE_WOOD1A,1), (eStructs.STRUCT_GATE_WOOD1B,1),
            (eStructs.STRUCT_GATE_WOOD1C,1), (eStructs.STRUCT_GATE_WOOD1D,1),
            (eStructs.STRUCT_GATE_STONE1A,1), (eStructs.STRUCT_GATE_STONE1B,1),
            (eStructs.STRUCT_GATE_STONE2A,1), (eStructs.STRUCT_GATE_STONE2B,1),
            (eStructs.STRUCT_TOWER,2), (eStructs.STRUCT_TOWER1,2), (eStructs.STRUCT_TOWER2,2),
            (eStructs.STRUCT_TOWER3,2), (eStructs.STRUCT_TOWER4,2), (eStructs.STRUCT_TOWER5,2),
            (eStructs.STRUCT_TOWER5_DESTROYED,2), (eStructs.STRUCT_TOWER1_DESTROYED,2),
            (eStructs.STRUCT_TOWER2_DESTROYED,2), (eStructs.STRUCT_TOWER3_DESTROYED,2),
            (eStructs.STRUCT_TOWER4_DESTROYED,2)
        };

        internal static byte[] Assemble(Assembler a, ulong address)
        {
            using (var stream = new MemoryStream())
            {
                if (!a.TryAssemble(new StreamCodeWriter(stream), address, out string error, out AssemblerResult result))
                    throw new InvalidOperationException("Iced assembly failed: " + error);
                return stream.ToArray();
            }
        }
        private static void Replay(Assembler a, Instruction instruction)
        {
            // Two logical paths replay the same block. Its branches all leave the
            // span, so suppress duplicate BlockEncoder identity while retaining targets.
            instruction.IP = 0;
            a.AddInstruction(instruction);
        }
        internal static Instruction[] Decode(byte[] bytes, ulong address)
        {
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes)); decoder.IP = address;
            var result = new System.Collections.Generic.List<Instruction>();
            while (decoder.IP < address + (uint)bytes.Length) { var i = decoder.Decode(); if (i.IsInvalid) throw new InvalidOperationException("Invalid native instruction."); result.Add(i); }
            return result.ToArray();
        }
    }
}

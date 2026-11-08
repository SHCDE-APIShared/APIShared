using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Iced.Intel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Compatibility
{
    internal static partial class FixesWheatAnalysis
    {
        internal sealed class WheatStub
        {
            internal int Length, PrefixLength, WriteOffset, PlayerDisplacement;
            internal string Variant;
            internal byte[] Before, After;
            internal int WriteLength;
            internal bool IsCorrect => WriteLength == 0;

            internal byte[] Slice(byte[] bytes)
            {
                var result = new byte[WriteLength];
                Array.Copy(bytes, WriteOffset, result, 0, result.Length);
                return result;
            }
        }

        // The complete prefix must equal a freshly assembled contract, with independently verified
        // flags, frame and domain values. This checks every branch/register operation, not just a
        // plausible player load. The displaced tail and continuation are never part of a repair.
        internal static WheatStub AnalyzeStub(byte[] bytes, ulong stub, ulong flags, byte[] displaced,
            ulong continuation, int playerHome, int flagCount)
        {
            Require(bytes != null && bytes.Length >= 14 && flags != 0 && continuation != 0 &&
                displaced != null && displaced.Length >= 14 && flagCount >= 9 && playerHome > 0 &&
                playerHome <= int.MaxValue - 16, "Invalid stub input");
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes), stub, DecoderOptions.None);
            var instructions = new List<Instruction>();
            // Constant offsets describe the most recently decoded instruction; capture them per instruction.
            var constants = new List<ConstantOffsets>();
            int length = -1;
            while (decoder.IP - stub + 6 <= (ulong)Math.Min(bytes.Length, MaximumStubLength))
            {
                int offset = (int)(decoder.IP - stub);
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid, "Undecodable Fixes stub");
                ConstantOffsets constant = decoder.GetConstantOffsets(instruction);
                if (instruction.Code == Code.Jmp_rm64 && instruction.IsIPRelativeMemoryOperand &&
                    instruction.IPRelativeMemoryAddress == instruction.NextIP && offset + 14 <= bytes.Length &&
                    BitConverter.ToUInt64(bytes, offset + 6) == continuation)
                {
                    Require(offset + 14 <= MaximumStubLength, "Fixes stub exceeds the bounded decoder range");
                    length = offset + 14;
                    break;
                }
                instructions.Add(instruction);
                constants.Add(constant);
            }
            Require(length > displaced.Length, "Fixes stub has no absolute continuation to the hook site");
            byte[] tail = new byte[displaced.Length];
            Array.Copy(bytes, length - 14 - displaced.Length, tail, 0, tail.Length);
            RequireEqual(tail, displaced, "original instructions before the stub continuation");

            int prefixLength = length - 14 - displaced.Length;
            Require(prefixLength > 0, "Missing Fixes wheat prefix");
            var before = new byte[length];
            Array.Copy(bytes, before, length);
            int required = playerHome + 16; // The corrected body preserves RDX and R8.
            var result = new WheatStub { Length = length, PrefixLength = prefixLength,
                PlayerDisplacement = required, Before = before, After = (byte[])before.Clone() };

            byte[] corrected = AssemblePrefix(stub, flags, required, flagCount, "bounded-stack-load");
            if (MatchesPrefix(before, corrected, prefixLength, true))
            {
                result.Variant = "bounded-stack-load";
                return result;
            }
            byte[] division = AssemblePrefix(stub, flags, 0, flagCount, "division");
            if (MatchesPrefix(before, division, prefixLength, false))
            {
                Require(corrected.Length <= prefixLength, "Corrected prefix does not fit the existing Fixes stub");
                Array.Copy(corrected, result.After, corrected.Length);
                for (int i = corrected.Length; i < prefixLength; i++) result.After[i] = 0x90;
                result.Variant = "division";
                result.WriteLength = prefixLength;
                return result;
            }

            // Legacy emitters use exactly one explicit stack load. Its original displacement may
            // differ; all other bytes must still implement the complete audited legacy contract.
            int reads = 0, readIndex = -1;
            for (int index = 0; index < instructions.Count; index++)
            {
                Instruction instruction = instructions[index];
                if (instruction.IP >= stub + (uint)prefixLength) break;
                if (UsesExplicitStackMemory(instruction))
                {
                    reads++;
                    readIndex = index;
                }
            }
            Require(reads == 1, "Unknown Fixes wheat prefix: no supported complete instruction contract");
            Instruction load = instructions[readIndex];
            Require(load.Mnemonic == Mnemonic.Mov && load.OpCount == 2 && load.Op0Kind == OpKind.Register &&
                load.Op0Register == Register.EDX && load.Op1Kind == OpKind.Memory &&
                load.MemoryBase == Register.RSP && load.MemoryIndex == Register.None &&
                load.MemorySize == MemorySize.UInt32, "Fixes stack load is not a 32-bit player load");
            ConstantOffsets offsets = constants[readIndex];
            Require(offsets.HasDisplacement && offsets.DisplacementSize == 1, "Fixes player load has no 8-bit displacement");
            Require(required > 0 && required <= 0x7F, "Correct player displacement does not fit the 8-bit operand");
            int displacement = checked((int)load.MemoryDisplacement64);
            Require(MatchesPrefix(before, AssemblePrefix(stub, flags, displacement, flagCount, "stack-load"),
                prefixLength, false), "Unknown Fixes wheat stack prefix: complete contract differs");
            int readOffset = (int)(load.IP - stub);
            result.Variant = "stack-load";
            if (displacement != required)
            {
                result.WriteOffset = readOffset + offsets.DisplacementOffset;
                result.WriteLength = 1;
                result.After[result.WriteOffset] = (byte)required;
            }
            return result;
        }

        private static bool MatchesPrefix(byte[] actual, byte[] expected, int length, bool allowPadding)
        {
            if (expected.Length > length || !allowPadding && expected.Length != length) return false;
            for (int i = 0; i < expected.Length; i++) if (actual[i] != expected[i]) return false;
            for (int i = expected.Length; i < length; i++) if (actual[i] != 0x90) return false;
            return true;
        }

        private static byte[] AssemblePrefix(ulong address, ulong flags, int displacement, int flagCount, string variant)
        {
            var a = new Assembler(64);
            Label vanilla = a.CreateLabel();
            bool division = variant == "division";
            if (division) { a.push(rax); a.push(rcx); }
            a.push(rdx); a.push(r8);
            a.cmp(rdi, (int)eGoods.STORED_RAW_WHEAT);
            a.jne(vanilla);
            if (division)
            {
                // Recognition only: these instructions are never emitted as a correction.
                a.mov(rax, r9);
                a.mov(rcx, Marshal.SizeOf(typeof(GamePlayerResources)));
                a.xor(rdx, rdx);
                a.div(rcx);
            }
            else
            {
                a.mov(edx, __dword_ptr[rsp + displacement]);
                if (variant == "bounded-stack-load") { a.cmp(edx, flagCount); a.jae(vanilla); }
            }
            a.push(r8);
            a.mov(r8, flags);
            a.cmp(__byte_ptr[r8 + rdx], 0);
            a.pop(r8);
            a.jz(vanilla);
            a.mov(ecx, (int)AISellCategory.Resource);
            a.Label(ref vanilla);
            a.pop(r8); a.pop(rdx);
            if (division) { a.pop(rcx); a.pop(rax); }
            using (var stream = new MemoryStream())
            {
                a.Assemble(new StreamCodeWriter(stream), address);
                return stream.ToArray();
            }
        }
    }
}

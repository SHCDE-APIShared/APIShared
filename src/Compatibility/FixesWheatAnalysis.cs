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
    // Version-independent machine contract of the Fixes wheat-sale correction. Nothing here depends on a
    // file hash, a version or a fixed RVA: the hook site comes from Fixes' own hook handle, the seller frame
    // from the game's PE unwind data and decoded prolog. Entire supported prefixes are rebuilt from
    // those derived values and compared before any write; an old byte-template allowlist is not used.
    // Every derivation is fail-closed; an unexpected shape refuses the correction instead of guessing.
    // No compound assignments: the wheat source guardrail treats every += as a later callback subscription.
    internal static class FixesWheatAnalysis
    {
        // Entry-relative home slot of the second integer argument (RDX) in the Windows x64 ABI.
        internal const int SecondArgumentHome = 0x10;
        private const int MaximumStubLength = 256;

        internal sealed class SellerFrame
        {
            internal uint FunctionBegin, PrologLength, SiteRva;
            // Stack bytes the seller's prolog allocates and pushes (fixed for the whole body: no frame register).
            internal int Allocation;
            // RSP displacement of the player argument's home slot at the hook site.
            internal int PlayerHome;
        }

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

        internal readonly struct RuntimeFunction
        {
            internal RuntimeFunction(uint begin, uint end, uint unwind) { Begin = begin; End = end; Unwind = unwind; }
            internal uint Begin { get; }
            internal uint End { get; }
            internal uint Unwind { get; }
        }

        private sealed class UnwindInfo
        {
            internal int Version, Flags, PrologLength, FrameRegister;
            internal readonly List<KeyValuePair<byte, byte>> Codes = new List<KeyValuePair<byte, byte>>();
            internal RuntimeFunction? Chained;
        }

        // Disk image of a PE32+ module. The disk file is the unpatched code; live memory is compared separately.
        internal sealed class PeImage
        {
            private readonly byte[] file;
            private readonly List<uint[]> sections = new List<uint[]>(); // rva, virtualSize, rawPointer, rawSize, characteristics
            private RuntimeFunction[] functions;

            internal PeImage(byte[] file)
            {
                this.file = file ?? throw new ArgumentNullException(nameof(file));
                Require(file.Length > 0x40 && file[0] == (byte)'M' && file[1] == (byte)'Z', "Not a PE image");
                int pe = ReadInt(0x3C);
                Require(pe > 0 && pe < file.Length - 0x108 && ReadUInt(pe) == 0x4550, "Missing PE signature");
                Require(ReadUShort(pe + 4) == 0x8664, "Not an x64 image");
                int count = ReadUShort(pe + 6), optionalSize = ReadUShort(pe + 20), optional = pe + 24;
                Require(ReadUShort(optional) == 0x20B, "Not a PE32+ image");
                SizeOfImage = ReadUInt(optional + 56);
                Require(ReadUInt(optional + 108) > 3, "Missing exception directory");
                ExceptionRva = ReadUInt(optional + 112 + 3 * 8);
                ExceptionSize = ReadUInt(optional + 112 + 3 * 8 + 4);
                Require(ExceptionRva != 0 && ExceptionSize >= 12 && ExceptionSize % 12 == 0, "Invalid exception directory");
                for (int index = 0; index < count; index++)
                {
                    int header = optional + optionalSize + index * 40;
                    Require(header + 40 <= file.Length, "Truncated section table");
                    sections.Add(new[] { ReadUInt(header + 12), ReadUInt(header + 8), ReadUInt(header + 20),
                        ReadUInt(header + 16), ReadUInt(header + 36) });
                }
            }

            internal uint SizeOfImage { get; }
            internal uint ExceptionRva { get; }
            internal uint ExceptionSize { get; }

            internal byte[] Read(uint rva, int length)
            {
                Require(length >= 0, "Negative read");
                foreach (uint[] section in sections)
                    if (rva >= section[0] && (ulong)rva + (uint)length <= (ulong)section[0] + Math.Max(section[1], section[3]))
                    {
                        ulong offset = (ulong)section[2] + (rva - section[0]);
                        Require((ulong)rva + (uint)length <= (ulong)section[0] + section[3] && offset + (uint)length <= (ulong)file.Length,
                            "Range is not backed by file data");
                        byte[] result = new byte[length];
                        Array.Copy(file, (long)offset, result, 0, length);
                        return result;
                    }
                throw new InvalidOperationException("Range 0x" + rva.ToString("X") + " is outside every section");
            }

            internal bool IsExecutable(uint rva, int length)
            {
                foreach (uint[] section in sections)
                    if (rva >= section[0] && (ulong)rva + (uint)length <= (ulong)section[0] + section[1])
                        return (section[4] & 0x20000000) != 0;
                return false;
            }

            internal RuntimeFunction[] Functions
            {
                get
                {
                    if (functions != null) return functions;
                    byte[] table = Read(ExceptionRva, (int)ExceptionSize);
                    var result = new RuntimeFunction[table.Length / 12];
                    for (int index = 0; index < result.Length; index++)
                        result[index] = new RuntimeFunction(BitConverter.ToUInt32(table, index * 12),
                            BitConverter.ToUInt32(table, index * 12 + 4), BitConverter.ToUInt32(table, index * 12 + 8));
                    return functions = result;
                }
            }

            private int ReadInt(int offset) => BitConverter.ToInt32(file, offset);
            private uint ReadUInt(int offset) => BitConverter.ToUInt32(file, offset);
            private ushort ReadUShort(int offset) => BitConverter.ToUInt16(file, offset);
        }

        // Derives the seller's player-argument slot at the hook site from unwind data and the decoded prolog.
        internal static SellerFrame AnalyzeSeller(PeImage image, uint siteRva, byte[] displaced)
        {
            Require(displaced != null && displaced.Length >= 14, "Displaced byte count is below the 14-byte entry jump");
            Require(image.IsExecutable(siteRva, displaced.Length), "Hook site is not in an executable section");
            RequireEqual(image.Read(siteRva, displaced.Length), displaced, "disk bytes at the hook site");
            ValidateDisplaced(displaced, siteRva);

            RuntimeFunction containing = FindContaining(image, siteRva);
            UnwindInfo fragment = ReadUnwind(image, containing.Unwind);
            Require(siteRva >= containing.Begin + (uint)fragment.PrologLength, "Hook site lies inside a prolog");
            RuntimeFunction primary = containing;
            UnwindInfo primaryInfo = fragment;
            for (int depth = 0; primaryInfo.Chained.HasValue; depth++)
            {
                Require(depth < 8, "Unwind chain too deep");
                RequireNoStackChange(primaryInfo, "chained fragment");
                primary = primaryInfo.Chained.Value;
                primaryInfo = ReadUnwind(image, primary.Unwind);
            }
            int allocation = StackAllocation(primaryInfo);
            Require(allocation > 0 && allocation % 8 == 0, "Unexpected seller stack allocation");

            // The prolog must store the untouched second argument at its entry home slot before any RSP change
            // relevant to that store, and its decoded stack changes must match the unwind description.
            byte[] prolog = image.Read(primary.Begin, primaryInfo.PrologLength);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(prolog), primary.Begin, DecoderOptions.None);
            var info = new InstructionInfoFactory();
            int lowered = 0, homeStores = 0;
            bool rdxWritten = false;
            while (decoder.IP < primary.Begin + (ulong)prolog.Length)
            {
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid, "Undecodable seller prolog");
                if (IsExplicitStackStore(instruction, out long displacement) &&
                    (instruction.Op1Kind == OpKind.Register &&
                     (instruction.Op1Register == Register.EDX || instruction.Op1Register == Register.RDX)))
                {
                    Require(!rdxWritten && displacement - lowered == SecondArgumentHome,
                        "Seller prolog stores RDX outside its entry home slot");
                    homeStores++;
                }
                foreach (UsedRegister used in info.GetInfo(instruction).GetUsedRegisters())
                    if (used.Register.GetFullRegister() == Register.RDX && IsWrite(used.Access))
                        rdxWritten = true;
                lowered = lowered + StackLowering(instruction);
            }
            Require(decoder.IP == primary.Begin + (ulong)prolog.Length, "Seller prolog ends inside an instruction");
            Require(homeStores == 1, "Seller prolog does not save the player argument exactly once");
            Require(lowered == allocation, "Decoded seller prolog differs from its unwind data");

            int home = allocation + SecondArgumentHome;
            foreach (RuntimeFunction range in FunctionRanges(image, primary))
            {
                // Every fragment shares the primary frame only while it keeps RSP unchanged.
                if (range.Begin != primary.Begin) RequireNoStackChange(ReadUnwind(image, range.Unwind), "seller fragment");
                RequireHomeUntouched(image, range, range.Begin == primary.Begin ? (uint)prolog.Length : 0u, home);
            }
            return new SellerFrame { FunctionBegin = primary.Begin, PrologLength = (uint)prolog.Length,
                SiteRva = siteRva, Allocation = allocation, PlayerHome = home };
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

        // The writer owns page protection and cache synchronization. No rollback is allowed.
        internal static bool Correct(Func<byte[]> read, Action<int, byte[], byte[]> write, Func<byte[], WheatStub> analyze)
        {
            byte[] before = read();
            WheatStub stub = analyze(before);
            if (stub.IsCorrect) return false;
            RequireEqual(read(), before, "stub changed before the startup write");
            write(stub.WriteOffset, stub.Slice(stub.Before), stub.Slice(stub.After));
            byte[] after = read();
            byte[] expected = (byte[])before.Clone();
            Array.Copy(stub.After, stub.WriteOffset, expected, stub.WriteOffset, stub.WriteLength);
            RequireEqual(after, expected, "post-write bytes outside the approved correction range");
            Require(analyze(after).IsCorrect, "Post-write stub does not match the corrected contract");
            return true;
        }

        // Live entry written by the backend: jmp qword [rip+0], the stub address, NOP padding.
        internal static byte[] ExpectedEntry(ulong stub, int displacedLength)
        {
            byte[] result = new byte[displacedLength];
            result[0] = 0xFF; result[1] = 0x25;
            Array.Copy(BitConverter.GetBytes(stub), 0, result, 6, 8);
            for (int i = 14; i < result.Length; i++) result[i] = 0x90;
            return result;
        }

        internal static void RequireEqual(byte[] actual, byte[] expected, string description)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                throw new InvalidOperationException(description + ": length mismatch");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != expected[i])
                    throw new InvalidOperationException(description + ": mismatch at +0x" + i.ToString("X"));
        }

        private static void ValidateDisplaced(byte[] displaced, uint siteRva)
        {
            // Re-executed verbatim in the stub: complete instructions without branches, RIP-relative operands
            // or stack changes, so neither the frame nor the copy can differ from the original.
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(displaced), siteRva, DecoderOptions.None);
            while (decoder.IP < siteRva + (ulong)displaced.Length)
            {
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid && instruction.FlowControl == FlowControl.Next &&
                    !instruction.IsIPRelativeMemoryOperand && !WritesRsp(instruction) && StackLowering(instruction) == 0,
                    "Displaced instructions are not position- and stack-neutral");
            }
            Require(decoder.IP == siteRva + (ulong)displaced.Length, "Displaced bytes end inside an instruction");
        }

        private static RuntimeFunction FindContaining(PeImage image, uint rva)
        {
            RuntimeFunction? found = null;
            foreach (RuntimeFunction function in image.Functions)
                if (function.Begin <= rva && rva < function.End)
                {
                    Require(!found.HasValue, "Overlapping unwind entries");
                    found = function;
                }
            Require(found.HasValue, "Hook site has no unwind entry (leaf code is not supported)");
            return found.Value;
        }

        // The primary range and every fragment whose unwind chain ends at it. Unrelated functions are only
        // read for their chain link, never validated against the seller's frame rules.
        private static IEnumerable<RuntimeFunction> FunctionRanges(PeImage image, RuntimeFunction primary)
        {
            foreach (RuntimeFunction function in image.Functions)
            {
                RuntimeFunction current = function;
                for (int depth = 0; depth < 8; depth++)
                {
                    if (current.Begin == primary.Begin) { yield return function; break; }
                    if (!TryReadChain(image, current.Unwind, out current)) break;
                }
            }
        }

        private static bool TryReadChain(PeImage image, uint rva, out RuntimeFunction chained)
        {
            chained = default;
            byte[] header = image.Read(rva, 4);
            if ((header[0] >> 3 & 4) == 0) return false;
            byte[] link = image.Read(rva + 4 + (uint)(((header[2] + 1) & ~1) * 2), 12);
            chained = new RuntimeFunction(BitConverter.ToUInt32(link, 0), BitConverter.ToUInt32(link, 4),
                BitConverter.ToUInt32(link, 8));
            return true;
        }

        // Linear decoding of every range: no instruction may write the home slot or take its address.
        private static void RequireHomeUntouched(PeImage image, RuntimeFunction range, uint skip, int home)
        {
            Require(range.End > range.Begin && range.End - range.Begin <= 0x100000, "Implausible function range");
            byte[] code = image.Read(range.Begin, (int)(range.End - range.Begin));
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(code), range.Begin, DecoderOptions.None);
            var info = new InstructionInfoFactory();
            while (decoder.IP < range.End)
            {
                Instruction instruction = decoder.Decode();
                Require(!instruction.IsInvalid, "Undecodable seller code at 0x" + instruction.IP.ToString("X"));
                if (instruction.IP < range.Begin + skip) continue;
                // Taking any stack address could let a later indirect store alias the home slot.
                Require(instruction.Mnemonic != Mnemonic.Lea || instruction.MemoryBase != Register.RSP,
                    "Seller takes a stack address; player home aliasing is unsupported");
                foreach (UsedMemory used in info.GetInfo(instruction).GetUsedMemory())
                {
                    if (used.Base != Register.RSP || !IsWrite(used.Access)) continue;
                    Require(used.Index == Register.None, "Indexed seller stack write may alias the player home slot");
                    int width = used.MemorySize.GetSize();
                    Require(width > 0, "Unknown seller stack write width");
                    long begin = unchecked((long)used.Displacement);
                    // The second argument's saved value is four bytes, even for a wider store.
                    if (begin < (long)home + sizeof(int) && begin > (long)home - width)
                        throw new InvalidOperationException("Seller overlaps the player home slot at 0x" +
                            instruction.IP.ToString("X"));
                }
            }
        }

        private static UnwindInfo ReadUnwind(PeImage image, uint rva)
        {
            byte[] header = image.Read(rva, 4);
            var info = new UnwindInfo
            {
                Version = header[0] & 7, Flags = header[0] >> 3, PrologLength = header[1], FrameRegister = header[3] & 0xF
            };
            Require(info.Version == 1 || info.Version == 2, "Unsupported unwind version");
            Require(info.FrameRegister == 0, "Seller uses a frame register; RSP is not fixed in its body");
            int count = header[2];
            byte[] codes = image.Read(rva + 4, count * 2);
            for (int index = 0; index < count; index++)
                info.Codes.Add(new KeyValuePair<byte, byte>(codes[index * 2], codes[index * 2 + 1]));
            if ((info.Flags & 4) != 0)
            {
                byte[] chained = image.Read(rva + 4 + (uint)(((count + 1) & ~1) * 2), 12);
                info.Chained = new RuntimeFunction(BitConverter.ToUInt32(chained, 0), BitConverter.ToUInt32(chained, 4),
                    BitConverter.ToUInt32(chained, 8));
            }
            return info;
        }

        private static int StackAllocation(UnwindInfo info)
        {
            int total = 0;
            for (int index = 0; index < info.Codes.Count; index++)
            {
                int op = info.Codes[index].Value & 0xF, opInfo = info.Codes[index].Value >> 4;
                switch (op)
                {
                    case 0: total = total + 8; break; // UWOP_PUSH_NONVOL
                    case 1: // UWOP_ALLOC_LARGE
                        if (opInfo == 0) { total = total + (Slot(info, ++index) * 8); }
                        else { Require(opInfo == 1, "Invalid large allocation"); total = total + (Slot(info, ++index) | (Slot(info, ++index) << 16)); }
                        break;
                    case 2: total = total + (opInfo * 8 + 8); break; // UWOP_ALLOC_SMALL
                    case 4: case 8: index = index + 1; break; // UWOP_SAVE_NONVOL, UWOP_SAVE_XMM128
                    case 5: case 9: index = index + 2; break; // *_FAR
                    case 6: Require(info.Version == 2, "Epilog code in version 1 unwind data"); break; // UWOP_EPILOG
                    default: throw new InvalidOperationException("Unsupported unwind operation " + op);
                }
            }
            return total;
        }

        private static void RequireNoStackChange(UnwindInfo info, string description)
        {
            for (int index = 0; index < info.Codes.Count; index++)
            {
                int op = info.Codes[index].Value & 0xF;
                if (op == 4 || op == 8) index = index + 1;
                else if (op == 5 || op == 9) index = index + 2;
                else Require(op == 6 && info.Version == 2, "The " + description + " changes the stack");
            }
        }

        private static int Slot(UnwindInfo info, int index)
        {
            Require(index < info.Codes.Count, "Truncated unwind codes");
            return info.Codes[index].Key | (info.Codes[index].Value << 8);
        }

        private static int StackLowering(Instruction instruction)
        {
            if (instruction.Mnemonic == Mnemonic.Push) return 8;
            if (instruction.Mnemonic == Mnemonic.Sub && instruction.Op0Kind == OpKind.Register &&
                instruction.Op0Register == Register.RSP && instruction.Op1Kind != OpKind.Register)
                return (int)instruction.GetImmediate(1);
            Require(!WritesRsp(instruction), "Unexpected RSP change in prolog or displaced code");
            return 0;
        }

        private static bool IsExplicitStackStore(Instruction instruction, out long displacement)
        {
            displacement = (long)instruction.MemoryDisplacement64;
            return instruction.Mnemonic == Mnemonic.Mov && instruction.Op0Kind == OpKind.Memory &&
                instruction.MemoryBase == Register.RSP && instruction.MemoryIndex == Register.None;
        }

        private static bool UsesExplicitStackMemory(Instruction instruction)
        {
            for (int operand = 0; operand < instruction.OpCount; operand++)
                if (instruction.GetOpKind(operand) == OpKind.Memory &&
                    (instruction.MemoryBase == Register.RSP || instruction.MemoryIndex == Register.RSP))
                    return true;
            return false;
        }

        private static bool WritesRsp(Instruction instruction) =>
            instruction.Mnemonic != Mnemonic.Push && instruction.Mnemonic != Mnemonic.Pop &&
            instruction.Mnemonic != Mnemonic.Call && instruction.Mnemonic != Mnemonic.Ret &&
            WritesRegister(instruction, Register.RSP);

        private static bool WritesRegister(Instruction instruction, Register full)
        {
            foreach (UsedRegister used in new InstructionInfoFactory().GetInfo(instruction).GetUsedRegisters())
                if (used.Register.GetFullRegister() == full && IsWrite(used.Access))
                    return true;
            return false;
        }

        private static bool IsWrite(OpAccess access) =>
            access == OpAccess.Write || access == OpAccess.ReadWrite || access == OpAccess.CondWrite ||
            access == OpAccess.ReadCondWrite;

        private static void Require(bool valid, string reason)
        {
            if (!valid) throw new InvalidOperationException(reason);
        }
    }
}

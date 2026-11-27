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
        private sealed class UnwindInfo
        {
            internal int Version, Flags, PrologLength, FrameRegister;
            internal readonly List<KeyValuePair<byte, byte>> Codes = new List<KeyValuePair<byte, byte>>();
            internal RuntimeFunction? Chained;
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
    }
}

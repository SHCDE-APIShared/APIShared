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
        internal readonly struct RuntimeFunction
        {
            internal RuntimeFunction(uint begin, uint end, uint unwind) { Begin = begin; End = end; Unwind = unwind; }
            internal uint Begin { get; }
            internal uint End { get; }
            internal uint Unwind { get; }
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
    }
}

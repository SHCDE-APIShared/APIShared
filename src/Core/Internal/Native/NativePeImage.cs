using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace APIShared
{
    internal readonly struct NativeSection
    {
        public NativeSection(int start, int length, uint characteristics)
        {
            Start = start;
            Length = length;
            Characteristics = characteristics;
        }

        public int Start { get; }
        public int Length { get; }
        public uint Characteristics { get; }
        public int End => checked(Start + Length);
        public bool Executable => (Characteristics & 0x20000000u) != 0;
        public bool Contains(int start, int length) => start >= Start && length >= 0 && start <= End - length;
    }

    internal sealed class NativePeImage
    {
        private NativePeImage(int imageSize, NativeSection[] sections)
        {
            ImageSize = imageSize;
            Sections = sections;
        }

        public int ImageSize { get; }
        public NativeSection[] Sections { get; }

        public static NativePeImage Parse(ReadOnlySpan<byte> memory)
        {
            if (memory.Length < 0x100 || memory[0] != 0x4D || memory[1] != 0x5A)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native module has no valid DOS header.");
            int pe = ReadInt32(memory, 0x3C);
            if (pe < 0 || pe > memory.Length - 0x80 || ReadInt32(memory, pe) != 0x4550)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native module has no valid PE header.");
            int sectionCount = ReadUInt16(memory, pe + 6);
            int optionalSize = ReadUInt16(memory, pe + 20);
            int optional = pe + 24;
            if (optionalSize < 60 || ReadUInt16(memory, optional) != 0x20B)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native module is not a valid PE32+ image.");
            int imageSize = checked((int)ReadUInt32(memory, optional + 56));
            if (sectionCount <= 0 || sectionCount > 96 || imageSize <= 0 || imageSize > memory.Length)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native PE image size or section count is invalid.");
            int table = checked(optional + optionalSize);
            if (table < 0 || table > memory.Length - sectionCount * 40)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "The native PE section table lies outside the module.");
            var sections = new NativeSection[sectionCount];
            for (int index = 0; index < sectionCount; index++)
            {
                int header = table + index * 40;
                int virtualSize = checked((int)ReadUInt32(memory, header + 8));
                int virtualAddress = checked((int)ReadUInt32(memory, header + 12));
                int rawSize = checked((int)ReadUInt32(memory, header + 16));
                int length = Math.Max(virtualSize, rawSize);
                if (virtualAddress < 0 || length <= 0 || virtualAddress > imageSize - length)
                    throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "A native PE section lies outside the image.");
                sections[index] = new NativeSection(virtualAddress, length, ReadUInt32(memory, header + 36));
            }
            return new NativePeImage(imageSize, sections);
        }

        public NativeSection RequireExecutableRange(int start, int length, string target)
        {
            foreach (NativeSection section in Sections)
                if (section.Executable && section.Contains(start, length))
                    return section;
            throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, target + " lies outside executable PE sections.");
        }

        public NativeSection RequireMappedRange(int start, int length, string target)
        {
            foreach (NativeSection section in Sections)
                if (section.Contains(start, length))
                    return section;
            throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, target + " lies outside mapped PE sections.");
        }

        internal static int ReadInt32(ReadOnlySpan<byte> memory, int offset)
        {
            if (offset < 0 || offset > memory.Length - 4)
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, "A native Int32 read lies outside the module.");
            return memory[offset] | memory[offset + 1] << 8 | memory[offset + 2] << 16 | memory[offset + 3] << 24;
        }

        private static int ReadUInt16(ReadOnlySpan<byte> memory, int offset) => memory[offset] | memory[offset + 1] << 8;
        private static uint ReadUInt32(ReadOnlySpan<byte> memory, int offset) => unchecked((uint)ReadInt32(memory, offset));
    }
}

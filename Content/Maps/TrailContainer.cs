using System;
using System.IO;

namespace APIShared.Maps
{
    /// <summary>Byte offsets of the restart block inside one validated SCDE map or Trail container.
    /// All offsets refer to the exact byte array that was inspected.</summary>
    public sealed class TrailContainerLayout
    {
        internal TrailContainerLayout(int restartSizeFieldOffset, int restartPayloadOffset, int restartSize,
            int directoryTagOffset, uint directoryTag)
        {
            RestartSizeFieldOffset = restartSizeFieldOffset;
            RestartPayloadOffset = restartPayloadOffset;
            RestartSize = restartSize;
            DirectoryTagOffset = directoryTagOffset;
            DirectoryTag = directoryTag;
        }

        /// <summary>Offset of the 32-bit restart size, or -1 when the preamble has no restart field.</summary>
        public int RestartSizeFieldOffset { get; }
        /// <summary>Offset of the restart payload, or -1 when the preamble has no restart field.</summary>
        public int RestartPayloadOffset { get; }
        /// <summary>Restart payload length in bytes; 0 for an ordinary map.</summary>
        public int RestartSize { get; }
        /// <summary>Offset of the section-directory tag. Everything from here to the end is opaque data.</summary>
        public int DirectoryTagOffset { get; }
        /// <summary>The recognised section-directory tag value.</summary>
        public uint DirectoryTag { get; }
        /// <summary>True when the container carries a non-empty restart block, as saved Trail missions do.</summary>
        public bool HasRestartData => RestartSize > 0;
    }

    /// <summary>
    /// Reads the restart block of SCDE <c>.trail</c> containers and extracts the embedded map without it.
    /// </summary>
    /// <remarks>
    /// Game independent: no installation, Unity, file system or Script Extender access. Callers pass bytes they
    /// own; every result is an independent copy and the input is never modified. Only the preamble is parsed:
    /// the magic, six length-prefixed blocks, the optional restart field and a known directory tag. Section
    /// data and any unknown trailing data are copied byte for byte and never re-serialised. A valid layout
    /// does not prove that the game can load the map or decode the restart payload. Malformed, truncated or
    /// unsupported containers throw <see cref="InvalidDataException"/>; null arguments throw
    /// <see cref="ArgumentNullException"/>. Thread safe; no shared state.
    /// </remarks>
    public static class TrailContainer
    {
        /// <summary>Default upper bound for an inspected container, in bytes.</summary>
        public const int DefaultMaximumContainerBytes = 64 * 1024 * 1024;

        private const uint ScdeMagic = 0xFFFFFFFEu;
        private const uint ClassicMagic = 0xFFFFFFFFu;
        private const int PreambleBlockCount = 6;
        // A non-empty sixth length-prefixed block (index 5) announces the optional restart field.
        private const int RestartAnnouncingBlock = 5;

        /// <summary>Validates the preamble and returns the restart layout.</summary>
        public static TrailContainerLayout ReadLayout(byte[] container,
            int maximumContainerBytes = DefaultMaximumContainerBytes)
        {
            if (container == null) throw new ArgumentNullException(nameof(container));
            if (maximumContainerBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumContainerBytes));
            if (container.Length > maximumContainerBytes)
                throw new InvalidDataException("The container exceeds its byte limit.");
            int position = 0;
            uint magic = ReadUInt32(container, ref position, "magic");
            if (magic != ScdeMagic && magic != ClassicMagic)
                throw new InvalidDataException("The container has an unknown magic value.");

            bool restartAnnounced = false;
            for (int block = 0; block < PreambleBlockCount; block++)
            {
                uint size = ReadUInt32(container, ref position, "preamble block size");
                Skip(container, ref position, size, "preamble block");
                if (block == RestartAnnouncingBlock)
                    restartAnnounced = size != 0;
            }

            int sizeFieldOffset = -1, payloadOffset = -1, restartSize = 0;
            if (restartAnnounced)
            {
                sizeFieldOffset = position;
                uint declared = ReadUInt32(container, ref position, "restart size");
                payloadOffset = position;
                Skip(container, ref position, declared, "restart payload");
                restartSize = (int)declared;
                if (restartSize > 0 && ReadUInt32(container, ref position, "restart terminator") != 0)
                    throw new InvalidDataException("The restart block terminator is missing or not zero.");
            }

            int tagOffset = position;
            uint tag = ReadUInt32(container, ref position, "section directory tag");
            if (!IsKnownDirectoryTag(tag))
                throw new InvalidDataException("The container has an unknown section directory tag.");
            return new TrailContainerLayout(sizeFieldOffset, payloadOffset, restartSize, tagOffset, tag);
        }

        /// <summary>Returns a copy of the restart payload. Throws when the container has no restart data.</summary>
        public static byte[] ReadRestartData(byte[] trail, int maximumContainerBytes = DefaultMaximumContainerBytes)
        {
            TrailContainerLayout layout = ReadLayout(trail, maximumContainerBytes);
            if (!layout.HasRestartData)
                throw new InvalidDataException("The container has no restart data.");
            var result = new byte[layout.RestartSize];
            Buffer.BlockCopy(trail, layout.RestartPayloadOffset, result, 0, result.Length);
            return result;
        }

        /// <summary>
        /// Returns the embedded map: the input without restart payload and terminator and with an empty restart
        /// size field, as ordinary maps carry it. Every other byte keeps its value and order.
        /// Throws when the container has no restart data.
        /// </summary>
        public static byte[] ExtractMap(byte[] trail, int maximumContainerBytes = DefaultMaximumContainerBytes)
        {
            TrailContainerLayout layout = ReadLayout(trail, maximumContainerBytes);
            if (!layout.HasRestartData)
                throw new InvalidDataException("The container has no restart data.");
            int tailLength = trail.Length - layout.DirectoryTagOffset;
            var map = new byte[checked(layout.RestartSizeFieldOffset + 4 + tailLength)];
            Buffer.BlockCopy(trail, 0, map, 0, layout.RestartSizeFieldOffset);
            // The four bytes after the prefix stay zero: an empty restart size field.
            Buffer.BlockCopy(trail, layout.DirectoryTagOffset, map, layout.RestartSizeFieldOffset + 4, tailLength);
            TrailContainerLayout extracted = ReadLayout(map, maximumContainerBytes);
            if (extracted.HasRestartData || extracted.RestartSizeFieldOffset != layout.RestartSizeFieldOffset)
                throw new InvalidDataException("The extracted map failed restart-field verification.");
            return map;
        }

        private static bool IsKnownDirectoryTag(uint tag) =>
            tag == 2036 || tag == 3036 || tag == 4036 || tag == 1076 || tag == 2100 || tag == 2108;

        private static uint ReadUInt32(byte[] data, ref int position, string subject)
        {
            if (position > data.Length - 4)
                throw new InvalidDataException("The container ended while reading its " + subject + ".");
            uint value = (uint)(data[position] | data[position + 1] << 8 | data[position + 2] << 16 |
                data[position + 3] << 24);
            position += 4;
            return value;
        }

        private static void Skip(byte[] data, ref int position, uint size, string subject)
        {
            if (size > (uint)(data.Length - position))
                throw new InvalidDataException("The container ended inside its " + subject + ".");
            position += (int)size;
        }
    }
}

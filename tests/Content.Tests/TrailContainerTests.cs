using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIShared.Maps;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Content.Tests
{
    [TestClass]
    public class TrailContainerTests
    {
        // Synthetic containers: real game files are proprietary. The layout follows the SCDE preamble
        // (magic, six length-prefixed blocks, optional restart field, directory tag, opaque tail).
        private static byte[] Container(byte[] restart, uint u4Size = 4, uint tag = 3036,
            byte[] tail = null, uint terminator = 0, uint magic = 0xFFFFFFFE)
        {
            var bytes = new List<byte>();
            void U32(uint value) => bytes.AddRange(BitConverter.GetBytes(value));
            U32(magic);
            byte[][] blocks = { new byte[] { 1, 2 }, new byte[] { 3 }, Array.Empty<byte>(),
                new byte[] { 4, 5, 6 }, new byte[] { 7 }, Enumerable.Repeat((byte)9, (int)u4Size).ToArray() };
            foreach (byte[] block in blocks) { U32((uint)block.Length); bytes.AddRange(block); }
            if (u4Size != 0)
            {
                U32((uint)(restart?.Length ?? 0));
                if (restart != null && restart.Length > 0) { bytes.AddRange(restart); U32(terminator); }
            }
            U32(tag);
            bytes.AddRange(tail ?? new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE });
            return bytes.ToArray();
        }

        [TestMethod]
        public void ExtractionRemovesOnlyTheRestartBlockAndKeepsTheTailUnchanged()
        {
            byte[] restart = { 10, 20, 30, 40, 50 };
            byte[] tail = Enumerable.Range(0, 300).Select(value => (byte)value).ToArray();
            byte[] trail = Container(restart, tail: tail);
            byte[] original = (byte[])trail.Clone();

            CollectionAssert.AreEqual(restart, TrailContainer.ReadRestartData(trail));
            byte[] map = TrailContainer.ExtractMap(trail);
            CollectionAssert.AreEqual(original, trail, "The caller's bytes were modified.");
            CollectionAssert.AreEqual(Container(Array.Empty<byte>(), tail: tail), map);
            TrailContainerLayout layout = TrailContainer.ReadLayout(map);
            Assert.IsFalse(layout.HasRestartData);
            CollectionAssert.AreEqual(tail, map.Skip(layout.DirectoryTagOffset + 4).ToArray());

            // Reinserting the original block restores the input byte for byte.
            TrailContainerLayout source = TrailContainer.ReadLayout(trail);
            byte[] rebuilt = map.Take(layout.RestartSizeFieldOffset)
                .Concat(trail.Skip(source.RestartSizeFieldOffset).Take(source.DirectoryTagOffset - source.RestartSizeFieldOffset))
                .Concat(map.Skip(layout.DirectoryTagOffset)).ToArray();
            CollectionAssert.AreEqual(trail, rebuilt);
        }

        [TestMethod]
        public void ResultsAreIndependentCopies()
        {
            byte[] trail = Container(new byte[] { 1, 2, 3 });
            byte[] restart = TrailContainer.ReadRestartData(trail);
            restart[0] = 99;
            Assert.AreEqual(1, TrailContainer.ReadRestartData(trail)[0]);
        }

        [TestMethod]
        public void OrdinaryMapsHaveNoRestartData()
        {
            foreach (byte[] map in new[] { Container(Array.Empty<byte>()), Container(null, u4Size: 0) })
            {
                Assert.IsFalse(TrailContainer.ReadLayout(map).HasRestartData);
                Assert.ThrowsExactly<InvalidDataException>(() => TrailContainer.ReadRestartData(map));
                Assert.ThrowsExactly<InvalidDataException>(() => TrailContainer.ExtractMap(map));
            }
            Assert.AreEqual(-1, TrailContainer.ReadLayout(Container(null, u4Size: 0)).RestartSizeFieldOffset);
        }

        [TestMethod]
        public void MalformedContainersAreRejected()
        {
            byte[] valid = Container(new byte[] { 1, 2, 3 });
            var invalid = new List<byte[]>
            {
                Container(new byte[] { 1 }, magic: 0x12345678),
                Container(new byte[] { 1 }, tag: 999),
                Container(new byte[] { 1 }, terminator: 7),
                valid.Take(3).ToArray(),
                valid.Take(valid.Length - 9).ToArray(),
            };
            // A declared restart size beyond the end of the data.
            byte[] overflow = (byte[])valid.Clone();
            int sizeOffset = TrailContainer.ReadLayout(valid).RestartSizeFieldOffset;
            BitConverter.GetBytes(uint.MaxValue).CopyTo(overflow, sizeOffset);
            invalid.Add(overflow);
            // A preamble block length beyond the end of the data.
            byte[] block = (byte[])valid.Clone();
            BitConverter.GetBytes(0x7FFFFFF0u).CopyTo(block, 4);
            invalid.Add(block);
            foreach (byte[] container in invalid)
                Assert.ThrowsExactly<InvalidDataException>(() => TrailContainer.ReadLayout(container));
            Assert.ThrowsExactly<InvalidDataException>(() => TrailContainer.ReadLayout(valid, valid.Length - 1));
            Assert.ThrowsExactly<ArgumentNullException>(() => TrailContainer.ReadLayout(null));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => TrailContainer.ReadLayout(valid, 0));
        }
    }
}

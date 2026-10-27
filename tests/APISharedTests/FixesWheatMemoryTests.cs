using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Compatibility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class FixesWheatMemoryTests
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint freeType);

        [TestMethod]
        public void WriterChangesOnlyApprovedRangeAndRestoresProtection()
        {
            WithPage((page, address, before) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(0, new byte[1], new byte[1]));
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(address, new byte[1], new byte[1]),
                    "Writable data is not executable code.");
                MakeExecutable(page);
                foreach (int length in new[] { 1, 61 })
                {
                    byte[] expected = new byte[length], replacement = new byte[length];
                    Array.Copy(before, 12, expected, 0, length);
                    for (int i = 0; i < length; i++) replacement[i] = (byte)(expected[i] ^ 0x55);
                    FixesWheatMemory.WriteRange(address + 12, expected, replacement);
                    Array.Copy(replacement, 0, before, 12, length);
                    CollectionAssert.AreEqual(before, FixesWheatMemory.Read(address, before.Length));
                    Assert.IsTrue(VirtualProtect(page, (UIntPtr)4096, 0x20, out uint old));
                    Assert.AreEqual(0x20u, old);
                }
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(address, new byte[0], new byte[0]));
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(address, new byte[257], new byte[257]));
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(address, new byte[1], new byte[2]));
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRange(address, new byte[] { 0 }, new byte[] { 1 }));
                CollectionAssert.AreEqual(before, FixesWheatMemory.Read(address, before.Length));
            });
        }

        [TestMethod]
        public void ProtectedRegionCrossingIsRefusedWithoutChangingEitherPage()
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, (UIntPtr)8192, 0x3000, 0x40);
            if (page == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                Assert.IsTrue(VirtualProtect(IntPtr.Add(page, 4096), (UIntPtr)4096, 0x01, out _));
                ulong address = unchecked((ulong)page.ToInt64());
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    FixesWheatMemory.WriteRange(address + 4090, new byte[16], new byte[16]));
                Assert.AreEqual((byte)0, Marshal.ReadByte(page, 4090));
            }
            finally { Assert.IsTrue(VirtualFree(page, UIntPtr.Zero, 0x8000)); }
        }

        [TestMethod]
        public void DeniedProtectionAndPreimageRaceCauseNoWrite()
        {
            WithPage((page, address, before) =>
            {
                MakeExecutable(page);
                byte[] expected = { before[0] }, replacement = { 0x55 };
                bool flushed = false;
                FixesWheatMemory.ProtectMemory deny = (IntPtr p, UIntPtr n, uint v, out uint old) => { old = 0; return false; };
                Assert.ThrowsExactly<Win32Exception>(() => FixesWheatMemory.WriteRangeCore(address, expected, replacement,
                    deny, (p, n) => { flushed = true; return true; }));
                Assert.IsFalse(flushed);
                CollectionAssert.AreEqual(before, FixesWheatMemory.Read(address, before.Length));
                int calls = 0;
                FixesWheatMemory.ProtectMemory race = (IntPtr p, UIntPtr n, uint v, out uint old) =>
                {
                    bool ok = VirtualProtect(p, n, v, out old);
                    if (++calls == 1) Marshal.WriteByte(p, 0x66); // Foreign preimage change after first validation.
                    return ok;
                };
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteRangeCore(address, expected,
                    replacement, race, (p, n) => true));
                Assert.AreEqual(2, calls);
                Assert.AreEqual((byte)0x66, Marshal.ReadByte(page), "Correction must not overwrite a changed preimage.");
            });
        }

        [TestMethod]
        public void FailedOrThrowingRestoreStillFlushesAndNeverRollsBack()
        {
            foreach (string failure in new[] { "restore", "restore-throws", "flush", "flush-throws", "readback" })
            WithPage((page, address, before) =>
            {
                MakeExecutable(page);
                int calls = 0, flushes = 0;
                FixesWheatMemory.ProtectMemory protect = (IntPtr p, UIntPtr n, uint v, out uint old) =>
                {
                    bool ok = VirtualProtect(p, n, v, out old);
                    if (++calls == 2)
                    {
                        if (failure == "restore") return false;
                        if (failure == "restore-throws") throw new IOException("Simulated OS boundary failure");
                        if (failure == "readback")
                        {
                            VirtualProtect(p, n, 0x40, out _);
                            Marshal.WriteByte(p, 0x77);
                            VirtualProtect(p, n, v, out _);
                        }
                    }
                    return ok;
                };
                Func<IntPtr, UIntPtr, bool> flush = (p, n) =>
                {
                    flushes++;
                    if (failure == "flush-throws") throw new IOException("Simulated cache failure");
                    return failure != "flush";
                };
                Action run = () => FixesWheatMemory.WriteRangeCore(address, new[] { before[0] }, new byte[] { 0x55 }, protect, flush);
                if (failure.EndsWith("throws", StringComparison.Ordinal)) Assert.ThrowsExactly<IOException>(run);
                else Assert.ThrowsExactly<InvalidOperationException>(run);
                Assert.AreEqual(2, calls);
                Assert.AreEqual(1, flushes);
                Assert.AreEqual(failure == "readback" ? (byte)0x77 : (byte)0x55, Marshal.ReadByte(page),
                    "No rollback after the write boundary.");
                Assert.AreEqual(before[1], Marshal.ReadByte(page, 1), "Adjacent byte changed");
            });
        }

        private static void MakeExecutable(IntPtr page)
        {
            if (!VirtualProtect(page, (UIntPtr)4096, 0x20, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        private static void WithPage(Action<IntPtr, ulong, byte[]> action)
        {
            IntPtr page = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
            if (page == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] before = new byte[96];
                for (int i = 0; i < before.Length; i++) before[i] = (byte)(0xA0 + i);
                Marshal.Copy(before, 0, page, before.Length);
                action(page, unchecked((ulong)page.ToInt64()), before);
            }
            finally { Assert.IsTrue(VirtualFree(page, UIntPtr.Zero, 0x8000)); }
        }
    }
}

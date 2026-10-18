using System;
using System.ComponentModel;
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
        public void ActualWriterRejectsUnsafeMemoryChangesOneByteAndRestoresProtection()
        {
            // Private, unpublished storage only. No game address, hook, trampoline
            // or instruction stream is executed or modified by this fixture.
            IntPtr page = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
            if (page == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                byte[] before = new byte[16];
                for (int i = 0; i < before.Length; i++) before[i] = (byte)(0xA0 + i);
                Marshal.Copy(before, 0, page, before.Length);
                ulong address = unchecked((ulong)page.ToInt64());
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteOperand(0, 0x78));
                Assert.ThrowsExactly<InvalidOperationException>(() => FixesWheatMemory.WriteOperand(address + 12, 0x78),
                    "A writable data page is not an audited executable range.");
                byte[] actual = new byte[before.Length];
                Marshal.Copy(page, actual, 0, actual.Length);
                CollectionAssert.AreEqual(before, actual, "Refused writes changed storage.");
                if (!VirtualProtect(page, (UIntPtr)4096, 0x20, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                FixesWheatMemory.WriteOperand(address + 12, 0x78);
                actual = FixesWheatMemory.Read(address, before.Length);
                before[12] = 0x78;
                CollectionAssert.AreEqual(before, actual, "The production writer changed more than its one-byte operand.");
                // Reapplying the same protection observes its previous value without
                // introducing any published code or rollback of executable hooks.
                if (!VirtualProtect(page, (UIntPtr)4096, 0x20, out uint restored)) throw new Win32Exception(Marshal.GetLastWin32Error());
                Assert.AreEqual(0x20u, restored, "Original execute/read protection was not restored.");
            }
            finally
            {
                if (!VirtualFree(page, UIntPtr.Zero, 0x8000)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}

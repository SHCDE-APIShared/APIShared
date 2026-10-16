using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace APIShared.Compatibility
{
    internal static class FixesWheatMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryInfo
        {
            internal IntPtr BaseAddress, AllocationBase;
            internal uint AllocationProtect;
            internal UIntPtr RegionSize;
            internal uint State, Protect, Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern UIntPtr VirtualQuery(IntPtr address, out MemoryInfo info, UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool VirtualProtect(IntPtr address, UIntPtr size, uint protection, out uint old);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        private static uint Check(ulong address, int length, bool executable)
        {
            if (IntPtr.Size != 8 || address == 0 || length <= 0 || address > long.MaxValue - (ulong)length)
                throw new InvalidOperationException("Invalid memory range");
            if (VirtualQuery(new IntPtr((long)address), out MemoryInfo info,
                (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQuery refused");
            ulong regionStart = (ulong)info.BaseAddress.ToInt64();
            ulong size = info.RegionSize.ToUInt64();
            uint basic = info.Protect & 0xFF;
            bool readable = basic == 2 || basic == 4 || basic == 8 || basic == 0x20 || basic == 0x40 || basic == 0x80;
            if (info.State != 0x1000 || (info.Protect & 0x100) != 0 || !readable ||
                address < regionStart || address - regionStart > size ||
                (ulong)length > size - (address - regionStart) ||
                (executable && basic != 0x20 && basic != 0x40 && basic != 0x80))
                throw new InvalidOperationException("Unreadable, non-executable or split memory range");
            return info.Protect;
        }

        internal static byte[] Read(ulong address, int length, bool executable = true)
        {
            Check(address, length, executable);
            byte[] bytes = new byte[length];
            Marshal.Copy(new IntPtr((long)address), bytes, 0, length);
            return bytes;
        }

        internal static void WriteOperand(ulong address, byte value)
        {
            uint observed = Check(address, 1, true);
            IntPtr pointer = new IntPtr((long)address);
            if (!VirtualProtect(pointer, (UIntPtr)1, 0x40, out uint old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect refused before write");
            bool restored = false, flushed = false;
            try
            {
                if (old != observed) throw new InvalidOperationException("Memory protection changed during preflight");
                Marshal.WriteByte(pointer, value);
            }
            finally
            {
                restored = VirtualProtect(pointer, (UIntPtr)1, old, out _);
                flushed = FlushInstructionCache(GetCurrentProcess(), pointer, (UIntPtr)1);
                if (!restored || !flushed)
                    throw new InvalidOperationException("Post-write protection/cache failure; bytes may be corrected, protection is NOT confirmed; restored=" + restored + ", flushed=" + flushed);
            }
            if (Check(address, 1, true) != old)
                throw new InvalidOperationException("Post-write protection differs; protection is NOT confirmed");
        }
    }
}

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

        // Reads up to maximum bytes of one committed region; the decoder determines the actual length.
        internal static byte[] ReadAvailable(ulong address, int maximum, bool executable = true)
        {
            if (IntPtr.Size != 8 || address == 0 || maximum <= 0)
                throw new InvalidOperationException("Invalid memory range");
            if (VirtualQuery(new IntPtr((long)address), out MemoryInfo info,
                (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQuery refused");
            ulong regionEnd = (ulong)info.BaseAddress.ToInt64() + info.RegionSize.ToUInt64();
            if (address >= regionEnd) throw new InvalidOperationException("Address outside its memory region");
            return Read(address, (int)Math.Min((ulong)maximum, regionEnd - address), executable);
        }

        // The injectable OS calls exercise denial/restoration/cache failures against the same
        // production writer. Production always supplies the real Win32 functions below.
        internal delegate bool ProtectMemory(IntPtr address, UIntPtr size, uint protection, out uint old);

        internal static void WriteRange(ulong address, byte[] expected, byte[] replacement) =>
            WriteRangeCore(address, expected, replacement, VirtualProtect,
                (pointer, length) => FlushInstructionCache(GetCurrentProcess(), pointer, length));

        internal static void WriteRangeCore(ulong address, byte[] expected, byte[] replacement,
            ProtectMemory protect, Func<IntPtr, UIntPtr, bool> flush)
        {
            if (expected == null || replacement == null || expected.Length == 0 || expected.Length > 256 ||
                expected.Length != replacement.Length || protect == null || flush == null)
                throw new InvalidOperationException("Invalid bounded wheat write plan");
            int length = expected.Length;
            uint observed = Check(address, length, true);
            FixesWheatAnalysis.RequireEqual(Read(address, length), expected, "wheat write preimage");
            IntPtr pointer = new IntPtr((long)address);
            if (!protect(pointer, (UIntPtr)length, 0x40, out uint old))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualProtect refused before write");
            bool restored = false, flushed = false;
            try
            {
                if (old != observed) throw new InvalidOperationException("Memory protection changed during preflight");
                FixesWheatAnalysis.RequireEqual(Read(address, length), expected, "wheat write preimage under protection");
                Marshal.Copy(replacement, 0, pointer, length);
            }
            finally
            {
                // Even a failing restore must not skip instruction-cache synchronization.
                try { restored = protect(pointer, (UIntPtr)length, old, out _); }
                finally { flushed = flush(pointer, (UIntPtr)length); }
                if (!restored || !flushed)
                    throw new InvalidOperationException("Post-write protection/cache failure; bytes may be corrected, protection is NOT confirmed; restored=" + restored + ", flushed=" + flushed);
            }
            if (Check(address, length, true) != old)
                throw new InvalidOperationException("Post-write protection differs; protection is NOT confirmed");
            FixesWheatAnalysis.RequireEqual(Read(address, length), replacement, "wheat write read-back");
        }
    }
}

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace APIShared.Internal
{
    // Reads generated stubs and trampolines for validation without crossing the end of their committed,
    // readable memory region. A shorter result decodes as invalid and fails the caller's check instead
    // of raising an access violation.
    internal static class NativeCodeReader
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

        internal static byte[] ReadBounded(IntPtr address, int maximum)
        {
            if (address == IntPtr.Zero || maximum <= 0) throw new ArgumentException("A code address and a positive length are required.");
            if (VirtualQuery(address, out MemoryInfo info, (UIntPtr)Marshal.SizeOf(typeof(MemoryInfo))) == UIntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "VirtualQuery refused");
            uint basic = info.Protect & 0xFF;
            bool readable = basic == 0x02 || basic == 0x04 || basic == 0x08 || basic == 0x20 || basic == 0x40 || basic == 0x80;
            if (info.State != 0x1000 || (info.Protect & 0x100) != 0 || !readable)
                throw new InvalidOperationException("Code memory is not committed and readable.");
            ulong start = unchecked((ulong)address.ToInt64());
            ulong end = unchecked((ulong)info.BaseAddress.ToInt64()) + info.RegionSize.ToUInt64();
            if (start >= end) throw new InvalidOperationException("Address outside its memory region.");
            var bytes = new byte[(int)Math.Min((ulong)maximum, end - start)];
            Marshal.Copy(address, bytes, 0, bytes.Length);
            return bytes;
        }
    }
}

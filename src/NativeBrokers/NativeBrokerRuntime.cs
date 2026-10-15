using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;

namespace APIShared.NativeBrokers
{
    // Audited native contract of both brokered entries, bound from APIShared's LibraryLoaded handler.
    // Binding validates the build only; each entry is checked live and patched at its first registration.
    internal static class NativeBrokerRuntime
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        // RedBird's default scan limit, which every other detour owner of these two entries uses.
        internal const int ScanLimit = 16384;

        // 0xE2610: push rbp; push r12; push r13; push r14; lea rbp,[rsp-0x888] (16 bytes, displaced by the
        // 14-byte Absolute patch); continuation sub rsp,0x988. No RIP operand or branch in the displaced span.
        internal const int PclReachabilityRva = 0xE2610, PclReachabilityDisplaced = 16;
        internal static readonly byte[] PclReachabilityBytes =
        {
            0x40,0x55, 0x41,0x54, 0x41,0x55, 0x41,0x56, 0x48,0x8D,0xAC,0x24,0x78,0xF7,0xFF,0xFF,
            0x48,0x81,0xEC,0x88,0x09,0x00,0x00
        };

        // 0xD90D0: mov [rsp+8],rbx; mov [rsp+0x10],rbp; mov [rsp+0x18],rsi (15 bytes displaced);
        // continuation push rdi. No RIP operand or branch in the displaced span.
        internal const int TileRefreshRva = 0xD90D0, TileRefreshDisplaced = 15;
        internal static readonly byte[] TileRefreshBytes =
        {
            0x48,0x89,0x5C,0x24,0x08, 0x48,0x89,0x6C,0x24,0x10, 0x48,0x89,0x74,0x24,0x18,
            0x57
        };

        // Process-lifetime unmanaged demand bytes: the permanent bridges embed these addresses.
        private static readonly IntPtr demand = Allocate();
        internal static IntPtr PclReachabilityDemand => demand;
        internal static IntPtr TileRefreshDemand => demand + 1;

        private static IntPtr Allocate()
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            for (int index = 0; index < 16; index++) Marshal.WriteByte(memory, index, 0);
            return memory;
        }

        internal static void Bind(IntPtr module, int imageLength, string hash, ManualLogSource log, NativeOwnershipRegistry ownership)
        {
            Bind(PclReachabilityBroker.Process.Site, module, imageLength, hash, log, ownership, PclReachabilityBytes.Length);
            Bind(TileRefreshBroker.Process.Site, module, imageLength, hash, log, ownership, TileRefreshBytes.Length);
        }

        private static void Bind<T>(NativeBrokerSite<T> site, IntPtr module, int imageLength, string hash, ManualLogSource log,
            NativeOwnershipRegistry ownership, int window) where T : Delegate
        {
            if (!string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                site.MarkUnavailable("CrusaderDE.dll " + (string.IsNullOrEmpty(hash) ? "hash unavailable" : hash) +
                    " is not the audited build " + AuditedHash + ".");
            else if (module == IntPtr.Zero || site.Rva + window > imageLength)
                site.MarkUnavailable("The entry lies outside the loaded CrusaderDE.dll image.");
            else
                site.Bind(unchecked((ulong)module.ToInt64() + (uint)site.Rva), ownership, log);
        }
    }
}

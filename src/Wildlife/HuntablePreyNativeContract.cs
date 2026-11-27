using System;
using System.Runtime.InteropServices;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Hash-bound field contract of the Hunter reservation word. No hook, no executable target: the
    // instructions below are only compared in the unmodified load image (other mods hook nearby,
    // e.g. at 0x12FE63). An unknown image leaves huntable prey unavailable; there is no pattern
    // fallback because the word's meaning was audited for this image only (see UpdateToNewDLL.md).
    internal static unsafe class HuntablePreyNativeContract
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        // Unit slots are 0x490 bytes; the Extender's public GameUnit* starts 0x65C into the native
        // slot address used by these instructions (same relation as AI state 0x918 -> public 0x2BC and
        // target global 0x9F8 -> public 0x39C).
        internal const int NativeReservationDisplacement = 0xAA4;
        internal const int PublicHeader = 0x65C;
        internal const int PublicReservationOffset = NativeReservationDisplacement - PublicHeader;
        internal const int UnitSlotSize = 0x490;

        private static readonly Anchor[] Anchors =
        {
            // Hunter state 0: mov word ptr [r8+r13+0xAA4], r14w (lease 2).
            new Anchor(0x12FE63, new byte[] { 0x66, 0x47, 0x89, 0xB4, 0x28, 0xA4, 0x0A, 0x00, 0x00 }),
            // Deer/goat update: movzx eax, word ptr [rbx+rsi+0xAA4] ... mov word ptr [rbx+rsi+0xAA4], ax.
            new Anchor(0x156203, new byte[] { 0x0F, 0xB7, 0x84, 0x33, 0xA4, 0x0A, 0x00, 0x00 }),
            new Anchor(0x156213, new byte[] { 0x66, 0x89, 0x84, 0x33, 0xA4, 0x0A, 0x00, 0x00 }),
            // Hunter query, unit loop base mgr+0xD88: cmp word ptr [rbx+0x1AC], 0.
            new Anchor(0x18AFFD, new byte[] { 0x66, 0x83, 0xBB, 0xAC, 0x01, 0x00, 0x00, 0x00 }),
            // Unit dispatcher 0x182B00 (rbx = unit manager): the per-type update loop (table call at
            // 0x18410C) ends with cmp edx, [rbx]; jl, so only IDs below the manager's first DWORD update.
            new Anchor(0x184E76, new byte[] { 0x3B, 0x13, 0x0F, 0x8C })
        };
        internal const int UnitManagerRva = 0x67E8400;

        internal static bool TryValidate(CrusaderLibraryLoadContext context, string hash, out string reason)
        {
            if (!string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
            { reason = "The Hunter reservation field is audited only for native image " + AuditedHash + "."; return false; }
            if (sizeof(GameUnit) != UnitSlotSize)
            { reason = "The loaded GameUnit layout is not the audited 0x490-byte slot."; return false; }
            if (context == null)
            { reason = "The native load image is unavailable."; return false; }
            if (Marshal.OffsetOf(typeof(GameUnitManager), nameof(GameUnitManager.GameUnitArray)).ToInt32() != PublicHeader ||
                Marshal.OffsetOf(typeof(GameUnitManager), nameof(GameUnitManager.r_NextUnitId)).ToInt32() != 0 ||
                (ulong)GameUnitManagerAPI.Instance.GetUnitManager().Pointer != unchecked((ulong)context.ModuleHandle.ToInt64()) + UnitManagerRva)
            { reason = "The Extender's unit manager does not match the audited base, unit array and loop bound."; return false; }
            ReadOnlySpan<byte> image = context.Memory;
            foreach (var anchor in Anchors)
                if (anchor.Rva < 0 || anchor.Rva + anchor.Bytes.Length > image.Length ||
                    !image.Slice(anchor.Rva, anchor.Bytes.Length).SequenceEqual(new ReadOnlySpan<byte>(anchor.Bytes)))
                {
                    reason = "The Hunter reservation instruction at RVA 0x" + anchor.Rva.ToString("X") + " differs from the audit.";
                    return false;
                }
            reason = "";
            return true;
        }

        private readonly struct Anchor
        {
            internal readonly int Rva;
            internal readonly byte[] Bytes;
            internal Anchor(int rva, byte[] bytes) { Rva = rva; Bytes = bytes; }
        }
    }
}

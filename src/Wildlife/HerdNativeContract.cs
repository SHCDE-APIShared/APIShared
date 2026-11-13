using System;
using System.Runtime.InteropServices;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Hash-bound layout contract of the herd records (no hook here). The Extender's GameTribe begins 0x2A
    // after a raw tribe record; its named fields must sit where the audited instructions below read the
    // raw record. The two unnamed fields (kind, flight) are pinned by those instructions only, so herds stay
    // bound to the audited image; an unknown image leaves them unavailable (see UpdateToNewDLL.md "Herds").
    internal static unsafe class HerdNativeContract
    {
        internal const int RawHeader = 0x2A, RecordSize = 0x688;

        private static readonly (int Rva, byte[] Bytes)[] Anchors =
        {
            // Spawn 0x11E150: entry; deer art (cmp esi, 0xDC); kind 0xD written to raw +0x42.
            (HerdRuntime.SpawnRva, HerdRuntime.SpawnEntryBytes),
            (0x11E1D9, WildlifeInlineSite.Hex("81FEDC000000")),
            (0x11E1FD, WildlifeInlineSite.Hex("B80D000000 6689442942")),
            // Herd dispatcher 0x117520: record state raw +0x40 == 2, then the kind word raw +0x42.
            (0x11756D, WildlifeInlineSite.Hex("66837C1A4002")),
            (0x1175D9, WildlifeInlineSite.Hex("410FBF4C1842")),
            // Deer update: flight word raw +0x5F4 starts at 1 when the sector shows a threat.
            (0x124F75, WildlifeInlineSite.Hex("6683BBF405000000")),
            // Route builder: route index raw +0x5DC and count raw +0x5DE zeroed, waypoints from raw +0x5B4.
            (0x1283D6, WildlifeInlineSite.Hex("664489A42EFC6CCC07")),
            (0x1283E1, WildlifeInlineSite.Hex("488DB6FE6CCC07")),
            (0x128A20, WildlifeInlineSite.Hex("6641898488D46CCC07"))
        };

        // lea rcx, [rip+disp] anchors: the tribe manager passed to the herd maintenance (0xCE272) and
        // the sector grid read by the deer update (0x124EFA).
        private const int ManagerLeaRva = 0xCE272, GridLeaRva = 0x124EFA;

        internal static bool TryValidate(CrusaderLibraryLoadContext context, string hash, out string reason)
        {
            if (!string.Equals(hash, HuntablePreyNativeContract.AuditedHash, StringComparison.OrdinalIgnoreCase))
            { reason = "Herds are audited only for native image " + HuntablePreyNativeContract.AuditedHash + "."; return false; }
            if (context == null) { reason = "The native load image is unavailable."; return false; }
            if (!TryValidateLayout(out reason)) return false;
            ulong image = unchecked((ulong)context.ModuleHandle.ToInt64());
            if ((ulong)GameTribeManagerAPI.Instance.GetTribeManager().Pointer != image + HerdRuntime.ManagerRva)
            { reason = "The Extender's tribe manager is not the audited herd manager."; return false; }
            ReadOnlySpan<byte> memory = context.Memory;
            foreach (var (rva, bytes) in Anchors)
                if (rva + bytes.Length > memory.Length || !memory.Slice(rva, bytes.Length).SequenceEqual(new ReadOnlySpan<byte>(bytes)))
                { reason = "The herd instruction at RVA 0x" + rva.ToString("X") + " differs from the audit."; return false; }
            if (LeaTarget(memory, ManagerLeaRva) != HerdRuntime.ManagerRva || LeaTarget(memory, GridLeaRva) != HerdRuntime.GridRva)
            { reason = "The herd manager or sector grid address differs from the audit."; return false; }
            reason = "";
            return true;
        }

        // The referenced Extender's tribe structures against the raw offsets the anchors read.
        internal static bool TryValidateLayout(out string reason)
        {
            if (sizeof(GameTribe) != RecordSize ||
                Marshal.OffsetOf(typeof(GameTribeManager), nameof(GameTribeManager.GameTribeArray)).ToInt32() != RawHeader)
            { reason = "The Extender's tribe layout is not the audited 0x688-byte record behind a 0x2A header."; return false; }
            if (!Field(nameof(GameTribe.r_PlayerIdOwner), 0x2C) || !Field(nameof(GameTribe.r_GlobalId), 0x34) ||
                !Field(nameof(GameTribe.r_AliveState), 0x40) || !Field(nameof(GameTribe.r_LeaderUnitId), 0x5A) ||
                !Field(nameof(GameTribe.r_UnitsInGroup), 0x5C) || !Field(nameof(GameTribe.r_UnitIdsInGroupBitfield), 0x60) ||
                !Field(nameof(GameTribe.r_PatrolPoint1TileX), 0x5B4) || !Field(nameof(GameTribe.r_PatrolCurrentTargetIndex), 0x5DC) ||
                !Field(nameof(GameTribe.r_CurrentPatrolPoints), 0x5DE) ||
                HerdRuntime.KindOffset + RawHeader != 0x42 || HerdRuntime.FlightOffset + RawHeader != 0x5F4)
            { reason = "The Extender's tribe fields differ from the audited herd record."; return false; }
            reason = "";
            return true;
        }

        // Raw record offset of a named GameTribe field.
        private static bool Field(string name, int raw) => Marshal.OffsetOf(typeof(GameTribe), name).ToInt32() + RawHeader == raw;

        // lea rcx, [rip+disp32] (48 8D 0D disp32): target RVA, or -1 for another instruction.
        private static long LeaTarget(ReadOnlySpan<byte> memory, int rva)
        {
            if (rva + 7 > memory.Length || memory[rva] != 0x48 || memory[rva + 1] != 0x8D || memory[rva + 2] != 0x0D) return -1;
            return rva + 7L + BitConverter.ToInt32(memory.Slice(rva + 3, 4).ToArray(), 0);
        }
    }
}

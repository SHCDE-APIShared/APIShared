using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Sight;
using RedBird.X64.Extensions;

// Executes Vanilla's own sight wrapper (0xA06F0), sight core (0x9E350) and obstacle-height helper (0x6B990) of the
// private image copy on a small synthetic tile row, with APIShared's building-height dispatch override
// (Sight.SightBlockingBuildingsNative) patched into the copy at 0x6BA0C. Checks the SightBlockingBuildings and
// LineOfSight contracts: a dormant table keeps Vanilla (the Hunter's hut is transparent to sight), an active entry
// makes it block like an ordinary building, other types and the projectile mode keep their heights, the private
// context changes only at +0xC, and the core's eighth argument switches obstacles off. Synthetic map data only;
// in-game acceptance remains separate.
internal static partial class Program
{
    private const int SightWrapperRva = 0xA06F0, SightCoreRva = 0x9E350, SightHelperRva = 0x6B990;
    private const int SightReaderRva = 0x6BA0C, SightReaderLength = 17;
    // Tile manager passed by the core (lea at 0x9E3FC), row lookup table (stride 12), building records.
    private const int SightTilesRva = 0x405EDB0, SightRowTableRva = 0x402FF2C, SightBuildingsRva = 0x64CCBB0;
    private const int SightTileFlags = 0x898400, SightTileBuilding = 0xB0BCA0, SightTileHeight = 0xD7E5A0;
    private const int SightBuildingStride = 0x32C, SightBuildingType = 0x12E, SightBuildingFlag = 0x400;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SightWrapper(IntPtr context, int sx, int sy, int sh, int ex, int ey, int eh);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SightCore(IntPtr context, int sx, int sy, int sh, int ex, int ey, int eh, int noObstacles);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SightHeight(IntPtr tiles, int tile, int mode);

    private static void TestSightBuildingDispatch()
    {
        const int row = 100, firstX = 95, lastX = 115, rowStart = row * 800, terrain = 5;
        const int hutX = 105, ordinaryX = 112, hutId = 5, ordinaryId = 6;
        int count = lastX - firstX + 1;
        int Tile(int x) => rowStart + x;
        var saved = new List<(int Rva, byte[] Bytes)>();
        void Save(int rva, int length) => saved.Add((rva, Read(rva, length)));
        Save(SightRowTableRva + row * 12, 4);
        Save(SightTilesRva + SightTileFlags + Tile(firstX) * 4, count * 4);
        Save(SightTilesRva + SightTileBuilding + Tile(firstX) * 2, count * 2);
        Save(SightTilesRva + SightTileHeight + Tile(firstX), count);
        Save(SightBuildingsRva + hutId * SightBuildingStride + SightBuildingType, 2);
        Save(SightBuildingsRva + ordinaryId * SightBuildingStride + SightBuildingType, 2);
        Save(SightReaderRva, SightReaderLength);
        IntPtr overrides = Marshal.AllocHGlobal(SightNativeAssembly.DispatchTypeCount);
        IntPtr context = Marshal.AllocHGlobal(32);
        try
        {
            Check(Convert.ToHexString(Read(SightReaderRva, SightReaderLength)) == "410FB68400C4BA0600418B8C80B4BA0600",
                "Sight dispatch reader is the audited pair of table loads.");
            Check(Read(0x6BAC4, 2).SequenceEqual(new byte[] { 0, SightNativeAssembly.OrdinaryHeightCase }),
                "Vanilla sends type 7 (Hunter's hut) to case 0 and type 8 to the ordinary case 3.");

            // One open row of tiles at terrain height 5; a Hunter's hut and an ordinary type-8 building on it.
            Put(SightRowTableRva + row * 12, rowStart);
            for (int x = firstX; x <= lastX; x++)
            {
                Put(SightTilesRva + SightTileFlags + Tile(x) * 4, 0);
                Word(SightTilesRva + SightTileBuilding + Tile(x) * 2, 0);
                Marshal.WriteByte(image, SightTilesRva + SightTileHeight + Tile(x), terrain);
            }
            Put(SightTilesRva + SightTileFlags + Tile(hutX) * 4, SightBuildingFlag);
            Word(SightTilesRva + SightTileBuilding + Tile(hutX) * 2, hutId);
            Word(SightBuildingsRva + hutId * SightBuildingStride + SightBuildingType, SightNativeAssembly.FirstDispatchType);
            Put(SightTilesRva + SightTileFlags + Tile(ordinaryX) * 4, SightBuildingFlag);
            Word(SightTilesRva + SightTileBuilding + Tile(ordinaryX) * 2, ordinaryId);
            Word(SightBuildingsRva + ordinaryId * SightBuildingStride + SightBuildingType, SightNativeAssembly.FirstDispatchType + 1);

            // Patch the copy exactly as the RedBird inline hook does: jmp [rip+0], stub address, NOP padding.
            for (int index = 0; index < SightNativeAssembly.DispatchTypeCount; index++)
                Marshal.WriteByte(overrides, index, SightNativeAssembly.VanillaCase);
            var original = WorkerProductionBonusNativeDefinition.Decode(Read(SightReaderRva, SightReaderLength), Address(SightReaderRva));
            using var stub = new Code(a =>
            {
                SightNativeAssembly.EmitDispatchOverride(a, original, unchecked((ulong)overrides.ToInt64()));
                a.AddUnrestrictedJmp(Address(SightReaderRva + SightReaderLength));
            });
            var jump = new byte[SightReaderLength];
            jump[0] = 0xFF; jump[1] = 0x25;
            BitConverter.GetBytes(unchecked((ulong)stub.Address.ToInt64())).CopyTo(jump, 6);
            for (int index = 14; index < SightReaderLength; index++) jump[index] = 0x90;
            Copy(SightReaderRva, jump);
            FlushInstructionCache(GetCurrentProcess(), image + SightReaderRva, (UIntPtr)SightReaderLength);

            var height = Marshal.GetDelegateForFunctionPointer<SightHeight>(image + SightHelperRva);
            var wrapper = Marshal.GetDelegateForFunctionPointer<SightWrapper>(image + SightWrapperRva);
            var core = Marshal.GetDelegateForFunctionPointer<SightCore>(image + SightCoreRva);
            IntPtr tiles = image + SightTilesRva;
            const int y = row * 8 + 4, viewerX = 100 * 8 + 4, targetX = 110 * 8 + 4;
            const int viewerHeight = terrain + 30, targetHeight = terrain + 26;

            foreach (bool active in new[] { false, true, false })
            {
                Marshal.WriteByte(overrides, 0, active ? SightNativeAssembly.OrdinaryHeightCase : SightNativeAssembly.VanillaCase);
                string state = active ? "active" : "dormant";
                Check(height(tiles, Tile(hutX), 1) == terrain + (active ? 40 : 0),
                    "Sight mode sees the Hunter's hut " + (active ? "with its table height 40" : "as bare terrain") + " (" + state + ").");
                Check(height(tiles, Tile(hutX), 0) == terrain + 40, "Projectile mode keeps the hut's table height (" + state + ").");
                Check(height(tiles, Tile(ordinaryX), 1) == terrain + 40 && height(tiles, Tile(ordinaryX), 0) == terrain + 40,
                    "Other building types keep their own height (" + state + ").");
                Check(height(tiles, Tile(firstX), 1) == terrain, "Open tiles keep the terrain height (" + state + ").");

                int forward = CoreGuarded(core, context, viewerX, y, viewerHeight, targetX, y, targetHeight, 0);
                int backward = CoreGuarded(core, context, targetX, y, targetHeight, viewerX, y, viewerHeight, 0);
                int both = WrapperGuarded(wrapper, context, viewerX, y, viewerHeight, targetX, y, targetHeight);
                if (active)
                    Check(forward == 0 && backward == 0 && both == 0, "An active hut blocks sight in both directions.");
                else
                    Check(forward > 0 && backward > 0 && both == forward && LineOfSightRule(both),
                        "A dormant hut is transparent: Vanilla sees through it (forward=" + forward + ", backward=" + backward + ").");
                Check(CoreGuarded(core, context, viewerX, y, viewerHeight, targetX, y, targetHeight, 1) > 0,
                    "A non-zero eighth core argument switches every obstacle check off (" + state + ").");
            }

            // An ordinary building between viewer and target blocks whatever the override says.
            Check(WrapperGuarded(wrapper, context, viewerX, y, viewerHeight, 114 * 8 + 4, y, targetHeight) == 0,
                "An ordinary building on the line blocks sight independent of the override.");
            Check(WrapperGuarded(wrapper, context, viewerX, y, viewerHeight, viewerX, y, targetHeight) == 100000,
                "Identical X and Y return the same-point result.");
        }
        finally
        {
            foreach (var (rva, bytes) in saved) Copy(rva, bytes);
            FlushInstructionCache(GetCurrentProcess(), image + SightReaderRva, (UIntPtr)SightReaderLength);
            Marshal.FreeHGlobal(context);
            // The reader is restored, so no code reads the table any more.
            Marshal.FreeHGlobal(overrides);
        }
    }

    // LineOfSight.IsVisibleForTargetQuery, restated: the Hunter target search accepts results 1..0x1B0.
    private static bool LineOfSightRule(int result) => unchecked((uint)(result - 1)) < 0x1B0;

    private static int WrapperGuarded(SightWrapper wrapper, IntPtr buffer, int sx, int sy, int sh, int ex, int ey, int eh)
    {
        PrepareSightContext(buffer);
        int result = wrapper(buffer + 8, sx, sy, sh, ex, ey, eh);
        CheckSightContext(buffer);
        return result;
    }

    private static int CoreGuarded(SightCore core, IntPtr buffer, int sx, int sy, int sh, int ex, int ey, int eh, int noObstacles)
    {
        PrepareSightContext(buffer);
        int result = core(buffer + 8, sx, sy, sh, ex, ey, eh, noObstacles);
        CheckSightContext(buffer);
        return result;
    }

    // Canaries around a zeroed 16-byte context, as LineOfSightNative passes it.
    private static void PrepareSightContext(IntPtr buffer)
    {
        Marshal.WriteInt32(buffer, 0, 0x13579BDF);
        Marshal.WriteInt32(buffer, 4, 0x2468ACE0);
        for (int offset = 8; offset < 24; offset += 4) Marshal.WriteInt32(buffer, offset, 0);
        Marshal.WriteInt32(buffer, 24, 0x55AA33CC);
        Marshal.WriteInt32(buffer, 28, unchecked((int)0xAA55CC33));
    }

    private static void CheckSightContext(IntPtr buffer) =>
        Check(Marshal.ReadInt32(buffer, 0) == 0x13579BDF && Marshal.ReadInt32(buffer, 4) == 0x2468ACE0 &&
            Marshal.ReadInt32(buffer, 8) == 0 && Marshal.ReadInt32(buffer, 12) == 0 && Marshal.ReadInt32(buffer, 16) == 0 &&
            Marshal.ReadInt32(buffer, 24) == 0x55AA33CC && Marshal.ReadInt32(buffer, 28) == unchecked((int)0xAA55CC33),
            "Vanilla's sight routines write only context+0xC.");
}

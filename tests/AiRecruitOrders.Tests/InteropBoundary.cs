using System;
using System.Runtime.InteropServices;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace SHCDESE.Interop.Enums
{
    internal enum AliveState : ushort { IsAlive = 2 }
    internal enum eChimps : ushort { CHIMP_TYPE_LORD = 55 }
    [Flags] internal enum TilePropertyFlag : uint
    { GoodsyardRelated = 2, IsWall = 0x100, CrenelatedLow = 0x200, PlannedMoat = 0x4000, CrenelatedHigh = 0x400000, IsMoat = 0x40000000 }
    internal enum MoatWorkType : byte { DigPlannedMoat = 1, FillDugMoat = 2 }
}
namespace SHCDESE.Interop
{
    [StructLayout(LayoutKind.Explicit, Size = 0x490)] internal struct GameUnit
    {
        [FieldOffset(0x8A)] internal eChimps r_UnitChimp;
        [FieldOffset(0x92)] internal ushort r_ControllableForPlayerId;
        [FieldOffset(0x94)] internal uint r_GlobalId;
        [FieldOffset(0x2D4)] internal ushort r_TribeId;
        [FieldOffset(0x426)] internal ushort r_AITribeRole;
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x5E4)] internal struct InternalAIC { }
    [StructLayout(LayoutKind.Explicit, Size = 0x583C)] internal unsafe struct GamePlayerResources
    {
        [FieldOffset(0x21F8)] internal uint r_LordUnitId;
        [FieldOffset(0x21FC)] internal uint r_LordUnitGlobalId;
        [FieldOffset(0x2300)] internal uint r_AILordMinusOne;
        [FieldOffset(0x2BA4)] internal uint r_AISiegeState;
        [FieldOffset(0x2BD8)] internal uint r_SiegeAttackTargetPlayerId;
        [FieldOffset(0x384C)] internal uint r_AIAllyOrderMode;
        [FieldOffset(0x3924)] internal uint r_AISiegeAttempts;
        [FieldOffset(0x48A0)] internal fixed ushort r_AITribeIdsByStorageRole[300];
        [FieldOffset(0x4AF8)] internal fixed uint r_AITribeGlobalIdsByStorageRole[300];
    }
    [StructLayout(LayoutKind.Explicit, Size = 0x32C)] public struct GameBuilding
    {
        [FieldOffset(0xCC)] internal AliveState r_AliveState;
        [FieldOffset(0xD6)] internal ushort r_PlayerIdOwner;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct MoatWorkTask
    {
        internal int r_TileId; internal short r_TileX, r_TileY;
        internal short Mode, Unknown; internal byte r_OwnerPlayerId, Progress;
        internal MoatWorkType r_WorkType; internal byte Penalty;
    }
    internal unsafe struct NativePointer<T> where T : unmanaged { internal T* Pointer; }
}
namespace SHCDESE.API
{
    internal static unsafe class Fixture
    {
        internal static IntPtr Module;
        internal static GamePlayerResources* Player(int id) => (GamePlayerResources*)((byte*)Module + 0x379ADD0 + id * 0x583C);
    }
    internal sealed unsafe class GamePlayerManagerAPI
    {
        internal static readonly GamePlayerManagerAPI Instance = new GamePlayerManagerAPI();
        internal bool TryGetPlayerResourcesById(int id, out GamePlayerResources* resources)
        { resources = id >= 1 && id <= 8 ? Fixture.Player(id) : null; return resources != null; }
    }
    public sealed unsafe class GameBuildingManagerAPI
    {
        internal static readonly GameBuildingManagerAPI Instance = new GameBuildingManagerAPI();
        public bool IsValidId(int id) => id >= 1 && id < 10000;
        internal bool TryGetBuildingById(int id, out GameBuilding* building)
        { building = id >= 1 && id < 10000 ? (GameBuilding*)((byte*)Fixture.Module + 0x64CCBB0 + id * 0x32C + 0x5C) : null; return building != null; }
    }
    internal sealed unsafe class GameTileManagerAPI
    {
        internal static readonly GameTileManagerAPI Instance = new GameTileManagerAPI();
        internal bool TryGetMoatWorkTaskAtTile(int x, int y, out NativePointer<MoatWorkTask> task)
        {
            MoatWorkTask* value = (MoatWorkTask*)((byte*)Fixture.Module + 0x100000);
            bool found = value->r_TileX == x && value->r_TileY == y && value->r_OwnerPlayerId != 0;
            task = new NativePointer<MoatWorkTask> { Pointer = found ? value : null }; return found;
        }
        internal bool HasTilePropertyFlag(int id, TilePropertyFlag flag) =>
            ((*(uint*)((byte*)Fixture.Module + 0x48F71B0 + id * 4)) & (uint)flag) != 0;
    }
}
namespace APIShared.Units.AiMilitary
{
    internal static unsafe class AiMilitaryCatalog
    {
        internal static bool TryReadAic(int owner, out InternalAIC* aic)
        { aic = (InternalAIC*)((byte*)SHCDESE.API.Fixture.Module + 0x366C210); return SHCDESE.API.Fixture.Player(owner)->r_AILordMinusOne == 1; }
    }
}
namespace APIShared.Units.AiMilitary.Native
{
    internal static class AiMilitaryNativeObservers
    {
        internal static int Owner = 1, Target = 2;
        internal static bool TryGetGlobalTaskOwner(out int owner, out int target, out long epoch)
        { owner = Owner; target = Target; epoch = 1; return owner != 0; }
    }
}

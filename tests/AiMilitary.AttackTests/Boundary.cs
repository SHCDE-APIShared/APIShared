using System;
using System.Reflection;
using System.Runtime.InteropServices;
using SHCDESE.Interop;
using APIShared.Units.AiMilitary.Native;

// Controlled external native/Extender boundaries. The production member initializer is
// linked unchanged. These tests establish its writes and refusal paths, not native ABI
// or in-game acceptance (the hash-bound native audit is separate).
namespace APIShared.Units.AiMilitary.Native
{
    internal sealed unsafe class AiMilitaryNativeBackend : IDisposable
    {
        internal const int UnitStride = 0x490;
        internal readonly IntPtr Module = Marshal.AllocHGlobal(0x9000000);
        internal IntPtr UnitManager => Module + 0x67E8400;
        internal IntPtr TribeManager => Module + 0x7CC6720;
        internal bool VetoRoute;
        internal int RouteCalls;
        internal long WorldGeneration { get; set; } = 1;
        internal bool IsWorldCurrent(long world) => world == WorldGeneration;
        internal AiMilitaryNativeBackend()
        {
            new Span<byte>((void*)Module, 0x9000000).Clear();
            for (int y = 0; y < 800; y++) *(int*)((byte*)Module + 0x402FF2C + y * 12) = y * 800;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GroupQueryDelegate(IntPtr manager, int group);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int UnitQueryDelegate(IntPtr manager, int unit);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void UnitActionDelegate(IntPtr manager, int unit);
        internal byte* NativeRawUnit(int id) => (byte*)UnitManager + id * UnitStride;
        internal byte* Tribe(AiNativeGroup group) => (byte*)TribeManager + group.Id * 0x688;
        internal bool IsCurrent(AiNativeGroup group) => group.Id == 1 && group.GlobalId == 123 && group.Owner == 1;
        internal bool HasMember(AiNativeGroup group, int id) => IsCurrent(group) && id == 1;
        internal bool TryUnit(int id, uint global, out GameUnit* unit, out string reason)
        {
            unit = id >= 1 && id <= 3 ? (GameUnit*)(NativeRawUnit(id) + 0x65C) : null;
            reason = "stale unit";
            if (unit == null || unit->r_AliveState != SHCDESE.Interop.Enums.AliveState.IsAlive || global != 0 && unit->r_GlobalId != global) return false;
            reason = ""; return true;
        }
        internal bool IsMapPosition(int x, int y) => (uint)x < 800 && (uint)y < 800;
        internal bool TryTilePosition(int tile, out int x, out int y)
        { x = tile % 800; y = tile / 800; return tile > 0 && IsMapPosition(x, y); }
        internal bool TryBuildRecruitRoute(int id, uint global, AiNativeGroup group, int x, int y, out string reason)
        {
            RouteCalls++; reason = VetoRoute ? "native veto" : "";
            if (VetoRoute) return false;
            if (id != 1) throw new Exception("A foreign member received a route!");
            *(short*)(NativeRawUnit(id) + 0x744) = (short)x; *(short*)(NativeRawUnit(id) + 0x746) = (short)y;
            *(short*)(NativeRawUnit(id) + 0x754) = 10; return true;
        }
        internal void Unit(int id, eChimps type, uint global)
        {
            byte* raw = NativeRawUnit(id); new Span<byte>(raw + 0x65C, UnitStride).Clear();
            ((GameUnit*)(raw + 0x65C))->r_UnitChimp = type;
            ((GameUnit*)(raw + 0x65C))->r_GlobalId = global;
            ((GameUnit*)(raw + 0x65C))->r_AliveState = SHCDESE.Interop.Enums.AliveState.IsAlive;
            *(short*)(raw + 0x71C) = 10; *(short*)(raw + 0x71E) = 10; *(int*)(raw + 0x72C) = 8010;
            *(short*)((byte*)Module + 0x50EC690 + 8010 * 2) = 1;
        }
        public void Dispose() => Marshal.FreeHGlobal(Module);
    }
    internal sealed class AiMilitaryNativeBindings
    {
        internal const int PredictTargetRva = 0x118310, AttackUnitSearchRva = 0xDBC60, AttackBuildingSearchRva = 0xDA020,
            BuildingPositionSearchRva = 0xDECE0, NormalDistanceSearchRva = 0xDA590, AssassinDistanceSearchRva = 0xD9C40,
            MachineDistanceSearchRva = 0xDB650, AllAssassinsRva = 0x117820, CrewCountRva = 0x198780, ClearPathRva = 0x197950;
        internal T Bind<T>(int rva) where T : Delegate => (T)Delegate.CreateDelegate(typeof(T), typeof(Program).GetMethod(
            rva == PredictTargetRva ? nameof(Program.Predict) : rva == AttackUnitSearchRva ? nameof(Program.UnitCandidates) :
            rva == AttackBuildingSearchRva ? nameof(Program.BuildingCandidates) : rva == BuildingPositionSearchRva ? nameof(Program.RamPosition) :
            rva == MachineDistanceSearchRva ? nameof(Program.Wide) : rva == AllAssassinsRva ? nameof(Program.AllAssassins) :
            rva == CrewCountRva ? nameof(Program.MissingCrew) : rva == ClearPathRva ? nameof(Program.ClearPath) : nameof(Program.Flood), BindingFlags.Static | BindingFlags.Public));
    }
}
namespace SHCDESE.API
{
    internal sealed unsafe class GameBuildingManagerAPI
    {
        internal static readonly GameBuildingManagerAPI Instance = new GameBuildingManagerAPI();
        internal GameBuilding* Building;
        public bool TryGetBuildingById(int id, out GameBuilding* building) { building = Building; return id == 1 && building != null; }
    }
}

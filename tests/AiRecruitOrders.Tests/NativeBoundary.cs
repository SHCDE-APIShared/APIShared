using System;
using System.Runtime.InteropServices;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary.Native
{
    // External engine boundary only. Selection, journalling, receipts and ledger
    // decisions execute the linked production sources, not replacement algorithms.
    internal sealed unsafe partial class AiMilitaryNativeBackend : IDisposable
    {
        internal readonly IntPtr Module = Marshal.AllocHGlobal(0x8000000);
        internal Func<bool> Route;
        internal int PositionResult;
        internal bool Completed;
        internal long WorldGeneration;
        internal bool IsWorldCurrent(long value) => WorldGeneration == value && Orders.AiRecruitMutationReceipts.IsCurrentWorld;
        internal AiMilitaryNativeBackend()
        { new Span<byte>((void*)Module, 0x8000000).Clear(); SHCDESE.API.Fixture.Module = Module; }
        internal byte* NativeRawUnit(int id) => (byte*)Module + 0x67E8400 + id * 0x490;
        internal byte* Tribe(AiNativeGroup group) => (byte*)Module + 0x7CC6720 + group.Id * 0x688;
        internal bool TryTilePosition(int tile, out int x, out int y)
        { x = tile % 800; y = tile / 800; return tile > 0 && tile < 640000; }
        internal bool IsMapPosition(int x, int y) => (uint)x < 800 && (uint)y < 800;
        internal bool TryBuildRecruitRoute(int id, uint global, AiNativeGroup group, int x, int y, out string reason)
        { bool result = Route == null || Route(); reason = result ? string.Empty : "Fixture route rejected"; return result; }
        internal int InitializeJobPosition(int id) => PositionResult;
        internal int EffectiveTileHeight(int tile) => *((byte*)Module + 0x4DDD350 + tile);
        internal bool TryUnit(int id, uint global, out GameUnit* value, out string reason)
        {
            value = id > 0 && id < 10000 ? (GameUnit*)(NativeRawUnit(id) + 0x65C) : null;
            bool valid = value != null && (global == 0 || value->r_GlobalId == global);
            reason = valid ? string.Empty : "Fixture unit identity absent"; return valid;
        }
        internal bool HasMember(AiNativeGroup group, int id) => true;
        internal bool IsCurrent(AiNativeGroup group) => group.Id == 1 && group.Owner == 1 && group.GlobalId == 77;
        internal bool IsGroupMovementCompleted(AiNativeGroup group) => Completed;
        public void Dispose() { Marshal.FreeHGlobal(Module); }
    }
}

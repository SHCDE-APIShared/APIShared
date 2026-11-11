using System;
using System.Runtime.InteropServices;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary.Native
{
    // Recruit-only rollback. Shared reservation/target metadata is journalled by the order handler.
    // The service calls this only for its new unit at a quiescent point, never for arbitrary members.
    internal sealed unsafe class AiRecruitSnapshot
    {
        private readonly byte[] record = new byte[AiMilitaryNativeBackend.UnitStride];
        private readonly byte[] path = new byte[1000];
        private readonly IntPtr pathAddress;
        internal AiRecruitSnapshot(IntPtr module, int unitId, GameUnit* unit)
        {
            pathAddress = module + 0x67E8400 + 0xB4FE78 + unitId * 1000;
            // Native serialization starts at manager+658; SE's public GameUnit
            // view starts four bytes later. Copying its advertised full Size would
            // include the first four bytes of the following native unit record.
            Marshal.Copy((IntPtr)unit - 4, record, 0, record.Length);
            Marshal.Copy(pathAddress, path, 0, path.Length);
        }
        internal bool Committed { get; private set; }
        internal void Commit() { Committed = true; }
        internal void Restore(GameUnit* unit)
        {
            Marshal.Copy(record, 0, (IntPtr)unit - 4, record.Length);
            Marshal.Copy(path, 0, pathAddress, path.Length);
        }
    }
}

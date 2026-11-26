using System;
using System.Buffers.Binary;
using System.IO;
using APIShared.Internal;
using APIShared.Multiplayer;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    internal static unsafe partial class HuntablePreyRuntime
    {
        private const int MeatResyncRowBytes = sizeof(ushort) + sizeof(uint) * 2 + sizeof(int);
        private static bool meatResyncRegistered;

        internal static bool TryEnableMeatResync(out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread || !IsAvailable || !HunterNativeSites.IsAvailable)
            {
                reason = "Hunter repair registration requires initialized Hunter sites and the Unity thread.";
                return false;
            }
            if (meatResyncRegistered) { reason = string.Empty; return true; }
            meatResyncRegistered = MultiplayerResyncData.TryRegister<ButcheredPrey[]>(APISharedPlugin.PluginGuid,
                "wildlife-butchered-prey", 1, sizeof(int) + (HunterNativeSites.UnitCapacity - 1) * MeatResyncRowBytes,
                CaptureButcheredForRepair, data => PrepareButcheredForRepair(data.Span, MatchesHunter),
                CommitButcheredForRepair, out reason);
            return meatResyncRegistered;
        }

        private static byte[] CaptureButcheredForRepair()
        {
            if (System.Threading.Volatile.Read(ref meatDemand) == 0) return Array.Empty<byte>();
            return EncodeButcheredHistory((id, hunter, corpse) => MatchesHunter(id, hunter, corpse));
        }

        private static bool MatchesHunter(int id, uint hunterId, uint corpseId) =>
            UnitAccess.TryGetById(id, out GameUnit* hunter, out _) && UnitAccess.IsReallyAlive(hunter) &&
            hunter->r_UnitChimp == eChimps.CHIMP_TYPE_HUNTER && hunter->r_GlobalId == hunterId &&
            hunter->r_AI_ContextTargetUnitGlobalId == corpseId;

        internal readonly struct ButcheredPrey
        {
            internal ButcheredPrey(int id, uint hunter, uint corpse, int preyType)
            { Id = id; Hunter = hunter; Corpse = corpse; PreyType = preyType; }
            internal readonly int Id, PreyType;
            internal readonly uint Hunter, Corpse;
        }

        internal static ButcheredPrey[] PrepareButcheredForRepair(ReadOnlySpan<byte> bytes, Func<int, uint, uint, bool> matches)
        {
            if (bytes.IsEmpty) return Array.Empty<ButcheredPrey>();
            if (bytes.Length < sizeof(int)) throw new InvalidDataException("Short Hunter repair record.");
            int count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            if (count < 0 || count >= HunterNativeSites.UnitCapacity || bytes.Length != sizeof(int) + count * MeatResyncRowBytes)
                throw new InvalidDataException("Invalid Hunter repair record size.");

            var valid = new System.Collections.Generic.List<ButcheredPrey>(count);
            int previous = 0;
            for (int index = 0; index < count; index++)
            {
                var row = bytes.Slice(sizeof(int) + index * MeatResyncRowBytes, MeatResyncRowBytes);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(row);
                uint hunter = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2));
                uint corpse = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(6));
                int prey = BinaryPrimitives.ReadInt32LittleEndian(row.Slice(10));
                if (id <= previous || id >= HunterNativeSites.UnitCapacity || hunter == 0 || corpse == 0 ||
                    prey < 1 || prey > ushort.MaxValue)
                    throw new InvalidDataException("Invalid Hunter repair identity.");
                previous = id;
                if (matches(id, hunter, corpse)) valid.Add(new ButcheredPrey(id, hunter, corpse, prey));
            }
            return valid.ToArray();
        }

        private static void CommitButcheredForRepair(ButcheredPrey[] rows)
        {
            HunterNativeSites.ClearTable();
            foreach (var row in rows) HunterNativeSites.SetButchered(row.Id, row.Hunter, row.Corpse, row.PreyType);
        }
    }
}

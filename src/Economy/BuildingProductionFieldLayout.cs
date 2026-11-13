using System;
using APIShared.NativeKnowledge;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Resolves the audited offsets once at native initialization for the workshop runtime.</summary>
    internal static unsafe class BuildingProductionFieldLayout
    {
        // The returned offsets are relative to GameBuilding*. The caller still owns record
        // identity, synchronization and the native production adapter's read/write contract.
        internal static bool TryResolve(string verifiedNativeHash, out int currentOffset,
            out int nextOffset, out string reason)
        {
            currentOffset = nextOffset = 0;
            if (!BitConverter.IsLittleEndian || Enum.GetUnderlyingType(typeof(eGoods)) != typeof(short))
            {
                reason = "The workshop goods word does not have the expected little-endian Int16 representation.";
                return false;
            }

            GameBuilding probe = default;
            byte* origin = (byte*)&probe;
            // The source names identify storage. NativeKnowledge supplies the opposite meanings.
            int interopCurrent = (int)((byte*)&probe.r_NextProducedGoodId - origin);
            int interopNext = (int)((byte*)&probe.r_ProducedGoodId - origin);
            if (!Matches(NativeFieldIds.F000093, verifiedNativeHash, interopCurrent) ||
                !Matches(NativeFieldIds.F000094, verifiedNativeHash, interopNext))
            {
                reason = "The native image and loaded GameBuilding layout do not match the audited workshop fields.";
                return false;
            }

            currentOffset = interopCurrent;
            nextOffset = interopNext;
            reason = string.Empty;
            return true;
        }

        private static bool Matches(string id, string verifiedNativeHash, int interopOffset)
        {
            if (!NativeFieldCatalog.Default.TryGet(id, out NativeFieldDescriptor field) ||
                field.RecordTypeId != NativeRecordTypes.GameBuilding ||
                field.Meanings[0].Confidence != NativeMeaningConfidence.Confirmed)
                return false;

            foreach (NativeFieldBinding binding in field.Bindings)
                if (binding.ReadVerified && string.Equals(binding.NativeHash, verifiedNativeHash, StringComparison.OrdinalIgnoreCase) &&
                    binding.RecordSize == sizeof(GameBuilding) && binding.Offset == interopOffset &&
                    binding.ScalarType == NativeScalarType.Int16 && binding.ElementCount == 1 &&
                    binding.ElementStride == sizeof(short))
                    return true;

            return false;
        }
    }
}

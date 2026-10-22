using System;
using APIShared.NativeKnowledge;

namespace ThirdPartyMod.Examples
{
    // The caller owns valid immediate Unit bytes and the game-thread/lifetime contract.
    // No named Extender field, pointer lookup, new hook or polling callback is needed here.
    internal static class NativeFieldExample
    {
        internal static ushort? ReadDeathMarker(ReadOnlySpan<byte> unitRecordBytes)
        {
            if (!NativeFieldReader.TryGetCurrent(out var reader)) return null;
            if (!NativeFieldCatalog.Default.TryGetKey<ushort>(NativeFieldIds.F000054, out var key)) return null;
            return reader.TryRead(new NativeRecordView(NativeRecordTypes.GameUnit, unitRecordBytes), key,
                out ushort raw, out _) ? (ushort?)raw : null;
        }
    }
}

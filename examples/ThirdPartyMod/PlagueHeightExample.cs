// Called after ApiShared.ForMod("your.mod.guid").WhenReady(...), outside a running mission.
using APIShared;

internal static class PlagueHeightExample
{
    internal static bool Configure(bool enabled, out NativeCapabilityDiagnostic diagnostic)
    {
        if (!ApiShared.ForMod("example.plague.rules").TryGetPlagueHeight(out var heights, out diagnostic)) return false;
        return heights.TrySetPolicy(enabled,
            PlagueSurfaceCategory.Walls | PlagueSurfaceCategory.Gates | PlagueSurfaceCategory.Towers,
            20, out diagnostic);
    }
}

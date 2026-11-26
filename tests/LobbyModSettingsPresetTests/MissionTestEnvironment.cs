using APIShared.GameModes;
// Managed/native input stand-ins. Policy, state machine and adapter are the actual production sources.
internal static class EngineInterface
{
    internal struct LoadMapReturnData
    {
        internal int game_type, skirmishGameType, skirmishTrail, coopTrailID;
    }
}
namespace APIShared
{
    // This isolated UI suite does not link the game/archive savegame service. Its
    // production move/formatter sources only require the service's wire bounds;
    // savegame behavior and bounds are exercised by APISharedTests.
    internal static class SavegameModSettings
    {
        internal const int MaximumMods = 1024;
        internal const int MaximumPropertiesPerMod = 16384;
        internal const int MaximumPropertyBytes = 1024 * 1024;
    }
    public sealed class NativeCapabilityDiagnostic { }
    internal static class MissionLifecycleService
    {
        internal static APIShared.GameModes.GameModeSnapshot Snapshot;
        internal static bool HasContext => Snapshot.Kind != APIShared.GameModes.GameModeKind.Unknown;
    }
}

// Only game/thread/log boundaries are replaced. Production target mapping and all table
// validation/read/write operations run unchanged; enums and mapper conversion use the real SE.
namespace BepInEx.Logging { public sealed class ManualLogSource { } }
namespace APIShared
{
    internal static class NativeApiLog
    {
        internal static void Debug(BepInEx.Logging.ManualLogSource log, string value) { }
        internal static void Warning(BepInEx.Logging.ManualLogSource log, string value) { }
    }
}
namespace APIShared.Internal { internal static class UnityMainThreadDispatch { internal static bool IsMainThread => true; } }
namespace APIShared.GameModes { internal static class GameModeHelper { internal static bool IsMapEditor() => false; } }
public static class Enums { public enum eGameTypeModes { GAMETYPE_SIEGE_THAT_BUILDER = 6 } }
public sealed class GameData
{
    public static GameData Instance { get; } = new GameData();
    public State lastGameState;
    public sealed class State { public int game_type; }
    public static void getStructureCosts(int row, ref int wood, ref int stone, ref int iron, ref int pitch, ref int gold) { }
}

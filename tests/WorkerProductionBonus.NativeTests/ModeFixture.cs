// Detached mode-input boundary only. Production mode policy is source-linked; these facts
// avoid loading the game's singleton-based capture machinery into an executable native fixture.
namespace APIShared.GameModes
{
    public enum GameModeKind { Unknown, MapEditor, Campaign, StandaloneMission, CustomGame, VanillaTrail, CustomTrail, CoopTrail, SandsOfTime, Tutorial }
    public enum GameModeLaunchVariant { Standard, Customized, RestoredCustomizedSave }
    public readonly struct GameModeSnapshot
    {
        public GameModeSnapshot(GameModeKind kind,bool customized=false,bool multiplayer=false,bool conflicting=false)
        { Kind=kind; LaunchVariant=customized ? GameModeLaunchVariant.Customized : GameModeLaunchVariant.Standard;
          IsRealMultiplayer=multiplayer; HasConflictingCustomizedOrigin=conflicting; }
        public GameModeSnapshot(bool isRealMultiplayer,bool isSingleplayerSkirmishMode,bool isSingleplayerSkirmish,
            bool isSingleplayerTrail,bool isMapEditor,bool multiplayerSave,bool directorAvailable,bool directorMultiplayer,
            bool directorSkirmish,bool lowLevelNetworked,bool platformMultiplayer,int lobbyMembers,int realLobbyMembers,
            int skirmishLobbyMembers,int gameMembers,int realNetworkGameMembers,int gameType,int skirmishGameType,
            int skirmishTrailType,int coopTrailId,GameModeKind kind,GameModeLaunchVariant launchVariant,int campaignMapId,
            int eventTrailType,int customizedTrailId,int customizedMissionId,int customizedOriginKind,bool hasConflictingCustomizedOrigin)
        { Kind=kind; LaunchVariant=launchVariant; IsRealMultiplayer=isRealMultiplayer; HasConflictingCustomizedOrigin=hasConflictingCustomizedOrigin; }
        public GameModeKind Kind { get; }
        public GameModeLaunchVariant LaunchVariant { get; }
        public bool IsCustomized => LaunchVariant!=GameModeLaunchVariant.Standard;
        public bool IsRealMultiplayer { get; }
        public bool HasConflictingCustomizedOrigin { get; }
        public bool IsMissionContent => Kind==GameModeKind.Campaign || Kind==GameModeKind.StandaloneMission;
    }
}

using APIShared.GameModes;
using APIShared.Internal;
using SHCDESE.API;
using SHCDESE.EventAPI.MapLoader;
using CrusaderDE;
using System;
using System.Collections.Generic;
using System.Linq;
#if !API_SHARED_PRESET_TESTS
using Steamworks;
#endif

namespace APIShared.GameModes
{
    /// <summary>Immutable mode evidence for one captured context; inspect Kind and conflicting-origin status before applying your own policy.</summary>
    public readonly struct GameModeSnapshot
    {
        /// <summary>Immutable mode evidence for one captured context; inspect Kind and conflicting-origin status before applying your own policy.</summary>
        public GameModeSnapshot(
            bool isRealMultiplayer,
            bool isSingleplayerSkirmishMode,
            bool isSingleplayerSkirmish,
            bool isSingleplayerTrail,
            bool isMapEditor,
            bool multiplayerSave,
            bool directorAvailable,
            bool directorMultiplayer,
            bool directorSkirmish,
            bool lowLevelNetworked,
            bool platformMultiplayer,
            int lobbyMembers,
            int realLobbyMembers,
            int skirmishLobbyMembers,
            int gameMembers,
            int realNetworkGameMembers,
            int gameType,
            int skirmishGameType,
            int skirmishTrailType,
            int coopTrailId,
            GameModeKind kind,
            GameModeLaunchVariant launchVariant,
            int campaignMapId,
            int eventTrailType,
            int customizedTrailId,
            int customizedMissionId,
            int customizedOriginKind,
            bool hasConflictingCustomizedOrigin)
        {
            IsRealMultiplayer = isRealMultiplayer;
            IsSingleplayerSkirmishMode = isSingleplayerSkirmishMode;
            IsSingleplayerSkirmish = isSingleplayerSkirmish;
            IsSingleplayerTrail = isSingleplayerTrail;
            IsMapEditor = isMapEditor;
            MultiplayerSave = multiplayerSave;
            DirectorAvailable = directorAvailable;
            DirectorMultiplayer = directorMultiplayer;
            DirectorSkirmish = directorSkirmish;
            LowLevelNetworked = lowLevelNetworked;
            PlatformMultiplayer = platformMultiplayer;
            LobbyMembers = lobbyMembers;
            RealLobbyMembers = realLobbyMembers;
            SkirmishLobbyMembers = skirmishLobbyMembers;
            GameMembers = gameMembers;
            RealNetworkGameMembers = realNetworkGameMembers;
            GameType = gameType;
            SkirmishGameType = skirmishGameType;
            SkirmishTrailType = skirmishTrailType;
            CoopTrailId = coopTrailId;
            Kind = kind;
            LaunchVariant = launchVariant;
            CampaignMapId = campaignMapId;
            EventTrailType = eventTrailType;
            CustomizedTrailId = customizedTrailId;
            CustomizedMissionId = customizedMissionId;
            CustomizedOriginKind = customizedOriginKind;
            HasConflictingCustomizedOrigin = hasConflictingCustomizedOrigin;
        }

        /// <summary>True for a resolved real multiplayer context; low-level networking alone is insufficient.</summary>
        public bool IsRealMultiplayer { get; }
        /// <summary>IsSingleplayerSkirmishMode in the centralized mission policy contract.</summary>
        public bool IsSingleplayerSkirmishMode { get; }
        /// <summary>IsSingleplayerSkirmish in the centralized mission policy contract.</summary>
        public bool IsSingleplayerSkirmish { get; }
        /// <summary>IsSingleplayerTrail in the centralized mission policy contract.</summary>
        public bool IsSingleplayerTrail { get; }
        /// <summary>Whether the captured context is the map editor.</summary>
        public bool IsMapEditor { get; }
        /// <summary>Whether capture was supplied with multiplayer-save evidence.</summary>
        public bool MultiplayerSave { get; }
        /// <summary>Whether the Vanilla director was available during capture.</summary>
        public bool DirectorAvailable { get; }
        /// <summary>Raw multiplayer evidence captured from the director.</summary>
        public bool DirectorMultiplayer { get; }
        /// <summary>Raw skirmish evidence captured from the director.</summary>
        public bool DirectorSkirmish { get; }
        /// <summary>Low-level networking evidence; local skirmish may also set this flag.</summary>
        public bool LowLevelNetworked { get; }
        /// <summary>Multiplayer evidence captured from the platform layer.</summary>
        public bool PlatformMultiplayer { get; }
        /// <summary>Number of entries reported in the active multiplayer lobby; -1 when no lobby member list is available.</summary>
        public int LobbyMembers { get; }
        /// <summary>Number of non-null active-lobby entries that are not skirmish members.</summary>
        public int RealLobbyMembers { get; }
        /// <summary>Number of non-null active-lobby entries marked as skirmish members.</summary>
        public int SkirmishLobbyMembers { get; }
        /// <summary>Number of reported in-game network member entries; -1 when no member list is available.</summary>
        public int GameMembers { get; }
        /// <summary>Number of non-null in-game network members that are not skirmish AI and have a Steam ID above 1000.</summary>
        public int RealNetworkGameMembers { get; }
        /// <summary>GameType in the centralized mission policy contract.</summary>
        public int GameType { get; }
        /// <summary>SkirmishGameType in the centralized mission policy contract.</summary>
        public int SkirmishGameType { get; }
        /// <summary>Native trail type: 0..2 Vanilla, 11..18 Sands of Time; -1 when absent.</summary>
        public int SkirmishTrailType { get; }
        /// <summary>Native one-based Coop trail ID (1..4); zero when absent.</summary>
        public int CoopTrailId { get; }
        /// <summary>Resolved category for this captured context.</summary>
        public GameModeKind Kind { get; }
        /// <summary>Resolved standard/customized/restored-customized origin.</summary>
        public GameModeLaunchVariant LaunchVariant { get; }
        /// <summary>True when the resolved launch variant carries customization evidence.</summary>
        public bool IsCustomized => LaunchVariant != GameModeLaunchVariant.Standard;
        /// <summary>Whether the resolved category is campaign, standalone mission, or trail content.</summary>
        public bool IsMissionContent =>
            Kind == GameModeKind.Campaign ||
            Kind == GameModeKind.StandaloneMission ||
            Kind == GameModeKind.VanillaTrail ||
            Kind == GameModeKind.CustomTrail ||
            Kind == GameModeKind.CoopTrail ||
            Kind == GameModeKind.SandsOfTime;
        /// <summary>Established Serps permission convenience; use an explicit profile or your own policy for third-party rules.</summary>
        public bool AllowsCustomGameMods =>
            !HasConflictingCustomizedOrigin && GameModeHelper.AllowsCustomGameMods(Kind, LaunchVariant);
        /// <summary>Established Serps gameplay permission convenience; not an automatic restriction on third-party mods.</summary>
        public bool AllowsRegularGameplayMods =>
            !HasConflictingCustomizedOrigin &&
            GameModeHelper.AllowsRegularGameplayMods(Kind, LaunchVariant);
        /// <summary>Positive native campaign map ID supplied to the loader; zero if not captured. This is distinct from the zero-based mission index.</summary>
        public int CampaignMapId { get; }
        /// <summary>EventTrailType in the centralized mission policy contract.</summary>
        public int EventTrailType { get; }
        /// <summary>Trail ID of a customized launch: <see cref="CustomizedLaunchOrigin.TrailId"/> when a provider supplied an origin, otherwise Vanilla's customized-trail ID.</summary>
        public int CustomizedTrailId { get; }
        /// <summary>Mission ID of a customized launch: <see cref="CustomizedLaunchOrigin.MissionId"/> when a provider supplied an origin. Vanilla records no separate mission ID, so without a provider origin this repeats Vanilla's customized-trail ID.</summary>
        public int CustomizedMissionId { get; }
        /// <summary>Provider-supplied customized launch origin as the integer value of <see cref="CustomizedLaunchOriginKind"/>; -1 when the supplied origin was invalid.</summary>
        public int CustomizedOriginKind { get; }
        /// <summary>Always false for snapshots captured by APIShared: unusable customization evidence is ignored,
        /// the mode is decided from the game's own data and the decision is logged. Kept for compatibility;
        /// a caller-constructed snapshot can still set it, and the optional permission evaluator then denies it.</summary>
        public bool HasConflictingCustomizedOrigin { get; }

        // A restart is the same mission again: the ready source session is its record, under the
        // same rule as save restoration (the record decides unless native data names a definite
        // other mode). Live roster/native facts are kept. The optional origin provider may be absent.
        internal GameModeSnapshot WithRestartProvenance(GameModeSnapshot source)
        {
            var kind = Kind;
            var variant = LaunchVariant;
            GameModeHelper.ReconcileRestoredSaveMode(Kind, true, source.Kind, source.LaunchVariant,
                ref kind, ref variant);
            return new GameModeSnapshot(
                IsRealMultiplayer, IsSingleplayerSkirmishMode, IsSingleplayerSkirmish, IsSingleplayerTrail,
                IsMapEditor, MultiplayerSave, DirectorAvailable, DirectorMultiplayer, DirectorSkirmish,
                LowLevelNetworked, PlatformMultiplayer, LobbyMembers, RealLobbyMembers, SkirmishLobbyMembers,
                GameMembers, RealNetworkGameMembers, GameType, SkirmishGameType, SkirmishTrailType, CoopTrailId,
                kind, variant,
                CampaignMapId, EventTrailType,
                kind == source.Kind ? source.CustomizedTrailId : CustomizedTrailId,
                kind == source.Kind ? source.CustomizedMissionId : CustomizedMissionId,
                kind == source.Kind ? source.CustomizedOriginKind : CustomizedOriginKind, false);
        }

        /// <summary>Formats captured evidence for diagnostics without recapturing live state.</summary>
        public string ToDiagnosticString()
        {
            return
                $"realMultiplayer={IsRealMultiplayer}, singleplayerSkirmishMode={IsSingleplayerSkirmishMode}, " +
                $"singleplayerSkirmish={IsSingleplayerSkirmish}, singleplayerTrail={IsSingleplayerTrail}, " +
                $"mapEditor={IsMapEditor}, multiplayerSave={MultiplayerSave}, directorAvailable={DirectorAvailable}, " +
                $"directorMultiplayer={DirectorMultiplayer}, directorSkirmish={DirectorSkirmish}, " +
                $"lowLevelNetworked={LowLevelNetworked}, platformMultiplayer={PlatformMultiplayer}, " +
                $"lobbyMembers={LobbyMembers}, realLobbyMembers={RealLobbyMembers}, " +
                $"skirmishLobbyMembers={SkirmishLobbyMembers}, " +
                $"gameMembers={GameMembers}, realNetworkGameMembers={RealNetworkGameMembers}, " +
                $"gameType={GameType}, skirmishGameType={SkirmishGameType}, skirmishTrailType={SkirmishTrailType}, " +
                $"coopTrailId={CoopTrailId}, kind={Kind}, launchVariant={LaunchVariant}, " +
                $"allowsCustomGameMods={AllowsCustomGameMods}, " +
                $"allowsRegularGameplayMods={AllowsRegularGameplayMods}, campaignMapId={CampaignMapId}, " +
                $"eventTrailType={EventTrailType}, customizedTrailId={CustomizedTrailId}, " +
                $"customizedMissionId={CustomizedMissionId}, customizedOriginKind={CustomizedOriginKind}, " +
                $"conflictingCustomizedOrigin={HasConflictingCustomizedOrigin}";
        }

#if API_SHARED_PRESET_TESTS
        internal GameModeSnapshot WithModeEvidenceForTests(
            GameModeKind kind,
            GameModeLaunchVariant launchVariant,
            int eventTrailType,
            bool hasConflictingCustomizedOrigin = false) =>
            new GameModeSnapshot(
                IsRealMultiplayer,
                IsSingleplayerSkirmishMode,
                IsSingleplayerSkirmish,
                IsSingleplayerTrail,
                IsMapEditor,
                MultiplayerSave,
                DirectorAvailable,
                DirectorMultiplayer,
                DirectorSkirmish,
                LowLevelNetworked,
                PlatformMultiplayer,
                LobbyMembers,
                RealLobbyMembers,
                SkirmishLobbyMembers,
                GameMembers,
                RealNetworkGameMembers,
                GameType,
                SkirmishGameType,
                SkirmishTrailType,
                CoopTrailId,
                kind,
                launchVariant,
                CampaignMapId,
                eventTrailType,
                CustomizedTrailId,
                CustomizedMissionId,
                CustomizedOriginKind,
                hasConflictingCustomizedOrigin);
#endif
    }
}

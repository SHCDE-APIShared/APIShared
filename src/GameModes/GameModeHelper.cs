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
    /// <summary>Central mode capture. Capturing describes context; it does not activate features or apply a caller permission profile.</summary>
    public static class GameModeHelper
    {
        internal static Func<SavedModeEvidence> ReadSavedModeEvidence;
        // Installed by the mission lifecycle; receives each distinct mode decision once.
        internal static Action<string> ReportModeDecision;
        private static string lastModeDecision;
        // Each mission attempt reports its own decision, even one identical to an earlier attempt's.
        internal static void BeginModeDecisions() => lastModeDecision = null;
        private const int NoGameValue = -1;
        private const int NoCoopTrail = 0;
        /// <summary>Reads the central current/pending mission snapshot. The legacy save argument does not override authoritative evidence.</summary>
        public static GameModeSnapshot Capture(bool multiplayerSave = false) =>
            APIShared.MissionLifecycleService.Snapshot;

        internal static GameModeSnapshot CaptureMission(bool multiplayer, bool restoredSave, int campaign, int trail,
            bool editor, EngineInterface.LoadMapReturnData? data, GameModeKind intent, int? coopTrail = null) =>
            CaptureCore(multiplayer && restoredSave, campaign, trail, editor, data, intent, multiplayer, coopTrail, restoredSave);

        internal static bool AllowsCustomGameMods(
            GameModeKind kind,
            GameModeLaunchVariant launchVariant)
        {
            if (kind == GameModeKind.CustomGame)
                return true;
            if (launchVariant == GameModeLaunchVariant.Standard)
                return false;

            return kind == GameModeKind.VanillaTrail ||
                kind == GameModeKind.CustomTrail ||
                kind == GameModeKind.CoopTrail ||
                kind == GameModeKind.SandsOfTime;
        }

        internal static bool AllowsRegularGameplayMods(
            GameModeKind kind,
            GameModeLaunchVariant launchVariant) =>
            kind == GameModeKind.MapEditor || AllowsCustomGameMods(kind, launchVariant);

        private static GameModeSnapshot CaptureCore(
            bool multiplayerSave,
            int campaignMapId,
            int eventTrailType,
            bool editorLoad, EngineInterface.LoadMapReturnData? data = null, GameModeKind intent = GameModeKind.Unknown,
            bool realMultiplayerOverride = false, int? knownCoopTrail = null, bool restoredSave = false)
        {
            Director director = Director.instance;
            GameData gameData = GameData.Instance;
            Platform_Multiplayer platform = Platform_Multiplayer.Instance;
            Platform_Multiplayer.MPLobby lobby = platform?.activeLobby;

            int lobbyMembers = lobby?.members?.Count ?? -1;
            int realLobbyMembers = 0;
            int skirmishLobbyMembers = 0;
            if (lobby?.members != null)
            {
                foreach (Platform_Multiplayer.MPLobbyMember member in lobby.members)
                {
                    if (member == null)
                        continue;
                    if (member.SkirmishMember)
                        skirmishLobbyMembers++;
                    else
                        realLobbyMembers++;
                }
            }

            int gameMembers = platform?.gameMembers?.Count ?? -1;
            int realNetworkGameMembers = 0;
            if (platform?.gameMembers != null)
            {
                foreach (Platform_Multiplayer.MPGameMember member in platform.gameMembers)
                {
                    if (member != null && !member.skirmishAI && member.steamID > 1000)
                        realNetworkGameMembers++;
                }
            }

            bool directorMultiplayer = director != null && director.MultiplayerGame;
            // Entry-point evidence is authoritative. Old rosters and mode flags can
            // survive a transition and must not turn a new local game into multiplayer.
            bool platformMultiplayer = GameNetworkAPI.IsMultiplayerGame();
            bool realMultiplayer = realMultiplayerOverride;

            int gameType = data?.game_type ?? (intent == GameModeKind.Campaign ? (int)Enums.eGameTypeModes.GAMETYPE_CAMPAIGN :
                intent == GameModeKind.Tutorial ? (int)Enums.eGameTypeModes.GAMETYPE_TUTORIAL :
                intent == GameModeKind.StandaloneMission ? (int)Enums.eGameTypeModes.GAMETYPE_MAP :
                intent == GameModeKind.MapEditor ? (int)Enums.eGameTypeModes.GAMETYPE_BUILDER :
                intent != GameModeKind.Unknown ? (int)Enums.eGameTypeModes.GAMETYPE_MULTIPLAYER : NoGameValue);
            int skirmishGameType = data?.skirmishGameType ?? (intent == GameModeKind.CustomTrail ?
                (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM_TRAIL : intent == GameModeKind.CustomGame ?
                (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM : NoGameValue);
            int skirmishTrailType = data?.skirmishTrail ?? eventTrailType;
            int coopTrailId = data?.coopTrailID ?? knownCoopTrail ?? NoCoopTrail;
            bool mapEditor = editorLoad;
            bool sandsOfTime = intent == GameModeKind.SandsOfTime || IsSandsTrailType(skirmishTrailType);
            bool customTrailRestart = intent == GameModeKind.CustomTrail;
            // game_type 3 is Vanilla's skirmish family. Immediately after leaving a
            // real multiplayer game, a new local skirmish can reach OnStartMap before
            // Vanilla changes SkirmishGameType from -1. Its all-local skirmish lobby
            // is the stable transition signal; no map or mission allow-list is needed.
            bool localSkirmishTransition =
                lobbyMembers > 0 &&
                skirmishLobbyMembers == lobbyMembers &&
                realLobbyMembers == 0 &&
                realNetworkGameMembers == 0;
            bool singleplayerSkirmishMode =
                !realMultiplayer &&
                !mapEditor &&
                gameType == (int)Enums.eGameTypeModes.GAMETYPE_MULTIPLAYER &&
                (skirmishGameType >= 0 || localSkirmishTransition);

            bool vanillaCustomized = TryCaptureVanillaCustomizedTrail(
                out int customizedTrailType,
                out int customizedTrailId);
            ExternalCustomizedOrigin externalOrigin = CaptureExternalCustomizedOrigin();
            GameModeKind observedKind = ResolveKind(
                mapEditor,
                gameType,
                skirmishGameType,
                skirmishTrailType,
                coopTrailId,
                campaignMapId,
                eventTrailType);
            string unusableOrigin = ClassifyLaunch(externalOrigin, observedKind, intent, sandsOfTime,
                customTrailRestart, vanillaCustomized, customizedTrailType, customizedTrailId,
                skirmishTrailType, coopTrailId, eventTrailType,
                out GameModeKind kind, out GameModeLaunchVariant launchVariant);
            if (unusableOrigin != null)
            {
                // Provider evidence that does not fit this start is never a reason to withhold a
                // decision: the mode is then classified from the game's own data alone, which is
                // the same on every multiplayer peer, and the rejected evidence is reported.
                ExternalCustomizedOrigin rejected = externalOrigin;
                externalOrigin = default;
                ClassifyLaunch(externalOrigin, observedKind, intent, sandsOfTime,
                    customTrailRestart, vanillaCustomized, customizedTrailType, customizedTrailId,
                    skirmishTrailType, coopTrailId, eventTrailType, out kind, out launchVariant);
                // A restored save does not consult providers at all; their state before the native
                // load belongs to the previous mission and is expected to differ.
                if (!restoredSave)
                    ReportUnusableOrigin(rejected, unusableOrigin, observedKind, kind, launchVariant, realMultiplayer);
            }

            if (restoredSave)
            {
                // Without the save service there is no record to consult: a legacy save.
                SavedModeEvidence evidence = ReadSavedModeEvidence?.Invoke() ??
                    new SavedModeEvidence(false, GameModeKind.Unknown, GameModeLaunchVariant.Standard);
                ReconcileRestoredSaveMode(observedKind, evidence.HasSaved, evidence.Kind, evidence.Variant,
                    ref kind, ref launchVariant);
            }

            return new GameModeSnapshot(
                realMultiplayer,
                singleplayerSkirmishMode,
                singleplayerSkirmishMode && skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM,
                singleplayerSkirmishMode &&
                    (skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_TRAIL ||
                     skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM_TRAIL),
                mapEditor,
                multiplayerSave,
                director != null,
                directorMultiplayer,
                director != null && director.SkirmishModeGame,
                GameNetworkAPI.IsNetworkedEnvironment(),
                platformMultiplayer,
                lobbyMembers,
                realLobbyMembers,
                skirmishLobbyMembers,
                gameMembers,
                realNetworkGameMembers,
                gameType,
                skirmishGameType,
                skirmishTrailType,
                coopTrailId,
                kind,
                launchVariant,
                campaignMapId,
                eventTrailType,
                externalOrigin.Origin != ExternalCustomizedOrigin.None
                    ? externalOrigin.TrailId
                    : customizedTrailId,
                externalOrigin.Origin != ExternalCustomizedOrigin.None
                    ? externalOrigin.MissionId
                    : customizedTrailId,
                externalOrigin.Origin,
                // Every capture decides its mode; nothing is left conflicting (see ClassifyLaunch).
                false);
        }

        // Classifies a launch from native data, managed entry intent, Vanilla's Customize fields and
        // one provider origin. Returns null when that origin is absent or fits the result, otherwise
        // the reason why it cannot describe this start.
        private static string ClassifyLaunch(
            ExternalCustomizedOrigin externalOrigin,
            GameModeKind observedKind,
            GameModeKind intent,
            bool sandsOfTime,
            bool customTrailRestart,
            bool vanillaCustomized,
            int customizedTrailType,
            int customizedTrailId,
            int skirmishTrailType,
            int coopTrailId,
            int eventTrailType,
            out GameModeKind kind,
            out GameModeLaunchVariant launchVariant)
        {
            kind = observedKind;
            // A successful native load can omit its skirmish subtype (notably for
            // older multiplayer maps). Preserve the authoritative managed entry
            // intent only when native data cannot classify the mode at all.
            if (kind == GameModeKind.Unknown && intent != GameModeKind.Unknown)
                kind = intent;
            if (observedKind == GameModeKind.CustomGame && externalOrigin.LaunchPending)
            {
                GameModeKind originKind = ResolveExternalOriginKind(externalOrigin.Origin);
                if (originKind != GameModeKind.Unknown)
                    kind = originKind;
            }
            if (sandsOfTime && kind != GameModeKind.MapEditor && kind != GameModeKind.CoopTrail)
                kind = GameModeKind.SandsOfTime;
            else if (customTrailRestart && (kind == GameModeKind.Unknown || kind == GameModeKind.CustomGame))
                kind = GameModeKind.CustomTrail;
            if (vanillaCustomized && customizedTrailId >= 0 && kind == GameModeKind.CustomGame)
            {
                bool builtInOriginRequired = externalOrigin.SupportsBuiltInOrigins;
                if (IsVanillaTrailType(customizedTrailType) &&
                    (!builtInOriginRequired || externalOrigin.Origin == ExternalCustomizedOrigin.VanillaTrail))
                    kind = GameModeKind.VanillaTrail;
                else if (IsSandsTrailType(customizedTrailType) &&
                    (!builtInOriginRequired || externalOrigin.Origin == ExternalCustomizedOrigin.SandsOfTime))
                    kind = GameModeKind.SandsOfTime;
            }
            launchVariant = ResolveLaunchVariant(
                kind,
                vanillaCustomized,
                customizedTrailType,
                customizedTrailId,
                observedKind == GameModeKind.CustomGame,
                externalOrigin);
            if (externalOrigin.IsInvalid)
                return "a provider failed, supplied invalid trail or mission IDs, or several providers were active at once";
            if (externalOrigin.Origin == ExternalCustomizedOrigin.None) return null;
            if (!ExternalOriginMatchesKind(externalOrigin, kind))
                return "it names another mode than the one the game started";
            return ExternalOriginMatchesEvidence(externalOrigin, kind, skirmishTrailType, coopTrailId,
                eventTrailType, vanillaCustomized, customizedTrailType, customizedTrailId)
                ? null : "its trail or mission IDs differ from those the game reports";
        }

        // Cold path. A capture repeats at every initialization checkpoint, so one decision is
        // reported once. Reporting must never disturb the capture itself.
        private static void ReportUnusableOrigin(ExternalCustomizedOrigin rejected, string reason,
            GameModeKind observedKind, GameModeKind kind, GameModeLaunchVariant launchVariant, bool realMultiplayer)
        {
            try
            {
                string text = "GAME_MODE_ORIGIN_REJECTED: customized launch evidence was ignored because " + reason +
                    ". evidence: origin=" + rejected.Origin + ",trailType=" + rejected.TrailType +
                    ",trailId=" + rejected.TrailId + ",missionId=" + rejected.MissionId +
                    ",launchPending=" + rejected.LaunchPending + ",restoredFromSave=" + rejected.RestoredFromSave +
                    ",invalid=" + rejected.IsInvalid + "; game reports=" + observedKind +
                    "; decision=" + kind + "/" + launchVariant +
                    " from the game's own data, so no mod is locked and every player decides alike" +
                    (realMultiplayer ? " (multiplayer: a player whose provider state differs would otherwise diverge)" : "") +
                    ". The provider that left this evidence behind should clear it when its launch does not start.";
                if (text == lastModeDecision) return;
                lastModeDecision = text;
                ReportModeDecision?.Invoke(text);
            }
            catch { /* Diagnostics only. */ }
        }

        // A save's own record is the authoritative mode evidence: it was written from the running
        // mission, while launch-origin providers and frontend fields describe whatever preceded this
        // load (their state for this save is restored only during the native load). A restart is
        // the same mission again and treats its ready source session as that record.
        // Native data can still overrule the record: a definite other mode (campaign, standalone
        // mission, native trail family) wins without customization, so a record cannot open a
        // foreign mission. Either way the result is one decided mode, never a lock.
        internal static void ReconcileRestoredSaveMode(
            GameModeKind observedKind,
            bool hasSaved,
            GameModeKind savedKind,
            GameModeLaunchVariant savedVariant,
            ref GameModeKind kind,
            ref GameModeLaunchVariant launchVariant)
        {
            if (!hasSaved)
            {
                // No usable record is a legacy save. Preserve native mode evidence; only an
                // unclassified save receives the user's current-settings fallback.
                kind = observedKind == GameModeKind.Unknown ? GameModeKind.CustomGame : observedKind;
                launchVariant = GameModeLaunchVariant.Standard;
                return;
            }
            // Vanilla loads every customized Trail through its generic CustomGame carrier and can omit
            // the subtype altogether; neither contradicts a saved Trail family.
            bool nativeNamesAnotherMode = observedKind != GameModeKind.Unknown &&
                observedKind != GameModeKind.CustomGame && observedKind != savedKind;
            kind = nativeNamesAnotherMode ? observedKind : savedKind;
            launchVariant = nativeNamesAnotherMode ? GameModeLaunchVariant.Standard : savedVariant;
        }

        internal static GameModeKind ResolveKind(
            bool mapEditor,
            int gameType,
            int skirmishGameType,
            int skirmishTrailType,
            int coopTrailId,
            int campaignMapId = 0,
            int eventTrailType = NoGameValue)
        {
            if (mapEditor)
                return GameModeKind.MapEditor;
            if (gameType == (int)Enums.eGameTypeModes.GAMETYPE_TUTORIAL)
                return GameModeKind.Tutorial;
            if (gameType == (int)Enums.eGameTypeModes.GAMETYPE_CAMPAIGN || campaignMapId > 0)
                return GameModeKind.Campaign;
            if (gameType == (int)Enums.eGameTypeModes.GAMETYPE_MAP)
                return GameModeKind.StandaloneMission;
            if (coopTrailId > NoCoopTrail)
                return GameModeKind.CoopTrail;

            bool hasTrailEvent = eventTrailType >= 0;
            int effectiveTrailType = hasTrailEvent ? eventTrailType : skirmishTrailType;
            bool vanillaTrailMode =
                skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_TRAIL;
            if ((vanillaTrailMode || hasTrailEvent) && IsSandsTrailType(effectiveTrailType))
                return GameModeKind.SandsOfTime;
            if ((vanillaTrailMode || hasTrailEvent) && IsVanillaTrailType(effectiveTrailType))
            {
                return GameModeKind.VanillaTrail;
            }
            if (skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM_TRAIL)
                return GameModeKind.CustomTrail;
            if (gameType == (int)Enums.eGameTypeModes.GAMETYPE_MULTIPLAYER &&
                skirmishGameType == (int)Enums.eSkirmishGameMode.SKIRMISH_GAME_CUSTOM &&
                coopTrailId == NoCoopTrail)
            {
                return GameModeKind.CustomGame;
            }
            return GameModeKind.Unknown;
        }

        internal static GameModeLaunchVariant ResolveLaunchVariant(
            GameModeKind kind,
            bool vanillaCustomized,
            int customizedTrailType,
            int customizedTrailId,
            bool vanillaCustomGameContext,
            ExternalCustomizedOrigin externalOrigin)
        {
            bool vanillaMatches = vanillaCustomized &&
                vanillaCustomGameContext &&
                customizedTrailId >= 0 &&
                (!externalOrigin.SupportsBuiltInOrigins ||
                 ExternalOriginMatchesKind(externalOrigin, kind)) &&
                ((kind == GameModeKind.VanillaTrail && IsVanillaTrailType(customizedTrailType)) ||
                 (kind == GameModeKind.SandsOfTime && IsSandsTrailType(customizedTrailType)));
            bool externalMatches =
                ExternalOriginMatchesKind(externalOrigin, kind) &&
                (kind != GameModeKind.CustomGame || externalOrigin.LaunchPending);
            if (!vanillaMatches && !externalMatches)
                return GameModeLaunchVariant.Standard;
            return externalMatches && externalOrigin.RestoredFromSave
                ? GameModeLaunchVariant.RestoredCustomizedSave
                : GameModeLaunchVariant.Customized;
        }

        private static bool IsVanillaTrailType(int value) =>
            value >= (int)GameTrailType.FirstEdition && value <= (int)GameTrailType.Extreme;

        private static bool IsSandsTrailType(int value) =>
            value >= (int)GameTrailType.SandsOne && value <= (int)GameTrailType.SandsEight;

        private static GameModeKind ResolveExternalOriginKind(int origin)
        {
            switch (origin)
            {
                case ExternalCustomizedOrigin.CustomTrail: return GameModeKind.CustomTrail;
                case ExternalCustomizedOrigin.CoopTrail: return GameModeKind.CoopTrail;
                case ExternalCustomizedOrigin.VanillaTrail: return GameModeKind.VanillaTrail;
                case ExternalCustomizedOrigin.SandsOfTime: return GameModeKind.SandsOfTime;
                default: return GameModeKind.Unknown;
            }
        }

        private static bool ExternalOriginMatchesKind(ExternalCustomizedOrigin origin, GameModeKind kind) =>
            (origin.Origin == ExternalCustomizedOrigin.CustomTrail && kind == GameModeKind.CustomTrail) ||
            (origin.Origin == ExternalCustomizedOrigin.CoopTrail && kind == GameModeKind.CoopTrail) ||
            (origin.Origin == ExternalCustomizedOrigin.VanillaTrail && kind == GameModeKind.VanillaTrail) ||
            (origin.Origin == ExternalCustomizedOrigin.SandsOfTime && kind == GameModeKind.SandsOfTime);

        internal static bool ExternalOriginMatchesEvidence(
            ExternalCustomizedOrigin origin,
            GameModeKind kind,
            int skirmishTrailType,
            int coopTrailId,
            int eventTrailType,
            bool vanillaCustomized,
            int customizedTrailType,
            int customizedTrailId)
        {
            if (kind == GameModeKind.CoopTrail && coopTrailId > NoCoopTrail)
                return origin.TrailId + 1 == coopTrailId;
            if (kind != GameModeKind.VanillaTrail && kind != GameModeKind.SandsOfTime)
                return true;

            int observedTrailType = eventTrailType >= 0 ? eventTrailType : skirmishTrailType;
            if (observedTrailType >= 0 && origin.TrailType != observedTrailType)
                return false;
            if (!vanillaCustomized)
                return true;
            return origin.TrailType == customizedTrailType &&
                origin.MissionId == customizedTrailId;
        }

        private static bool TryCaptureVanillaCustomizedTrail(out int trailType, out int trailId)
        {
#if API_SHARED_PRESET_TESTS
            trailType = NoGameValue;
            trailId = NoGameValue;
            return false;
#else
            trailType = FRONT_Multiplayer.customizedTrailType;
            trailId = FRONT_Multiplayer.customizedTrailID;
            return FRONT_Multiplayer.customizedTrail;
#endif
        }

        private static ExternalCustomizedOrigin CaptureExternalCustomizedOrigin() =>
            CustomizedLaunchOrigins.Capture();

        /// <summary>Reads mission network identity, or active-game network flags outside a mission. This alone never grants gameplay permission.</summary>
        /// <remarks>Active-game flags are false in a fresh multiplayer lobby. Pre-game launch guards use
        /// <see cref="APIShared.Lobby.LobbyLaunchGuardContext.RequiresPeerAgreement"/> for the actual Vanilla launch branch.</remarks>
        public static bool IsRealMultiplayer(bool multiplayerSave = false) =>
            APIShared.MissionLifecycleService.HasContext ? Capture().IsRealMultiplayer : GameNetworkAPI.IsMultiplayerGame();

        // Keep the legacy property contract while deriving it from Vanilla's named subtype enum.
        /// <summary>IsSingleplayerSkirmish in the centralized mission policy contract.</summary>
        public static bool IsSingleplayerSkirmish(bool multiplayerSave = false) =>
            Capture(multiplayerSave).IsSingleplayerSkirmish;

        /// <summary>IsSingleplayerTrail in the centralized mission policy contract.</summary>
        public static bool IsSingleplayerTrail(bool multiplayerSave = false) =>
            Capture(multiplayerSave).IsSingleplayerTrail;

        // This broader check also covers future/utility subtypes initialized by Vanilla as skirmish mode.
        /// <summary>IsSingleplayerSkirmishMode in the centralized mission policy contract.</summary>
        public static bool IsSingleplayerSkirmishMode(bool multiplayerSave = false) =>
            Capture(multiplayerSave).IsSingleplayerSkirmishMode;

        /// <summary>Whether the captured context is the map editor.</summary>
        public static bool IsMapEditor() => Capture().IsMapEditor;

    }
}

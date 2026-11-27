// APIShared-owned implementation; independent of workspace Shared helpers.
// Initial provenance: a7888900e, Shared/GameModeHelper.cs. No automatic synchronization.
using APIShared.GameModes;
using SHCDESE.API;
using SHCDESE.EventAPI.MapLoader;
using CrusaderDE;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
#if !API_SHARED_PRESET_TESTS
using Steamworks;
#endif

namespace APIShared.Internal
{
    internal readonly struct PlayerIdentityResolution
    {
        internal PlayerIdentityResolution(int playerId, bool isResolved, string error, string diagnostic)
        {
            PlayerId = playerId;
            IsResolved = isResolved;
            Error = error ?? string.Empty;
            Diagnostic = diagnostic ?? string.Empty;
        }

        internal int PlayerId { get; }
        internal bool IsResolved { get; }
        internal string Error { get; }
        internal string Diagnostic { get; }
    }

    internal static class PlayerIdentityHelper
    {
        // Resolve the local player only from sources whose slot semantics are known.
        // The GameNetworkAPI local-player getter reads the same managed rosters and logs a
        // warning whenever they are still transitional, so it must not be used as an
        // additional fallback from persistent lobby observers.
        internal static PlayerIdentityResolution ResolveLocalPlayerId(
            bool realMultiplayer,
            bool hasInGameHumanRoster,
            int nativePlayerId,
            int gameMemberPlayerId,
            int lobbyPlayerId)
        {
            bool nativeValid = PlayerSlots.IsValidPlayerId(nativePlayerId);
            bool gameMemberValid = PlayerSlots.IsValidPlayerId(gameMemberPlayerId);
            bool lobbyValid = PlayerSlots.IsValidPlayerId(lobbyPlayerId);

            if (realMultiplayer && hasInGameHumanRoster)
            {
                if (nativeValid && gameMemberValid && nativePlayerId != gameMemberPlayerId)
                {
                    return Failure(
                        $"Authoritative local player ID mismatch: native={nativePlayerId}, gameMember={gameMemberPlayerId}, " +
                        $"lobby={lobbyPlayerId}.");
                }

                int authoritative = nativeValid ? nativePlayerId : gameMemberPlayerId;
                if (!PlayerSlots.IsValidPlayerId(authoritative))
                {
                    return Failure(
                        $"No authoritative local player ID is available in the active multiplayer roster: " +
                        $"native={nativePlayerId}, gameMember={gameMemberPlayerId}, lobby={lobbyPlayerId}.");
                }
                if (lobbyValid && lobbyPlayerId != authoritative)
                {
                    return Failure(
                        $"Final lobby mapping disagrees with the authoritative local player ID: " +
                        $"authoritative={authoritative}, lobby={lobbyPlayerId}.");
                }

                return Success(authoritative, string.Empty);
            }

            if (realMultiplayer)
            {
                if (lobbyValid)
                    return Success(lobbyPlayerId, string.Empty);
                return Failure($"No local multiplayer player ID is available yet: lobby={lobbyPlayerId}.");
            }

            if (nativeValid)
                return Success(nativePlayerId, string.Empty);
            if (gameMemberValid)
                return Success(gameMemberPlayerId, string.Empty);
            if (lobbyValid)
                return Success(lobbyPlayerId, string.Empty);
            return Failure("No valid local player ID is available.");
        }

#if !API_SHARED_PRESET_TESTS
        // The in-game human roster is preferred whenever it exists; the lobby is only a fallback.
        internal static PlayerIdentityResolution CaptureLocalPlayerId(bool realMultiplayer)
        {
            Platform_Multiplayer platform = Platform_Multiplayer.Instance;
            ulong localSteamId = 0;
            try
            {
                localSteamId = SteamUser.GetSteamID().m_SteamID;
            }
            catch
            {
                // Steam can be unavailable during early singleplayer initialization.
            }

            Platform_Multiplayer.MPGameMember[] humanGameMembers = platform?.gameMembers?
                .Where(member => member != null && !member.kicked && !member.skirmishAI)
                .ToArray() ?? Array.Empty<Platform_Multiplayer.MPGameMember>();
            int gameMemberPlayerId = humanGameMembers
                .Where(member => localSteamId != 0 && member.steamID == localSteamId)
                .Select(member => member.playerID)
                .FirstOrDefault();

            int nativePlayerId = 0;
            try
            {
                nativePlayerId = GamePlayerManagerAPI.Instance?.GetLocalPlayerId() ?? 0;
            }
            catch
            {
                // Native player resources are not guaranteed to exist in the lobby.
            }

            int lobbyPlayerId = 0;
            try
            {
                if (localSteamId != 0 && platform?.activeLobby != null)
                    lobbyPlayerId = platform.activeLobby.getThisPlayerFromSteamID(localSteamId);
            }
            catch
            {
                // The final lobby mapping can still be under construction.
            }

            return ResolveLocalPlayerId(
                realMultiplayer,
                humanGameMembers.Length > 0,
                nativePlayerId,
                gameMemberPlayerId,
                lobbyPlayerId);
        }

        // Captures the authoritative in-game human roster (kicked members and skirmish AI excluded).
        internal static bool TryCaptureHumanRoster(
            out Dictionary<int, ulong> playersById,
            out string error)
        {
            playersById = new Dictionary<int, ulong>();
            Platform_Multiplayer.MPGameMember[] humanGameMembers = Platform_Multiplayer.Instance?.gameMembers?
                .Where(member => member != null && !member.kicked && !member.skirmishAI)
                .ToArray() ?? Array.Empty<Platform_Multiplayer.MPGameMember>();
            if (humanGameMembers.Length == 0)
            {
                error = "The active in-game human roster is unavailable.";
                return false;
            }
            foreach (Platform_Multiplayer.MPGameMember member in humanGameMembers)
            {
                if (!PlayerSlots.TryAddPlayer(playersById, member.playerID, member.steamID, out error))
                    return false;
            }
            error = string.Empty;
            return true;
        }
#endif

        private static PlayerIdentityResolution Success(int playerId, string diagnostic) =>
            new PlayerIdentityResolution(playerId, true, string.Empty, diagnostic);

        private static PlayerIdentityResolution Failure(string error) =>
            new PlayerIdentityResolution(0, false, error, string.Empty);
    }

}

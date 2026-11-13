using Steamworks;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace APIShared
{
    // Rules shared by the save-load and map-start readiness transactions. Each coordinator keeps
    // its own roster error texts, packet shapes, fingerprints and transaction state.
    internal static class LobbyReadinessSupport
    {
        internal const int PreparationSeconds = 60;
        // Vanilla starts a released load asynchronously: the host's start request only publishes
        // lobby data. The readiness deadline must not abort that window; it gets its own bound.
        internal const int StartWindowSeconds = 30;
        internal const double WaitingNoticeDelaySeconds = 1.5;
        // 64 KiB provenance plus at most 64 record digests (owner, identifier, SHA-256) and a reason.
        internal const int MaximumBodyBytes = 200000;
        // Lobby members at or below this ID are not Steam accounts.
        private const ulong MaximumNonHumanSteamId = 1000;

        // Differs on purpose from LobbyStateService's filter (which keeps SkirmishHumanMember): in real
        // Steam lobbies both select the same members; SkirmishHumanMember exists only in local skirmish.
        internal static bool IsHuman(Platform_Multiplayer.MPLobbyMember member) =>
            member != null && !member.dummyToBeKicked && !member.SkirmishMember && member.id.m_SteamID > MaximumNonHumanSteamId;

        // Ascending Steam IDs of the human members; empty without a member list.
        internal static ulong[] ObservedHumans(Platform_Multiplayer.MPLobby lobby) =>
            lobby.members?.Where(IsHuman).Select(member => member.id.m_SteamID).OrderBy(id => id).ToArray() ?? Array.Empty<ulong>();

        internal static string PlayerName(ulong steamId)
        {
            try
            {
                string name = SteamFriends.GetFriendPersonaName(new CSteamID(steamId));
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return steamId.ToString(CultureInfo.InvariantCulture);
        }

        internal static string TruncateReason(string reason) =>
            reason == null || reason.Length <= SavegameLoadPacket.MaximumReasonLength
                ? reason : reason.Substring(0, SavegameLoadPacket.MaximumReasonLength);

        internal static string NewAttemptId() => Guid.NewGuid().ToString("N");

        // Serializing and hashing the prepared settings each frame is too costly while peers
        // prepare, so waiting frames recheck the fingerprint at most twice per second.
        internal static bool FingerprintRecheckDue(long checkedAt, long now) => now - checkedAt >= Stopwatch.Frequency / 2;
    }
}

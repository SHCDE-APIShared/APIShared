using APIShared.Lobby;
using APIShared.Localization;
using APIShared.Networking;
using APIShared.Savegames;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace APIShared
{
    internal static partial class MapRecordLoadCoordinator
    {
        private static string BlockContext(string phase) =>
            "MAP_RECORD_LOAD_BLOCKED role=" + (attemptId == null ? "unknown" : attemptHost ? "host" : "client") +
            " phase=" + phase + " attempt=" + (attemptId ?? LobbyReadinessSupport.NewAttemptId());

        private static string BlockDetail(string phase)
        {
            var detail = new List<string> { BlockContext(phase) };
            if (pending != null)
                detail.Add("expectedFingerprint=" + pending.Fingerprint + ", expectedMap=" + pending.SaveName + ", expectedCrc=" + pending.Checksum);
            if (owner?.activeLobby is Platform_Multiplayer.MPLobby lobby)
            {
                detail.Add("currentMap=" + lobby.mapFileName + ", currentCrc=" + lobby.crc);
                if (pending != null)
                    detail.Add("expectedRoster=" + pending.Roster + ", observedRoster=" + string.Join(",", LobbyReadinessSupport.ObservedHumans(lobby)));
            }
            if (preparedSnapshot != null) detail.Add("prepared: " + preparedSnapshot.Summary());
            if (lastSnapshot != null)
            {
                detail.Add("last observed: " + lastSnapshot.Summary());
                if (preparedSnapshot != null)
                {
                    var categories = preparedSnapshot.DescribeChanges(lastSnapshot, detail);
                    if (categories.Count != 0) detail.Insert(1, "changed=" + string.Join(", ", categories));
                }
            }
            if (agreement != null) detail.Add("unconfirmed=" + string.Join(", ", agreement.Unconfirmed.Select(LobbyReadinessSupport.PlayerName)));
            if (lastSnapshot != null && pending != null && !fingerprintMatched)
                detail.Add("host/local differences=" + string.Join(",", Differences(pending.Metadata, lastSnapshot.Digests)));
            foreach (var report in PeerObservations.OrderBy(item => item.Key))
                detail.Add("peer=" + report.Key + " state=" + report.Value[0] +
                    " changed=" + string.Join(",", Differences(pending.Metadata, report.Value, 1)));
            if (configurationWaiting != null) detail.Add("configuration=" + configurationWaiting);
            return string.Join("; ", detail);
        }

        private static void ShowRejectedRequest(Platform_Multiplayer.MPLobby lobby, string reason) => ShowBlocked(reason,
            "MAP_RECORD_LOAD_BLOCKED role=host phase=request attempt=" + LobbyReadinessSupport.NewAttemptId(), lobby.id.m_SteamID, true, false);

        private static void ShowBlocked(string reason, string detail, ulong lobbyId, bool broadcast, bool localChat)
        {
            LobbyLaunchStatus.ReportPreparationBlocked(log, reason, detail, lobbyId, broadcast, localChat);
            try { HUD_ConfirmationPopup.ShowConfirmationOKMessage(UiText.Get("Savegames.LoadTitle"), () => { }, reason); }
            catch (Exception error) { NativeApiLog.Error(log, "Map preparation failure popup unavailable: " + error.Message); }
        }
    }
}

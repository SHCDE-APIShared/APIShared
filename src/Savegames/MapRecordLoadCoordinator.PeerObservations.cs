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
        private static string[] Differences(byte[] expected, byte[] observed, int offset = 0)
        {
            var result = new List<string>();
            for (int category = 0; category < Categories.Length; category++)
            {
                int start = category * DigestLength;
                for (int i = start; i < start + DigestLength; i++)
                {
                    if (expected[i] == observed[i + offset]) continue;
                    result.Add(Categories[category]);
                    break;
                }
            }
            return result.ToArray();
        }

        private static bool AcceptObservation(ulong peer, SavegameLoadPacket packet)
        {
            byte[] observed = packet.Metadata;
            if (observed?.Length != pending.Metadata.Length + 1 || observed[0] > ProductionObservation || packet.Records == null || packet.Records.Count != 0) return false;
            var changed = Differences(pending.Metadata, observed, 1);
            if ((observed[0] == DiffObservation) != (changed.Length != 0)) return false;
            if (PeerObservations.TryGetValue(peer, out byte[] previous) && previous.SequenceEqual(observed)) return false;
            PeerObservations[peer] = observed;
            waitingReasonDirty = true;
            LogObservation(peer, "peer status", changed, observed, preparedSnapshot);
            return true;
        }

        private static void SendObservation(byte state)
        {
            bool retryDue = observationSendFailed && DateTime.UtcNow >= nextObservationRetry;
            if (ReferenceEquals(lastSentSnapshot, lastSnapshot) && lastSentObservation != null &&
                lastSentObservation[0] == state && !retryDue) return;
            lastSentSnapshot = lastSnapshot;
            var observed = new byte[lastSnapshot.Digests.Length + 1];
            observed[0] = state;
            Buffer.BlockCopy(lastSnapshot.Digests, 0, observed, 1, lastSnapshot.Digests.Length);
            bool changed = lastSentObservation == null || !lastSentObservation.SequenceEqual(observed);
            if (!changed && !retryDue) return;
            if (changed)
                LogObservation(SteamUser.GetSteamID().m_SteamID, "local comparison", Differences(pending.Metadata, observed, 1), observed, lastSnapshot);
            lastSentObservation = observed;
            waitingReasonDirty = true;
            nextObservationRetry = DateTime.UtcNow.AddSeconds(2);
            var packet = Reply(MapRecordReadinessPacket.ObservationOperation);
            packet.Metadata = observed;
            try
            {
                var result = channel.SendToSteamPeer(new MapRecordReadinessPacket { Value = packet }, hostId);
                observationSendFailed = !result.Submitted;
                if (observationSendFailed) NativeApiLog.Warning(log, "Map readiness status send failed: " + result.Reason);
            }
            catch (Exception error)
            {
                observationSendFailed = true;
                NativeApiLog.Warning(log, "Map readiness status send failed: " + error.Message);
            }
        }

        private static void LogObservation(ulong peer, string phase, string[] changed, byte[] observed, LobbySnapshot local)
        {
            string prefix = $"MAP_RECORD_STATUS role={(attemptHost ? "host" : "client")} attempt={attemptId} phase={phase} peer={peer}";
            NativeApiLog.Info(log, prefix + " state=" + observed[0] + " changed=" + string.Join(",", changed) +
                " expected=" + BitConverter.ToString(pending.Metadata).Replace("-", "") +
                " observed=" + BitConverter.ToString(observed, 1).Replace("-", ""));
            if (changed.Contains("Mods") && lastLoggedModFingerprint != local.ModFingerprint)
            {
                lastLoggedModFingerprint = local.ModFingerprint;
                foreach (string property in local.PropertyDigests()) NativeApiLog.Info(log, prefix + " " + property);
            }
        }
    }
}

using APIShared.GameModes;
using APIShared.Maps;
using Steamworks;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace APIShared
{
    internal sealed partial class MissionLifecycleService
    {
        private MultiplayerMapStart completedMap;
        private long completedMapSession;

        private sealed class MultiplayerMapArguments
        {
            internal readonly Platform_Multiplayer Platform;
            internal readonly EngineInterface.MultiplayerSetupData Setup;
            internal readonly FileHeader Map;
            internal readonly int Coop, Mission;

            internal MultiplayerMapArguments(Platform_Multiplayer platform, EngineInterface.MultiplayerSetupData setup,
                FileHeader map, int coop, int mission)
            {
                Platform = platform;
                Setup = setup;
                Map = map;
                Coop = coop;
                Mission = mission;
            }
        }

        private sealed class MultiplayerMapStart
        {
            internal readonly MapRecordLoadCoordinator.NativeAdmissionTicket File;
            internal readonly ulong Lobby, Host;
            internal readonly uint Crc;
            internal readonly int Coop, Mission;
            internal readonly string MapName, Setup, Aiv, HostSettings;
            internal readonly bool Swapped;
            internal readonly (int Slot, ulong Steam, int Team, bool Ai, int Lord, int Subtype)[] Players;

            internal MultiplayerMapStart(MultiplayerMapArguments args, MapRecordLoadCoordinator.NativeAdmissionTicket file)
            {
                var lobby = args.Platform?.activeLobby ?? throw new InvalidOperationException("The original Steam lobby is unavailable.");
                if (args.Map == null || args.Setup == null || lobby.members == null ||
                    lobby.members.Count == 0 || lobby.members.Any(member => member == null || member.dummyToBeKicked))
                    throw new InvalidOperationException("The multiplayer launch arguments or player list are incomplete.");

                File = file;
                Lobby = lobby.id.m_SteamID;
                Host = SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID;
                MapName = lobby.mapFileName;
                Crc = args.Map.crc;
                Coop = args.Coop;
                Mission = Coop > 0 ? args.Mission : 0;
                Swapped = Coop > 0 && lobby.coopOrderSwapped;
                HostSettings = SavegameModSettings.CaptureHostFingerprint();
                Players = lobby.members.Select(member =>
                {
                    int team = lobby.getTeam(member);
                    return (lobby.getThisPlayerFromSteamID(member.id.m_SteamID), member.id.m_SteamID,
                        lobby.CountTeamMembers(team) > 1 ? team : 0, member.SkirmishMember,
                        member.GetLordType(), member.SkirmishMember ? member.GetLordSubType() : 0);
                }).OrderBy(player => player.Item1).ToArray();
                if (Players.Any(player => player.Slot <= 0) ||
                    Players.Select(player => player.Slot).Distinct().Count() != Players.Length)
                    throw new InvalidOperationException("The multiplayer player slots are unresolved.");

                var humans = LobbyReadinessSupport.ObservedHumans(lobby);
                int memberCount = SteamMatchmaking.GetNumLobbyMembers(lobby.id);
                var steamMembers = Enumerable.Range(0, memberCount)
                    .Select(index => SteamMatchmaking.GetLobbyMemberByIndex(lobby.id, index).m_SteamID).OrderBy(id => id);
                ulong local = SteamUser.GetSteamID().m_SteamID;
                if (Host == 0 || lobby.isHost != (local == Host) ||
                    !humans.Contains(Host) || !humans.Contains(local) || !humans.SequenceEqual(steamMembers))
                    throw new InvalidOperationException("Steam lobby membership or host authority changed.");

                Aiv = Players.Any(player => player.Ai) ? lobby.AIVDataChecksum() : string.Empty;
                Setup = MultiplayerRestartSetupIdentity(args.Setup,
                    Coop == 0 ? Players.Where(player => player.Ai).Select(player => player.Slot) : Array.Empty<int>());
            }

            internal void CheckArguments(MultiplayerMapArguments arguments, bool checkFile)
            {
                var current = new MultiplayerMapStart(arguments, File);
                if (Lobby != current.Lobby || Host != current.Host || !Players.SequenceEqual(current.Players))
                    throw new InvalidOperationException("The multiplayer host, players or slot assignments changed.");
                if (!string.Equals(MapName, current.MapName, StringComparison.OrdinalIgnoreCase) ||
                    Crc != current.Crc || Coop != current.Coop || Mission != current.Mission || Swapped != current.Swapped ||
                    Aiv != current.Aiv || Setup != current.Setup || HostSettings != current.HostSettings)
                    throw new InvalidOperationException("The multiplayer map, AI setup or synchronized settings changed.");
                if (!int.TryParse(arguments.Platform.activeLobby.crc, NumberStyles.Integer, CultureInfo.InvariantCulture, out int crc) ||
                    unchecked((uint)crc) != Crc)
                    throw new InvalidOperationException("The original lobby no longer selects the confirmed map.");
                if (checkFile && !MapRecordLoadCoordinator.MatchesFile(File, arguments.Map.filePath))
                    throw new InvalidOperationException("The confirmed multiplayer map file was replaced or changed.");
            }
        }

        internal static string MultiplayerRestartSetupIdentity(EngineInterface.MultiplayerSetupData setup,
            System.Collections.Generic.IEnumerable<int> overwrittenRotationSlots)
        {
            // ToString ends with preferredAIVs, keep order, and an empty field after the comma.
            var fields = setup.ToString().Split(',');
            int firstRotation = fields.Length - 1 - setup.start_keep_location_order.Length - setup.preferredAIVs.Length;
            foreach (int slot in overwrittenRotationSlots)
            {
                if (slot < 1 || slot > setup.preferredAIVs.Length)
                    throw new InvalidOperationException("A multiplayer AI rotation slot is outside the setup.");
                fields[firstRotation + slot - 1] = string.Empty;
            }
            return string.Join(",", fields);
        }

        internal static bool TryValidateMultiplayerRestart(EngineInterface.MultiplayerSetupData setup, FileHeader map,
            int coop, int mission, out string reason)
        {
            try
            {
                var service = published;
                var source = service?.state.Current;
                if (service == null || Thread.CurrentThread.ManagedThreadId != service.unityThread ||
                    source == null || source.IsSave || source.SessionId != service.completedMapSession || service.completedMap == null)
                    throw new InvalidOperationException("No confirmed multiplayer map belongs to the current mission.");
                service.completedMap.CheckArguments(new MultiplayerMapArguments(Platform_Multiplayer.Instance, setup, map, coop, mission), true);
                if (!Economy.WorkerProductionRuntime.TryCaptureRestartApproval(source.SessionId, out _))
                    throw new InvalidOperationException("The current mission has no confirmed production configuration.");
                reason = null;
                return true;
            }
            catch (Exception error)
            {
                reason = error.Message;
                return false;
            }
        }

        private void StartMultiplayerMap(Platform_Multiplayer platform, EngineInterface.MultiplayerSetupData setup,
            FileHeader map, int coop, int mission)
        {
            var outer = executing;
            var args = new MultiplayerMapArguments(platform, setup, map, coop, mission);
            bool direct = outer?.RestartedFrom != null && !MapRecordLoadCoordinator.HasPendingPreparation;
            string alias;
            MapRecordLoadCoordinator.NativeAdmissionTicket file;
            if (direct)
            {
                if (outer.RestartSourceMap == null || outer.ConfirmedMapFile != null)
                    throw new InvalidOperationException("No confirmed source map is available for this multiplayer restart.");
                if (!outer.HasRestartProductionApproval)
                    throw new InvalidOperationException("The source mission has no confirmed production configuration.");
                // Reviewed 2026-11-24, intentionally kept: a rejection here comes after Run's Begin ended the
                // source mission. Consumers pre-check with MissionRestart.TryValidateMultiplayerMap on the same
                // Unity call, so only arguments that differ from the pre-check reach this; failing is the safe outcome.
                outer.RestartSourceMap.CheckArguments(args, true);
                file = outer.RestartSourceMap.File;
                var decision = coop > 0 ? MapLoadDecision.Allow() : MapLoadPolicy.Evaluate(map.filePath);
                if (decision.Rejection != null) throw new InvalidOperationException(decision.Rejection);
                alias = decision.NativeFileName;
                outer.DirectMultiplayerRestart = true;
            }
            else
            {
                // Vanilla treats a normal return as a successful lobby start.
                if (!MapRecordLoadCoordinator.ValidateStart(map?.filePath, true, coop > 0, out alias))
                    throw new InvalidOperationException("The multiplayer map start was rejected; the lobby stays open.");
                file = MapRecordLoadCoordinator.CurrentAdmission;
            }
            var op = outer ?? new Operation { Kind = MissionStartKind.NewGame,
                Intent = coop > 0 ? GameModeKind.CoopTrail : GameModeKind.CustomGame, Multiplayer = true };
            op.File = op.MapAliasPath = map.filePath;
            op.MapAlias = alias;
            op.MapArguments = args;
            op.ConfirmedMapFile = file;
            Run(op, () =>
            {
                var receipt = ScriptExtenderFixes.LobbyStartPacketRuntime.BeginNativeStart(platform, op.Id);
                bool returnedNormally = false;
                try { multiplayerNew(platform, setup, map, coop, mission); returnedNormally = true; }
                finally { Safe(() => ScriptExtenderFixes.LobbyStartPacketRuntime.EndNativeStart(receipt,
                    returnedNormally && op.NativeSucceeded && !op.NativeFailed)); }
            });
            if (op.MapAliasFailure != null)
                throw new InvalidOperationException("The multiplayer map start was rejected; the lobby stays open.");
        }

        private void CheckMultiplayerMapBeforeNativeLoad(Operation op)
        {
            if (op.DirectMultiplayerRestart)
            {
                try
                {
                    if (Thread.CurrentThread.ManagedThreadId != unityThread)
                        throw new InvalidOperationException("Multiplayer restart validation requires Unity's main thread.");
                    op.RestartSourceMap.CheckArguments(op.MapArguments, false);
                    op.ValidatedMap = op.RestartSourceMap;
                }
                catch (Exception error)
                {
                    op.NativeFailed = true;
                    op.RestartFailure = error.Message;
                    throw new NativeAdmissionAbort(error.Message);
                }
            }
            else
            {
                // Initial lobby admission already succeeded. Failure to capture restart provenance
                // only affects an optional future restart of this mission.
                if (Thread.CurrentThread.ManagedThreadId == unityThread && op.ConfirmedMapFile != null)
                    Safe(() => op.ValidatedMap = new MultiplayerMapStart(op.MapArguments, op.ConfirmedMapFile));
            }
            op.MapArguments = null;
        }

        private void PublishCompletedMap(Operation op, MissionContext context)
        {
            if (!state.IsPending(op.Id) || context.IsSave || op.ValidatedMap == null) return;
            completedMap = op.ValidatedMap;
            completedMapSession = op.Id;
        }
    }
}

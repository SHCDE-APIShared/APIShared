using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using APIShared.Internal;
using APIShared.Savegames;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.Utils;
using R3;
using Steamworks;

namespace APIShared.ScriptExtenderFixes
{
    // SE owns the Steam receive. We preserve only the Vanilla messages it already consumed,
    // then deliver once through Vanilla's existing (possibly detoured) processMessage method.
    internal static class LobbyStartPacketRuntime
    {
        internal sealed class StartReceipt
        {
            internal long Generation;
            internal object OldMembers;
            internal long Session;
        }
        private sealed class Scope
        {
            internal Platform_Multiplayer Platform;
            internal Platform_Multiplayer.MPLobby Lobby;
            internal ulong LobbyId, Host, Local;
            internal ulong[] Humans;
            internal string Token, Announcement;
            internal long Session;
            internal object Members;
            internal bool Armed, ReplayReported;
            internal readonly int[] Staged = new int[10], Replayed = new int[10];
        }
        private static readonly LobbyStartPacketQueue queue = new LobbyStartPacketQueue();
        private static readonly List<Patching.SafeIlHook> hooks = new List<Patching.SafeIlHook>();
        private static Scope scope;
        private static ManualLogSource logger;
        private static FieldInfo mapLoaded;
        private static MethodInfo process;
        private static IDisposable ended;
        private static bool ready, replaying;
        private static int attempted;

        internal static void Initialize(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            logger = log;
            try
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                mapLoaded = typeof(Platform_Multiplayer).GetField("mapLoaded", flags);
                process = typeof(Platform_Multiplayer).GetMethod("processMessage", flags, null,
                    new[] { typeof(Platform_Multiplayer.MPData), typeof(Platform_Multiplayer.MPGameMember), typeof(bool) }, null);
                if (mapLoaded?.FieldType != typeof(bool) || process?.ReturnType != typeof(bool))
                    throw new MissingMemberException("Vanilla handshake metadata differs.");
                var manager = typeof(SHCDESE.API.GameNetworkAPI).Assembly.GetType("SHCDESE.ManagedHooks.ManagedHookManager", true);
                var drain = manager.GetMethod("Platform_Multiplayer_ReceiveLobbyMessages_Hook", flags, null,
                    new[] { typeof(Platform_Multiplayer) }, null);
                var receive = typeof(Platform_Multiplayer).GetMethod("ReceiveGameMessages", flags, null, new[] { typeof(bool) }, null);
                var exit = typeof(Platform_Multiplayer).GetMethod("exitMP", flags, null, Type.EmptyTypes, null);
                if (drain == null || receive == null || exit == null || drain.ReturnType != typeof(void) ||
                    receive.ReturnType != typeof(void) || exit.ReturnType != typeof(void))
                    throw new MissingMethodException("Lobby handshake receive/exit metadata differs.");
                var edits = new[] {
                    Tuple.Create(drain, (ILContext.Manipulator)LobbyStartPacketIl.Stage),
                    Tuple.Create(receive, (ILContext.Manipulator)LobbyStartPacketIl.Replay),
                    Tuple.Create(exit, (ILContext.Manipulator)LobbyStartPacketIl.Exit)
                };
                foreach (var edit in edits)
                    using (var definition = new DynamicMethodDefinition(edit.Item1))
                    using (var context = new ILContext(definition.Definition))
                    {
                        context.ReferenceBag = RuntimeILReferenceBag.Instance;
                        context.Invoke(edit.Item2); definition.Generate();
                    }
                foreach (var edit in edits)
                {
                    if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.LobbyHandshake." + edit.Item1.Name,
                        edit.Item1, edit.Item2, log, out var hook, out string diagnostic))
                        throw new InvalidOperationException(diagnostic);
                    hooks.Add(hook);
                }
                ended = MissionEvents.Ended.Subscribe(e =>
                {
                    if (UnityMainThreadDispatch.IsMainThread && scope?.Session == e.Context.SessionId) Clear("mission-ended");
                });
                ready = true;
                NativeApiLog.Info(log, "SE_LOBBY_HANDSHAKE: bounded Vanilla handshake preservation installed; process lifetime.");
            }
            catch (Exception error) { Fault("initialization", error); }
        }

        private static bool Available()
        {
            if (!ready || !UnityMainThreadDispatch.IsMainThread) return false;
            for (int i = 0; i < hooks.Count; i++) if (!hooks[i].IsApplied) { Clear("hook-rebuild-unavailable"); return false; }
            return true;
        }

        // Called only for four known types, after SE has decoded and released the Steam message.
        internal static void Stage(Platform_Multiplayer platform, Platform_Multiplayer.MPData data, CSteamID sender)
        {
            if (data == null || !LobbyStartPacketQueue.IsHandshake(data.packetType)) return;
            try
            {
                if (!Available() || !Bind(platform)) return;
                if (queue.Poisoned) return;
                if (queue.Add(data.packetType, data.data, data.dataOffset, data.dataLength,
                    sender.m_SteamID, scope.Local, scope.Host, scope.Humans)) scope.Staged[data.packetType]++;
                // Poison the entire epoch. Accepting the tail after clearing would replay
                // an incomplete handshake; only a genuinely new preparation resets it.
                if (queue.Poisoned)
                {
                    Report("bounded-queue-overflow-discarded");
                    try { NativeApiLog.Error(logger, "SE_LOBBY_HANDSHAKE: bounded queue overflow; entire preparation epoch discarded."); } catch { }
                }
            }
            catch (Exception error) { Fault("stage", error); }
        }

        internal static StartReceipt BeginNativeStart(Platform_Multiplayer platform, long missionSession)
        {
            try
            {
                if (!Available() || !Bind(platform, true)) return null;
                scope.Session = missionSession;
                return new StartReceipt { Generation = queue.Generation, OldMembers = platform.gameMembers, Session = missionSession };
            }
            catch (Exception error) { Fault("begin-native-start", error); return null; }
        }

        // nativeStartSucceeded includes the lifecycle owner's actual native load result, not just normal return.
        internal static void EndNativeStart(StartReceipt receipt, bool nativeStartSucceeded)
        {
            if (receipt == null) return;
            try
            {
                if (!Available() || scope == null || receipt.Generation != queue.Generation || receipt.Session != scope.Session) return;
                var platform = scope.Platform;
                if (!nativeStartSucceeded || !(bool)mapLoaded.GetValue(platform) || platform.gameMembers == null ||
                    ReferenceEquals(receipt.OldMembers, platform.gameMembers) || !SameLobby(platform))
                { Clear("native-start-not-successful"); return; }
                scope.Members = platform.gameMembers;
                scope.Armed = true;
            }
            catch (Exception error) { Fault("end-native-start", error); }
        }

        internal static void Replay(Platform_Multiplayer platform, bool fromThread)
        {
            // This prolog also runs on the simulation thread. Never inspect Unity state there.
            if (fromThread || replaying || queue.Count == 0 || !UnityMainThreadDispatch.IsMainThread) return;
            Scope replayScope = null;
            long generation = -1;
            try
            {
                if (!Available() || scope == null || !ReferenceEquals(platform, scope.Platform)) return;
                // A newer authenticated preparation wins even before its first packet arrives.
                if (!Bind(platform) || !scope.Armed) return;
                if (!SameLobby(platform) || !ReferenceEquals(scope.Members, platform.gameMembers) ||
                    MissionLifecycleService.ActiveContext?.SessionId != scope.Session || Platform_Multiplayer.MPGameActive)
                { Clear("native-epoch-ended"); return; }
                replaying = true;
                replayScope = scope;
                generation = queue.Generation;
                while (queue.Count != 0)
                {
                    if (!ReferenceEquals(scope, replayScope) || queue.Generation != generation) return;
                    if (!SameLobby(platform) || !ReferenceEquals(replayScope.Members, platform.gameMembers) ||
                        MissionLifecycleService.ActiveContext?.SessionId != replayScope.Session)
                    { Clear("replay-scope-ended"); return; }
                    var packet = queue.Take();
                    var member = platform.gameMembers.FirstOrDefault(m => m.steamID == packet.Sender &&
                        !m.skirmishAI && !m.isSelf && !m.kicked && m.stillWithSteamConnection);
                    if (member == null) { Clear("sender-no-longer-native-member"); break; }
                    // Same counters and main-thread dispatch as Vanilla ReceiveGameMessages.
                    member.packetsReceived++;
                    var data = new Platform_Multiplayer.MPData { packetType = packet.Type, data = packet.Bytes,
                        dataOffset = 0, dataLength = packet.Bytes.Length };
                    scope.Replayed[packet.Type]++;
                    process.Invoke(platform, new object[] { data, member, false });
                    // postLoading publishes synchronous consumer events. They may retire this
                    // world and establish a different operation; never touch that newer queue.
                    if (!ReferenceEquals(scope, replayScope) || queue.Generation != generation) return;
                    if (Platform_Multiplayer.MPGameActive || platform.activeLobby == null)
                    { Clear("handshake-completed"); break; }
                }
                if (scope != null && queue.Count == 0 && !scope.ReplayReported)
                { scope.ReplayReported = true; Report("replayed"); }
            }
            catch (Exception error)
            {
                if (replayScope == null || ReferenceEquals(scope, replayScope) && queue.Generation == generation)
                    Fault("replay", error);
                else try { NativeApiLog.Error(logger, "SE_LOBBY_HANDSHAKE: retired replay callback failed; newer scope retained; " + error); } catch { }
            }
            finally { replaying = false; }
        }

        internal static void Exit(Platform_Multiplayer platform)
        {
            if (!UnityMainThreadDispatch.IsMainThread || scope == null || !ReferenceEquals(platform, scope.Platform)) return;
            try { Clear("exit-multiplayer"); } catch { queue.Reset(); scope = null; }
        }

        private static bool Bind(Platform_Multiplayer platform, bool nativeEntry = false)
        {
            var lobby = platform?.activeLobby;
            if (!ReferenceEquals(platform, Platform_Multiplayer.Instance) || lobby == null || Platform_Multiplayer.MPGameActive)
            { if (scope != null) Clear("lobby-start-ended"); return false; }
            string token; ulong lobbyId, host; IReadOnlyList<ulong> humans;
            var save = MultiplayerSavePreparation.Current;
            if (save != null && save.Phase != MultiplayerSavePreparationPhase.Ended)
            {
                if (nativeEntry && (string.IsNullOrEmpty(lobby.startGame) || lobby.startGame != save.SaveName))
                { if (scope != null) Clear("different-save-announced"); return false; }
                token = "S:" + save.OperationId; lobbyId = save.LobbyId; host = save.HostId; humans = save.HumanPeers;
            }
            else if (MapRecordLoadCoordinator.TryGetLobbyPacketAdmission(out token, out lobbyId, out host, out humans)) token = "M:" + token;
            else
            {
                if (!nativeEntry && scope != null && scope.Armed && SameLobby(platform) && ReferenceEquals(scope.Members, platform.gameMembers) &&
                    MissionLifecycleService.ActiveContext?.SessionId == scope.Session) return true;
                if (scope != null) Clear("preparation-ended"); return false;
            }
            if (nativeEntry && token.StartsWith("M:", StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(lobby.startGame) || !lobby.startGame.StartsWith("GO!", StringComparison.Ordinal)))
            { if (scope != null) Clear("different-map-announced"); return false; }
            ulong local = SteamUser.GetSteamID().m_SteamID;
            ulong[] actual = LobbyReadinessSupport.ObservedHumans(lobby);
            if (lobby.id.m_SteamID != lobbyId || host == 0 || string.IsNullOrEmpty(token) ||
                SteamMatchmaking.GetLobbyOwner(lobby.id).m_SteamID != host || lobby.isHost != (local == host) ||
                !actual.SequenceEqual(humans.OrderBy(id => id)) || !actual.Contains(local) || !actual.Contains(host))
            { if (scope != null) Clear("authority-or-roster-changed"); return false; }
            if (scope != null && ReferenceEquals(scope.Platform, platform) && ReferenceEquals(scope.Lobby, lobby) &&
                scope.Token == token && scope.Host == host && scope.Local == local &&
                (!scope.Armed || scope.Announcement == lobby.startGame) && scope.Humans.SequenceEqual(actual))
            {
                // Steam lobby metadata and P2P messages have no cross-transport ordering.
                // Bind the announcement at the validated local native entry, not at receipt.
                if (nativeEntry) scope.Announcement = lobby.startGame;
                return true;
            }
            Clear("new-preparation");
            scope = new Scope { Platform = platform, Lobby = lobby, LobbyId = lobbyId, Host = host, Local = local,
                Humans = actual, Token = token, Announcement = nativeEntry ? lobby.startGame : null };
            return true;
        }

        private static bool SameLobby(Platform_Multiplayer platform) => scope != null &&
            ReferenceEquals(platform, scope.Platform) && ReferenceEquals(platform.activeLobby, scope.Lobby) &&
            scope.Lobby.id.m_SteamID == scope.LobbyId && scope.Lobby.startGame == scope.Announcement &&
            scope.Lobby.isHost == (scope.Local == scope.Host) &&
            SteamMatchmaking.GetLobbyOwner(scope.Lobby.id).m_SteamID == scope.Host &&
            LobbyReadinessSupport.ObservedHumans(scope.Lobby).SequenceEqual(scope.Humans);

        private static void Clear(string reason)
        {
            var retired = scope;
            queue.Reset(); scope = null;
            Report(retired, reason);
        }
        private static void Report(string reason) => Report(scope, reason);
        private static void Report(Scope observed, string reason)
        {
            if (observed == null || !observed.Staged.Any(n => n != 0)) return;
            try { NativeApiLog.Info(logger, "SE_LOBBY_HANDSHAKE: reason=" + reason + ",token=" + observed.Token +
                ",lobby=" + observed.LobbyId + ",session=" + observed.Session + ",staged[2,3,4,9]=" +
                string.Join(",", new[] { observed.Staged[2], observed.Staged[3], observed.Staged[4], observed.Staged[9] }) +
                ",replayed[2,3,4,9]=" + string.Join(",", new[] { observed.Replayed[2], observed.Replayed[3], observed.Replayed[4], observed.Replayed[9] })); }
            catch { }
        }
        private static void Fault(string stage, Exception error)
        {
            try { Clear("error-" + stage); } catch { queue.Reset(); scope = null; }
            try { NativeApiLog.Error(logger, "SE_LOBBY_HANDSHAKE: " + stage + " failed; staged data discarded; " + error); } catch { }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.API;
using Steamworks;

namespace APIShared.Lobby
{
    /// <summary>Shared, deduplicated lobby chat announcements explaining why a multiplayer lobby cannot start yet.</summary>
    /// <remarks>
    /// Consumers keep their own launch checks and enforcement; this service only explains them. Providers are
    /// permanent owner/ID registrations that run synchronously on the Unity thread in ordinal owner/ID order whenever
    /// a coalesced refresh runs, and only while the local player is in an active Steam multiplayer lobby outside a
    /// mission. Provider exceptions are isolated and logged.
    /// <para>Audience: the host is the only sender of automatic announcements for the whole lobby. On a lobby member,
    /// <see cref="LobbyLaunchStatusScope.Local"/> entries are only shown in the local lobby chat as Vanilla system
    /// hints (no network traffic, visible in muted chat), and <see cref="LobbyLaunchStatusScope.Lobby"/> entries are
    /// sent to everyone. Lobby members never announce "ready". A line that another player sent within the last five
    /// seconds is not sent or shown again. In a Coop lobby, a received or locally shown announcement opens the
    /// collapsed Coop chat the way Vanilla's chat button does; other chat messages keep Vanilla's behavior.</para>
    /// <para>Timing: a loading phase is announced once as a wait hint when it has lasted 1.5 seconds, so short
    /// synchronizations (for example browsing missions) stay silent; each loading entry is announced once more as
    /// blocking after 20 seconds; blocking entries are announced when they first appear; waiting entries are never
    /// announced. On the host, a single "ready" announcement follows once every communicated entry has disappeared,
    /// no waiting entry remains and that state has lasted 1.5 seconds. Every command rejected through
    /// <see cref="ReportAttempt"/> is announced; a rejected command alone does not arm "ready". Identical automatic
    /// texts are announced once per lobby until they disappear. Each refresh sends at most one message per category; long messages are split into numbered
    /// parts of at most 280 characters at word boundaries. Received announcements are classified as lobby system
    /// hints. There is no replay or teardown.</para>
    /// </remarks>
    public static class LobbyLaunchStatus
    {
        internal const string Marker = "[Mods] ";
        private static readonly OwnedCallbacks<Func<LobbyLaunchStatusQuery, IEnumerable<LobbyLaunchStatusItem>>> Providers =
            new OwnedCallbacks<Func<LobbyLaunchStatusQuery, IEnumerable<LobbyLaunchStatusItem>>>();
        // Broadcast: host entries, or a member's Lobby-scope entries. LocalOnly: a member's Local-scope entries.
        private static readonly LobbyLaunchAnnouncementState Broadcast = new LobbyLaunchAnnouncementState();
        private static readonly LobbyLaunchAnnouncementState LocalOnly = new LobbyLaunchAnnouncementState();
        private static readonly LobbyLaunchReceivedLines Received = new LobbyLaunchReceivedLines();
        private static readonly object TimerGate = new object();
        private static readonly Lazy<ManualLogSource> EarlyAttemptLog =
            new Lazy<ManualLogSource>(() => Logger.CreateLogSource("APIShared.LobbyLaunch"));
        private static ManualLogSource log;
        private static Timer dueTimer;
        private static int refreshQueued;
        private static string lastReceived = string.Empty;
        private static DateTime lastReceivedUtc;

        /// <summary>Whether the service initialized after LibraryLoaded. Registration is possible earlier.</summary>
        public static bool IsAvailable { get; private set; }

        /// <summary>Registers a permanent provider; invalid or duplicate identities return false.</summary>
        /// <remarks>Return the current entries for the queried lobby, or none. Call <see cref="RequestRefresh"/> when an
        /// input of the provider changes; APIShared also refreshes after <see cref="ReportAttempt"/> and when a loading
        /// entry becomes due.</remarks>
        public static bool TryRegisterProvider(string ownerGuid, string registrationId,
            Func<LobbyLaunchStatusQuery, IEnumerable<LobbyLaunchStatusItem>> provider, out string diagnostic) =>
            Providers.TryAdd(ownerGuid, registrationId, provider, out diagnostic);

        /// <summary>Schedules one coalesced refresh on the Unity thread. Safe from any thread; no effect before initialization.</summary>
        public static void RequestRefresh()
        {
            if (!IsAvailable || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
            if (!UnityMainThreadDispatch.TryEnqueue(Refresh)) Interlocked.Exchange(ref refreshQueued, 0);
        }

        /// <summary>Reports a Ready/Start command the owner has just rejected.</summary>
        /// <remarks>Call it where the command was rejected, normally the Unity thread; other threads are marshalled.
        /// The entry is always logged as an error. In an active lobby the rejecting player sends it to the whole lobby
        /// without waiting for a refresh, for every rejected click (players must always learn why a command did
        /// nothing); the item's scope is ignored. It counts as already announced for later automatic refreshes but
        /// does not arm "ready".</remarks>
        public static void ReportAttempt(string ownerGuid, LobbyLaunchOperation operation, LobbyLaunchStatusItem item)
        {
            if (item == null || item.Text.Length == 0) return;
            WriteAttemptError(log, "LOBBY_LAUNCH_BLOCKED owner=" + ownerGuid + ", operation=" + operation + ": " + item.Text,
                item.Detail);
            if (!IsAvailable) return;
            // Lobby and chat state belong to the Unity thread; other callers are marshalled without blocking.
            if (!UnityMainThreadDispatch.IsMainThread)
            {
                UnityMainThreadDispatch.TryEnqueue(() => AnnounceAttempt(operation, item));
                return;
            }
            AnnounceAttempt(operation, item);
        }

        // Coordinators own timing, attempt identity and repeat suppression. UI delivery
        // is scoped to the same lobby, including after a queued main-thread handoff.
        internal static void ReportPreparationWaiting(ManualLogSource fallback, string text, string detail, ulong lobbyId,
            bool broadcast, bool showLocal)
        {
            NativeApiLog.Info(log ?? fallback, PreparationLogLine("LOBBY_LAUNCH_LOADING", lobbyId, text) +
                (string.IsNullOrEmpty(detail) ? string.Empty : "; " + detail));
            AnnouncePreparation(text, lobbyId, broadcast, showLocal, LobbyLaunchAnnouncementKind.Loading);
        }

        internal static void ReportPreparationBlocked(ManualLogSource fallback, string text, string detail, ulong lobbyId,
            bool broadcast, bool showLocal)
        {
            WriteAttemptError(log ?? fallback, PreparationLogLine("LOBBY_LAUNCH_BLOCKED", lobbyId, text), detail);
            AnnouncePreparation(text, lobbyId, broadcast, showLocal, LobbyLaunchAnnouncementKind.Blocked);
        }

        private static string PreparationLogLine(string kind, ulong lobbyId, string text) =>
            kind + " operation=Start, lobby=" + lobbyId + ": " + text;

        private static void AnnouncePreparation(string text, ulong lobbyId, bool broadcast, bool showLocal,
            LobbyLaunchAnnouncementKind kind)
        {
            if (!IsAvailable || lobbyId == 0 || (!broadcast && !showLocal)) return;
            if (UnityMainThreadDispatch.IsMainThread) AnnouncePreparationCore(text, lobbyId, broadcast, kind);
            else UnityMainThreadDispatch.TryEnqueue(() => AnnouncePreparationCore(text, lobbyId, broadcast, kind));
        }

        private static void AnnouncePreparationCore(string text, ulong expectedLobby, bool broadcast, LobbyLaunchAnnouncementKind kind)
        {
            try
            {
                if (!TryGetLobby(out ulong lobbyId, out bool isHost, out var view) || lobbyId != expectedLobby) return;
                var item = new LobbyLaunchStatusItem(text);
                if (item.Text.Length == 0) return;
                string reason = kind == LobbyLaunchAnnouncementKind.Blocked
                    ? item.Text + " " + UiText.Get("LobbyLaunch.TryAgain") : item.Text;
                var lines = LobbyLaunchChat.Format(Prefix(kind) + " (" +
                    OperationText(LobbyLaunchOperation.Start) + ")", new[] { reason });
                DateTime now = DateTime.UtcNow;
                if (broadcast && isHost) SendExplicit(lobbyId, lines, now);
                else ShowLocal(view, lines, now, deduplicate: false);
            }
            catch (Exception error) { Report(false, "LOBBY_LAUNCH_ATTEMPT_FAILED: " + error, null); }
        }

        internal static void WriteAttemptError(ManualLogSource target, string text, string detail)
        {
            // An explicit rejection is one event per attempt. RuntimeHealth throttles repeating background faults.
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                string line = string.IsNullOrEmpty(detail) ? text : text + "; " + detail;
                (DebugLogHelper.UnwrapServiceLog(target) ?? EarlyAttemptLog.Value).LogError("[" + timestamp + "] " + line.Replace('\r', ' ').Replace('\n', ' '));
            }
            catch { /* Reporting must not interrupt the rejection. */ }
        }

        private static void AnnounceAttempt(LobbyLaunchOperation operation, LobbyLaunchStatusItem item)
        {
            try
            {
                DateTime now = DateTime.UtcNow;
                if (TryGetLobby(out ulong lobbyId, out bool isHost, out _))
                {
                    Broadcast.MarkAnnounced(Scope(lobbyId, isHost), item.Text);
                    LocalOnly.MarkAnnounced(Scope(lobbyId, isHost), item.Text);
                    // A rejected click is always answered, even if another player has just sent the same line.
                    SendExplicit(lobbyId, LobbyLaunchChat.Format(Prefix(LobbyLaunchAnnouncementKind.Blocked) + " (" + OperationText(operation) + ")",
                        new[] { item.Text }), now);
                }
            }
            catch (Exception error) { Report(false, "LOBBY_LAUNCH_ATTEMPT_FAILED: " + error, null); }
            RequestRefresh();
        }

        /// <summary>Whether a received chat text is a launch status announcement sent by this service.</summary>
        public static bool IsAnnouncement(string chatText) =>
            chatText != null && chatText.StartsWith(Marker, StringComparison.Ordinal);

        internal static void Initialize(ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            if (!LobbyChatPresentation.TryRegisterSystemMessageRule(APISharedPlugin.PluginGuid, "LobbyLaunchStatus", Classify, out string diagnostic))
                Report(false, "LOBBY_LAUNCH_SYSTEM_RULE unavailable: " + diagnostic, null);
            dueTimer = new Timer(_ => RequestRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            IsAvailable = true;
        }

        private static bool Classify(LobbyChatMessage message)
        {
            if (!IsAnnouncement(message.Message)) return false;
            DateTime now = DateTime.UtcNow;
            Received.Note(message.Message, now);
            RevealCoopChat();
            // Multi-part announcements and duplicate deliveries are logged once.
            if (message.Message != lastReceived || (now - lastReceivedUtc).TotalSeconds >= 2)
            {
                lastReceived = message.Message; lastReceivedUtc = now;
                NativeApiLog.Info(log, "LOBBY_LAUNCH_RECEIVED sender=" + message.Name + ": " + message.Message);
            }
            return true;
        }

        private static void Refresh()
        {
            Interlocked.Exchange(ref refreshQueued, 0);
            try
            {
                if (!TryGetLobby(out ulong lobbyId, out bool isHost, out FRONT_Multiplayer view))
                {
                    Broadcast.Reset();
                    LocalOnly.Reset();
                    Received.Clear();
                    Schedule(null);
                    return;
                }
                var query = new LobbyLaunchStatusQuery(lobbyId, isHost, view);
                var items = new List<LobbyLaunchStatusItem>();
                foreach (var entry in Providers.Snapshot)
                {
                    try
                    {
                        IEnumerable<LobbyLaunchStatusItem> provided = entry.Callback(query);
                        if (provided != null) items.AddRange(provided.Where(item => item != null && item.Text.Length != 0));
                    }
                    catch (Exception error) { Report(true, "LOBBY_LAUNCH_PROVIDER_FAILED " + entry.Owner + "/" + entry.Id + ": " + error, null); }
                }
                string scope = Scope(lobbyId, isHost);
                string context = "lobby=" + lobbyId + ", role=" + (isHost ? "host" : "client");
                DateTime now = DateTime.UtcNow;
                // The host reports every player's state; members only send what the host cannot detect.
                List<LobbyLaunchStatusItem> shared = isHost ? items : items.Where(item => item.Scope == LobbyLaunchStatusScope.Lobby).ToList();
                List<LobbyLaunchStatusItem> local = isHost ? new List<LobbyLaunchStatusItem>() :
                    items.Where(item => item.Scope == LobbyLaunchStatusScope.Local).ToList();
                Publish(Broadcast.Observe(scope, shared, now, isHost, out DateTime? sharedDue), items, context, null, now);
                Publish(LocalOnly.Observe(scope, local, now, false, out DateTime? localDue), items, context + ", audience=local", view, now);
                Schedule(!sharedDue.HasValue || localDue.HasValue && localDue.Value < sharedDue.Value ? localDue : sharedDue);
            }
            catch (Exception error) { Report(true, "LOBBY_LAUNCH_REFRESH_FAILED: " + error, null); }
        }

        // localView == null sends to the lobby; otherwise the lines are shown only in the local lobby chat.
        private static void Publish(IReadOnlyList<LobbyLaunchAnnouncement> announcements, List<LobbyLaunchStatusItem> items,
            string context, FRONT_Multiplayer localView, DateTime now)
        {
            foreach (LobbyLaunchAnnouncement announcement in announcements)
            {
                if (announcement.Kind == LobbyLaunchAnnouncementKind.Ready)
                    NativeApiLog.Info(log, "LOBBY_LAUNCH_READY " + context);
                else
                    foreach (string text in announcement.Texts)
                    {
                        string detail = string.Join("; ", items.Where(item => item.Text == text && item.Detail.Length != 0)
                            .Select(item => item.Detail).Distinct(StringComparer.Ordinal));
                        if (announcement.Kind == LobbyLaunchAnnouncementKind.Loading)
                            NativeApiLog.Info(log, "LOBBY_LAUNCH_LOADING " + context + ": " + text + (detail.Length == 0 ? "" : "; " + detail));
                        else Report(false, "LOBBY_LAUNCH_BLOCKED " + context + ": " + text, detail);
                    }
                string[] lines = announcement.Kind == LobbyLaunchAnnouncementKind.Ready
                    ? new[] { Marker + UiText.Get("LobbyLaunch.Ready") }
                    : LobbyLaunchChat.Format(Prefix(announcement.Kind), announcement.Texts);
                if (ReferenceEquals(localView, null)) Send(lines, now);
                else ShowLocal(localView, lines, now);
            }
        }

        private static void ShowLocal(FRONT_Multiplayer view, IEnumerable<string> lines, DateTime now, bool deduplicate = true)
        {
            try
            {
                foreach (string line in lines)
                {
                    if (deduplicate && Received.Contains(line, now)) continue;
                    if (LobbyChatPresentation.TryShowLocal(view, line)) RevealCoopChat();
                    else Report(false, "LOBBY_LAUNCH_LOCAL_UNAVAILABLE: " + line, null);
                }
            }
            catch (Exception error)
            {
                LocalOnly.Reset();
                Report(false, "LOBBY_LAUNCH_LOCAL_FAILED: " + error, null);
            }
        }

        // Coop lobbies collapse their chat by default and only flag new messages. An announcement opens it exactly like
        // Vanilla's "ChatToggle" button (visible flag set, new-message flag cleared). Where Vanilla offers no Coop
        // chat (Show_CoopConnectedChat false), nothing changes.
        private static void RevealCoopChat()
        {
            try
            {
                MainViewModel model = MainViewModel.Instance;
                if (!FRONT_Multiplayer.coopGame || ReferenceEquals(model, null) || !model.Show_CoopConnectedChat ||
                    model._show_CoopConnectedChatVisible) return;
                model.Show_CoopConnectedChatVisible = true;
                model.CoopNewChatVis = false;
            }
            catch (Exception error) { Report(false, "LOBBY_LAUNCH_CHAT_REVEAL_FAILED: " + error, null); }
        }

        private static string Scope(ulong lobbyId, bool isHost) => lobbyId + (isHost ? ":host" : ":member");

        private static void Schedule(DateTime? dueUtc)
        {
            lock (TimerGate)
            {
                if (dueTimer == null) return;
                long delay = dueUtc.HasValue ? Math.Max(1L, (long)Math.Ceiling((dueUtc.Value - DateTime.UtcNow).TotalMilliseconds)) : Timeout.Infinite;
                dueTimer.Change(delay, Timeout.Infinite);
            }
        }

        private static void SendExplicit(ulong lobbyId, IEnumerable<string> messages, DateTime now) =>
            LobbyLaunchChat.SendOrShowLocal(messages, message => TrySendExplicit(lobbyId, message, now),
                message =>
                {
                    if (TryGetLobby(out ulong current, out _, out var view) && current == lobbyId)
                        ShowLocal(view, new[] { message }, now, deduplicate: false);
                });

        private static bool TrySendExplicit(ulong lobbyId, string message, DateTime now)
        {
            if (!TryGetLobby(out ulong current, out _, out _) || current != lobbyId) return false;
            // Keep Extender's outbound notification. The Vanilla wrapper discards Steam's
            // result, so explicit system hints call the same Steam method directly.
            try { GameNetworkAPI.Instance.HandleSendLobbyChatMessage(message); }
            catch (Exception error) { Report(false, "LOBBY_LAUNCH_SEND_OBSERVER_FAILED: " + error, null); }
            if (!TryGetLobby(out current, out _, out _) || current != lobbyId) return false;
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(message);
                if (SteamMatchmaking.SendLobbyChatMsg(new CSteamID(lobbyId), bytes, bytes.Length))
                {
                    Received.ExpectEcho(message, now);
                    return true;
                }
                Report(false, "LOBBY_LAUNCH_SEND_FAILED: Steam rejected the lobby system message.", null);
            }
            catch (Exception error) { Report(false, "LOBBY_LAUNCH_SEND_FAILED: " + error, null); }
            return false;
        }

        private static bool Send(IEnumerable<string> messages, DateTime now, bool deduplicate = true)
        {
            try
            {
                foreach (string message in messages)
                {
                    // Another player has just sent the same line, e.g. two members with the same local problem.
                    if (deduplicate && Received.Contains(message, now)) continue;
                    Received.ExpectEcho(message, now);
                    Platform_Multiplayer.Instance.SendLobbyChatMessage(message);
                }
                return true;
            }
            catch (Exception error)
            {
                // A later refresh announces the current state again instead of assuming delivery.
                Broadcast.Reset();
                Report(false, "LOBBY_LAUNCH_SEND_FAILED: " + error, null);
                return false;
            }
        }

        // Same lobby identity rules as Vanilla's chat: a real Steam lobby, not a local skirmish or single-player Coop.
        private static bool TryGetLobby(out ulong lobbyId, out bool isHost, out FRONT_Multiplayer view)
        {
            lobbyId = 0; isHost = false; view = null;
            if (!MainViewModel.viewModelLoaded || MissionLifecycleService.ActiveContext != null) return false;
            view = MainViewModel.Instance?.FRONTMultiplayer;
            Platform_Multiplayer.MPLobby lobby = ReferenceEquals(view, null) ? null : view.currentLobby;
            if (lobby == null || view.singlePlayerCoop || FRONT_Multiplayer.skirmishGame || lobby.id.m_SteamID == 0 ||
                Platform_Multiplayer.Instance?.activeLobby?.id.m_SteamID != lobby.id.m_SteamID) return false;
            lobbyId = lobby.id.m_SteamID;
            isHost = lobby.isHost;
            return true;
        }

        private static string Prefix(LobbyLaunchAnnouncementKind kind) => UiText.Get(
            kind == LobbyLaunchAnnouncementKind.Loading ? "LobbyLaunch.Loading" : "LobbyLaunch.Blocked");

        private static string OperationText(LobbyLaunchOperation operation) => UiText.Get(
            operation == LobbyLaunchOperation.Ready ? "LobbyLaunch.Operation.Ready" : "LobbyLaunch.Operation.Start");

        // Long details stay complete: the shared health ledger bounds one detail to 4096 characters.
        private static void Report(bool error, string text, string detail)
        {
            string full = string.IsNullOrEmpty(detail) ? text : text + "; " + detail;
            var parts = LobbyLaunchChat.Split(full, 3000);
            for (int index = 0; index < parts.Count; index++)
            {
                string part = (parts.Count == 1 ? "" : "[" + (index + 1) + "/" + parts.Count + "] ") + parts[index];
                if (error) NativeApiLog.Error(log, part); else NativeApiLog.Warning(log, part);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using BepInEx.Logging;
using CrusaderDE;

namespace APIShared.Lobby
{
    /// <summary>How a lobby launch status item is announced.</summary>
    public enum LobbyLaunchStatusKind
    {
        /// <summary>A condition that currently prevents Ready/Start; announced immediately.</summary>
        Blocking = 0,
        /// <summary>An expected transitional state, such as a joining player loading the selected mission.
        /// Announced as a neutral wait hint; reported as blocking only if it persists.</summary>
        Loading = 1,
        /// <summary>An obvious open-ended wait, such as a lobby without its second player. Never announced;
        /// while present it suppresses the "ready" announcement.</summary>
        Waiting = 2,
    }

    /// <summary>The lobby command a consumer rejected; APIShared supplies the localized label.</summary>
    public enum LobbyLaunchOperation
    {
        /// <summary>The Ready or ReadyLock command of a lobby member.</summary>
        Ready = 0,
        /// <summary>A start command, such as the host's Play or Coop start.</summary>
        Start = 1,
    }

    /// <summary>One immutable, player-facing launch status entry.</summary>
    public sealed class LobbyLaunchStatusItem
    {
        /// <summary>Creates an entry. <paramref name="text"/> is sent to lobby chat in the sender's language;
        /// <paramref name="detail"/> is written only to the local log.</summary>
        public LobbyLaunchStatusItem(string text, LobbyLaunchStatusKind kind = LobbyLaunchStatusKind.Blocking, string detail = null)
        {
            Text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            Kind = kind;
            Detail = detail ?? string.Empty;
        }
        /// <summary>Localized text; its ordinal value identifies the entry for deduplication.</summary>
        public string Text { get; }
        /// <summary>Announcement class of this entry.</summary>
        public LobbyLaunchStatusKind Kind { get; }
        /// <summary>Technical log detail; never sent to other players.</summary>
        public string Detail { get; }
    }

    /// <summary>Lobby passed to providers during one refresh.</summary>
    public sealed class LobbyLaunchStatusQuery
    {
        internal LobbyLaunchStatusQuery(ulong lobbyId, bool isHost, FRONT_Multiplayer lobby)
        { LobbyId = lobbyId; IsHost = isHost; Lobby = lobby; }
        /// <summary>Steam identifier of the active lobby.</summary>
        public ulong LobbyId { get; }
        /// <summary>Whether the local player hosts the lobby.</summary>
        public bool IsHost { get; }
        /// <summary>Vanilla lobby view; read it only during the provider call.</summary>
        public FRONT_Multiplayer Lobby { get; }
    }

    /// <summary>Shared, deduplicated lobby chat announcements explaining why a multiplayer lobby cannot start yet.</summary>
    /// <remarks>
    /// Consumers keep their own launch checks and enforcement; this service only explains them. Providers are
    /// permanent owner/ID registrations that run synchronously on the Unity thread in ordinal owner/ID order whenever
    /// a coalesced refresh runs, and only while the local player is in an active Steam multiplayer lobby outside a
    /// mission. Provider exceptions are isolated and logged. A loading phase is announced once as a wait hint when it
    /// begins; each loading entry is announced
    /// once more as blocking after 20 seconds; blocking entries are announced when they first appear; waiting entries
    /// are never announced. A single "ready" announcement follows when every previously announced entry has
    /// disappeared and no waiting entry remains. Identical texts are
    /// announced once per lobby until they disappear. Each refresh sends at most one message per category; long
    /// messages are split into numbered parts of at most 280 characters at word boundaries. Received announcements
    /// are classified as lobby system hints. There is no replay or teardown.
    /// </remarks>
    public static class LobbyLaunchStatus
    {
        internal const string Marker = "[Mods] ";
        private static readonly OwnedCallbacks<Func<LobbyLaunchStatusQuery, IEnumerable<LobbyLaunchStatusItem>>> Providers =
            new OwnedCallbacks<Func<LobbyLaunchStatusQuery, IEnumerable<LobbyLaunchStatusItem>>>();
        private static readonly LobbyLaunchAnnouncementState State = new LobbyLaunchAnnouncementState();
        private static readonly object TimerGate = new object();
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
        /// The entry is always logged as an error. In an active lobby it is announced without waiting for a refresh
        /// unless the same text was sent within the last two seconds, and it counts as already announced for later
        /// automatic refreshes.</remarks>
        public static void ReportAttempt(string ownerGuid, LobbyLaunchOperation operation, LobbyLaunchStatusItem item)
        {
            if (item == null || item.Text.Length == 0) return;
            Report(true, "LOBBY_LAUNCH_BLOCKED owner=" + ownerGuid + ", operation=" + operation + ": " + item.Text, item.Detail);
            if (!IsAvailable) return;
            // Lobby and chat state belong to the Unity thread; other callers are marshalled without blocking.
            if (!UnityMainThreadDispatch.IsMainThread)
            {
                UnityMainThreadDispatch.TryEnqueue(() => AnnounceAttempt(operation, item));
                return;
            }
            AnnounceAttempt(operation, item);
        }

        private static void AnnounceAttempt(LobbyLaunchOperation operation, LobbyLaunchStatusItem item)
        {
            try
            {
                if (TryGetLobby(out ulong lobbyId, out _, out _) &&
                    State.ShouldAnnounceAttempt(lobbyId.ToString(), item.Text, DateTime.UtcNow))
                    Send(LobbyLaunchChat.Format(Prefix(LobbyLaunchAnnouncementKind.Blocked) + " (" + OperationText(operation) + ")",
                        new[] { item.Text }));
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
                    State.Reset();
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
                IReadOnlyList<LobbyLaunchAnnouncement> announcements =
                    State.Observe(lobbyId.ToString(), items, DateTime.UtcNow, out DateTime? due);
                foreach (LobbyLaunchAnnouncement announcement in announcements)
                {
                    string context = "lobby=" + lobbyId + ", role=" + (isHost ? "host" : "client");
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
                    Send(announcement.Kind == LobbyLaunchAnnouncementKind.Ready
                        ? new[] { Marker + UiText.Get("LobbyLaunch.Ready") }
                        : LobbyLaunchChat.Format(Prefix(announcement.Kind), announcement.Texts));
                }
                Schedule(due);
            }
            catch (Exception error) { Report(true, "LOBBY_LAUNCH_REFRESH_FAILED: " + error, null); }
        }

        private static void Schedule(DateTime? dueUtc)
        {
            lock (TimerGate)
            {
                if (dueTimer == null) return;
                long delay = dueUtc.HasValue ? Math.Max(1L, (long)Math.Ceiling((dueUtc.Value - DateTime.UtcNow).TotalMilliseconds)) : Timeout.Infinite;
                dueTimer.Change(delay, Timeout.Infinite);
            }
        }

        private static void Send(IEnumerable<string> messages)
        {
            try
            {
                foreach (string message in messages) Platform_Multiplayer.Instance.SendLobbyChatMessage(message);
            }
            catch (Exception error)
            {
                // A later refresh announces the current state again instead of assuming delivery.
                State.Reset();
                Report(false, "LOBBY_LAUNCH_SEND_FAILED: " + error, null);
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

    internal enum LobbyLaunchAnnouncementKind { Loading, Blocked, Ready }

    internal sealed class LobbyLaunchAnnouncement
    {
        internal LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind kind, string[] texts) { Kind = kind; Texts = texts; }
        internal LobbyLaunchAnnouncementKind Kind { get; }
        internal string[] Texts { get; }
    }

    // Pure state; all time is supplied by the caller. Not thread-safe: the service uses it on the Unity thread only.
    internal sealed class LobbyLaunchAnnouncementState
    {
        internal static readonly TimeSpan LoadingEscalation = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan AttemptThrottle = TimeSpan.FromSeconds(2);
        private string scope = string.Empty;
        private readonly Dictionary<string, DateTime> loadingSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> announcedBlocking = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> attempts = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private bool readyPending, loadingPhase;

        internal void Reset()
        {
            scope = string.Empty; loadingSince.Clear(); announcedBlocking.Clear();
            attempts.Clear(); readyPending = loadingPhase = false;
        }

        internal bool ShouldAnnounceAttempt(string currentScope, string text, DateTime now)
        {
            EnterScope(currentScope);
            if (attempts.TryGetValue(text, out DateTime last) && now - last < AttemptThrottle) return false;
            attempts[text] = now;
            if (attempts.Count > 128) { attempts.Clear(); attempts[text] = now; }
            announcedBlocking.Add(text);
            readyPending = true;
            return true;
        }

        internal IReadOnlyList<LobbyLaunchAnnouncement> Observe(string currentScope, IEnumerable<LobbyLaunchStatusItem> items,
            DateTime now, out DateTime? nextDue)
        {
            EnterScope(currentScope);
            var loading = new List<string>();
            var blocking = new List<string>();
            bool waiting = false;
            foreach (LobbyLaunchStatusItem item in items ?? Enumerable.Empty<LobbyLaunchStatusItem>())
            {
                if (item == null || item.Text.Length == 0) continue;
                if (item.Kind == LobbyLaunchStatusKind.Waiting) { waiting = true; continue; }
                List<string> target = item.Kind == LobbyLaunchStatusKind.Loading ? loading : blocking;
                if (!target.Contains(item.Text, StringComparer.Ordinal)) target.Add(item.Text);
            }
            // An entry reported as blocking by any owner is not also shown as loading.
            loading.RemoveAll(text => blocking.Contains(text, StringComparer.Ordinal));

            foreach (string stale in loadingSince.Keys.Where(text => !loading.Contains(text, StringComparer.Ordinal)).ToArray())
                loadingSince.Remove(stale);
            foreach (string text in loading)
                if (!loadingSince.ContainsKey(text)) loadingSince[text] = now;
            // Escalated loading entries are announced as blocking and tracked like blocking entries.
            var due = loading.Where(text => now - loadingSince[text] >= LoadingEscalation).ToList();
            var currentBlocking = blocking.Concat(due).ToList();
            announcedBlocking.IntersectWith(currentBlocking);

            var result = new List<LobbyLaunchAnnouncement>();
            var pendingLoading = loading.Except(due, StringComparer.Ordinal).ToArray();
            // One wait hint per loading phase: texts that change while players load are not repeated.
            if (pendingLoading.Length != 0 && !loadingPhase)
                result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Loading, pendingLoading));
            loadingPhase = loading.Count != 0;
            if (currentBlocking.Any(text => !announcedBlocking.Contains(text)))
                result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Blocked, currentBlocking.ToArray()));
            announcedBlocking.UnionWith(currentBlocking);

            if (result.Count != 0) readyPending = true;
            else if (readyPending && !waiting && loading.Count == 0 && blocking.Count == 0)
            {
                readyPending = false;
                result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Ready, Array.Empty<string>()));
            }

            nextDue = null;
            foreach (string text in pendingLoading)
            {
                DateTime candidate = loadingSince[text] + LoadingEscalation;
                if (!nextDue.HasValue || candidate < nextDue.Value) nextDue = candidate;
            }
            return result;
        }

        private void EnterScope(string currentScope)
        {
            if (string.Equals(scope, currentScope ?? string.Empty, StringComparison.Ordinal)) return;
            Reset();
            scope = currentScope ?? string.Empty;
        }
    }

    internal static class LobbyLaunchChat
    {
        internal const int MaximumMessageLength = 280;

        // Numbered parts keep every text; a part never ends inside a surrogate pair.
        internal static string[] Format(string prefix, IEnumerable<string> texts)
        {
            string body = prefix + ": " + string.Join("; ", texts.Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
            const int numbering = 10; // "[99/99] "
            List<string> parts = Split(body, MaximumMessageLength - LobbyLaunchStatus.Marker.Length - numbering);
            return parts.Select((part, index) => LobbyLaunchStatus.Marker +
                (parts.Count == 1 ? string.Empty : "[" + (index + 1) + "/" + parts.Count + "] ") + part).ToArray();
        }

        internal static List<string> Split(string text, int limit)
        {
            var parts = new List<string>();
            int offset = 0;
            while (offset < text.Length)
            {
                int length = Math.Min(limit, text.Length - offset);
                if (offset + length < text.Length)
                {
                    // Prefer the last space in the final third of the part.
                    int space = text.LastIndexOf(' ', offset + length - 1, length);
                    if (space > offset + limit * 2 / 3) length = space - offset + 1;
                    else if (char.IsHighSurrogate(text[offset + length - 1])) length--;
                }
                parts.Add(text.Substring(offset, length).Trim());
                offset += length;
            }
            return parts;
        }
    }
}

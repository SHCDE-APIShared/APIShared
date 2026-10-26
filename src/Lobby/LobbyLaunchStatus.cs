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

    /// <summary>Who sees an entry reported while the local player is a lobby member rather than the host.</summary>
    /// <remarks>The host always announces its entries to the whole lobby.</remarks>
    public enum LobbyLaunchStatusScope
    {
        /// <summary>Shown only in the local lobby chat. Use it for states the host already reports for every player.</summary>
        Local = 0,
        /// <summary>Sent to the whole lobby. Use it for local conditions the host cannot detect, such as a missing file.</summary>
        Lobby = 1,
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
        /// <summary>Creates a <see cref="LobbyLaunchStatusScope.Local"/> entry. <paramref name="text"/> is shown in lobby
        /// chat in the sender's language; <paramref name="detail"/> is written only to the local log.</summary>
        public LobbyLaunchStatusItem(string text, LobbyLaunchStatusKind kind = LobbyLaunchStatusKind.Blocking, string detail = null)
            : this(text, kind, detail, LobbyLaunchStatusScope.Local) { }

        /// <summary>Creates an entry with an explicit audience; <paramref name="scope"/> applies to lobby members.</summary>
        public LobbyLaunchStatusItem(string text, LobbyLaunchStatusKind kind, string detail, LobbyLaunchStatusScope scope)
        {
            Text = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            Kind = kind;
            Detail = detail ?? string.Empty;
            Scope = scope;
        }
        /// <summary>Localized text; its ordinal value identifies the entry for deduplication.</summary>
        public string Text { get; }
        /// <summary>Announcement class of this entry.</summary>
        public LobbyLaunchStatusKind Kind { get; }
        /// <summary>Technical log detail; never sent to other players.</summary>
        public string Detail { get; }
        /// <summary>Audience of the entry while the local player is a lobby member; ignored on the host.</summary>
        public LobbyLaunchStatusScope Scope { get; }
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
    /// mission. Provider exceptions are isolated and logged.
    /// <para>Audience: the host is the only sender of automatic announcements for the whole lobby. On a lobby member,
    /// <see cref="LobbyLaunchStatusScope.Local"/> entries are only shown in the local lobby chat as Vanilla system
    /// hints (no network traffic, visible in muted chat), and <see cref="LobbyLaunchStatusScope.Lobby"/> entries are
    /// sent to everyone. Lobby members never announce "ready". A line that another player sent within the last five
    /// seconds is not sent or shown again. In a Coop lobby, a received or locally shown announcement opens the
    /// collapsed Coop chat the way Vanilla's chat button does; other chat messages keep Vanilla's behavior.</para>
    /// <para>Timing: a loading phase is announced once as a wait hint when it begins; each loading entry is announced
    /// once more as blocking after 20 seconds; blocking entries are announced when they first appear; waiting entries
    /// are never announced. On the host, a single "ready" announcement follows once every previously announced entry
    /// has disappeared, no waiting entry remains and that state has lasted 1.5 seconds. A rejected command reported
    /// through <see cref="ReportAttempt"/> does not arm "ready". Identical texts are announced once per lobby until
    /// they disappear. Each refresh sends at most one message per category; long messages are split into numbered
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
        /// without waiting for a refresh, unless the same text was sent within the last two seconds; the item's scope
        /// is ignored. It counts as already announced for later automatic refreshes but does not arm "ready".</remarks>
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
                DateTime now = DateTime.UtcNow;
                if (TryGetLobby(out ulong lobbyId, out bool isHost, out _) &&
                    Broadcast.ShouldAnnounceAttempt(Scope(lobbyId, isHost), item.Text, now))
                {
                    LocalOnly.MarkAnnounced(Scope(lobbyId, isHost), item.Text);
                    Send(LobbyLaunchChat.Format(Prefix(LobbyLaunchAnnouncementKind.Blocked) + " (" + OperationText(operation) + ")",
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

        private static void ShowLocal(FRONT_Multiplayer view, IEnumerable<string> lines, DateTime now)
        {
            try
            {
                foreach (string line in lines)
                {
                    if (Received.Contains(line, now)) continue;
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

        private static void Send(IEnumerable<string> messages, DateTime now)
        {
            try
            {
                foreach (string message in messages)
                {
                    // Another player has just sent the same line, e.g. two members with the same local problem.
                    if (Received.Contains(message, now)) continue;
                    Received.ExpectEcho(message, now);
                    Platform_Multiplayer.Instance.SendLobbyChatMessage(message);
                }
            }
            catch (Exception error)
            {
                // A later refresh announces the current state again instead of assuming delivery.
                Broadcast.Reset();
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
        internal static readonly TimeSpan ReadyDelay = TimeSpan.FromSeconds(1.5);
        private string scope = string.Empty;
        private readonly Dictionary<string, DateTime> loadingSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> announcedBlocking = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> attempts = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private bool readyPending, loadingPhase;
        private DateTime? emptySince;

        internal void Reset()
        {
            scope = string.Empty; loadingSince.Clear(); announcedBlocking.Clear();
            attempts.Clear(); readyPending = loadingPhase = false; emptySince = null;
        }

        internal bool ShouldAnnounceAttempt(string currentScope, string text, DateTime now)
        {
            EnterScope(currentScope);
            if (attempts.TryGetValue(text, out DateTime last) && now - last < AttemptThrottle) return false;
            attempts[text] = now;
            if (attempts.Count > 128) { attempts.Clear(); attempts[text] = now; }
            // A rejected command does not arm "ready": only provider entries describe a state that can end.
            announcedBlocking.Add(text);
            return true;
        }

        internal void MarkAnnounced(string currentScope, string text)
        {
            EnterScope(currentScope);
            announcedBlocking.Add(text);
        }

        internal IReadOnlyList<LobbyLaunchAnnouncement> Observe(string currentScope, IEnumerable<LobbyLaunchStatusItem> items,
            DateTime now, bool announceReady, out DateTime? nextDue)
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
            var result = new List<LobbyLaunchAnnouncement>();
            var pendingLoading = loading.Except(due, StringComparer.Ordinal).ToArray();
            // One wait hint per loading phase: texts that change while players load are not repeated. A reason the
            // players have just seen as a rejected command is not repeated as a wait hint either.
            if (pendingLoading.Length != 0 && !loadingPhase)
            {
                string[] hint = pendingLoading.Where(text => !announcedBlocking.Contains(text)).ToArray();
                if (hint.Length != 0) result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Loading, hint));
            }
            loadingPhase = loading.Count != 0;
            announcedBlocking.IntersectWith(currentBlocking);
            if (currentBlocking.Any(text => !announcedBlocking.Contains(text)))
                result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Blocked, currentBlocking.ToArray()));
            announcedBlocking.UnionWith(currentBlocking);

            // Every loading or blocking entry has been communicated, by this state or as a rejected command, so its
            // end is announced. A rejected command alone (no provider entry) never arms "ready".
            bool reported = loading.Count != 0 || blocking.Count != 0;
            if (reported && announceReady) readyPending = true;
            if (reported || waiting || !readyPending) emptySince = null;
            else
            {
                // A single empty refresh, e.g. right after a rejected click, must not claim readiness.
                if (!emptySince.HasValue) emptySince = now;
                if (now - emptySince.Value >= ReadyDelay)
                {
                    readyPending = false; emptySince = null;
                    result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Ready, Array.Empty<string>()));
                }
            }

            nextDue = emptySince.HasValue ? emptySince.Value + ReadyDelay : (DateTime?)null;
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

    // Announcement lines recently received from other players. Unity thread only.
    internal sealed class LobbyLaunchReceivedLines
    {
        internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
        private const int Capacity = 64;
        private readonly Dictionary<string, DateTime> received = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> echoes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        internal void Clear() { received.Clear(); echoes.Clear(); }

        // Steam delivers a sent lobby line back to its sender; that echo must not suppress the sender's own retry.
        internal void ExpectEcho(string line, DateTime now) => Put(echoes, line, now);

        internal void Note(string line, DateTime now)
        {
            if (echoes.TryGetValue(line, out DateTime sent) && now - sent < Window) { echoes.Remove(line); return; }
            Put(received, line, now);
        }

        internal bool Contains(string line, DateTime now) => received.TryGetValue(line, out DateTime at) && now - at < Window;

        private static void Put(Dictionary<string, DateTime> lines, string line, DateTime now)
        {
            if (lines.Count >= Capacity)
                foreach (string old in lines.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToArray())
                    lines.Remove(old);
            if (lines.Count >= Capacity) lines.Clear();
            lines[line] = now;
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

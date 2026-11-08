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
        internal static readonly TimeSpan LoadingGrace = TimeSpan.FromSeconds(1.5);
        internal static readonly TimeSpan ReadyDelay = TimeSpan.FromSeconds(1.5);
        private string scope = string.Empty;
        private readonly Dictionary<string, DateTime> loadingSince = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly HashSet<string> announcedBlocking = new HashSet<string>(StringComparer.Ordinal);
        // Loading phase: hintSent = wait hint announced; phaseCommunicated = hint sent or a loading text the players
        // already saw as a rejected command, so the phase's end is worth a "ready".
        private bool readyPending, hintSent, phaseCommunicated;
        private DateTime? loadingPhaseSince, emptySince;

        internal void Reset()
        {
            scope = string.Empty; loadingSince.Clear(); announcedBlocking.Clear();
            readyPending = hintSent = phaseCommunicated = false; loadingPhaseSince = emptySince = null;
        }

        // A rejected command was announced: later refreshes do not repeat its text. It does not arm "ready" by
        // itself; only provider entries describe a state that can end.
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
            if (loading.Count == 0) { loadingPhaseSince = null; hintSent = phaseCommunicated = false; }
            else if (!loadingPhaseSince.HasValue) loadingPhaseSince = now;
            if (loading.Any(text => announcedBlocking.Contains(text))) phaseCommunicated = true;
            // One wait hint per loading phase, only once it has lasted LoadingGrace: short synchronizations stay
            // silent, and texts that change while players load are not repeated. A reason the players have just seen
            // as a rejected command is not repeated as a wait hint either.
            if (pendingLoading.Length != 0 && !hintSent && now - loadingPhaseSince.Value >= LoadingGrace)
            {
                hintSent = phaseCommunicated = true;
                string[] hint = pendingLoading.Where(text => !announcedBlocking.Contains(text)).ToArray();
                if (hint.Length != 0) result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Loading, hint));
            }
            announcedBlocking.IntersectWith(currentBlocking);
            if (currentBlocking.Any(text => !announcedBlocking.Contains(text)))
                result.Add(new LobbyLaunchAnnouncement(LobbyLaunchAnnouncementKind.Blocked, currentBlocking.ToArray()));
            announcedBlocking.UnionWith(currentBlocking);

            // Only a communicated state earns a "ready": blocking entries (always announced) and loading phases whose
            // hint was sent or whose reason the players saw as a rejected command. A silent short synchronization and
            // a rejected command alone (no provider entry) never arm it.
            bool reported = loading.Count != 0 || blocking.Count != 0;
            if (announceReady && (currentBlocking.Count != 0 || loading.Count != 0 && phaseCommunicated)) readyPending = true;
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
            if (pendingLoading.Length != 0 && !hintSent)
            {
                DateTime hintDue = loadingPhaseSince.Value + LoadingGrace;
                if (!nextDue.HasValue || hintDue < nextDue.Value) nextDue = hintDue;
            }
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
}

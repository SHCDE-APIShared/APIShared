using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using APIShared.Lobby;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class LobbyLaunchStatusTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 26, 17, 31, 13, DateTimeKind.Utc);
        private static LobbyLaunchStatusItem Loading(string text) => new LobbyLaunchStatusItem(text, LobbyLaunchStatusKind.Loading);
        private static LobbyLaunchStatusItem Blocking(string text) => new LobbyLaunchStatusItem(text);

        // Host perspective: the host is the only participant that announces "ready".
        private static IReadOnlyList<LobbyLaunchAnnouncement> Observe(LobbyLaunchAnnouncementState state, double seconds,
            params LobbyLaunchStatusItem[] items) => state.Observe("lobby", items, Start.AddSeconds(seconds), true, out _);

        [TestMethod]
        public void JoiningPlayerIsAnnouncedAsLoadingAndThenAsReadyWithoutBlockMessages()
        {
            var state = new LobbyLaunchAnnouncementState();
            var first = Observe(state, 0, Loading("Loading for: Guest"));
            Assert.AreEqual(1, first.Count);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Loading, first[0].Kind);
            CollectionAssert.AreEqual(new[] { "Loading for: Guest" }, first[0].Texts);
            Assert.AreEqual(0, Observe(state, 1, Loading("Loading for: Guest")).Count, "an unchanged wait hint was repeated");
            Assert.AreEqual(0, Observe(state, 2, Loading("Lord data pending for: Guest")).Count,
                "a changed loading text within the same loading phase was announced again");
            Assert.AreEqual(0, Observe(state, 3).Count, "ready did not wait for a stable empty state");
            var ready = Observe(state, 4.5);
            Assert.AreEqual(1, ready.Count);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, ready[0].Kind);
            Assert.AreEqual(0, Observe(state, 6).Count, "ready was announced twice");
            Assert.AreEqual(0, new LobbyLaunchAnnouncementState().Observe("lobby", new LobbyLaunchStatusItem[0], Start, true, out _).Count,
                "a lobby without previous announcements claimed readiness");
        }

        [TestMethod]
        public void WaitingIsSilentAndPreventsAFalseReadyWhenAPlayerLeaves()
        {
            var state = new LobbyLaunchAnnouncementState();
            var waiting = new LobbyLaunchStatusItem("second player", LobbyLaunchStatusKind.Waiting);
            Assert.AreEqual(0, state.Observe("lobby", new[] { waiting }, Start, true, out DateTime? due).Count, "an obvious wait was announced");
            Assert.IsNull(due, "a waiting entry escalated");
            Assert.AreEqual(0, Observe(state, 60, waiting).Count, "a waiting entry escalated after the loading delay");
            Observe(state, 61, Loading("Loading for: Guest"));
            Assert.AreEqual(0, Observe(state, 62, waiting).Count, "a player who left while loading produced a ready message");
            Observe(state, 70, Loading("Loading for: Guest"));
            Assert.AreEqual(0, Observe(state, 72).Count);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 73.5).Single().Kind);
        }

        [TestMethod]
        public void PersistentLoadingEscalatesOnceAndReportsItsDueTime()
        {
            var state = new LobbyLaunchAnnouncementState();
            state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, Start, true, out DateTime? due);
            Assert.AreEqual(Start + LobbyLaunchAnnouncementState.LoadingEscalation, due);
            Assert.AreEqual(0, state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, Start.AddSeconds(19), true, out due).Count);
            var escalated = state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, due.Value, true, out due);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Blocked, escalated.Single().Kind);
            Assert.IsNull(due, "an escalated entry kept a timer");
            Assert.AreEqual(0, Observe(state, 40, Loading("Waiting for: Guest")).Count, "escalation repeated");
        }

        [TestMethod]
        public void BlockingEntriesAreDeduplicatedAndReannouncedAfterTheyDisappear()
        {
            var state = new LobbyLaunchAnnouncementState();
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Blocked, Observe(state, 0, Blocking("a"), Blocking("b")).Single().Kind);
            Assert.AreEqual(0, Observe(state, 60, Blocking("b"), Blocking("a")).Count, "reordered entries were repeated");
            var added = Observe(state, 61, Blocking("a"), Blocking("b"), Blocking("c")).Single();
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, added.Texts, "a new entry did not carry the current context");
            Assert.AreEqual(0, Observe(state, 62, Blocking("a")).Count, "a shrinking set produced a message");
            Assert.AreEqual(1, Observe(state, 63, Blocking("a"), Blocking("b")).Count, "a reappearing entry stayed hidden");
            Assert.AreEqual(1, state.Observe("other-lobby", new[] { Blocking("a") }, Start, true, out _).Count, "a new lobby inherited suppression");
        }

        [TestMethod]
        public void RejectedCommandIsNotAnnouncedAgainByTheFollowingRefresh()
        {
            var state = new LobbyLaunchAnnouncementState();
            Observe(state, 0, Blocking("a"));
            Assert.IsTrue(state.ShouldAnnounceAttempt("lobby", "x", Start.AddSeconds(1)));
            Assert.AreEqual(0, Observe(state, 1, Blocking("a"), Blocking("x")).Count, "the click reason was sent twice");
            Assert.IsFalse(state.ShouldAnnounceAttempt("lobby", "x", Start.AddSeconds(2)), "click throttle missing");
            Assert.IsTrue(state.ShouldAnnounceAttempt("lobby", "x", Start.AddSeconds(4)), "an explicit retry stayed hidden");
            Observe(state, 5);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 6.5).Single().Kind,
                "the provider entry announced before the click did not end with ready");

            // A member's local view gets the same suppression for the reason it has just sent to the lobby.
            var local = new LobbyLaunchAnnouncementState();
            local.MarkAnnounced("lobby", "x");
            Assert.AreEqual(0, Observe(local, 0, Blocking("x")).Count, "the sent click reason was shown again locally");
        }

        // Regression for the 2026-10-26 lobby log: a rejected Start followed by an empty refresh 4 ms later
        // announced "ready" although the next click was still rejected.
        [TestMethod]
        public void RejectedClickAloneNeverClaimsReadinessAndReadyNeedsAStableEmptyState()
        {
            var state = new LobbyLaunchAnnouncementState();
            Assert.IsTrue(state.ShouldAnnounceAttempt("lobby", "x", Start));
            Assert.AreEqual(0, Observe(state, 0.004).Count, "an empty refresh after a rejected click claimed readiness");
            Assert.AreEqual(0, Observe(state, 30).Count, "a rejected click armed a later ready message");

            Observe(state, 40, Loading("l"));
            Assert.AreEqual(0, state.Observe("lobby", new LobbyLaunchStatusItem[0], Start.AddSeconds(41), true, out DateTime? due).Count);
            Assert.AreEqual(Start.AddSeconds(41) + LobbyLaunchAnnouncementState.ReadyDelay, due, "the ready check was not scheduled");
            var waiting = new LobbyLaunchStatusItem("partner", LobbyLaunchStatusKind.Waiting);
            Assert.AreEqual(0, Observe(state, 42, waiting).Count);
            Assert.AreEqual(0, Observe(state, 42.6).Count, "a flickering entry did not restart the stable period");
            Assert.AreEqual(0, Observe(state, 43.5).Count, "ready was announced before the restarted period ended");
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 44.1).Single().Kind);

            // The provider confirms the rejected reason as a transitional state: no second message with the same
            // reason, but its end is announced.
            Assert.IsTrue(state.ShouldAnnounceAttempt("lobby", "Player 2 has not applied the Lord data.", Start.AddSeconds(50)));
            Assert.AreEqual(0, Observe(state, 50.01, Loading("Player 2 has not applied the Lord data.")).Count,
                "the rejected reason was repeated as a wait hint");
            Assert.AreEqual(0, Observe(state, 50.3).Count);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 51.8).Single().Kind,
                "the end of the confirmed reason was not announced");
        }

        [TestMethod]
        public void MembersAnnounceTheirEntriesButNeverReady()
        {
            var state = new LobbyLaunchAnnouncementState();
            Assert.AreEqual(1, state.Observe("lobby", new[] { Blocking("restart required") }, Start, false, out _).Count);
            Assert.AreEqual(0, state.Observe("lobby", new LobbyLaunchStatusItem[0], Start.AddSeconds(1), false, out DateTime? due).Count);
            Assert.AreEqual(0, state.Observe("lobby", new LobbyLaunchStatusItem[0], Start.AddSeconds(60), false, out _).Count,
                "a lobby member announced ready for the whole lobby");
            Assert.IsNull(due, "a member scheduled a ready check");
        }

        [TestMethod]
        public void LinesFromOtherPlayersSuppressDuplicatesButOwnEchoesDoNot()
        {
            var lines = new LobbyLaunchReceivedLines();
            lines.ExpectEcho("[Mods] own", Start);
            lines.Note("[Mods] own", Start.AddSeconds(0.3));
            Assert.IsFalse(lines.Contains("[Mods] own", Start.AddSeconds(2)), "the sender's own echo suppressed its retry");
            lines.Note("[Mods] own", Start.AddSeconds(2.5));
            Assert.IsTrue(lines.Contains("[Mods] own", Start.AddSeconds(3)), "an identical line from another player was not recognized");
            Assert.IsFalse(lines.Contains("[Mods] own", Start.AddSeconds(2.5) + LobbyLaunchReceivedLines.Window),
                "a received line suppressed later announcements indefinitely");
            lines.Note("[Mods] other", Start);
            lines.Clear();
            Assert.IsFalse(lines.Contains("[Mods] other", Start), "leaving the lobby kept received lines");
        }

        [TestMethod]
        public void BlockingAndLoadingWithTheSameTextAreShownOnceAsBlocking()
        {
            var state = new LobbyLaunchAnnouncementState();
            var result = Observe(state, 0, Loading("same"), Blocking("same"), Loading("other"));
            CollectionAssert.AreEqual(new[] { "other" }, result.Single(item => item.Kind == LobbyLaunchAnnouncementKind.Loading).Texts);
            CollectionAssert.AreEqual(new[] { "same" }, result.Single(item => item.Kind == LobbyLaunchAnnouncementKind.Blocked).Texts);
        }

        [TestMethod]
        public void ChatPartsKeepEveryWordWithinTheVanillaLimit()
        {
            string emojiWord = string.Concat(Enumerable.Repeat("\U0001F40D", 10));
            string reason = string.Join(" ", Enumerable.Repeat(emojiWord, 15)) + " " +
                string.Join(" ", Enumerable.Repeat("synchronized", 40)) + " complete final reason";
            string[] messages = LobbyLaunchChat.Format("Start blocked", new[] { reason, "other failure", reason });
            Assert.IsTrue(messages.Length > 1 && messages.All(message => message.Length <= LobbyLaunchChat.MaximumMessageLength));
            Assert.IsTrue(messages.All(LobbyLaunchStatus.IsAnnouncement));
            Assert.IsTrue(messages.All(message => !char.IsHighSurrogate(message[message.Length - 1])), "a part split a surrogate pair");
            string[] bodies = messages.Select(message => Regex.Replace(message.Substring(LobbyLaunchStatus.Marker.Length), @"^\[\d+/\d+\] ", "")).ToArray();
            Assert.IsTrue(bodies.Skip(1).All(body => !body.StartsWith("ynchronized", StringComparison.Ordinal)), "a word was split");
            string joined = Regex.Replace(string.Join(" ", bodies), @"\s+", " ");
            Assert.AreEqual(Regex.Replace("Start blocked: " + reason + "; other failure", @"\s+", " "), joined,
                "a text was lost, reordered or repeated");
            // Text without spaces is split hard, but never inside a surrogate pair and without loss.
            string unbroken = string.Concat(Enumerable.Repeat("\U0001F40D", 150));
            List<string> hard = LobbyLaunchChat.Split(unbroken, 101);
            Assert.IsTrue(hard.All(part => part.Length <= 101 && !char.IsHighSurrogate(part[part.Length - 1])));
            Assert.AreEqual(unbroken, string.Concat(hard));
            Assert.IsFalse(LobbyLaunchStatus.IsAnnouncement("ordinary chat"));
            Assert.IsFalse(LobbyLaunchStatus.IsAnnouncement(null));
        }

        [TestMethod]
        public void ItemsNormalizeLineBreaksAndKeepDetailsLocal()
        {
            var item = new LobbyLaunchStatusItem(" line\r\nbreak ", detail: null);
            Assert.AreEqual("line  break", item.Text);
            Assert.AreEqual(string.Empty, item.Detail);
            Assert.AreEqual(LobbyLaunchStatusKind.Blocking, item.Kind);
            Assert.AreEqual(LobbyLaunchStatusScope.Local, item.Scope, "member entries are shown only locally by default");
        }
    }
}

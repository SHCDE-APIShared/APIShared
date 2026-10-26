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

        private static IReadOnlyList<LobbyLaunchAnnouncement> Observe(LobbyLaunchAnnouncementState state, double seconds,
            params LobbyLaunchStatusItem[] items) => state.Observe("lobby", items, Start.AddSeconds(seconds), out _);

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
            var ready = Observe(state, 3);
            Assert.AreEqual(1, ready.Count);
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, ready[0].Kind);
            Assert.AreEqual(0, Observe(state, 4).Count, "ready was announced twice");
            Assert.AreEqual(0, new LobbyLaunchAnnouncementState().Observe("lobby", new LobbyLaunchStatusItem[0], Start, out _).Count,
                "a lobby without previous announcements claimed readiness");
        }

        [TestMethod]
        public void WaitingIsSilentAndPreventsAFalseReadyWhenAPlayerLeaves()
        {
            var state = new LobbyLaunchAnnouncementState();
            var waiting = new LobbyLaunchStatusItem("second player", LobbyLaunchStatusKind.Waiting);
            Assert.AreEqual(0, state.Observe("lobby", new[] { waiting }, Start, out DateTime? due).Count, "an obvious wait was announced");
            Assert.IsNull(due, "a waiting entry escalated");
            Assert.AreEqual(0, Observe(state, 60, waiting).Count, "a waiting entry escalated after the loading delay");
            Observe(state, 61, Loading("Loading for: Guest"));
            Assert.AreEqual(0, Observe(state, 62, waiting).Count, "a player who left while loading produced a ready message");
            Observe(state, 70, Loading("Loading for: Guest"));
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 72).Single().Kind);
        }

        [TestMethod]
        public void PersistentLoadingEscalatesOnceAndReportsItsDueTime()
        {
            var state = new LobbyLaunchAnnouncementState();
            state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, Start, out DateTime? due);
            Assert.AreEqual(Start + LobbyLaunchAnnouncementState.LoadingEscalation, due);
            Assert.AreEqual(0, state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, Start.AddSeconds(19), out due).Count);
            var escalated = state.Observe("lobby", new[] { Loading("Waiting for: Guest") }, due.Value, out due);
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
            Assert.AreEqual(1, state.Observe("other-lobby", new[] { Blocking("a") }, Start, out _).Count, "a new lobby inherited suppression");
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
            Assert.AreEqual(LobbyLaunchAnnouncementKind.Ready, Observe(state, 5).Single().Kind);
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
        }
    }
}

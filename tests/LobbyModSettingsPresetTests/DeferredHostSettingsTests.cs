using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace LobbyModSettingsPresetTests
{
    [TestClass]
    [DoNotParallelize]
    public class DeferredHostSettingsTests
    {
        private sealed class HostSettings : PresetLobbyModSettingsViewModel
        {
            [SyncHostOnly] public int Amount { get; private set; }
            [SyncHostOnly] public bool Enabled { get; private set; }
            public void SetAmount(int value) { Amount = value; System_TriggerUpdate(nameof(Amount)); }
            public void SetEnabled(bool value) { Enabled = value; System_TriggerUpdate(nameof(Enabled)); }
        }

        // Replace the outbound network boundary, not the coordinator. A read here stands
        // for the original sender's local-player lookup and current-value serialization.
        private sealed class Lobby : IDisposable
        {
            internal readonly HostSettings Model = new HostSettings();
            internal readonly PerPlayerLobbySettingsCoordinator Coordinator;
            internal readonly List<string> Sent = new List<string>();
            internal object Identity = new object();
            internal ulong? Id = 10;
            internal bool Host = true, TransportReady = true;
            internal int GetterCalls, Notifications;
            internal Action AfterSend;

            internal Lobby(bool activate = true)
            {
                Coordinator = new PerPlayerLobbySettingsCoordinator(Model, null, "host", "test.host",
                    new PerPlayerLobbySettingsBuilder(Model).Build(), false);
                Coordinator.CaptureBroadcastLobby = () => new SettingsBroadcastLobby(Identity, Id);
                Coordinator.HasHostAuthority = () => Host;
                Coordinator.TransportReady = _ => TransportReady;
                Model.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName != nameof(HostSettings.Amount) && args.PropertyName != nameof(HostSettings.Enabled)) return;
                    Notifications++;
                    if (Coordinator.DeferBroadcast(args.PropertyName)) return;
                    GetterCalls++;
                    PropertyInfo property = Model.GetType().GetProperty(args.PropertyName);
                    Sent.Add(args.PropertyName + "=" + property.GetValue(Model));
                    AfterSend?.Invoke();
                };
                if (activate) Coordinator.Activate();
            }

            internal void Observe(int localPlayer = 0, bool otherPlayerUnresolved = false) => Coordinator.Observe(Id,
                localPlayer == 0 ? new Dictionary<int, ulong>() : new Dictionary<int, ulong> { [localPlayer] = 100 },
                localPlayer == 0 || otherPlayerUnresolved, localPlayer, false);
            public void Dispose() => Coordinator.Deactivate();
        }

        [TestMethod]
        public void HostOnlyParticipantKeepsLocalValuesAndPublishesLatestPropertiesOnce()
        {
            using (var lobby = new Lobby())
            {
                lobby.Observe();
                lobby.Model.SetAmount(10);
                lobby.Model.SetAmount(20);
                lobby.Model.SetEnabled(true);
                Assert.AreEqual(20, lobby.Model.Amount);
                Assert.AreEqual(3, lobby.Notifications);
                Assert.AreEqual(0, lobby.GetterCalls);
                Assert.IsTrue(lobby.Coordinator.IsReady, "Host-only participants must not acquire per-player readiness requirements.");
                lobby.Observe(4, otherPlayerUnresolved: true);
                CollectionAssert.AreEqual(new[] { "Amount=20", "Enabled=True" }, lobby.Sent);
                Assert.IsTrue(lobby.Coordinator.ArePlayersReady(new[] { 4 }, out string error), error);
                Assert.AreEqual(2, lobby.GetterCalls);
                lobby.Observe(4);
                Assert.AreEqual(2, lobby.Sent.Count);
                lobby.Model.SetAmount(30);
                Assert.AreEqual("Amount=30", lobby.Sent[2], "Ready changes retain immediate publication.");
            }
        }

        [TestMethod]
        public void ObserverResolutionStillWaitsForTheActualTransportSlot()
        {
            using (var lobby = new Lobby { TransportReady = false })
            {
                lobby.Observe(4);
                lobby.Model.SetAmount(50);
                lobby.Observe(4);
                Assert.AreEqual(0, lobby.GetterCalls);
                lobby.TransportReady = true;
                lobby.Observe(4);
                CollectionAssert.AreEqual(new[] { "Amount=50" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void LobbyCanAcquireItsSteamIdAfterTheSettingWasQueued()
        {
            using (var lobby = new Lobby { Id = null })
            {
                lobby.Model.SetAmount(60);
                lobby.Observe();
                Assert.AreEqual(0, lobby.GetterCalls);
                lobby.Id = 11;
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=60" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void ReplacementLobbyCannotFlushTheOldLobbysPendingProperties()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(70);
                lobby.Identity = new object(); // Even reusing the same numeric ID must discard the queue.
                lobby.Observe(2);
                Assert.AreEqual(0, lobby.GetterCalls);
                lobby.Model.SetEnabled(true);
                CollectionAssert.AreEqual(new[] { "Enabled=True" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void ReusedLobbyObjectWithAnotherSteamIdAlsoDiscardsPendingProperties()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(71);
                lobby.Id = 12;
                lobby.Observe(2);
                Assert.AreEqual(0, lobby.GetterCalls);
            }
        }

        [TestMethod]
        public void LeavingOrDeactivatingDiscardsPendingNetworkWorkWithoutRevertingValues()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(80);
                lobby.Identity = null;
                lobby.Id = null;
                lobby.Observe();
                lobby.Identity = new object();
                lobby.Id = 20;
                lobby.Observe(3);
                Assert.AreEqual(0, lobby.GetterCalls);
                Assert.AreEqual(80, lobby.Model.Amount);
                lobby.TransportReady = false;
                lobby.Model.SetAmount(81);
                lobby.Coordinator.Deactivate();
                lobby.TransportReady = true;
                lobby.Observe(3);
                Assert.AreEqual(0, lobby.GetterCalls);
            }
        }

        [TestMethod]
        public void LosingHostAuthorityDropsTheQueueAndDoesNotPublishAfterRegainingIt()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(90);
                lobby.Host = false;
                lobby.Observe(2);
                lobby.Model.SetAmount(91);
                lobby.Host = true;
                lobby.Observe(2);
                Assert.AreEqual(0, lobby.GetterCalls);
                lobby.Model.SetAmount(92);
                CollectionAssert.AreEqual(new[] { "Amount=92" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void UnpreparedAndNonLobbyChangesNeverReachTheNetworkGetter()
        {
            using (var lobby = new Lobby(activate: false))
            {
                lobby.Model.SetAmount(100);
                lobby.Observe(2);
                Assert.AreEqual(0, lobby.GetterCalls);
            }
            using (var lobby = new Lobby { Identity = null, Id = null })
            {
                lobby.Model.SetAmount(101);
                lobby.Observe();
                Assert.AreEqual(0, lobby.GetterCalls);
            }
        }

        [TestMethod]
        public void TransportLossDuringFlushKeepsTheUnsentPropertiesForTheSameLobby()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(110);
                lobby.Model.SetEnabled(true);
                lobby.AfterSend = () => lobby.TransportReady = false;
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=110" }, lobby.Sent);
                lobby.AfterSend = null;
                lobby.TransportReady = true;
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=110", "Enabled=True" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void ObservationFailureBlocksHostSendingWithoutCreatingPerPlayerRequirements()
        {
            using (var lobby = new Lobby())
            {
                lobby.Observe(2);
                lobby.Coordinator.ObserveError("Temporary capture failure");
                lobby.Model.SetAmount(120);
                Assert.IsTrue(lobby.Coordinator.IsReady);
                Assert.AreEqual(0, lobby.GetterCalls);
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=120" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void ReplacingTheLobbyDuringFlushCannotSendTheRemainingOldProperties()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(130);
                lobby.Model.SetEnabled(true);
                lobby.AfterSend = () => { lobby.Identity = new object(); lobby.Id = 20; };
                lobby.Observe(2);
                lobby.AfterSend = null;
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=130" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void ReentrantLobbyObservationDoesNotDuplicatePendingProperties()
        {
            using (var lobby = new Lobby())
            {
                lobby.Model.SetAmount(140);
                lobby.Model.SetEnabled(true);
                lobby.AfterSend = () => { lobby.AfterSend = null; lobby.Observe(2); };
                lobby.Observe(2);
                CollectionAssert.AreEqual(new[] { "Amount=140", "Enabled=True" }, lobby.Sent);
            }
        }

        [TestMethod]
        public void MissionStartCancelsQueuedHostPublicationsUntilLobbyObservationResumes()
        {
            object identity = new object();
            var sent = new List<string>();
            var publisher = new HostLobbySettingsPublisher(typeof(HostSettings),
                () => new SettingsBroadcastLobby(identity, 10), () => true, _ => true, sent.Add);
            publisher.Activate();
            Assert.IsTrue(publisher.TryDefer(nameof(HostSettings.Amount), out bool defer) && defer);
            publisher.Suspend();
            Assert.IsTrue(publisher.TryDefer(nameof(HostSettings.Enabled), out defer) && defer);
            publisher.Observe(10, 2, true);
            Assert.AreEqual(0, sent.Count);
        }
    }
}

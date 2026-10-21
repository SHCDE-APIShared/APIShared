using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Network;
using System.Collections.Generic;

namespace LobbyModSettingsPresetTests
{
    [TestClass]
    [DoNotParallelize]
    public class DeferredPerPlayerTests
    {
        private sealed class Model : PresetLobbyModSettingsViewModel
        {
            [SyncPerPlayer] public string Status { get; set; } = "NONE";
            public string[] StatusData { get; } = new string[9];
            [SyncHostOnly] public bool HostChoice { get; set; }
        }

        [TestMethod]
        public void HostPublicationDoesNotWaitForThePersonalRosterConvergence()
        {
            var model = new Model { Status = "READY|local", HostChoice = true };
            object identity = new object();
            var coordinator = new PerPlayerLobbySettingsCoordinator(model, null, "mixed", "test.mixed",
                new PerPlayerLobbySettingsBuilder(model).Build(), false);
            coordinator.CaptureBroadcastLobby = () => new SettingsBroadcastLobby(identity, 10);
            var sent = new List<string>();
            model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName != nameof(Model.Status) && args.PropertyName != nameof(Model.HostChoice)) return;
                if (!coordinator.DeferBroadcast(args.PropertyName)) sent.Add(args.PropertyName);
            };
            coordinator.Activate();
            try
            {
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.HostChoice)));
                coordinator.Observe(10, new Dictionary<int, ulong> { [4] = 100 }, true, 4, false);
                CollectionAssert.AreEqual(new[] { nameof(Model.HostChoice) }, sent);
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.Status)));
                Assert.IsFalse(coordinator.IsReady);
                coordinator.Observe(10, new Dictionary<int, ulong> { [4] = 100 }, false, 4, false);
                CollectionAssert.AreEqual(new[] { nameof(Model.HostChoice), nameof(Model.Status) }, sent);
                Assert.AreEqual("READY|local", model.StatusData[4]);
            }
            finally { coordinator.Deactivate(); }
        }

        [TestMethod]
        public void UnknownSlotRetainsLatestScalarAndPublishesOnceAfterResolution()
        {
            var model = new Model();
            int publications = 0;
            var builder = new PerPlayerLobbySettingsBuilder(model);
            builder.ResetSlotsWith(nameof(Model.Status), () => null).AfterPublish(() => publications++);
            var coordinator = new PerPlayerLobbySettingsCoordinator(model, null, "test", "test.deferred", builder.Build(), false);
            coordinator.Activate();
            try
            {
                model.Status = "READY|first";
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.Status)));
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.HostChoice)));
                coordinator.Observe(10, new Dictionary<int, ulong>(), true, 0, false);
                model.Status = "READY|latest";
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.Status)));
                Assert.AreEqual(0, publications);
                Assert.IsNull(model.StatusData[1]);
                coordinator.TransportReady = id => false;
                coordinator.Observe(10, new Dictionary<int, ulong> { [4] = 100 }, false, 4, false);
                Assert.AreEqual(0, publications, "A resolved observer alone does not establish the transport slot.");
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.Status)));
                coordinator.TransportReady = id => id == 4;
                coordinator.Observe(10, new Dictionary<int, ulong> { [4] = 100 }, false, 4, false);
                Assert.AreEqual(1, publications);
                Assert.AreEqual("READY|latest", model.StatusData[4]);
                Assert.IsNull(model.StatusData[1]);
                Assert.IsFalse(coordinator.DeferBroadcast(nameof(Model.Status)));
                coordinator.Observe(10, new Dictionary<int, ulong> { [4] = 100 }, false, 4, false);
                Assert.AreEqual(1, publications);
                coordinator.Observe(null, null, false, 0, false);
                model.Status = "NONE";
                Assert.IsTrue(coordinator.DeferBroadcast(nameof(Model.Status)));
                coordinator.TransportReady = id => id == 2;
                coordinator.Observe(11, new Dictionary<int, ulong> { [2] = 100 }, false, 2, false);
                Assert.AreEqual(2, publications);
                Assert.AreEqual("NONE", model.StatusData[2]);
                Assert.IsNull(model.StatusData[4]);
            }
            finally { coordinator.Deactivate(); }
        }
    }
}

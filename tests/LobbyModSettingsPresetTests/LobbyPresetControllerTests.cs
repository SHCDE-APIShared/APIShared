using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace LobbyModSettingsPresetTests
{
    [TestClass, DoNotParallelize]
    public class LobbyPresetControllerTests
    {
        private string directory;
        private LobbyModSettingsChangeOrigin previousOrigin;
        private const string Name = "ControllerBoundary";

        [TestInitialize]
        public void Initialize()
        {
            directory = Path.Combine(Path.GetTempPath(), "APIShared-PresetController-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            previousOrigin = GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin;
            SetOrigin(LobbyModSettingsChangeOrigin.Local);
        }
        [TestCleanup]
        public void Cleanup()
        {
            SetOrigin(previousOrigin);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "APIShared-PresetController-");
            Assert.IsTrue(Path.GetFullPath(directory).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
        private static void SetOrigin(LobbyModSettingsChangeOrigin origin) =>
            typeof(GameXAMLManagerAPI).GetProperty(nameof(GameXAMLManagerAPI.CurrentLobbyModSettingsChangeOrigin))
                .GetSetMethod(true).Invoke(GameXAMLManagerAPI.Instance, new object[] { origin });

        private LobbyPresetController Start(Host host)
        {
            var controller = new LobbyPresetController(host, null, Path.Combine(directory, "Example.dll"),
                Name, "Foreign.Author.Example", new Version(1, 0), false);
            host.Target.Changed = controller.AfterPropertyChanged;
            controller.CaptureDefaults();
            controller.Activate();
            return controller;
        }
        private string SettingsPath => Path.Combine(directory, "LobbyModSettings", Name + ".msgpack");
        private Dictionary<string, byte[]> Read() =>
            MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(File.ReadAllBytes(SettingsPath));

        [TestMethod]
        public void ClientHostValuesStayTransientWhilePersonalValuesSurviveRestart()
        {
            var host = new Host();
            Start(host);
            host.IsLocalHost = false;
            SetOrigin(LobbyModSettingsChangeOrigin.IncomingNetwork);
            host.Target.HostAmount = 99;
            SetOrigin(LobbyModSettingsChangeOrigin.Local);
            host.Target.LocalAmount = 42;
            var saved = Read();
            Assert.AreEqual(17, MessagePackSerializer.Deserialize<int>(saved[nameof(Settings.HostAmount)]));
            Assert.AreEqual(42, MessagePackSerializer.Deserialize<int>(saved[nameof(Settings.LocalAmount)]));
            var restarted = new Host();
            Start(restarted);
            Assert.AreEqual(17, restarted.Target.HostAmount);
            Assert.AreEqual(42, restarted.Target.LocalAmount);
        }
        [TestMethod]
        public void EditableMissionSnapshotNeverReplacesPersonalStorage()
        {
            var host = new Host();
            var controller = Start(host);
            byte[] before = File.ReadAllBytes(SettingsPath);
            host.HasMissionContext = true;
            host.MissionPresetEditable = true;
            controller.EnterMissionPreset(new Dictionary<string, byte[]> {
                [nameof(Settings.HostAmount)] = MessagePackSerializer.Serialize(99)
            }, "Community mission", true);
            host.Target.HostAmount = 123;
            host.Target.LocalAmount = 42;
            Assert.AreEqual(123, MessagePackSerializer.Deserialize<int>(controller.CreateCurrentMissionSnapshot()[nameof(Settings.HostAmount)]));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(SettingsPath));
            host.HasMissionContext = false;
            host.MissionPresetEditable = false;
            controller.ExitMissionPreset();
            Assert.AreEqual(17, host.Target.HostAmount);
            Assert.AreEqual(5, host.Target.LocalAmount);
            Assert.AreEqual(17, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.HostAmount)]));
        }
        [TestMethod]
        public void SnapshotApplicationKeepsReentrantSettersFromWritingPartialPayloads()
        {
            var host = new Host();
            var controller = Start(host);
            int writes = controller.TestWriteCount;
            int notifications = host.Notifications;
            controller.ApplyWorkingSnapshot(new Dictionary<string, byte[]> {
                [nameof(Settings.HostAmount)] = MessagePackSerializer.Serialize(31),
                [nameof(Settings.LocalAmount)] = MessagePackSerializer.Serialize(43)
            });
            Assert.AreEqual(31, host.Target.HostAmount);
            Assert.AreEqual(43, host.Target.LocalAmount);
            Assert.AreEqual(notifications + 1, host.Notifications);
            Assert.AreEqual(writes + 1, controller.TestWriteCount);
            Assert.AreEqual(31, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.HostAmount)]));
            Assert.AreEqual(43, MessagePackSerializer.Deserialize<int>(Read()[nameof(Settings.LocalAmount)]));
        }
        [TestMethod]
        public void ReturnedSnapshotsCannotMutateTheStoredWorkingCopy()
        {
            var host = new Host();
            var controller = Start(host);
            var copy = controller.CreateCurrentMissionSnapshot();
            byte[] expected = (byte[])copy[nameof(Settings.HostAmount)].Clone();
            copy[nameof(Settings.HostAmount)][0] ^= 0x7F;
            CollectionAssert.AreEqual(expected, controller.CreateCurrentMissionSnapshot()[nameof(Settings.HostAmount)]);
            Assert.AreEqual(17, host.Target.HostAmount);
        }
        private sealed class Host : ILobbyPresetHost
        {
            internal readonly Settings Target = new Settings();
            internal int Notifications;
            public object SettingsTarget => Target;
            public IDynamicPresetSettingsProvider DynamicSettingsProvider => null;
            public IModSettingsApplicationBackend SettingsApplicationBackend => null;
            public bool IsLocalHost { get; set; } = true;
            public bool HasMissionContext { get; set; }
            public bool MissionPresetEditable { get; set; }
            public bool IsMissionPresetSelected => HasMissionContext && SelectedPreset == 1;
            public int SelectedPreset { get; private set; }
            public bool IsRetiredPresetProperty(string name) => false;
            public void SetSelectedPresetCore(int value) { SelectedPreset = value; }
            public void OnSettingsSnapshotApplied() { Notifications++; }
        }
        public sealed class Settings
        {
            internal Action<string> Changed;
            private int hostAmount = 17, localAmount = 5;
            [SyncHostOnly]
            public int HostAmount { get => hostAmount; set { hostAmount = value; Changed?.Invoke(nameof(HostAmount)); } }
            [PresetLocal]
            public int LocalAmount { get => localAmount; set { localAmount = value; Changed?.Invoke(nameof(LocalAmount)); } }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public class MissionModSettingsPreparationTests
    {
        private sealed class SyncHostOnlyAttribute : Attribute { }
        private sealed class PresetLocalAttribute : Attribute { }
        private sealed class Host : IModSettingsWorkingCopyEndpoint
        {
            internal bool RejectReads;
            private int value = 7;
            [SyncHostOnly] public int Value { get => RejectReads ? throw new Exception("Live getter") : value; set => this.value = value; }
            [SyncHostOnly] public int[] Array { get; set; } = new[] { 1, 2 };
            [SyncHostOnly] public float Fraction { get; set; } = 1.5f;
            [PresetLocal] public int Personal { get => throw new Exception("Personal getter"); set { } }
            internal Dictionary<string, byte[]> Defaults = new Dictionary<string, byte[]> {
                ["Value"] = MessagePackSerializer.Serialize(1), ["Personal"] = MessagePackSerializer.Serialize(99) };
            internal Dictionary<string, byte[]> Player = new Dictionary<string, byte[]> { ["Value"] = MessagePackSerializer.Serialize(2) };
            internal int PlayerCalls;
            public bool IsMissionPresetActive => false;
            public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() => Defaults;
            public Dictionary<string, byte[]> System_CreateModDefaultSnapshot() => Defaults;
            public Dictionary<string, byte[]> System_CreatePlayerMissionPresetSnapshot() { PlayerCalls++; return Player; }
            public Dictionary<string, byte[]> System_CreateCurrentMissionPresetSnapshot() => throw new Exception("Unexpected current");
            public Dictionary<string, byte[]> System_CreateCurrentWorkingSnapshot() => throw new Exception("Unexpected working");
            public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable) => throw new Exception("Must not apply");
            public void System_ExitMissionPreset() => throw new Exception("Must not exit");
            public void System_ApplyMissionPresetSnapshot(Dictionary<string, byte[]> snapshot, string label) => throw new Exception("Must not apply");
            public void System_ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot) => throw new Exception("Must not apply");
            public void System_LoadModDefaults() => throw new Exception("Must not apply");
        }

        [TestMethod]
        public void CaptureKeepsMissionEncodingAndDetachesArrays()
        {
            var host = new Host();
            var entry = MissionModSettingsPreparation.CaptureEntry(host, _ => PublishedPresetValueMode.Fixed);
            Assert.AreEqual(3, entry.Overrides.Count);
            StringAssert.StartsWith((string)entry.Overrides["Fraction"], ModSettingsPresetJson.EncodedMessagePackPrefix);
            Assert.AreEqual(host.Fraction, ModSettingsPresetJson.ConvertValue(entry.Overrides["Fraction"], typeof(float)));
            host.Array[0] = 8;
            Assert.AreEqual(1, ((int[])entry.Overrides["Array"])[0]);
        }

        [TestMethod]
        public void PlayerAndDefaultCaptureDoNotReadLiveValues()
        {
            var host = new Host { RejectReads = true };
            var entry = MissionModSettingsPreparation.CaptureEntry(host, name => name == "Value" ? PublishedPresetValueMode.Player : PublishedPresetValueMode.ModDefault);
            CollectionAssert.AreEqual(new[] { "Value" }, entry.PlayerSettings);
            Assert.AreEqual(0, entry.Overrides.Count);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MissionModSettingsPreparation.CaptureEntry(host, _ => (PublishedPresetValueMode)99));
        }

        [TestMethod]
        public void MaterializationResolvesPrecedenceAndOwnsBuffers()
        {
            var host = new Host();
            var entry = new MissionModSettingsEntry { PlayerSettings = new[] { "Value", "Array", "unknown" } };
            var player = MissionModSettingsPreparation.MaterializeSource(host, entry);
            Assert.AreEqual(2, MessagePackSerializer.Deserialize<int>(player["Value"]));
            player["Value"][0] = 0;
            Assert.AreEqual(2, MessagePackSerializer.Deserialize<int>(host.Player["Value"]));
            entry.Overrides["Value"] = 3L;
            entry.Overrides["unknown"] = null;
            var fixedValues = MissionModSettingsPreparation.MaterializeSource(host, entry);
            Assert.AreEqual(3, MessagePackSerializer.Deserialize<int>(fixedValues["Value"]));
            Assert.AreEqual(1, MessagePackSerializer.Deserialize<int>(host.Defaults["Value"]));
            fixedValues["Personal"][0] = 0;
            Assert.AreEqual(99, MessagePackSerializer.Deserialize<int>(host.Defaults["Personal"]));
        }

        [TestMethod]
        public void NullEntryOnlyReadsDefaultsAndCurrentCapturePreservesPersonalValues()
        {
            var host = new Host { Player = null };
            var defaults = MissionModSettingsPreparation.MaterializeSource(host, null);
            Assert.AreEqual(0, host.PlayerCalls);
            var current = MissionModSettingsPreparation.CaptureCurrentSnapshot(host);
            Assert.AreEqual(7, MessagePackSerializer.Deserialize<int>(current["Value"]));
            Assert.AreEqual(99, MessagePackSerializer.Deserialize<int>(current["Personal"]));
            Assert.AreEqual(1, MessagePackSerializer.Deserialize<int>(host.Defaults["Value"]));
            Assert.AreNotSame(defaults["Personal"], current["Personal"]);
        }

        [TestMethod]
        public void InvalidValuesAndSnapshotsFailBeforeApplication()
        {
            var host = new Host();
            var entry = new MissionModSettingsEntry(); entry.Overrides["Value"] = "invalid";
            Assert.ThrowsExactly<FormatException>(() => MissionModSettingsPreparation.ValidateEntry(host, entry));
            Assert.ThrowsExactly<FormatException>(() => MissionModSettingsPreparation.MaterializeSource(host, entry));
            host.Player["Value"] = null;
            entry.Overrides.Clear(); entry.PlayerSettings = new[] { "Value" };
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsPreparation.MaterializeSource(host, entry));
            host.Defaults = null;
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsPreparation.CaptureCurrentSnapshot(host));
            Assert.ThrowsExactly<InvalidDataException>(() => MissionModSettingsPreparation.MaterializeSource(host, null));
        }
    }
}

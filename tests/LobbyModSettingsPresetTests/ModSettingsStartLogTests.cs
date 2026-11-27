using APIShared;
using APIShared.GameModes;
using APIShared.Internal;
using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using TestAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        private T PrepareLogSettings<T>(T settings) where T : PresetLobbyModSettingsViewModel
        {
            settings.PreparePresets(null, Path.Combine(testRoot, "Log.dll"), "Log", "Tests.Log", new Version(1, 0));
            return settings;
        }

        [TestMethod]
        public void StartLogUsesCodeKeysOwnPlayerValuesAndAllDefaultsWithoutUiOrCompanionProperties()
        {
            var settings = PrepareLogSettings(new LogSettings());
            var row = settings.CaptureRuntimeLogSettings((key, error) => TestAssert.Fail(key + error));
            var host = LogObject(row["host"]);
            var player = LogObject(row["player"]);
            var local = LogObject(row["local"]);
            TestAssert.AreEqual(17L, Convert.ToInt64(host["HostAmount"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(23L, Convert.ToInt64(player["PlayerAmount"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(1.25, local["LocalAmount"]);
            TestAssert.IsTrue((bool)host["EnableMod"]);
            TestAssert.IsTrue((bool)player["EnableClientFeatures"]);
            TestAssert.AreEqual(600, ((List<object>)local["Items"]).Count);
            TestAssert.IsFalse(row.ContainsKey("disabled"));
            string json = DependencyFreeJson.SerializeCompact(row);
            TestAssert.IsFalse(json.Contains("PlayerAmountData"));
            TestAssert.IsFalse(json.Contains("UiLabel"));
            string[] keys = host.Keys.ToArray();
            CollectionAssert.AreEqual(keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(), keys);
        }

        [TestMethod]
        public void DisabledGroupsKeepOnlyTheirOriginalSwitchAndNeverReadHiddenDetails()
        {
            var settings = PrepareLogSettings(new LogSettings());
            settings.EnableMod = false;
            settings.ThrowHost = true;
            var row = settings.CaptureRuntimeLogSettings((key, error) => TestAssert.Fail(key + error));
            var host = LogObject(row["host"]);
            TestAssert.AreEqual(1, host.Count);
            TestAssert.AreEqual(false, host["EnableMod"]);
            TestAssert.AreEqual(23L, Convert.ToInt64(LogObject(row["player"])["PlayerAmount"], CultureInfo.InvariantCulture));
            CollectionAssert.AreEqual(new[] { "host" }, ((List<string>)row["disabled"]).ToArray());
            settings.EnableMod = true;
            settings.ThrowHost = false;
            settings.EnableClientFeatures = false;
            settings.ThrowLocal = true;
            row = settings.CaptureRuntimeLogSettings((key, error) => TestAssert.Fail(key + error));
            TestAssert.AreEqual(17L, Convert.ToInt64(LogObject(row["host"])["HostAmount"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(false, LogObject(row["player"])["EnableClientFeatures"]);
            TestAssert.AreEqual(1, LogObject(row["player"]).Count);
            TestAssert.AreEqual(0, LogObject(row["local"]).Count);
            CollectionAssert.AreEqual(new[] { "player", "local" }, ((List<string>)row["disabled"]).ToArray());
            TestAssert.IsFalse(row.ContainsKey("errors"));
        }

        [TestMethod]
        public void LocalOnlySettingsNeedNeitherHostSettingsNorAnActivationSwitch()
        {
            var settings = PrepareLogSettings(new LocalLogSettings());
            var row = settings.CaptureRuntimeLogSettings(null);
            TestAssert.IsFalse(row.ContainsKey("host"));
            TestAssert.IsFalse(row.ContainsKey("player"));
            TestAssert.IsFalse(row.ContainsKey("disabled"));
            TestAssert.AreEqual(false, LogObject(row["local"])["ShowMarkers"]);
        }

        [TestMethod]
        public void LocalActivationShortensOnlyTheExistingLocalGroup()
        {
            var settings = PrepareLogSettings(new LocalActivatedLogSettings());
            settings.EnableMod = false;
            var row = settings.CaptureRuntimeLogSettings(null);
            var local = LogObject(row["local"]);
            TestAssert.AreEqual(1, local.Count);
            TestAssert.AreEqual(false, local["EnableMod"]);
            CollectionAssert.AreEqual(new[] { "local" }, ((List<string>)row["disabled"]).ToArray());
            TestAssert.IsFalse(row.ContainsKey("host"));
        }

        [TestMethod]
        public void FailedActivationReadDoesNotPretendTheHostGroupIsDisabled()
        {
            var settings = PrepareLogSettings(new LogSettings());
            settings.ThrowActivation = true;
            var row = settings.CaptureRuntimeLogSettings(null);
            TestAssert.IsFalse(row.ContainsKey("disabled"));
            TestAssert.IsFalse(LogObject(row["host"]).ContainsKey("EnableMod"));
            TestAssert.AreEqual(17L, Convert.ToInt64(LogObject(row["host"])["HostAmount"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(1, ((List<object>)row["errors"]).Count);
            TestAssert.AreEqual("EnableMod", LogObject(((List<object>)row["errors"])[0])["key"]);
        }

        [TestMethod]
        public void StartLogDetachesArraysAndIsReadOnlyForPersonalAndMissionSettings()
        {
            var settings = StartDocuments();
            byte[] before = File.ReadAllBytes(NewFile);
            settings.System_EnterMissionPreset(settings.System_CreateDisabledMissionPresetSnapshot(), "Mission", false);
            var row = settings.CaptureRuntimeLogSettings(null);
            TestAssert.AreEqual(false, LogObject(row["host"])["EnableMod"]);
            TestAssert.IsTrue(settings.IsMissionPresetActive);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(NewFile));
            var arrays = PrepareLogSettings(new LogSettings());
            row = arrays.CaptureRuntimeLogSettings(null);
            arrays.Items[0] = 999;
            TestAssert.AreEqual(0L, Convert.ToInt64(((List<object>)LogObject(row["local"])["Items"])[0], CultureInfo.InvariantCulture));
        }

        [TestMethod]
        public void BackendLoggingUsesAppliedValuesAndAppliedActivationWithoutReadingTheWorkingCopy()
        {
            var settings = PrepareLogSettings(new LogBackendSettings());
            settings.Backend.Active["EnableMod"] = false;
            settings.Backend.Desired["EnableMod"] = true;
            settings.Backend.Desired["Value"] = 99;
            settings.Backend.FailWorkingReads = true;
            var row = settings.CaptureRuntimeLogSettings((key, error) => TestAssert.Fail(key + error));
            TestAssert.AreEqual(false, LogObject(row["host"])["EnableMod"]);
            TestAssert.IsFalse(LogObject(row["host"]).ContainsKey("Value"));
            settings.Backend.Active["EnableMod"] = true;
            row = settings.CaptureRuntimeLogSettings(null);
            TestAssert.AreEqual(7L, Convert.ToInt64(LogObject(row["host"])["Value"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(2, settings.Backend.ActiveReads);
            TestAssert.AreEqual(99, settings.Backend.Desired["Value"]);
        }

        [TestMethod]
        public void ReadAndEncodingFailuresAreStructuredAndCannotLoseOtherSettingsOrMods()
        {
            var settings = PrepareLogSettings(new LogSettings());
            settings.ThrowHost = true;
            settings.LocalAmount = double.NaN;
            var warnings = new List<string>();
            var lines = new List<string>();
            var participants = new Dictionary<string, PresetLobbyModSettingsViewModel>
            {
                ["Z.Local"] = PrepareLogSettings(new LocalLogSettings()),
                ["A.Broken"] = settings,
                ["M.Unprepared"] = new LocalLogSettings()
            };
            var writer = new ModSettingsStartLog(() => participants, lines.Add, (key, error) => warnings.Add(key));
            writer.Handle(StartLogNotification(1));
            TestAssert.AreEqual(4, lines.Count);
            TestAssert.AreEqual(3, warnings.Count);
            var broken = ParseLogLine(lines[1]);
            TestAssert.AreEqual("A.Broken", broken["mod"]);
            TestAssert.IsFalse(LogObject(broken["host"]).ContainsKey("HostAmount"));
            TestAssert.AreEqual(true, LogObject(broken["host"])["EnableMod"]);
            TestAssert.AreEqual(2, ((List<object>)broken["errors"]).Count);
            TestAssert.IsFalse(LogObject(broken["local"]).ContainsKey("LocalAmount"));
            TestAssert.AreEqual("M.Unprepared", ParseLogLine(lines[2])["mod"]);
            TestAssert.AreEqual("Z.Local", ParseLogLine(lines[3])["mod"]);
        }

        [TestMethod]
        public void BackendFailureNeverFallsBackToDesiredValues()
        {
            var settings = PrepareLogSettings(new LogBackendSettings());
            settings.Backend.FailActiveRead = true;
            var row = settings.CaptureRuntimeLogSettings(null);
            TestAssert.IsFalse(row.ContainsKey("host"));
            TestAssert.AreEqual("mod", LogObject(((List<object>)row["errors"])[0])["scope"]);
        }

        [TestMethod]
        public void DeepSettingValuesCannotMakeTheEntireLogRowExceedTheJsonDepthLimit()
        {
            var settings = PrepareLogSettings(new LogSettings());
            object nested = 1;
            for (int i = 0; i < DependencyFreeJson.MaximumDepth; i++) nested = new[] { nested };
            settings.Nested = nested;
            var lines = new List<string>();
            var writer = new ModSettingsStartLog(() => new Dictionary<string, PresetLobbyModSettingsViewModel>
                { ["Foreign.Deep"] = settings }, lines.Add, null);
            writer.Handle(StartLogNotification(1));
            var row = ParseLogLine(lines[1]);
            TestAssert.IsFalse(LogObject(row["local"]).ContainsKey("Nested"));
            TestAssert.AreEqual("Nested", LogObject(((List<object>)row["errors"])[0])["key"]);
            TestAssert.AreEqual(1.25, LogObject(row["local"])["LocalAmount"]);
        }

        [TestMethod]
        public void LogLinesAreSingleLineJsonWithInvariantNumbersAndUnchangedEscapedStrings()
        {
            var settings = PrepareLogSettings(new LogSettings());
            settings.Text = "  spaced \"quote\\\" \\ \\\\ \r\n\tä Ω  ";
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var lines = new List<string>();
                var writer = new ModSettingsStartLog(() => new Dictionary<string, PresetLobbyModSettingsViewModel>
                    { ["Foreign.Mod"] = settings }, lines.Add, null);
                writer.Handle(StartLogNotification(2));
                foreach (string line in lines)
                {
                    TestAssert.IsFalse(line.Contains("\r") || line.Contains("\n") || line.Contains("\t"));
                    TestAssert.AreEqual(1L, Convert.ToInt64(ParseLogLine(line)["schema"], CultureInfo.InvariantCulture));
                }
                var local = LogObject(ParseLogLine(lines[1])["local"]);
                TestAssert.AreEqual(settings.Text, local["Text"]);
                TestAssert.AreEqual(1.25, local["LocalAmount"]);
                TestAssert.AreEqual(600, ((List<object>)local["Items"]).Count);
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }

        [TestMethod]
        public void SummaryReachesTheRetainedInfoLoggerWithOneMillisecondTimestamp()
        {
            var settings = PrepareLogSettings(new LocalLogSettings());
            var lines = new List<string>();
            using (var source = new BepInEx.Logging.ManualLogSource("SettingsLogTest"))
            {
                source.LogEvent += (_, args) =>
                {
                    TestAssert.AreEqual(BepInEx.Logging.LogLevel.Info, args.Level);
                    lines.Add(Convert.ToString(args.Data, CultureInfo.InvariantCulture));
                };
                var retained = DebugLogHelper.ForExternalCode(source);
                var writer = new ModSettingsStartLog(() => new Dictionary<string, PresetLobbyModSettingsViewModel>
                    { ["Foreign.Local"] = settings }, message => DebugLogHelper.LogInfo(retained, message), null);
                writer.Handle(StartLogNotification(1));
            }
            TestAssert.AreEqual(2, lines.Count);
            foreach (string line in lines)
            {
                TestAssert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(line,
                    @"^\[\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}\] MODSETTINGS(?:_BEGIN)? \{"));
                ParseLogLine(line.Substring(line.IndexOf("MODSETTINGS", StringComparison.Ordinal)));
            }
        }

        [TestMethod]
        public void LifecycleLogsEachSuccessfulMapSaveRestartAndEditorExactlyOnceAndSkipsFailures()
        {
            var settings = PrepareLogSettings(new LocalLogSettings());
            var lines = new List<string>();
            var writer = new ModSettingsStartLog(() => new Dictionary<string, PresetLobbyModSettingsViewModel>
                { ["Foreign.Client"] = settings }, lines.Add, null);
            var state = new MissionLifecycleState(error => TestAssert.Fail(error.ToString()));
            state.Register("Tests", "SettingsLog", writer.Handle);
            foreach (var kind in new[] { MissionStartKind.NewGame, MissionStartKind.LoadedSave,
                MissionStartKind.EditorCreated, MissionStartKind.EditorLoaded, MissionStartKind.NewGame })
            {
                bool restart = lines.Count != 0 && kind == MissionStartKind.NewGame;
                var seed = new MissionContext(0, kind, default, null, null, restart);
                long id = state.Begin(seed);
                state.Checkpoint(id, seed, MissionInitializationPhase.BeforeLoad);
                int before = lines.Count;
                state.Ready(id, seed);
                TestAssert.AreEqual(before + 2, lines.Count);
                TestAssert.AreEqual(kind.ToString(), ParseLogLine(lines[before])["start"]);
                TestAssert.AreEqual(restart, ParseLogLine(lines[before])["restart"]);
                TestAssert.IsNull(ParseLogLine(lines[before])["localPlayer"]);
                writer.Handle(new MissionLifecycleNotification(state.Current, MissionLifecycleKind.Start,
                    MissionInitializationPhase.ManagedReady, MissionEndReason.None, true).AsReplay());
                state.Ready(id, seed);
                TestAssert.AreEqual(before + 2, lines.Count);
            }
            var failed = new MissionContext(0, MissionStartKind.NewGame, default, null, null, false);
            long attempt = state.Begin(failed);
            state.Finish(attempt, failed, true, false, false, false);
            TestAssert.AreEqual(10, lines.Count);
        }

        [TestMethod]
        [DataRow(false, null, "local")]
        [DataRow(true, true, "host")]
        [DataRow(true, false, "client")]
        [DataRow(true, null, "unknown")]
        public void HeaderUsesMissionNetworkAuthorityAndKeepsTheLocalPlayerId(bool multiplayer, bool? isHost, string role)
        {
            var lines = new List<string>();
            var mode = new GameModeSnapshot(multiplayer, false, false, false, false, false, false,
                false, false, false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, GameModeKind.CustomGame,
                GameModeLaunchVariant.Standard, 0, 0, 0, 0, 0, false);
            var context = new MissionContext(1, MissionStartKind.LoadedSave, mode, null, null, false, player: 3, host: isHost);
            var writer = new ModSettingsStartLog(() => new Dictionary<string, PresetLobbyModSettingsViewModel>(), lines.Add, null);
            writer.Handle(new MissionLifecycleNotification(context, MissionLifecycleKind.Start,
                MissionInitializationPhase.ManagedReady, MissionEndReason.None, true));
            TestAssert.AreEqual(1, lines.Count);
            var header = ParseLogLine(lines[0]);
            TestAssert.AreEqual(role, header["role"]);
            TestAssert.AreEqual("CustomGame", header["mode"]);
            TestAssert.AreEqual(3L, Convert.ToInt64(header["localPlayer"], CultureInfo.InvariantCulture));
            TestAssert.AreEqual(0L, Convert.ToInt64(header["mods"], CultureInfo.InvariantCulture));
        }

        private static MissionLifecycleNotification StartLogNotification(long session) =>
            new MissionLifecycleNotification(new MissionContext(session, MissionStartKind.NewGame, default,
                null, null, false), MissionLifecycleKind.Start, MissionInitializationPhase.ManagedReady, MissionEndReason.None, true);
        private static IDictionary<string, object> LogObject(object value) =>
            (IDictionary<string, object>)DependencyFreeJson.Parse(DependencyFreeJson.Serialize(value));
        private static IDictionary<string, object> ParseLogLine(string line) =>
            LogObject(DependencyFreeJson.Parse(line.Substring(line.IndexOf(' ') + 1)));

        private sealed class LogSettings : PresetLobbyModSettingsViewModel
        {
            internal bool ThrowHost, ThrowLocal, ThrowActivation;
            private bool enable = true;
            [SyncHostOnly] public bool EnableMod { get => ThrowActivation ? throw new InvalidOperationException("Activation read failed") : enable; set => enable = value; }
            [SyncHostOnly] public int HostAmount { get => ThrowHost ? throw new InvalidOperationException("Host read failed") : 17; set { } }
            [SyncPerPlayer] public bool EnableClientFeatures { get; set; } = true;
            public bool[] EnableClientFeaturesData => new[] { false, true, false, false, false, false, false, false, false };
            [SyncPerPlayer] public int PlayerAmount { get; set; } = 23;
            public int[] PlayerAmountData => new[] { 0, 99, 55, 0, 0, 0, 0, 0, 0 };
            [PresetLocal] public double LocalAmount { get; set; } = 1.25;
            [PresetLocal] public string Text { get => ThrowLocal ? throw new InvalidOperationException("Local read failed") : text; set => text = value; }
            private string text = "default";
            [PresetLocal] public int[] Items { get; set; } = Enumerable.Range(0, 600).ToArray();
            [PresetLocal] public object Nested { get; set; }
            public string UiLabel => "Not a stored setting";
        }
        private sealed class LocalLogSettings : PresetLobbyModSettingsViewModel
        {
            [PresetLocal] public bool ShowMarkers { get; set; }
        }
        private sealed class LocalActivatedLogSettings : PresetLobbyModSettingsViewModel
        {
            [PresetLocal] public bool EnableMod { get; set; } = true;
            [PresetLocal] public int Amount { get => EnableMod ? 1 : throw new InvalidOperationException("Inactive detail must not be read"); set { } }
        }
        private sealed class LogBackendSettings : PresetLobbyModSettingsViewModel
        {
            internal readonly LogBackend Backend = new LogBackend();
            protected override IDynamicPresetSettingsProvider DynamicSettingsProvider => Backend;
        }
        private sealed class LogBackend : IDynamicPresetSettingsProvider, IModSettingsApplicationBackend
        {
            internal bool FailWorkingReads, FailActiveRead;
            internal int ActiveReads;
            internal readonly Dictionary<string, object> Active = new Dictionary<string, object> { ["EnableMod"] = true, ["Value"] = 7 };
            internal Dictionary<string, object> Desired = new Dictionary<string, object> { ["EnableMod"] = true, ["Value"] = 7 };
            public IReadOnlyList<DynamicPresetSetting> GetSettings() => Active.Select(item => new DynamicPresetSetting
            { Key = item.Key, ValueType = item.Value.GetType(), DefaultValue = item.Value, Scope = PresetSettingScope.Host }).ToArray();
            public object ReadValue(string key) => FailWorkingReads ? throw new InvalidOperationException("Must not read desired") : Desired[key];
            public Dictionary<string, object> ReadValues() => new Dictionary<string, object>(Desired);
            public void ReplaceValues(Dictionary<string, object> values) => Desired = values;
            public void ValidateValues(Dictionary<string, object> values) { }
            public Dictionary<string, object> ReadActiveValues()
            {
                ActiveReads++;
                if (FailActiveRead) throw new InvalidOperationException("Active read failed");
                return new Dictionary<string, object>(Active);
            }
            public Dictionary<string, object> ReadDesiredValues() => ReadValues();
            public Dictionary<string, object> ReadOwnValues() => ReadValues();
            public Dictionary<string, object> ReadPendingValues() => new Dictionary<string, object> { ["EnableMod"] = true, ["Value"] = 1000 };
            public string ActiveContextId => "test";
            public void ReplaceDesiredValues(Dictionary<string, object> values) => ReplaceValues(values);
            public void StageValues(Dictionary<string, object> values, string contextId) => TestAssert.Fail("Logging must not stage settings.");
            public void ApplyValues(Dictionary<string, object> values, string contextId) => TestAssert.Fail("Logging must not apply settings.");
            public void ReturnToOwnConfiguration() => TestAssert.Fail("Logging must not restore settings.");
            public void DiscardPendingConfiguration() => TestAssert.Fail("Logging must not discard settings.");
        }
    }
}

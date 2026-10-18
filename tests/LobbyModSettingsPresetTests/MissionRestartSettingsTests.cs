using APIShared;
using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Network;
using System;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        [TestMethod]
        [DataRow("Map")]
        [DataRow("Trail")]
        [DataRow("Savegame")]
        public void RepeatedRestartRetainsMissionValuesAndPersonalStorageUntilActualExit(string label)
        {
            string assembly = Path.Combine(testRoot, "Restart", "PresetTest.dll");
            var settings = new RestartSettings();
            settings.PreparePresets(null, assembly, "Restart", "Tests.Restart", new Version(1, 0));
            settings.ActivatePresets();
            settings.EnableMoveFormationEnhancements = true;
            settings.PersonalChoice = 17;
            var personal = settings.System_CreateCurrentWorkingSnapshot();
            var mission = settings.System_CreateCurrentWorkingSnapshot();
            mission[nameof(settings.EnableMoveFormationEnhancements)] = MessagePackSerializer.Serialize(false);
            settings.System_EnterMissionPreset(mission, label, false);
            int writes = settings.System_WorkingStateWriteCount;
            var lease = new SavegameMissionPresetLease();
            var state = new MissionLifecycleState(error => throw error);
            var seed = new MissionContext(0, MissionStartKind.LoadedSave, default, "castle.map", "castle", false);
            long current = state.Begin(seed);
            state.Ready(current, seed);
            lease.Track(current, "settings", settings);
            state.Register("owner", "settings", e =>
            {
                if (e.Kind != MissionLifecycleKind.End) return;
                if (e.ReplacementContext?.RestartedFromSessionId == e.Context.SessionId)
                    lease.Transfer(e.Context.SessionId, e.ReplacementContext.SessionId);
                else lease.ReleaseOnEnd(e.Context.SessionId, (_, error) => throw error);
            });
            for (int i = 0; i < 3; i++)
            {
                var restart = new MissionContext(0, MissionStartKind.NewGame, default, "castle.map", "castle", true).AsRestartOf(current);
                current = state.Begin(restart);
                state.Ready(current, restart);
                Assert(!settings.EnableMoveFormationEnhancements && settings.PersonalChoice == 17 && settings.IsMissionPresetActive,
                    "A same-mission restart replaced the active mission values.");
                Assert(!settings.CanEditHostSettings, "Restart removed the mission's editing lock.");
                Assert(settings.System_TestPresetStatusText == label, "Restart lost mission preset attribution.");
                Assert(settings.System_WorkingStateWriteCount == writes, "Restart persisted temporary mission settings.");
            }
            state.End(MissionEndReason.SceneChanged);
            Assert(settings.EnableMoveFormationEnhancements && settings.PersonalChoice == 17 && !settings.IsMissionPresetActive,
                "Actual departure did not restore the personal working state.");
            var restored = settings.System_CreateCurrentWorkingSnapshot();
            Assert(personal.All(p => restored.TryGetValue(p.Key, out var bytes) && p.Value.SequenceEqual(bytes)),
                "Mission values leaked into the personal snapshot.");
        }

        // Same host-owned formation flag as the reported consumer; the preset controller,
        // lifecycle and lease are production code, only the settings schema is a fixture.
        private sealed class RestartSettings : PresetLobbyModSettingsViewModel
        {
            private bool formation = true;
            private int personal;
            [SyncHostOnly]
            public bool EnableMoveFormationEnhancements
            {
                get => formation;
                set { if (formation == value) return; formation = value; OnPropertyChanged(nameof(EnableMoveFormationEnhancements)); }
            }
            [PresetLocal]
            public int PersonalChoice
            {
                get => personal;
                set { if (personal == value) return; personal = value; OnPropertyChanged(nameof(PersonalChoice)); }
            }
        }
    }
}

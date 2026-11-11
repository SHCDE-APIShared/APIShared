using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using APIShared.GameModes;
using APIShared.Internal;
using APIShared.Simulation;
using CrusaderDE;
using SHCDESE.API;

namespace APIShared.Automation
{
    public sealed partial class AutomationRun
    {
        /// <summary>Loads a local .sav from the ready main menu. Relative paths use the game's Saves directory.</summary>
        /// <remarks>Calls the normal load path once and waits for a new managed start matching that path.
        /// The existing save-settings policy still applies. Use an unbound step for this transition.</remarks>
        public IEnumerable<AutomationWait> LoadSave(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A save path is required.", nameof(path));
            yield return AutomationWait.Until("ready main menu", MainMenuReady);
            path = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ConfigSettings.GetSavesPath(), path));
            if (!string.Equals(Path.GetExtension(path), ".sav", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
                throw new FileNotFoundException("Expected an existing single-player .sav.", path);
            var header = MapFileManager.Instance.GetFileInfoFromFileName(path, path, 0, true);
            if (header == null) throw new InvalidDataException("Vanilla rejected the save header: " + path);
            long before = CurrentMission?.SessionId ?? 0;
            var view = MainViewModel.Instance;
            Platform_Multiplayer.Instance.gameMembers = null;
            view.InitNewScene(Enums.SceneIDS.MainGame);
            Director.instance.SetPausedState(false);
            EditorDirector.instance.stopGameSim();
            EditorDirector.instance.loadSaveGame(path, header.standAlone_filename, header);
            view.InitObjectiveGoodsPanelDelayed();
            yield return AutomationWait.Until("managed save start: " + path, () => MatchStart(before, path, true));
        }

        /// <summary>Waits for the first fresh PlayState of the specified current mission.</summary>
        public AutomationWait WaitForPlayState(MissionContext mission) =>
            AutomationWait.Until("fresh PlayState", () => {
                RequireMission(mission);
                return readiness.Current?.SessionId == mission.SessionId;
            });

        /// <summary>Waits for native map time to advance by at least the requested ticks after the first sample.</summary>
        /// <remarks>Samples only while this wait is active under the engine monitor. The test deadline still
        /// applies while paused, when the source is unavailable or when the engine monitor is busy.</remarks>
        public AutomationWait WaitForTicks(MissionContext mission, int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            RequireMission(mission);
            var engine = EngineMonitor.Current ?? throw new InvalidOperationException("Engine monitor unavailable.");
            int? first = null;
            return AutomationWait.Until(count + " native map ticks", () => {
                RequireMission(mission);
                if (!Monitor.TryEnter(engine)) return false;
                try
                {
                    if (!SimulationClock.TryGetCurrentTick(mission, out var now)) return false;
                    if (!first.HasValue) first = now;
                    return SimulationClock.TryGetElapsedTicks(first.Value, now, out var elapsed) && elapsed >= count;
                }
                finally { Monitor.Exit(engine); }
            });
        }

        /// <summary>Requests an explicit local gameplay pause state and waits for the actual native state.</summary>
        /// <remarks>Use after fresh PlayState and outside managed menus or briefings. A mod veto remains effective.</remarks>
        public IEnumerable<AutomationWait> SetPause(MissionContext mission, bool paused)
        {
            RequireInteractiveMission(mission);
            if (Director.instance.Paused)
                throw new InvalidOperationException("Close the managed pause/menu before changing the simulation pause.");
            var players = GamePlayerManagerAPI.Instance;
            if (players.IsLocalPaused() != paused)
            {
                int state = paused ? 1 : 0;
                EngineInterface.GameAction(Enums.GameActionCommand.Game_Paused, state, state);
            }
            yield return AutomationWait.Until("native pause=" + paused, () => {
                RequireMission(mission);
                return players.IsLocalPaused() == paused;
            });
        }

        /// <summary>Requests one zoom step and reports the actual scale after interpolation, including at a zoom limit.</summary>
        public IEnumerable<AutomationWait> ZoomStep(MissionContext mission, bool zoomIn, Action<float> actualScale)
        {
            if (actualScale == null) throw new ArgumentNullException(nameof(actualScale));
            RequireInteractiveMission(mission);
            yield return AutomationWait.Until("idle zoom camera", () => {
                RequireMission(mission);
                return CameraControls2D.instance != null && PerfectPixelWithZoom.instance != null &&
                    !PerfectPixelWithZoom.instance.midZoom;
            });
            var zoom = PerfectPixelWithZoom.instance;
            var step = ConfigSettings.Settings_ExtraZoom ? 0.5f : 1f;
            zoom.adjustZoom(zoomIn ? step : -step, loop: false);
            yield return AutomationWait.Until("zoom interpolation", () => {
                RequireMission(mission);
                if (zoom == null || !ReferenceEquals(zoom, PerfectPixelWithZoom.instance))
                    throw new InvalidOperationException("The test camera changed.");
                return !zoom.midZoom;
            });
            actualScale(zoom.getZoom());
        }

        /// <summary>Runs short synchronous Extender work under the existing engine monitor in a ready local mission.</summary>
        /// <remarks>Call on Unity from a test step. The callback must not block, yield, operate on UI, or retain
        /// native pointers. Return managed data only. The mission is revalidated inside the monitor.</remarks>
        public T InSimulation<T>(MissionContext mission, Func<T> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            RequireInteractiveMission(mission);
            var engine = EngineMonitor.Current ?? throw new InvalidOperationException("Engine monitor unavailable.");
            if (!Monitor.TryEnter(engine, 100))
                throw new InvalidOperationException("Simulation is busy. Pause it before preparing the test world.");
            try
            {
                RequireMission(mission);
                return action();
            }
            finally { Monitor.Exit(engine); }
        }

        private bool MainMenuReady()
        {
            if (CurrentMission != null || !MainViewModel.viewModelLoaded ||
                FatControler.currentScene != Enums.SceneIDS.FrontEnd) return false;
            var view = MainViewModel.Instance;
            return view.Show_Frontend_MainMenu && !view.Show_IntroSequence && view.HUDmain != null &&
                view.HUDIngameMenu != null && Director.instance != null && EditorDirector.instance != null;
        }

        private bool MatchStart(long previous, string path, bool save)
        {
            var current = CurrentMission;
            if (current == null || current.SessionId == previous) return false;
            if (current.IsSave != save || current.Mode.IsRealMultiplayer ||
                (!save && !current.Mode.IsSingleplayerSkirmish) || string.IsNullOrEmpty(current.FilePath) ||
                !string.Equals(Path.GetFullPath(current.FilePath), path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("An unexpected mission started: " + current.FilePath);
            return true;
        }

        private void RequireMission(MissionContext mission)
        {
            AutomationClient.RequireUnity();
            if (finished || iterator == null || mission == null || CurrentMission?.SessionId != mission.SessionId)
                throw new InvalidOperationException("The expected test mission is no longer current.");
        }

        private void RequireInteractiveMission(MissionContext mission)
        {
            RequireMission(mission);
            var mode = mission.Mode;
            if (mode.IsRealMultiplayer || mode.IsMapEditor || mode.Kind == GameModeKind.Unknown ||
                mode.Kind == GameModeKind.Tutorial || mode.Kind == GameModeKind.CoopTrail ||
                readiness.Current?.SessionId != mission.SessionId || Director.instance == null ||
                !Director.instance.SimRunning || !MainViewModel.viewModelLoaded || MainViewModel.Instance.Show_HUD_Briefing)
                throw new InvalidOperationException("Expected a ready local gameplay mission without briefing.");
        }
    }
}

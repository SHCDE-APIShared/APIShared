using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using CrusaderDE;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Extensions;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using Path = System.IO.Path;

namespace APIShared.Automation
{
    public sealed partial class AutomationRun
    {
        /// <summary>Starts a local Skirmish with the selected map and additional built-in opponents.</summary>
        /// <remarks>Use an unbound step from the main menu. A basename must match exactly one lobby row.
        /// Optional peace time requires the game to offer and retain it. Briefing and ticks are separate.</remarks>
        public IEnumerable<AutomationWait> StartSkirmish(string map, IEnumerable<AILords> opponents,
            int? peaceTimeMinutes = null)
        {
            if (string.IsNullOrWhiteSpace(map)) throw new ArgumentException("A map is required.", nameof(map));
            if (peaceTimeMinutes < 0) throw new ArgumentOutOfRangeException(nameof(peaceTimeMinutes));
            var lords = (opponents ?? throw new ArgumentNullException(nameof(opponents))).ToArray();
            if (lords.Length == 0 || lords.Any(lord => !Enum.IsDefined(typeof(AILords), lord) ||
                lord <= AILords.SK_NULL || lord >= AILords.SK_X1))
                throw new ArgumentException("Provide one or more built-in AI lords.", nameof(opponents));

            yield return AutomationWait.Until("ready main menu", MainMenuReady);
            long before = CurrentMission?.SessionId ?? 0;
            MainViewModel.Instance.ButtonMainMenuFunction("Custom");
            yield return AutomationWait.Until("local Skirmish lobby and map list", () =>
                LocalLobby()?.FindName("MapList") is ListView list && list.ItemsSource != null);
            var lobby = LocalLobby();
            var room = lobby.currentLobby;
            var maps = (ListView)lobby.FindName("MapList");
            bool byName = string.Equals(Path.GetFileName(map), map, StringComparison.Ordinal);
            string match = byName ? map : Path.GetFullPath(map);
            var matches = ((IEnumerable)maps.ItemsSource).OfType<FileRow>().Where(row =>
                !string.IsNullOrEmpty(row.fileHeader?.filePath) && string.Equals(byName
                    ? Path.GetFileName(row.fileHeader.filePath) : Path.GetFullPath(row.fileHeader.filePath),
                    match, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1)
                throw new InvalidOperationException("Expected one lobby map for " + map + ", found " + matches.Length + ".");
            var selected = matches[0];
            string path = Path.GetFullPath(selected.fileHeader.filePath);
            maps.SelectedItem = selected;
            yield return AutomationWait.Until("Vanilla map selection", () =>
                string.Equals(LobbySelectedMapHeader.Read(RequireLobby(room))?.filePath,
                    selected.fileHeader.filePath, StringComparison.OrdinalIgnoreCase));

            foreach (var lord in lords)
            {
                int type = (int)lord;
                int count = CountLord(RequireLobby(room), type);
                lobby.SkirmishAIAddClick(type.ToString(CultureInfo.InvariantCulture));
                yield return AutomationWait.Until("opponent added: " + lord, () =>
                    CountLord(RequireLobby(room), type) == count + 1);
            }

            if (peaceTimeMinutes.HasValue)
            {
                RequireLobby(room).ButtonClicked("Setup");
                yield return AutomationWait.Until("Skirmish setup", () => {
                    RequireLobby(room);
                    return MainViewModel.Instance.Show_MPSettings && FRONT_Multiplayer_Setup.Instance != null &&
                        FRONT_Multiplayer_Setup.Instance.RefMP_Settings_Peacetime_Slider != null;
                });
                if (!MainViewModel.Instance.Show_MPPeacetime)
                    throw new InvalidOperationException("Peace time is not offered by this lobby.");
                var slider = FRONT_Multiplayer_Setup.Instance.RefMP_Settings_Peacetime_Slider;
                slider.Value = peaceTimeMinutes.Value;
                if (slider.Value != peaceTimeMinutes.Value)
                    throw new InvalidOperationException("The requested peace time is outside the lobby range.");
                RequireLobby(room).ButtonClicked("ApplySettings");
                yield return AutomationWait.Until("Skirmish settings applied", () => {
                    RequireLobby(room);
                    return !MainViewModel.Instance.Show_MPSettings;
                });
            }

            var ready = RequireLobby(room);
            if (!string.Equals(LobbySelectedMapHeader.Read(ready)?.filePath,
                selected.fileHeader.filePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected Skirmish map changed before launch.");
            ready.ButtonClicked("Play");
            yield return AutomationWait.Until("managed local Skirmish start: " + path, () => MatchStart(before, path, false));
            if (peaceTimeMinutes.HasValue && peaceTimeMinutes.Value > 0)
            {
                var mission = CurrentMission;
                yield return WaitForPlayState(mission);
                yield return AutomationWait.Until("active peace time", () => {
                    RequireMission(mission);
                    return Simulation.PeaceTimeState.TryGetActive(out bool active) && active;
                });
            }
        }

        /// <summary>Dispatches a left click at the center of a currently interactive Noesis element.</summary>
        /// <remarks>Uses the Extender's global lookup on Unity. Callers assert the observable UI effect separately.</remarks>
        public IEnumerable<AutomationWait> ClickNoesis(string elementName)
        {
            if (string.IsNullOrWhiteSpace(elementName)) throw new ArgumentException("An element name is required.", nameof(elementName));
            long? session = CurrentMission?.SessionId;
            if (CurrentMission?.Mode.IsRealMultiplayer == true)
                throw new InvalidOperationException("Noesis automation is limited to local tests.");

            FrameworkElement element = null;
            yield return AutomationWait.Until("interactive Noesis element " + elementName, () => {
                if (session != CurrentMission?.SessionId)
                    throw new InvalidOperationException("The UI test mission changed.");
                if (FatControler.instance?.NGview == null) return false;
                element = GameXAMLManagerAPI.Instance.FindGlobalElement(elementName);
                return element != null && element.IsVisible && element.IsEnabled && element.IsHitTestVisible &&
                    element.ActualWidth > 0 && element.ActualHeight > 0;
            });

            var view = FatControler.instance.NGview;
            var point = element.PointToScreen(new Point((float)element.ActualWidth / 2f, (float)element.ActualHeight / 2f));
            int x = checked((int)point.X), y = checked((int)point.Y);
            view.MouseMove(x, y);
            try { view.MouseButtonDown(x, y, MouseButton.Left); }
            finally { view.MouseButtonUp(x, y, MouseButton.Left); }
        }

        /// <summary>Attempts one ordinary building placement and returns its confirmed game and global IDs.</summary>
        /// <remarks>Runs under the engine monitor and uses the existing synchronous Extender spawn event.
        /// The caller supplies the mapper-specific sprite variant. Placement rules remain in effect.
        /// A false result means an unconfirmed placement. Compound or unusual spawns require scenario-specific checks.</remarks>
        public unsafe bool TrySpawnBuilding(MissionContext mission, int playerId, int tileX, int tileY,
            eMappers mapper, int spriteVariation, out int buildingId, out uint globalId)
        {
            int capturedId = 0, observations = 0;
            uint capturedGlobal = 0;
            bool confirmed = InSimulation(mission, () => {
                var tiles = GameTileManagerAPI.Instance;
                int scale = BuildingScales.GetScale(mapper);
                if (playerId < 1 || playerId > 8 || !tiles.IsTileInsideMapBounds(tileX, tileY) || scale <= 0)
                    throw new ArgumentException("Expected a valid player, map tile and supported building scale.");
                eStructs type = mapper.ConvertToEStructs();
                int thread = Thread.CurrentThread.ManagedThreadId;
                var buildings = GameBuildingManagerAPI.Instance;
                using (BuildingR3EventHooks.OnBuildingSpawn.Observable.Subscribe(args => {
                    if (Thread.CurrentThread.ManagedThreadId != thread || args.Phase != EventHookPhase.Post ||
                        args.PlayerId != playerId || args.Building != type || args.TileX != tileX || args.TileY != tileY) return;
                    observations++;
                    if (args.ReturnValue <= 0 || args.ReturnValue > int.MaxValue) return;
                    int id = (int)args.ReturnValue;
                    if (MatchesBuilding(buildings, id, playerId, type, tileX, tileY, out uint global))
                    {
                        capturedId = id;
                        capturedGlobal = global;
                    }
                }))
                    buildings.CreatePrefab(playerId, tileX, tileY, mapper, scale, spriteVariation, bIsFree: true);

                return observations == 1 && capturedId != 0 &&
                    MatchesBuilding(buildings, capturedId, playerId, type, tileX, tileY, out uint actual) &&
                    actual == capturedGlobal;
            });
            buildingId = confirmed ? capturedId : 0;
            globalId = confirmed ? capturedGlobal : 0;
            return confirmed;
        }

        private static unsafe bool MatchesBuilding(GameBuildingManagerAPI buildings, int id, int owner,
            eStructs type, int x, int y, out uint global)
        {
            global = 0;
            if (!buildings.TryGetBuildingById(id, out GameBuilding* building) || building == null ||
                building->r_PlayerIdOwner != owner || building->r_BuildingType != type ||
                building->r_TilePositionXBegin != x || building->r_TilePositionYBegin != y ||
                (building->r_AliveState != AliveState.NeedsInit && building->r_AliveState != AliveState.IsAlive))
                return false;
            global = building->r_GlobalId;
            return global != 0;
        }

        private static FRONT_Multiplayer LocalLobby()
        {
            if (FatControler.currentScene != Enums.SceneIDS.FrontEnd || !MainViewModel.viewModelLoaded ||
                !FRONT_Multiplayer.skirmishGame) return null;
            var view = MainViewModel.Instance;
            var lobby = view.FRONTMultiplayer;
            if (lobby == null || !lobby.panelActive || !view.Show_MPGameCreation || lobby.currentLobby == null ||
                !lobby.currentLobby.isHost || lobby.currentLobby.members.Count(member =>
                    (!member.SkirmishMember || member.SkirmishHumanMember) && !member.dummyToBeKicked) != 1)
                return null;
            return lobby;
        }

        private static FRONT_Multiplayer RequireLobby(Platform_Multiplayer.MPLobby expected)
        {
            var lobby = LocalLobby();
            if (lobby == null || !ReferenceEquals(lobby.currentLobby, expected))
                throw new InvalidOperationException("The local Skirmish lobby changed.");
            return lobby;
        }

        private static int CountLord(FRONT_Multiplayer lobby, int type) =>
            lobby.currentLobby.members.Count(member => member.SkirmishMember && !member.SkirmishHumanMember &&
                !member.dummyToBeKicked && member.GetLordType() == type);
    }
}

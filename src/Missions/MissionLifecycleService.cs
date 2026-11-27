using APIShared.GameModes;
using APIShared.Maps;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using R3;
using SHCDESE.EventAPI;
using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace APIShared
{
    internal sealed partial class MissionLifecycleService
    {
        private delegate void CreateMap(EditorDirector self, int size, Enums.GameModes mode, bool siege, bool multiplayer);
        private delegate bool EditorLoad(EditorDirector self, string file, string name);
        private delegate EngineInterface.LoadMapReturnData NewMap(int size, int type, bool siege, bool multiplayer);
        private delegate void Campaign(EditorDirector self, int id, int difficulty);
        private delegate void Trail(EditorDirector self, int type, int id, int difficulty);
        private delegate bool CustomTrail(EditorDirector self, string name, int id, int difficulty);
        private delegate void Save(EditorDirector self, string file, string name, FileHeader header);
        private delegate void Standalone(HUD_IngameMenu.RestartMapInfo info);
        private delegate void Skirmish(FRONT_Multiplayer self, HUD_IngameMenu.RestartSkirmishMapInfo info);
        private delegate void Tutorial(HUD_Tutorial self);
        private delegate void MultiplayerNew(Platform_Multiplayer self, EngineInterface.MultiplayerSetupData setup, FileHeader map, int trail, int mission);
        private delegate void MultiplayerSave(Platform_Multiplayer self, EngineInterface.MultiplayerSetupData setup, FileHeader map);
        private delegate EngineInterface.MultiplayerSetupData MultiplayerInit(bool skirmish, byte[] restart, int coopTrail, int coopMission, bool test, bool custom, bool extreme);
        private delegate bool Message(Platform_Multiplayer self, Platform_Multiplayer.MPData data, Platform_Multiplayer.MPGameMember member, bool thread);
        private delegate void PostLoad(EditorDirector self, EngineInterface.LoadMapReturnData data, bool thread, bool save);
        private delegate EngineInterface.LoadMapReturnData NativeLoad(int campaign, string file, bool dummy, bool save, int trail, int mission, bool classic);
        private delegate EngineInterface.LoadMapReturnData SaveFile(string file);
        private delegate EngineInterface.LoadMapReturnData EditorFile(string file, bool editor);
        private delegate void Screen(MainViewModel self, Enums.SceneIDS scene);
        private delegate void Scene(FatControler self, Enums.SceneIDS scene);
        private delegate void Exit(FatControler self);

        private readonly ManualLogSource log;
        private readonly int unityThread;
        private readonly MissionLifecycleState state;
        private readonly MissionExecutionSession executionSession = new MissionExecutionSession();
        private readonly List<Hook> hooks = new List<Hook>();
        private readonly List<IDisposable> subscriptions = new List<IDisposable>();
        private Operation executing;
        private Operation waiting;
        private static MissionLifecycleService published;
        // Validated against the real game assembly; publicizing does not grant runtime access.
        private static readonly FieldInfo localPlayerField = typeof(EditorDirector).GetField(
            "gameLocalPlayerID", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static GameModeSnapshot Snapshot => published?.state.Context?.Mode ?? default;
        internal static bool HasContext => published?.state.Context != null;
        // Set before the Start notification is published and cleared before End, so every Start
        // observer sees it regardless of observer order.
        internal static bool HasStartedMission => published?.state.Current != null;
        internal static MissionContext ActiveContext => published?.state.Context;
        // Capture only while this caller is synchronously executing a tracked mission attempt.
        // Pending context is installed before replacement End observers run, even before op.Id
        // receives Begin's return value. A ready/outside context never lends its identity to a load.
        internal static long ExecutingSessionId
        {
            get
            {
                var service = published;
                if (service == null) return 0;
                var operation = service.executing;
                return service.executionSession.Read(service.state, operation != null, operation?.Id ?? 0);
            }
        }
        internal static MissionLifecycleState.NativeClockState NativeClock => published?.state.NativeClock;
        // Identity of the currently loaded native world; null between invalidation and the next successful load.
        internal static MissionLifecycleState.NativeWorldState NativeWorld => NativeClock?.World;

        private CreateMap create; private EditorLoad editorLoad; private NewMap newMap;
        private Campaign campaign; private Trail trail; private CustomTrail customTrail;
        private Save save; private Standalone standalone; private Skirmish skirmish, restartSkirmish; private Tutorial tutorial;
        private MultiplayerNew multiplayerNew; private MultiplayerSave multiplayerSave; private Message message;
        private MultiplayerInit multiplayerInit;
        private PostLoad postLoad; private NativeLoad nativeLoad; private SaveFile saveFile; private EditorFile editorFile;
        private Screen screen; private Scene scene; private Exit exit;

        private sealed class Operation
        {
            internal long Id;
            internal MissionStartKind Kind;
            internal GameModeKind Intent;
            internal string File, Name;
            internal int Campaign, Trail = -1;
            internal int? CoopTrail;
            internal int? MissionIndex;
            internal int? Size, Difficulty;
            internal bool SiegeThatEditor;
            internal bool Multiplayer, Restart, NativeFailed, NativeSucceeded, ManagedSucceeded;
            internal long? RestartedFrom;
            internal Multiplayer.SimulationAgreement.Attempt SimulationAttempt;
            internal GameModeSnapshot? RestartMode;
            internal EngineInterface.LoadMapReturnData? Data;
            internal string MapAlias, MapAliasPath;
            internal string MapAliasFailure;
            internal MultiplayerMapArguments MapArguments;
            internal MapRecordLoadCoordinator.NativeAdmissionTicket ConfirmedMapFile;
            internal MultiplayerMapStart ValidatedMap, RestartSourceMap;
            internal bool DirectMultiplayerRestart;
            internal string RestartFailure;
            internal bool HasRestartProductionApproval;
            internal Economy.WorkerProductionRuntime.RestartAgreement RestartProductionApproval;
        }

        private MissionLifecycleService(ManualLogSource log)
        {
            this.log = log;
            unityThread = Thread.CurrentThread.ManagedThreadId;
            state = new MissionLifecycleState(ex => Report(ex));
        }

        // Script Extender and game members this service's own code binds; checked once at creation (issue #7).
        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Field(typeof(global::CrusaderDE.MainViewModel), "CurrentScreenNo", typeof(global::Enums.SceneIDS))
            .Field(typeof(global::CrusaderDE.MainViewModel), "viewModelLoaded", typeof(bool))
            .Field(typeof(global::CrusaderDE.FRONT_Multiplayer), "coopGame", typeof(bool))
            .Field(typeof(global::CrusaderDE.FRONT_Multiplayer), "singlePlayerCoop", typeof(bool))
            .Field(typeof(global::CrusaderDE.FRONT_Multiplayer), "trailMakerMode", typeof(bool))
            .Field(typeof(global::CrusaderDE.HUD_IngameMenu.RestartSkirmishMapInfo), "selectedHeader", typeof(global::FileHeader))
            .Field(typeof(global::CrusaderDE.HUD_IngameMenu.RestartSkirmishMapInfo), "customTestMission", typeof(bool))
            .Field(typeof(global::EditorDirector), "instance", typeof(global::EditorDirector))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "coopMissionID", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "difficulty_level", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "errorCode", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "mapSize", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "mission_level", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "multiplayerMap", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "playerID", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "siege_or_invasion", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "skirmishTrail", typeof(int))
            .Field(typeof(global::EngineInterface.LoadMapReturnData), "skirmishTrailLevel", typeof(int))
            .Field(typeof(global::FatControler), "currentScene", typeof(global::Enums.SceneIDS))
            .Field(typeof(global::FileHeader), "filePath", typeof(string))
            .Field(typeof(global::FileHeader), "crc", typeof(uint))
            .Field(typeof(global::FileHeader), "missionMap", typeof(bool))
            .Field(typeof(global::FileHeader), "skirmishMap", typeof(bool))
            .Field(typeof(global::Platform_Multiplayer), "activeLobby", typeof(global::Platform_Multiplayer.MPLobby))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "id", typeof(global::Steamworks.CSteamID))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "mapFileName", typeof(string))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "isHost", typeof(bool))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "crc", typeof(string))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "coopOrderSwapped", typeof(bool))
            .Field(typeof(global::Platform_Multiplayer.MPLobby), "members", typeof(global::System.Collections.Generic.List<global::Platform_Multiplayer.MPLobbyMember>))
            .Field(typeof(global::Platform_Multiplayer.MPLobbyMember), "id", typeof(global::Steamworks.CSteamID))
            .Field(typeof(global::Platform_Multiplayer.MPLobbyMember), "dummyToBeKicked", typeof(bool))
            .Field(typeof(global::Platform_Multiplayer.MPLobbyMember), "SkirmishMember", typeof(bool))
            .Field(typeof(global::EngineInterface.MultiplayerSetupData), "preferredAIVs", typeof(int[]))
            .Field(typeof(global::EngineInterface.MultiplayerSetupData), "start_keep_location_order", typeof(int[]))
            .Field(typeof(global::SHCDESE.EventAPI.MapLoaderR3EventHooks), "OnStartMap", typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs>))
            .Field(typeof(global::SHCDESE.EventAPI.MapLoaderR3EventHooks), "OnUnloadMap", typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.MapLoader.MapUnloadEventArgs>))
            .Method(typeof(global::Platform_Multiplayer), "get_Instance", typeof(global::Platform_Multiplayer))
            .Method(typeof(global::Platform_Multiplayer), "IsGameMemberHost", typeof(bool))
            .Method(typeof(global::Platform_Multiplayer.MPLobby), "AIVDataChecksum", typeof(string))
            .Method(typeof(global::Platform_Multiplayer.MPLobby), "getTeam", typeof(int), typeof(global::Platform_Multiplayer.MPLobbyMember))
            .Method(typeof(global::Platform_Multiplayer.MPLobby), "CountTeamMembers", typeof(int), typeof(int))
            .Method(typeof(global::Platform_Multiplayer.MPLobby), "getThisPlayerFromSteamID", typeof(int), typeof(ulong))
            .Method(typeof(global::Platform_Multiplayer.MPLobbyMember), "GetLordType", typeof(int))
            .Method(typeof(global::Platform_Multiplayer.MPLobbyMember), "GetLordSubType", typeof(int))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Instance", typeof(global::CrusaderDE.MainViewModel))
            .Method(typeof(global::CrusaderDE.MainViewModel), "set_Show_BlackOut", typeof(void), typeof(bool))
            .Method(typeof(global::CrusaderDE.MainViewModel), "InitNewScene", typeof(void), typeof(global::Enums.SceneIDS))
            .Method(typeof(global::CrusaderDE.HUD_ConfirmationPopup), "ShowConfirmationOKMessage", typeof(void),
                typeof(string), typeof(Action), typeof(string), typeof(bool))
            .Method(typeof(global::SHCDESE.EventAPI.EventHookBase), "get_Phase", typeof(global::SHCDESE.EventAPI.EventHookPhase))
            .Method(typeof(global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs), "get_bMultiplayerSave", typeof(byte))
            .Method(typeof(global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs), "get_CampaignMapId", typeof(int))
            .Method(typeof(global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs), "get_ReturnValue", typeof(ulong))
            .Method(typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs>), "get_Observable", typeof(global::R3.Observable<global::SHCDESE.EventAPI.MapLoader.MapStartEventArgs>))
            .Method(typeof(global::SHCDESE.EventAPI.R3EventHook<global::SHCDESE.EventAPI.MapLoader.MapUnloadEventArgs>), "get_Observable", typeof(global::R3.Observable<global::SHCDESE.EventAPI.MapLoader.MapUnloadEventArgs>));

        internal static bool TryCreate(ManualLogSource log, out MissionLifecycleService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.MissionLifecycle, log, out diagnostic))
                return false;
            var candidate = new MissionLifecycleService(log);
            try
            {
                candidate.Prepare();
                foreach (var hook in candidate.hooks) hook.Apply();
                candidate.Subscribe();
                published = service = candidate;
                // Mode captures run inside this service's checkpoints; their decisions use its log.
                GameModeHelper.ReportModeDecision = text => candidate.Safe(() => NativeApiLog.Error(log, text));
                diagnostic = Status(NativeCapabilityState.Available, "Process-wide mission lifecycle installed.");
                candidate.Safe(() => NativeApiLog.Debug(log, "Process-wide mission lifecycle installed."));
                return true;
            }
            catch (Exception ex)
            {
                foreach (var subscription in candidate.subscriptions) { try { subscription.Dispose(); } catch { } }
                for (int i = candidate.hooks.Count - 1; i >= 0; i--)
                { try { candidate.hooks[i].Undo(); } catch { } try { candidate.hooks[i].Dispose(); } catch { } }
                diagnostic = Status(NativeCapabilityState.Faulted, ex.Message);
                candidate.Report(ex);
                return false;
            }
        }

        private T Hook<T>(Type type, string name, T callback) where T : Delegate
        {
            var signature = typeof(T).GetMethod("Invoke").GetParameters();
            bool instance = signature.Length > 0 && signature[0].ParameterType == type;
            var parameters = new Type[signature.Length - (instance ? 1 : 0)];
            for (int i = 0; i < parameters.Length; i++) parameters[i] = signature[i + (instance ? 1 : 0)].ParameterType;
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance, null, parameters, null)
                ?? throw new MissingMethodException(type.FullName, name);
            var hook = new Hook(method, callback, new HookConfig { ManualApply = true, ID = "APIShared.MissionLifecycle." + name });
            hooks.Add(hook);
            return hook.GenerateTrampoline<T>();
        }

        private void Prepare()
        {
            PrepareRestartMenu();
            create = Hook<CreateMap>(typeof(EditorDirector), nameof(EditorDirector.createNewMap), (self, size, mode, siege, multi) =>
                Run(new Operation { Kind = MissionStartKind.EditorCreated, Intent = GameModeKind.MapEditor, Size = size, SiegeThatEditor = siege },
                    () => { create(self, size, mode, siege, multi); MarkManaged(); }));
            editorLoad = Hook<EditorLoad>(typeof(EditorDirector), nameof(EditorDirector.loadMapIntoEditor), (self, file, name) =>
                Run(Seed(MissionStartKind.EditorLoaded, GameModeKind.MapEditor, file, name), () =>
                { bool ok = editorLoad(self, file, name); if (ok) MarkManaged(); return ok; }));
            newMap = Hook<NewMap>(typeof(EngineInterface), nameof(EngineInterface.newMapEditor), (size, type, siege, multi) =>
                CaptureNative(() => newMap(size, type, siege, multi)));
            campaign = Hook<Campaign>(typeof(EditorDirector), nameof(EditorDirector.LoadCampaignMap), (self, id, difficulty) =>
                Run(new Operation { Kind = MissionStartKind.NewGame, Intent = GameModeKind.Campaign, Campaign = id, Difficulty = difficulty }, () => campaign(self, id, difficulty)));
            trail = Hook<Trail>(typeof(EditorDirector), nameof(EditorDirector.LoadSkirmishMap), (self, type, id, difficulty) =>
                Run(new Operation { Kind = MissionStartKind.NewGame, Intent = GameModeKind.VanillaTrail, Trail = type, MissionIndex = id, Difficulty = difficulty }, () => trail(self, type, id, difficulty)));
            customTrail = Hook<CustomTrail>(typeof(EditorDirector), nameof(EditorDirector.LoadCustomTrailMap), (self, name, id, difficulty) =>
                Run(new Operation { Kind = MissionStartKind.NewGame, Intent = GameModeKind.CustomTrail, Name = name, MissionIndex = id - 1, Difficulty = difficulty }, () => customTrail(self, name, id, difficulty)));
            save = Hook<Save>(typeof(EditorDirector), nameof(EditorDirector.loadSaveGame), (self, file, name, header) =>
            {
                SavegameModSettings.BeginLoad(file, false);
                Run(Seed(MissionStartKind.LoadedSave, GameModeKind.Unknown, file, name), () => save(self, file, name, header));
            });
            standalone = Hook<Standalone>(typeof(FRONT_StandaloneMission), nameof(FRONT_StandaloneMission.StartMap), info =>
                Run(Seed(MissionStartKind.NewGame, GameModeKind.StandaloneMission), () => standalone(info)));
            skirmish = Hook<Skirmish>(typeof(FRONT_Multiplayer), "StartSkirmishGame", (self, info) =>
            {
                var operation = Seed(MissionStartKind.NewGame, GameModeKind.CustomGame);
                if (executing == null && MapLoadPolicy.HasProvider && info == null &&
                    !FRONT_Multiplayer.coopGame && !self.singlePlayerCoop && !self.trailMakerMode &&
                    !PrepareLocalMap(operation, LobbySelectedMapHeader.Read(self))) return;
                Run(operation, () => skirmish(self, info));
            });
            restartSkirmish = Hook<Skirmish>(typeof(FRONT_Multiplayer), nameof(FRONT_Multiplayer.RestartSkirmishGame), (self, info) =>
            {
                // Only a ready gameplay mission on the Unity thread is a lifecycle restart. The editor or a
                // foreign thread starts a tracked new attempt instead of throwing inside Vanilla's path.
                var operation = new Operation { Kind = MissionStartKind.NewGame, Intent = GameModeKind.CustomGame, Restart = true };
                if ((executing == null || executing.Restart) && MapLoadPolicy.HasProvider && info != null &&
                    !FRONT_Multiplayer.coopGame && !self.singlePlayerCoop && !info.customTestMission &&
                    !PrepareLocalMap(executing ?? operation, info.selectedHeader)) return;
                if (IsLifecycleRestart(executing != null, state.Current,
                        Thread.CurrentThread.ManagedThreadId == unityThread))
                    Restart(() => restartSkirmish(self, info), operation);
                else
                    Run(operation, () => restartSkirmish(self, info));
            });
            tutorial = Hook<Tutorial>(typeof(HUD_Tutorial), nameof(HUD_Tutorial.StartTutorial), self =>
                Run(Seed(MissionStartKind.NewGame, GameModeKind.Tutorial), () => tutorial(self)));
            multiplayerNew = Hook<MultiplayerNew>(typeof(Platform_Multiplayer), nameof(Platform_Multiplayer.StartGame), StartMultiplayerMap);
            multiplayerSave = Hook<MultiplayerSave>(typeof(Platform_Multiplayer), nameof(Platform_Multiplayer.StartSave), (self, setup, map) =>
            {
                SavegameModSettings.BeginLoad(map?.filePath, true);
                var operation = new Operation { Kind = MissionStartKind.LoadedSave, Intent = GameModeKind.Unknown, Multiplayer = true };
                Run(operation, () =>
                {
                    var receipt = ScriptExtenderFixes.LobbyStartPacketRuntime.BeginNativeStart(self, operation.Id);
                    bool returnedNormally = false;
                    try { multiplayerSave(self, setup, map); returnedNormally = true; }
                    finally { Safe(() => ScriptExtenderFixes.LobbyStartPacketRuntime.EndNativeStart(receipt,
                        returnedNormally && operation.NativeSucceeded && !operation.NativeFailed)); }
                });
            });
            multiplayerInit = Hook<MultiplayerInit>(typeof(EngineInterface), nameof(EngineInterface.initMultiplayerGame), (skirmishGame, restart, coop, mission, test, custom, extreme) =>
            {
                Safe(() =>
                {
                    if (executing == null) return;
                    executing.CoopTrail = coop;
                    if (coop > 0) { executing.Intent = GameModeKind.CoopTrail; executing.MissionIndex = mission; }
                    else if (custom && executing.Intent == GameModeKind.CustomGame) executing.Intent = GameModeKind.CustomTrail;
                });
                // Vanilla's Coop branch uploads AI castles right after this call; collect the plans first.
                if (executing != null && coop > 0)
                    Safe(() => CoopAiStartImportService.NotifyCoopInitialization(coop, mission));
                // Lobby settings received during this start must reach the models before the map loads.
                if (executing != null && executing.Multiplayer)
                    Safe(ModSettings.LobbySettingsReceiveRuntime.FlushBeforeMultiplayerStart);
                return multiplayerInit(skirmishGame, restart, coop, mission, test, custom, extreme);
            });
            message = Hook<Message>(typeof(Platform_Multiplayer), "processMessage", ProcessMessage);
            postLoad = Hook<PostLoad>(typeof(EditorDirector), nameof(EditorDirector.postLoading), (self, data, thread, fromSave) =>
            { postLoad(self, data, thread, fromSave); Safe(() => { if (executing != null && data.errorCode == 1) executing.ManagedSucceeded = true; }); });
            nativeLoad = Hook<NativeLoad>(typeof(EngineInterface), "loadMap", (id, file, dummy, fromSave, type, mission, classic) =>
            {
                Safe(() => { if (executing != null) { executing.File = file; executing.Campaign = id > 0 ? id : 0; executing.Trail = type; } });
                var op = executing;
                if (op?.MapArguments != null && op.Kind == MissionStartKind.NewGame)
                    CheckMultiplayerMapBeforeNativeLoad(op);
                return CaptureNative(() =>
                {
                    if (op?.MapAlias == null || id > 0 || dummy || fromSave || type >= 0 ||
                        !string.Equals(op.MapAliasPath, file, StringComparison.Ordinal))
                        return nativeLoad(id, file, dummy, fromSave, type, mission, classic);
                    if (!MapLoadPolicy.CanAlias)
                    {
                        op.MapAliasFailure = "The native map filename hook became unavailable before loading. Filename length " +
                            System.IO.Path.GetFileName(file).Length + ", native limit " + MapLoadPolicy.NativeFileNameLimit +
                            " UTF-16 code units. Path: " + file;
                        if (op.DirectMultiplayerRestart) op.RestartFailure = op.MapAliasFailure;
                        else if (op.Multiplayer) MapRecordLoadCoordinator.AbortBeforeMapLoad(op.MapAliasFailure);
                        throw new NativeAdmissionAbort(op.MapAliasFailure);
                    }
                    return MapLoadPolicy.RunWithName(file, op.MapAlias,
                        () => nativeLoad(id, file, dummy, fromSave, type, mission, classic));
                });
            });
            saveFile = Hook<SaveFile>(typeof(EngineInterface), nameof(EngineInterface.LoadSaveFile), file => CaptureNative(() => saveFile(file)));
            editorFile = Hook<EditorFile>(typeof(EngineInterface), nameof(EngineInterface.LoadMapFile), (file, editor) => CaptureNative(() => editorFile(file, editor)));
            screen = Hook<Screen>(typeof(MainViewModel), nameof(MainViewModel.GoToScreen), (self, target) =>
            { try { screen(self, target); } finally { Safe(() => { if (self.CurrentScreenNo != Enums.SceneIDS.ActualMainGame) End(MissionEndReason.SceneChanged); }); } });
            scene = Hook<Scene>(typeof(FatControler), nameof(FatControler.NewScene), (self, target) =>
            { try { scene(self, target); } finally { Safe(() => { if (FatControler.currentScene != Enums.SceneIDS.ActualMainGame) End(MissionEndReason.SceneChanged); }); } });
            exit = Hook<Exit>(typeof(FatControler), nameof(FatControler.ExitApp), self =>
            { Safe(() => End(MissionEndReason.ApplicationExit)); exit(self); });
        }

        private void Subscribe()
        {
            // These callbacks must NEVER escape into SE: its detour catch blocks can call Vanilla twice.
            subscriptions.Add(MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(e => Safe(() =>
            {
                if (e.Phase != EventHookPhase.Pre) return;
                state.InvalidateNativeClock();
                if (executing == null) End(MissionEndReason.Unloaded);
            })));
            subscriptions.Add(MapLoaderR3EventHooks.OnStartMap.Observable.Subscribe(e => Safe(() =>
            {
                var op = executing;
                if (op == null || !state.IsPending(op.Id)) return;
                if (e.bMultiplayerSave != 0) op.Kind = MissionStartKind.LoadedSave;
                if (e.CampaignMapId > 0) op.Campaign = e.CampaignMapId;
                if (e.Phase == EventHookPhase.Pre)
                    state.Checkpoint(op.Id, Capture(op), MissionInitializationPhase.BeforeNativeStart);
                else if (unchecked((uint)e.ReturnValue) == uint.MaxValue)
                    state.Checkpoint(op.Id, Capture(op), MissionInitializationPhase.AfterNativeStart);
                else op.NativeFailed = true;
            })));
        }

        private static Operation Seed(MissionStartKind kind, GameModeKind intent, string file = null, string name = null) =>
            new Operation { Kind = kind, Intent = intent, File = file, Name = name };

        private MissionContext Capture(Operation op)
        {
            GameModeSnapshot mode;
            try { mode = GameModeHelper.CaptureMission(op.Multiplayer, op.Kind == MissionStartKind.LoadedSave, op.Campaign, op.Trail,
                op.Intent == GameModeKind.MapEditor, op.Data, op.Intent, op.CoopTrail); }
            catch (Exception ex) { Report(ex); mode = default; } // Unknown evidence never opens a mode gate.
            if (op.RestartMode.HasValue) mode = mode.WithRestartProvenance(op.RestartMode.Value);
            var data = op.Data;
            int? player = data.HasValue && data.Value.playerID >= 1 && data.Value.playerID <= 8 ? data.Value.playerID : (int?)null;
            bool? host = null;
            try
            {
                if (op.ManagedSucceeded && EditorDirector.instance != null)
                {
                    if (localPlayerField == null || localPlayerField.FieldType != typeof(int))
                        throw new MissingFieldException(nameof(EditorDirector), "gameLocalPlayerID : Int32");
                    int assigned = (int)localPlayerField.GetValue(EditorDirector.instance);
                    player = assigned >= 1 && assigned <= 8 ? assigned : (int?)null;
                }
                if (op.Multiplayer) host = Platform_Multiplayer.Instance.IsGameMemberHost();
            }
            catch (Exception ex) { Report(ex); }
            int? mission = op.MissionIndex;
            if (!mission.HasValue && data.HasValue)
            {
                if (mode.Kind == GameModeKind.CoopTrail) mission = data.Value.coopMissionID;
                else if (mode.Kind == GameModeKind.Campaign) mission = data.Value.mission_level;
                else if (data.Value.skirmishTrail >= 0 &&
                    (mode.Kind == GameModeKind.VanillaTrail || mode.Kind == GameModeKind.SandsOfTime)) mission = data.Value.skirmishTrailLevel;
            }
            if (mission < 0) mission = null;
            return new MissionContext(op.Id, op.Kind, mode, op.File, op.Name, op.Restart,
                data.HasValue && data.Value.mapSize > 0 ? data.Value.mapSize : op.Size,
                data.HasValue && data.Value.difficulty_level >= 0 ? data.Value.difficulty_level : op.Difficulty, player,
                mission, host,
                data.HasValue ? MapType(data.Value.siege_or_invasion) : MissionMapType.Unknown,
                data.HasValue ? data.Value.multiplayerMap != 0 : (bool?)null, op.SiegeThatEditor)
                .AsRestartOf(op.RestartedFrom);
        }

        // LoadMapReturnData uses the native serialized subtype order, not Enums.GameModes values.
        internal static MissionMapType MapType(int nativeSubtype)
        {
            switch (nativeSubtype)
            {
                case 0: return MissionMapType.Siege;
                case 1: return MissionMapType.Invasion;
                case 2: return MissionMapType.Economy;
                case 3: return MissionMapType.FreeBuild;
                default: return MissionMapType.Unknown;
            }
        }

        private void Run(Operation op, Action original) => Run(op, () => { original(); return true; });

        private bool PrepareLocalMap(Operation operation, FileHeader header)
        {
            if (header == null || header.missionMap || header.skirmishMap) return true;
            var decision = MapLoadPolicy.Evaluate(header.filePath);
            if (decision.Rejection != null)
            {
                ReportLocalMapRejection(log, decision.Rejection);
                return false;
            }
            operation.MapAliasPath = header.filePath;
            operation.MapAlias = decision.NativeFileName;
            return true;
        }

        internal static void ReportLocalMapRejection(ManualLogSource logger, string reason)
        {
            Lobby.LobbyLaunchStatus.WriteAttemptError(logger, "MAP_FILE_LOAD_BLOCKED: " + reason, null);
            if (Internal.UnityMainThreadDispatch.IsMainThread && MainViewModel.viewModelLoaded)
                try { HUD_ConfirmationPopup.ShowConfirmationOKMessage(Localization.UiText.Get("Savegames.LoadTitle"),
                    () => { }, reason); }
                catch (Exception error) { NativeApiLog.Error(logger, "Map load rejection popup unavailable: " + error.Message); }
        }
        internal sealed class NativeAdmissionAbort : Exception
        { internal NativeAdmissionAbort(string reason) : base(reason) { } }

        internal static T InvokeNativeAdmission<T>(Func<T> original, out bool returnedNormally)
        {
            returnedNormally = false;
            try { T result = original(); returnedNormally = true; return result; }
            catch (NativeAdmissionAbort) { return default; }
        }
        private T Run<T>(Operation op, Func<T> original)
        {
            if (executing != null) return original(); // Nested custom-trail -> skirmish belongs to the outer attempt.
            var previousExecution = executionSession.Enter(state);
            try
            {
                executing = op;
                completedMap = null;
                completedMapSession = 0;
                if (waiting?.Kind == MissionStartKind.LoadedSave && op.Kind != MissionStartKind.LoadedSave)
                    SavegameModSettings.FinishLoad();
                waiting = null;
                bool normal = false;
                GameModeHelper.BeginModeDecisions();
                op.SimulationAttempt = Multiplayer.SimulationAgreement.PrepareAttempt(op.RestartedFrom);
                Safe(() => op.Id = state.Begin(Capture(op)));
                try
                {
                    if (op.Kind == MissionStartKind.LoadedSave) SavegameModSettings.ApplyBeforeLoad(op.Multiplayer);
                    T result = InvokeNativeAdmission(original, out normal);
                    if (!normal) op.NativeFailed = true;
                    return result;
                }
                finally
                {
                    executing = null;
                    if (op.NativeFailed || !normal)
                        Multiplayer.SimulationAgreement.Retire(op.Id);
                    Safe(CoopAiStartImportService.NotifyAttemptFinished);
                    if (op.Kind == MissionStartKind.LoadedSave && normal && op.NativeSucceeded &&
                        !op.NativeFailed && op.ManagedSucceeded)
                    {
                        try { SavegameModSettings.ApplyLoadedSettings(Capture(op)); }
                        catch (Exception error) { SavegameModSettings.ReportRestoreFailure(error); }
                    }
                    Safe(() =>
                    {
                        var context = Capture(op);
                        if (normal && op.NativeSucceeded && !op.NativeFailed && op.ManagedSucceeded)
                            PublishCompletedMap(op, context);
                        if (state.Finish(op.Id, context, normal, op.NativeSucceeded && !op.NativeFailed,
                            op.ManagedSucceeded, op.Multiplayer && context.IsHost == false)) waiting = op;
                    });
                    if (op.Kind == MissionStartKind.LoadedSave && waiting == null)
                        SavegameModSettings.FinishLoad();
                    if (op.Kind == MissionStartKind.NewGame && op.Multiplayer && waiting == null && !op.DirectMultiplayerRestart)
                        MapRecordLoadCoordinator.FinishLoad();
                    if (op.MapAliasFailure != null && !op.Multiplayer)
                    {
                        Safe(() =>
                        {
                            if (!MainViewModel.viewModelLoaded) return;
                            MainViewModel.Instance.Show_BlackOut = false;
                            MainViewModel.Instance.InitNewScene(Enums.SceneIDS.FrontEnd);
                        });
                        ReportLocalMapRejection(log, op.MapAliasFailure);
                    }
                }
            }
            finally { executionSession.Restore(previousExecution); }
        }

        private bool ProcessMessage(Platform_Multiplayer self, Platform_Multiplayer.MPData data,
            Platform_Multiplayer.MPGameMember member, bool thread)
        {
            var op = waiting;
            if (!thread && op != null && !state.IsPending(op.Id))
            {
                waiting = null;
                SavegameModSettings.FinishLoad();
                MapRecordLoadCoordinator.FinishLoad();
            }
            if (thread || op == null || !state.IsPending(op.Id) || executing != null) return message(self, data, member, thread);
            var previousExecution = executionSession.Enter(state);
            try
            {
                executing = op;
                bool normal = false;
                try { bool result = message(self, data, member, thread); normal = true; return result; }
                finally
                {
                    executing = null;
                    if (!normal)
                    {
                        waiting = null;
                        FailDeferredMessage(op);
                    }
                    else if (op.ManagedSucceeded)
                    {
                        waiting = null;
                        CompleteDeferredMessage(op);
                    }
                }
            }
            finally { executionSession.Restore(previousExecution); }
        }

        // Runs for every multiplayer message: the lambdas capturing op live in these slow-path
        // methods, so the common path allocates no closure on entry.
        private void FailDeferredMessage(Operation op)
        {
            try { Safe(() => state.End(MissionEndReason.Exception, op.Id)); }
            finally { SavegameModSettings.FinishLoad(); MapRecordLoadCoordinator.FinishLoad(); }
        }

        private void CompleteDeferredMessage(Operation op) =>
            Safe(() => CompleteDeferredRestore(
                () => SavegameModSettings.ApplyLoadedSettings(Capture(op)),
                () =>
                {
                    var context = Capture(op);
                    PublishCompletedMap(op, context);
                    state.Ready(op.Id, context);
                },
                () => { SavegameModSettings.FinishLoad(); if (!op.DirectMultiplayerRestart) MapRecordLoadCoordinator.FinishLoad(); },
                SavegameModSettings.ReportRestoreFailure));

        internal static void CompleteDeferredRestore(Action restore, Action ready,
            Action cleanup, Action<Exception> reportRestoreError)
        {
            try
            {
                try { restore?.Invoke(); }
                catch (Exception error)
                {
                    try { reportRestoreError?.Invoke(error); }
                    catch { /* Failure reporting must not prevent the lifecycle transition. */ }
                }
                ready?.Invoke();
            }
            finally { cleanup?.Invoke(); }
        }

        internal static Economy.WorkerProductionRuntime.RestartAgreement RestartProductionApproval(long sessionId)
        {
            var op = published?.executing;
            return op?.Id == sessionId && op.DirectMultiplayerRestart ? op.RestartProductionApproval : null;
        }

        private EngineInterface.LoadMapReturnData CaptureNative(Func<EngineInterface.LoadMapReturnData> original)
        {
            var op = executing;
            if (op?.DirectMultiplayerRestart == true)
            {
                if (!MapRecordLoadCoordinator.MatchesFile(op.ConfirmedMapFile, op.File))
                {
                    op.NativeFailed = true;
                    op.RestartFailure = "The confirmed multiplayer map file changed before restart loading.";
                    throw new NativeAdmissionAbort(op.RestartFailure);
                }
            }
            else if (op != null && op.Multiplayer && op.Kind == MissionStartKind.NewGame &&
                !MapRecordLoadCoordinator.ValidateNativeAdmission(op.File, true))
            {
                op.NativeFailed = true;
                MapRecordLoadCoordinator.Abort("The confirmed map file changed before native initialization.");
                // StartGame does not check errorCode before StartMultiplayerGame/postLoading.
                // Unwind its entire Original to our outer managed start boundary instead.
                throw new NativeAdmissionAbort("The confirmed map file changed before native initialization.");
            }
            Safe(() => { state.InvalidateNativeClock(); if (op != null) state.BeginNativeLoad(op.Id); });
            Safe(() => { if (op != null) state.Checkpoint(op.Id, Capture(op), MissionInitializationPhase.BeforeLoad); });
            string productionRejection=op==null ? null : Economy.WorkerProductionRuntime.ValidateNativeAdmission(Capture(op));
            if(!string.IsNullOrEmpty(productionRejection))
            {
                op.NativeFailed=true;
                if (op.DirectMultiplayerRestart) op.RestartFailure = productionRejection;
                Safe(()=>state.CompleteNativeLoad(op.Id,false));
                throw new NativeAdmissionAbort(productionRejection);
            }
            string simulationRejection = op == null ? null : Multiplayer.SimulationAgreement.Admit(Capture(op), op.SimulationAttempt);
            if (!string.IsNullOrEmpty(simulationRejection))
            {
                op.NativeFailed = true;
                if (op.DirectMultiplayerRestart) op.RestartFailure = simulationRejection;
                Safe(() => state.CompleteNativeLoad(op.Id, false));
                Report(new InvalidOperationException(simulationRejection));
                throw new NativeAdmissionAbort(simulationRejection);
            }
            long nativeGeneration = Units.AiMilitary.AiMilitaryService.Instance.Generation;
            EngineInterface.LoadMapReturnData result;
            bool returned = false;
            try
            {
                // Hold the same monitor beyond the nested native wrapper's own lock, closing the
                // simulation-thread gap before internal state reconciliation. Public notifications
                // below retain their previous position outside this internal critical section.
                object engine = Internal.EngineMonitor.Current;
                if (engine == null) { result = original(); returned = true; }
                else lock (engine)
                {
                    // A synchronous load can be nested inside an old placement callback. Its new
                    // world must initialize within its own scope, rather than inherit the retired
                    // parent's mutation receipt. The parent becomes invalid again on return.
                    using (Units.AiMilitary.Orders.AiRecruitMutationReceipts.Enter(() =>
                        nativeGeneration == Units.AiMilitary.AiMilitaryService.Instance.Generation))
                        result = original();
                    returned = true;
                    if (nativeGeneration != Units.AiMilitary.AiMilitaryService.Instance.Generation ||
                        Units.AiMilitary.AiMilitaryService.Instance.DidRawRestoreFail) result.errorCode = 0;
                    if (result.errorCode != 1 && op != null &&
                        (Units.AiMilitary.AiMilitaryService.Instance.DidRawRestoreFail ||
                            op.Multiplayer && op.Kind == MissionStartKind.NewGame))
                    {
                        op.NativeFailed = true;
                        if (op.DirectMultiplayerRestart)
                            op.RestartFailure = "Protected multiplayer map initialization failed.";
                        else if (op.Kind == MissionStartKind.NewGame)
                            MapRecordLoadCoordinator.Abort("The multiplayer map could not complete protected native initialization.");
                        throw new NativeAdmissionAbort("Protected multiplayer map initialization failed.");
                    }
                    if (result.errorCode == 1)
                        Safe(() => Units.AiMilitary.AiMilitaryService.Instance.CompleteNativeLoad());
                }
            }
            finally { if (!returned) Safe(() => { if (op != null) state.CompleteNativeLoad(op.Id, false); }); }
            Safe(() =>
            {
                if (op == null || !state.IsPending(op.Id)) return; // Noninteractive export is not a mission.
                op.Data = result;
                op.NativeSucceeded = result.errorCode == 1;
                state.CompleteNativeLoad(op.Id, op.NativeSucceeded && !op.NativeFailed);
                if (op.NativeSucceeded && !op.NativeFailed)
                {
                    // Isolate the call itself: even an optional observer's type initializer must
                    // not suppress the existing NativeLoaded checkpoint or alter native admission.
                    try { Economy.WeaponProductionRuntime.BindWorkerObservationWorld(op.Id, state.NativeClock?.World); }
                    catch (Exception error) { Report(error); }
                    state.Checkpoint(op.Id, Capture(op), MissionInitializationPhase.NativeLoaded);
                }
            });
            return result;
        }
        private void MarkManaged() => Safe(() => { if (executing != null) executing.ManagedSucceeded = true; });
        private void End(MissionEndReason reason)
        {
            completedMap = null;
            completedMapSession = 0;
            bool abandonedSave = waiting?.Kind == MissionStartKind.LoadedSave;
            waiting = null;
            try { state.End(reason); }
            finally
            {
                if (abandonedSave) SavegameModSettings.FinishLoad();
                MapRecordLoadCoordinator.FinishLoad();
            }
        }
        private void Safe(Action action) { try { action(); } catch (Exception ex) { Report(ex); } }
        private void Report(Exception ex) { try { NativeApiLog.Error(log, "Mission lifecycle callback failed: " + ex); } catch { } }
        private static NativeCapabilityDiagnostic Status(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.MissionLifecycle, state, string.Empty, reason);
        internal IMissionLifecycleCapability Bind(string owner) => new Binding(this, owner);
        private sealed class Binding : IMissionLifecycleCapability
        {
            private readonly MissionLifecycleService service;
            private readonly string owner;
            internal Binding(MissionLifecycleService service, string owner) { this.service = service; this.owner = owner; }
            public MissionContext Current => service.state.Current;
            public bool TryRegisterObserver(string id, Action<MissionLifecycleNotification> onStart,
                Action<MissionLifecycleNotification> onEnd, Action<MissionLifecycleNotification> onInitialization,
                out NativeCapabilityDiagnostic diagnostic)
            {
                bool ok = Thread.CurrentThread.ManagedThreadId == service.unityThread &&
                    (onStart != null || onEnd != null || onInitialization != null) && service.state.Register(owner, id, e =>
                    { if (e.Kind == MissionLifecycleKind.Start) onStart?.Invoke(e); else if (e.Kind == MissionLifecycleKind.End) onEnd?.Invoke(e); else onInitialization?.Invoke(e); });
                diagnostic = Status(ok ? NativeCapabilityState.Available : NativeCapabilityState.ValidationFailed,
                    ok ? "Mission observer registered." : "Registration requires the Unity thread and a unique owner-local ID.");
                return ok;
            }
        }
    }
}

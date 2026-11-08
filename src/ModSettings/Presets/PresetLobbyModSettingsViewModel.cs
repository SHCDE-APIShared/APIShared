using APIShared.GameModes;
using APIShared.ModSettings;
using APIShared.Internal;
using BepInEx;
using BepInEx.Logging;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using SHCDESE.BepInEx.Bootstrap;
using SHCDESE.ViewModels;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#if !API_SHARED_PRESET_TESTS
using R3;
using SHCDESE.EventAPI;
using SHCDESE.NoesisUtil;
#endif
using ComboBoxItem = Noesis.ComboBoxItem;
using Visibility = Noesis.Visibility;
#if API_SHARED_LOBBY_OBSERVER && !API_SHARED_PRESET_TESTS
using APIShared;
#endif

namespace APIShared.ModSettings
{
    /// <summary>
    /// Persists a setting in the shared local preset file without exposing it to
    /// the Script Extender's multiplayer synchronization layer. Mission sources never lock or restore it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class PresetLocalAttribute : Attribute
    {
    }

    /// <summary>
    /// Integrates personal and published presets, temporary mission settings and role-aware UI with a Script Extender settings model while
    /// keeping the outer MessagePack dictionary readable by the Script Extender.
    /// </summary>
    /// <remarks>Register on the Unity thread and retain the model in a static runtime or persistent publisher. System_* members are shared XAML/integration bindings, not another mod network protocol. Classify settings with SyncHostOnly, SyncPerPlayer or PresetLocal, guard setters with CanMutateSetting and notify after changes. Internal controller/storage components own snapshots and persistence.</remarks>
    public abstract partial class PresetLobbyModSettingsViewModel : LobbyModSettingsBaseViewModel, IModSettingsWorkingCopyEndpoint, IModSettingsMissionSourceEndpoint
    {
        private enum SettingsMenuContext { Other, Campaign, DirectTrail, CustomizeSetup }
        private static readonly MethodInfo NotifyRevertMethod =
            typeof(LobbyModSettingsBaseViewModel).GetMethod(
                "NotifyRevert",
                BindingFlags.Instance | BindingFlags.NonPublic);

        private LobbyPresetController presetController;
        private int selectedPreset;
        private bool missionPresetContext;
        private bool missionPresetEditable;
        private bool missionPresetHasExplicitSettings;
        private Func<string> directLaunchNoticeProvider;
        private string lastDirectLaunchNotice = string.Empty;
        private SettingsMenuContext settingsMenuContext;
        private bool isRealMultiplayer;
        private bool isLocalHost = true;
#if API_SHARED_PRESET_TESTS
        internal Func<bool> System_TestPersonalHostRestoreAllowed;
#endif
        internal bool CanRestorePersonalHostSettings
        {
            get
            {
#if API_SHARED_PRESET_TESTS
                return System_TestPersonalHostRestoreAllowed?.Invoke() ?? isLocalHost;
#else
                var lobby = Platform_Multiplayer.Instance?.activeLobby;
                if (lobby != null) return lobby.isHost;
                var mission = APIShared.MissionLifecycleService.ActiveContext;
                return mission == null || !mission.Mode.IsRealMultiplayer || mission.IsHost == true;
#endif
            }
        }

        private void PublishSettingsSnapshot(IReadOnlyList<string> properties)
        {
            bool publishPersonal = false;
            foreach (string name in properties)
            {
                var property = GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property?.GetCustomAttribute<SyncHostOnlyAttribute>() != null)
                {
                    // Revalidate transport authority after the complete transaction.
                    // The existing host publisher defers until the lobby slot is ready.
#if API_SHARED_PRESET_TESTS
                    if (isLocalHost) base.System_TriggerUpdate(name);
#else
                    if (GameNetworkAPI.IsLocalHost()) base.System_TriggerUpdate(name);
#endif
                }
                else if (property?.GetCustomAttribute<SyncPerPlayerAttribute>() != null)
                    publishPersonal = true;
            }
            if (publishPersonal) perPlayerSettingsCoordinator?.RequestPublish();
        }
        private PerPlayerLobbySettingsCoordinator perPlayerSettingsCoordinator;
#if !API_SHARED_PRESET_TESTS
        private string modSettingsSearchText = string.Empty;
        private string modSettingsSearchExactKey = string.Empty;
        private bool modSettingsSearchIncludeToolTips;
        private bool modSettingsSearchExpanded;
        private int modSettingsSearchFocusRequest;
        private bool presetSavePanelOpen;
        private bool presetLoadPanelOpen;
        private string presetSaveName = string.Empty;
        private string presetSaveDescription = string.Empty;
        private ModSettingsPresetListEntry selectedPresetLoadEntry;
        private ModSettingsPresetSaveTarget selectedPresetSaveTarget;
        private readonly ObservableCollection<ModSettingsPresetListEntry> presetLoadEntries =
            new ObservableCollection<ModSettingsPresetListEntry>();
        private readonly ObservableCollection<ModSettingsPresetSaveTarget> presetSaveTargets =
            new ObservableCollection<ModSettingsPresetSaveTarget>();
        private bool applyingPresetSaveBulkMode;
        private readonly ObservableCollection<PresetSaveSettingViewModel> presetSaveSettings =
            new ObservableCollection<PresetSaveSettingViewModel>();
        private readonly ObservableCollection<ModSettingsWorkingSource> settingsSources =
            new ObservableCollection<ModSettingsWorkingSource>();
        private ModSettingsWorkingSource selectedSettingsSource;
        private string preferredSettingsSourceToken = string.Empty;
        private PublishedModSettingsPreset pendingDeletePreset;
        private string pendingOverwriteId;
        private PresetSaveSelection[] pendingOverwriteSelections;
        private string presetInlineConfirmationTitle = string.Empty;
        private string presetInlineConfirmationMessage = string.Empty;
        private string presetOperationStatus = string.Empty;
        private bool presetOperationFailed;
#endif

        /// <summary>Creates commands and local model state; call LobbyModSettingsPresetRegistration.Register on the Unity thread to initialize persistence and publish integration.</summary>
        protected PresetLobbyModSettingsViewModel()
        {
            System_ModeAvailability.PropertyChanged += (_, __) => RaiseModeNoticeProperties();
#if !API_SHARED_PRESET_TESTS
            System_ToggleModSettingsSearchCommand = new RelayCommand(ToggleModSettingsSearch);
            System_ClearModSettingsSearchCommand = new RelayCommand(ClearModSettingsSearch);
            ModSettingsSearchCatalog.StateChanged += OnModSettingsSearchStateChanged;
            ModSettingsSearchCatalog.SourceChanged += OnModSettingsSearchSourceChanged;
            System_OpenPresetLoadCommand = new RelayCommand(OpenPresetLoad);
            System_ConfirmPresetLoadCommand = new RelayCommand(ConfirmPresetLoad);
            System_DeletePresetCommand = new RelayCommand(DeleteSelectedPreset);
            System_CancelPresetLoadCommand = new RelayCommand(CancelPresetLoad);
            System_OpenPresetSaveCommand = new RelayCommand(OpenPresetSave);
            System_ConfirmPresetSaveCommand = new RelayCommand(ConfirmPresetSave);
            System_CancelPresetSaveCommand = new RelayCommand(CancelPresetSave);
            System_LoadSettingsSourceCommand = new RelayCommand(LoadSelectedSettingsSource);
            System_ConfirmPresetInlineActionCommand = new RelayCommand(ConfirmPresetInlineAction);
            System_CancelPresetInlineActionCommand = new RelayCommand(CancelPresetInlineAction);
            System_DismissPresetStatusCommand = new RelayCommand(DismissPresetStatus);
#endif
        }

        /// <summary>Recognizes removed properties whose stored preset values may be ignored.</summary>
        protected virtual bool IsRetiredPresetProperty(string propertyName) => false;

        /// <summary>Applies an explicitly confirmed selection. Overrides may add deferred application.</summary>
        protected virtual void ApplyConfirmedPresetSelection(PublishedModSettingsPreset preset)
        {
            using (ModSettingsSearchUpdateScope.Begin(this))
            {
                if (presetController == null) throw new InvalidOperationException("Preset controller is not initialized.");
                if (SettingsApplicationBackend != null)
                    System_RefreshOwnConfiguration();
                presetController.LoadPreset(preset);
                System_CommitConfiguration();
            }
        }

        /// <summary>Whether the controller is applying a whole snapshot; use to avoid reentrant partial persistence in custom setters.</summary>
        protected bool IsApplyingSettingsSnapshot =>
            presetController?.IsApplyingSnapshot == true;

        /// <summary>
        /// True only while entering, exiting or restoring a mission/remote-host snapshot.
        /// Local working copies, published presets and resets do not enter this scope.
        /// Like snapshot application itself, this is synchronous and does not establish network authority.
        /// Existing mutation/host checks still apply.
        /// </summary>
        protected bool IsApplyingMissionSettingsSnapshot =>
            presetController?.IsApplyingMissionSnapshot == true;

        /// <summary>
        /// Replaces one captured code-default value without changing the current working settings.
        /// This is intended for defaults which can only be materialized after dynamic discovery.
        /// </summary>
        protected void SetModDefaultValue<T>(string propertyName, T value)
        {
            if (presetController == null)
                throw new InvalidOperationException("Preset storage must be prepared before dynamic defaults are updated.");
            presetController.SetDefaultValue(propertyName, value);
        }

        /// <summary>Optional participant hook after snapshot application; refresh derived runtime state without introducing another persistence or network engine.</summary>
        protected virtual void OnSettingsSnapshotApplied()
        {
        }

#if API_SHARED_PRESET_TESTS
        internal int System_WorkingStateWriteCount => presetController?.TestWriteCount ?? 0;

        internal string System_TestPresetStatusText => presetController?.GetStatusText(
            "Based on", "modified", "Personal presets", "Bundled with this mod", "External presets") ?? string.Empty;

        // Retained only in the source-linked regression harness for hostile legacy selection attempts.
        public int SelectedPreset
        {
            get => selectedPreset;
            set
            {
                int normalized = presetController?.NormalizeSelection(value, missionPresetContext) ??
                    (value == 1 ? 1 : 0);
                if (selectedPreset == normalized)
                    return;

                if (presetController == null)
                {
                    selectedPreset = normalized;
                    base.OnPropertyChanged(nameof(SelectedPreset));
                    return;
                }

                presetController.SwitchTo(normalized);
            }
        }
#endif

        internal void PreparePresets(
            ManualLogSource log,
            string pluginAssemblyLocation,
            string modName,
            string targetGuid,
            Version targetVersion,
            bool logRoutineActivity = true, bool publishParticipant = true)
        {
            if (presetController != null)
                throw new InvalidOperationException($"Preset storage for [{modName}] was already prepared.");

            presetController = new LobbyPresetController(
                new PresetHost(this),
                log,
                pluginAssemblyLocation,
                modName,
                targetGuid,
                targetVersion,
                logRoutineActivity);
            presetController.CaptureDefaults();
            if (publishParticipant)
            {
#if !API_SHARED_PRESET_TESTS
                PublishParticipant(targetGuid, pluginAssemblyLocation);
#endif
            }
#if !API_SHARED_PRESET_TESTS
            RebuildSettingsSources();
#endif
            PropertyChanged += OnOwnPropertyChangedForAccess;
            settingsAccessObserved = true;
            System_RefreshSettingsAccess();
        }

#if API_SHARED_PRESET_TESTS
        internal void PreparePresets(
            ManualLogSource log,
            string pluginAssemblyLocation,
            string modName)
        {
            PreparePresets(
                log,
                pluginAssemblyLocation,
                modName,
                "Tests." + modName,
                new Version(1, 0, 0));
        }
#endif

        internal void LoadInitialPresetSettings() => presetController?.LoadInitialSettings();

        internal void ActivatePresets()
        {
            if (presetController == null)
                throw new InvalidOperationException("Preset storage must be prepared before it is activated.");

            presetController.Activate();
        }

        internal void PublishParticipant(string id, string assemblyPath)
        {
            ModSettingsApplication.Register(id, this, assemblyPath);
#if !API_SHARED_PRESET_TESTS
            ModSettingsWorkingSourceRegistry.SourcesChanged += RebuildSettingsSources;
#endif
        }

        internal void PreparePerPlayerLobbySettings(
            ManualLogSource log,
            string modName,
            string ownerGuid,
            bool logRoutineActivity = true)
        {
            if (perPlayerSettingsCoordinator != null)
                throw new InvalidOperationException($"Per-player lobby settings for [{modName}] were already prepared.");

            var builder = new PerPlayerLobbySettingsBuilder(this);
            ConfigurePerPlayerLobbySettings(builder);
            perPlayerSettingsCoordinator = new PerPlayerLobbySettingsCoordinator(
                this,
                log,
                modName,
                ownerGuid,
                builder.Build(),
                logRoutineActivity);
#if !API_SHARED_PRESET_TESTS
            PerPlayerLobbyBroadcastGuard.Register(this, perPlayerSettingsCoordinator);
#endif
        }

        internal void ActivatePerPlayerLobbySettings()
        {
            if (perPlayerSettingsCoordinator == null)
                throw new InvalidOperationException("Per-player lobby settings must be prepared before activation.");
            perPlayerSettingsCoordinator.Activate();
        }

        internal void DeactivatePerPlayerLobbySettings()
        {
            perPlayerSettingsCoordinator?.Deactivate();
            perPlayerSettingsCoordinator = null;
        }

        private bool explicitModDefaultsReset;

        /// <summary>Returns the persistent settings available to shared preset authoring UI.</summary>
        public IReadOnlyList<PresetSettingDescriptor> System_GetPresetSettingDescriptors() =>
            presetController?.GetSettingDescriptors() ?? Array.Empty<PresetSettingDescriptor>();

        /// <summary>Imports a validated preset through the shared catalog, without overwriting an existing preset.</summary>
        public string System_ImportPresetJson(string json)
        {
            if (!CanChangePreset) throw new InvalidOperationException("Preset editing is locked.");
            string path = presetController.ImportPresetJson(json);
#if !API_SHARED_PRESET_TESTS
            RebuildPresetDialogCatalogs();
            RaisePresetDialogProperties();
#endif
            return path;
        }

#if !API_SHARED_PRESET_TESTS
        /// <summary>Exports the selected catalog preset with its original per-option modes.</summary>
        public string System_ExportSelectedPresetJson()
        {
            PublishedModSettingsPreset preset = selectedPresetLoadEntry?.Preset;
            if (preset == null) throw new InvalidOperationException("Select a saved preset in the load list first.");
            return ModSettingsPresetJson.Serialize(presetController.TargetGuid, preset.Id, preset.Name,
                preset.Description, preset.MinimumTargetVersion, preset.MaximumTargetVersion, preset.Settings);
        }

#endif
        /// <summary>Saves selected settings as a persistent, personal preset JSON file.</summary>
        public string System_SavePersonalPreset(
            string id,
            string name,
            string description,
            IEnumerable<PresetSaveSelection> selections,
            bool overwrite) =>
            presetController?.SavePersonalPreset(id, name, description, selections, overwrite) ?? string.Empty;

#if API_SHARED_PRESET_TESTS
        internal IReadOnlyList<PublishedModSettingsPreset> System_TestPublishedPresets =>
            presetController?.PublishedPresets ?? Array.Empty<PublishedModSettingsPreset>();

        internal void System_TestRefreshPresetCatalog() => presetController?.RefreshCatalog();

        internal void System_TestLoadPreset(string stableId)
        {
            PublishedModSettingsPreset preset = presetController?.PublishedPresets.FirstOrDefault(item =>
                string.Equals(item.StableId, stableId, StringComparison.Ordinal));
            if (preset == null)
                throw new InvalidDataException("The requested test preset is unavailable.");
            ApplyConfirmedPresetSelection(preset);
        }

        internal void System_TestDeletePreset(string stableId)
        {
            PublishedModSettingsPreset preset = presetController?.PublishedPresets.FirstOrDefault(item =>
                string.Equals(item.StableId, stableId, StringComparison.Ordinal));
            if (preset == null)
                throw new InvalidDataException("The requested test preset is unavailable.");
            presetController.DeletePersonalPreset(preset);
        }

#endif

        // Let the Extender process the notification first; then update our own working state.
        /// <summary>Notifies the Extender and coordinates persistence/publication for changed participant properties. Invoke after a real value change; snapshot guards prevent partial reentrant saves.</summary>
        protected new void OnPropertyChanged(string name)
        {
            int refreshesBefore = completedSettingsAccessRefreshes;
            try
            {
                // The access subscription refreshes for this notification, before persistence
                // reads the current role. The derived notifications below change no refresh input.
                base.OnPropertyChanged(name);

                if (presetController?.IsHostSettingsActivationProperty(name) == true)
                {
                    RaiseDerivedAccessOutput(nameof(HostSettingsEnabled));
                    RaiseDerivedAccessOutput(nameof(HostSettingsOpacity));
                }
                if (presetController?.IsClientSettingsActivationProperty(name) == true)
                {
                    RaiseDerivedAccessOutput(nameof(ClientSettingsEnabled));
                    RaiseDerivedAccessOutput(nameof(ClientSettingsOpacity));
                }
            }
            finally
            {
                presetController?.AfterPropertyChanged(name);
#if !API_SHARED_PRESET_TESTS
                if (presetController?.IsPersistentSetting(name) == true)
                {
                    RaiseDerivedAccessOutput(nameof(System_PresetStatusText));
                    RaiseDerivedAccessOutput(nameof(System_PresetStatusVisibility));
                }
#endif
                // Persistence changes no refresh input, so a refresh completed for this
                // notification already reflects the final state.
                if (completedSettingsAccessRefreshes == refreshesBefore)
                    System_RefreshSettingsAccess();
            }
        }

        private void SetSelectedPresetCore(int value)
        {
            if (selectedPreset == value)
                return;

            selectedPreset = value;
#if API_SHARED_PRESET_TESTS
            OnPropertyChanged(nameof(SelectedPreset));
#endif
            RaiseAccessProperties();
        }
    }

}

namespace APIShared.ModSettings
{
    /// <summary>Adapts the settings page to the preset protocol without giving it access to UI implementation fields.</summary>
    public abstract partial class PresetLobbyModSettingsViewModel
    {
        private sealed class PresetHost : ILobbyPresetHost
        {
            private readonly PresetLobbyModSettingsViewModel model;
            internal PresetHost(PresetLobbyModSettingsViewModel model) { this.model = model; }
            public object SettingsTarget => model;
            public IDynamicPresetSettingsProvider DynamicSettingsProvider => model.DynamicSettingsProvider;
            public IModSettingsApplicationBackend SettingsApplicationBackend => model.SettingsApplicationBackend;
            public bool IsLocalHost => model.isLocalHost;
            public bool HasMissionContext => model.missionPresetContext;
            public bool MissionPresetEditable => model.missionPresetEditable;
            public bool IsMissionPresetSelected => model.IsMissionPresetSelected;
            public int SelectedPreset => model.selectedPreset;
            public bool IsRetiredPresetProperty(string propertyName) => model.IsRetiredPresetProperty(propertyName);
            public void SetSelectedPresetCore(int value) => model.SetSelectedPresetCore(value);
            public void OnSettingsSnapshotApplied() => model.OnSettingsSnapshotApplied();
        }
    }
}

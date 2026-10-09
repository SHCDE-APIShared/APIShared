namespace APIShared.ModSettings
{
    // Integration boundary for the participant's live values and authority.
    // The controller owns snapshots/storage; the host owns UI, setters and role/context.
    internal interface ILobbyPresetHost
    {
        object SettingsTarget { get; }
        IDynamicPresetSettingsProvider DynamicSettingsProvider { get; }
        IModSettingsApplicationBackend SettingsApplicationBackend { get; }
        bool IsLocalHost { get; }
        bool HasMissionContext { get; }
        bool MissionPresetEditable { get; }
        bool IsMissionPresetSelected { get; }
        int SelectedPreset { get; }
        bool IsRetiredPresetProperty(string propertyName);
        void SetSelectedPresetCore(int value);
        void OnSettingsSnapshotApplied();
    }
}

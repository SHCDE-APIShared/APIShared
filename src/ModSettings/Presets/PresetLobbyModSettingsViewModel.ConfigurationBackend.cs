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
    public abstract partial class PresetLobbyModSettingsViewModel
    {
        /// <summary>Optional dynamically described local working configuration.</summary>
        protected virtual IDynamicPresetSettingsProvider DynamicSettingsProvider => null;
        /// <summary>Optional whole-configuration backend; defaults to the dynamic provider when it implements the application contract.</summary>
        protected virtual IModSettingsApplicationBackend SettingsApplicationBackend => DynamicSettingsProvider as IModSettingsApplicationBackend;
        /// <summary>Whether this model supplies a dynamic schema rather than only reflected properties.</summary>
        public bool System_HasDynamicSettings => DynamicSettingsProvider != null;
        /// <summary>Reads one dynamic setting; requires a DynamicSettingsProvider and a valid schema key.</summary>
        public object System_ReadDescribedValue(string key) => DynamicSettingsProvider.ReadValue(key);
        /// <summary>Whether this model supports separating desired, active and startup-staged configuration.</summary>
        public bool System_HasApplicationBackend => SettingsApplicationBackend != null;
        /// <summary>Current application/restart explanation shown in common preset status UI.</summary>
        public string System_ApplicationNotice { get; private set; } = "";
        /// <summary>Whether startup staging exists; errors keep discard reachable for a corrupt package.</summary>
        public bool System_HasPendingConfiguration
        {
            get
            {
                try { return SettingsApplicationBackend != null && (SettingsApplicationBackend.ReadPendingValues() != null || ModSettingsApplication.HasRestartPreparation); }
                catch { return SettingsApplicationBackend != null; } // Keep discard reachable for a corrupt package.
            }
        }
        /// <summary>Discards shared restart preparation or this participant package and refreshes application state; backend errors propagate.</summary>
        public void System_DiscardPendingConfiguration()
        {
            if (ModSettingsApplication.HasRestartPreparation) ModSettingsApplication.DiscardPreparation();
            else DiscardApplicationPackage();
        }
        internal void DiscardApplicationPackage()
        {
            var backend = SettingsApplicationBackend;
            if (backend == null) return;
            backend.DiscardPendingConfiguration();
            bool returnToOwn = !string.IsNullOrEmpty(backend.ActiveContextId);
            backend.ReplaceDesiredValues(LobbyPresetController.CopyConfigurationValues(returnToOwn ? backend.ReadOwnValues() : backend.ReadActiveValues()));
            if (returnToOwn)
            {
                if (System_GetPresetSettingDescriptors().Any(x => x.RequiresRestart)) backend.ReturnToOwnConfiguration();
                else
                {
                    backend.ApplyValues(backend.ReadDesiredValues(), "");
                    var applied = backend.ReadActiveValues();
                    if (backend.ReadDesiredValues().Any(x => !applied.TryGetValue(x.Key, out var value) || !Equals(value, x.Value)))
                        throw new InvalidOperationException("Configuration backend did not restore personal values.");
                }
            }
            var activeAfterDiscard = backend.ReadActiveValues();
            var desiredAfterDiscard = backend.ReadDesiredValues();
            ReportConfigurationResult(returnToOwn && System_GetPresetSettingDescriptors().Any(x => x.RequiresRestart &&
                (!activeAfterDiscard.TryGetValue(x.PropertyName, out var value) || !Equals(value, desiredAfterDiscard[x.PropertyName]))));
        }
        /// <summary>Commits the current working configuration through the shared application coordinator and updates the restart notice.</summary>
        public void System_CommitConfiguration()
        {
            using (ModSettingsSearchUpdateScope.Begin(this))
            {
                bool restart = ModSettingsApplication.Commit(presetController.TargetGuid);
                ReportConfigurationResult(restart);
            }
        }
        internal void ReportConfigurationResult(bool restart) => SetConfigurationNotice(restart
            ? ResolveSettingsUiTextSafe("Common.RestartRequired", APIShared.Localization.UiText.English("Common.RestartRequired")) : "");
        /// <summary>Updates the application notice and dependent UI bindings; null clears the message.</summary>
        protected void SetConfigurationNotice(string message)
        {
            System_ApplicationNotice = message ?? "";
            RefreshConfigurationBindings();
        }
        internal void RefreshConfigurationBindings()
        {
            OnPropertyChanged(nameof(System_ApplicationNotice));
            OnPropertyChanged(nameof(System_HasPendingConfiguration));
#if !API_SHARED_PRESET_TESTS
            OnPropertyChanged(nameof(System_PresetStatusText));
            OnPropertyChanged(nameof(System_PresetStatusVisibility));
#endif
        }
        internal Dictionary<string, byte[]> CaptureApplicationSnapshot()
        {
            if (SettingsApplicationBackend == null) return System_CreateCurrentWorkingSnapshot();
            return SettingsApplicationBackend.ReadDesiredValues().ToDictionary(x => x.Key,
                x => MessagePackSerializer.Serialize(x.Value.GetType(), x.Value), StringComparer.Ordinal);
        }
        internal Dictionary<string, string> CaptureRestartSources()
        {
#if API_SHARED_PRESET_TESTS
            return new Dictionary<string, string>();
#else
            var sources = new Dictionary<string, string> { ["resetSource"] = selectedSettingsSource?.Id ?? "" };
            // Raw mission status only; the status line is rendered again in the current language.
            if (presetController != null && presetController.TryCaptureMissionStatus(out string sourceLabel, out string presetId, out bool modified))
            {
                sources["sourceLabel"] = sourceLabel;
                sources["basedOnPreset"] = presetId;
                sources["modified"] = modified ? "true" : "false";
            }
            return sources;
#endif
        }
        internal void RestoreRestartSources(Dictionary<string, string> sources)
        {
#if !API_SHARED_PRESET_TESTS
            if (sources.TryGetValue("resetSource", out string selected))
                System_SelectedSettingsSource = settingsSources.FirstOrDefault(x => x.Id == selected) ?? selectedSettingsSource;
            if (sources.TryGetValue("sourceLabel", out string sourceLabel))
                presetController?.RestoreMissionStatus(sourceLabel,
                    sources.TryGetValue("basedOnPreset", out string presetId) ? presetId : "",
                    sources.TryGetValue("modified", out string modified) && modified == "true");
            else if (sources.TryGetValue("label", out string label) && !string.IsNullOrWhiteSpace(label))
                presetController?.RestorePreparationLabel(label); // Journal written before the raw status keys.
            RaiseAccessProperties();
#endif
        }
        /// <summary>Hashes the personal backend snapshot in ordinal key order for restart provenance; empty when no application backend exists.</summary>
        public string System_OwnConfigurationFingerprint()
        {
            if (SettingsApplicationBackend == null) return "";
            var values = SettingsApplicationBackend.ReadOwnValues();
            using (var buffer = new System.IO.MemoryStream())
            using (var writer = new System.IO.BinaryWriter(buffer))
            {
                foreach (var pair in values.OrderBy(x => x.Key, StringComparer.Ordinal))
                {
                    writer.Write(pair.Key);
                    byte[] bytes = MessagePackSerializer.Serialize(pair.Value.GetType(), pair.Value);
                    writer.Write(bytes.Length); writer.Write(bytes);
                }
                writer.Flush();
                return Sha256Hex.Of(buffer.ToArray());
            }
        }
        /// <summary>Copies backend personal values into desired working values without applying them live.</summary>
        public void System_RefreshOwnConfiguration()
        {
            var backend = SettingsApplicationBackend;
            if (backend == null) return;
            var own = LobbyPresetController.CopyConfigurationValues(backend.ReadOwnValues());
            if (!IsMissionPresetSelected) { backend.ReplaceDesiredValues(own); return; }
            var desired = LobbyPresetController.CopyConfigurationValues(backend.ReadDesiredValues());
            foreach (var property in System_GetPresetSettingDescriptors().Where(p => p.MissionIndependent &&
                (p.Scope != PresetSettingScope.Host || isLocalHost)))
                if (own.TryGetValue(property.PropertyName, out var value)) desired[property.PropertyName] = value;
            backend.ReplaceDesiredValues(desired);
        }
        /// <summary>Whether changed restart-marked settings need local startup staging; authenticated network clients defer to their transport.</summary>
        public bool System_ConfigurationNeedsRestart()
        {
            if (SettingsApplicationBackend == null) return false;
            if (SettingsApplicationBackend is INetworkModSettingsApplicationBackend network && network.IsNetworkConfigurationClient)
                return false; // The provider's authenticated transport owns client startup preparation.
            var active = SettingsApplicationBackend.ReadActiveValues();
            var desired = SettingsApplicationBackend.ReadDesiredValues();
            return System_GetPresetSettingDescriptors().Any(x => x.RequiresRestart &&
                (!active.TryGetValue(x.PropertyName, out var value) || !Equals(value, desired[x.PropertyName])));
        }
        /// <summary>Applies desired values live or stages restart-marked changes for contextId. Returns true when restart is needed; backend failures propagate. Empty identity denotes personal configuration.</summary>
        public bool System_ApplyConfiguration(string contextId) => System_ApplyConfiguration(contextId, false);
        internal void PersistPersonalConfiguration() => presetController?.PersistCurrentPersonalValues();

        internal bool System_ApplyConfiguration(string contextId, bool personalChoiceConfirmed)
        {
            var backend = SettingsApplicationBackend;
            if (backend == null) return false;
            PersistPersonalConfiguration();
            if (backend is INetworkModSettingsApplicationBackend network && network.IsNetworkConfigurationClient)
                return !network.PrepareNetworkConfiguration();
            var desired = backend.ReadDesiredValues();
            var active = backend.ReadActiveValues();
            var changed = System_GetPresetSettingDescriptors().Where(x => !active.TryGetValue(x.PropertyName, out var value) || !Equals(value, desired[x.PropertyName])).ToArray();
            if (changed.Length == 0)
            {
                // A temporary configuration can already match the requested personal preset.
                // Preserve that personal choice for future starts without inventing a restart need.
                var own = backend.ReadOwnValues();
                bool persistPersonal = string.IsNullOrEmpty(contextId) && desired.Any(x =>
                    !own.TryGetValue(x.Key, out var value) || !Equals(value, x.Value));
                if (persistPersonal && personalChoiceConfirmed)
                {
                    if (System_GetPresetSettingDescriptors().Any(x => x.RequiresRestart)) backend.StageValues(desired, "");
                    else backend.ApplyValues(desired, "");
                }
                else
                {
                    var pending = backend.ReadPendingValues();
                    bool keepConfirmedPersonalChoice = persistPersonal && pending != null && desired.All(x =>
                        pending.TryGetValue(x.Key, out var value) && Equals(value, x.Value));
                    if (pending != null && !keepConfirmedPersonalChoice) backend.DiscardPendingConfiguration();
                }
                return false;
            }
            if (changed.Any(x => x.RequiresRestart))
            {
                backend.StageValues(desired, contextId);
                return true;
            }
            backend.ApplyValues(desired, contextId);
            var applied = backend.ReadActiveValues();
            if (desired.Any(x => !applied.TryGetValue(x.Key, out var value) || !Equals(value, x.Value)))
                throw new InvalidOperationException("Configuration backend did not apply the requested values.");
            if (backend.ReadPendingValues() != null) backend.DiscardPendingConfiguration();
            return false;
        }
        /// <summary>Restores personal values after temporary context unless a pending restart package must be preserved.</summary>
        public void System_ReturnToOwnConfiguration()
        {
            var backend = SettingsApplicationBackend;
            if (backend == null) return;
            // Leaving for an explicitly prepared restart must not replace that preparation.
            if (backend.ReadPendingValues() != null) return;
            backend.ReplaceDesiredValues(LobbyPresetController.CopyConfigurationValues(backend.ReadOwnValues()));
            if (!string.IsNullOrEmpty(backend.ActiveContextId))
            {
                if (System_GetPresetSettingDescriptors().Any(x => x.RequiresRestart)) backend.ReturnToOwnConfiguration();
                else
                {
                    backend.ApplyValues(backend.ReadDesiredValues(), "");
                    var applied = backend.ReadActiveValues();
                    if (backend.ReadDesiredValues().Any(x => !applied.TryGetValue(x.Key, out var value) || !Equals(value, x.Value)))
                        throw new InvalidOperationException("Configuration backend did not restore personal values.");
                }
            }
        }
    }
}

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
        // Typed mission-preset endpoint used by ExtendedData and other optional coordinators.
        /// <summary>Creates a detached serialized mission snapshot with discovered activation settings disabled; does not apply it.</summary>
        public Dictionary<string, byte[]> System_CreateDisabledMissionPresetSnapshot() =>
            presetController?.CreateDisabledSnapshot() ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Creates a detached serialized snapshot of captured code defaults.</summary>
        public Dictionary<string, byte[]> System_CreateModDefaultSnapshot() =>
            presetController?.CreateDefaultSnapshot() ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Compatibility snapshot entry returning the current working settings, including desired application-backend values; empty before preparation.</summary>
        public Dictionary<string, byte[]> System_CreateCurrentMissionPresetSnapshot() =>
            System_CreateCurrentWorkingSnapshot();

        /// <summary>Creates the personal snapshot used as the player contribution to mission settings.</summary>
        public Dictionary<string, byte[]> System_CreatePlayerMissionPresetSnapshot()
        {
            if (SettingsApplicationBackend != null)
                return SettingsApplicationBackend.ReadOwnValues().ToDictionary(x => x.Key,
                    x => MessagePackSerializer.Serialize(x.Value.GetType(), x.Value), StringComparer.Ordinal);
            return presetController?.CreatePlayerMissionSnapshot() ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);
        }

        /// <summary>Replaces only mission-dependent Host values and the source label. Personal values contained in a snapshot are ignored.</summary>
        public void System_ApplyMissionPresetSnapshot(Dictionary<string, byte[]> snapshot, string label)
        {
            if (explicitModDefaultsReset) presetController?.ApplyExplicitMissionDefaults(snapshot, label);
            else presetController?.ApplyMissionWorkingSnapshot(snapshot, label);
            RaiseAccessProperties();
        }

        /// <summary>Creates a detached serialized snapshot of the currently editable settings.</summary>
        public Dictionary<string, byte[]> System_CreateCurrentWorkingSnapshot() =>
            SettingsApplicationBackend != null ? CaptureApplicationSnapshot() :
            presetController?.CreateCurrentMissionSnapshot() ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Applies a complete working snapshot through the preset controller without replacing its source.</summary>
        public void System_ApplyWorkingSnapshot(Dictionary<string, byte[]> snapshot)
        {
            presetController?.ApplyWorkingSnapshot(snapshot);
            RaiseAccessProperties();
        }

        /// <summary>Loads defaults into the current working settings and commits application state; may stage a restart through the backend. Requires prepared registration and respects editable scope.</summary>
        public void System_LoadModDefaults()
        {
            using (ModSettingsSearchUpdateScope.Begin(this))
            {
                SetConfigurationNotice("");
                var backend = SettingsApplicationBackend;
                var activeBefore = backend?.ReadActiveValues();
                var pendingBefore = backend?.ReadPendingValues();
                if (IsMissionPresetSelected)
                {
                    bool previousReset = explicitModDefaultsReset;
                    explicitModDefaultsReset = true;
                    try { ModSettingsWorkingSourceRegistry.Apply(presetController.TargetGuid, ModSettingsWorkingSourceRegistry.ModDefaultsId); }
                    finally { explicitModDefaultsReset = previousReset; }
                }
                else
                    presetController?.ApplyDefaultsAsWorkingCopy();
                System_CommitConfiguration();
                if (backend != null)
                {
                    var desired = backend.ReadDesiredValues();
                    var pendingAfter = backend.ReadPendingValues();
                    bool activeMatches = ConfigurationValuesEqual(desired, backend.ReadActiveValues());
                    if (pendingBefore != null && pendingAfter == null && activeMatches)
                        SetConfigurationNotice(ResolveSettingsUiTextSafe("Common.DefaultResetDiscarded", APIShared.Localization.UiText.English("Common.DefaultResetDiscarded")));
                    else if (pendingBefore != null && !ConfigurationValuesEqual(pendingBefore, pendingAfter) && pendingAfter != null)
                        SetConfigurationNotice(ResolveSettingsUiTextSafe("Common.DefaultResetReplaced", APIShared.Localization.UiText.English("Common.DefaultResetReplaced")) + " " + (activeMatches
                                ? ResolveSettingsUiTextSafe("Common.DefaultResetPreparedActive", APIShared.Localization.UiText.English("Common.DefaultResetPreparedActive"))
                                : ResolveSettingsUiTextSafe("Common.RestartRequired", APIShared.Localization.UiText.English("Common.RestartRequired"))));
                    else if (activeMatches && pendingAfter != null)
                        SetConfigurationNotice(ResolveSettingsUiTextSafe("Common.DefaultResetPreparedActive", APIShared.Localization.UiText.English("Common.DefaultResetPreparedActive")));
                    else if (activeMatches)
                        SetConfigurationNotice(ConfigurationValuesEqual(activeBefore, desired)
                            ? ResolveSettingsUiTextSafe("Common.DefaultResetUnchanged", APIShared.Localization.UiText.English("Common.DefaultResetUnchanged"))
                            : ResolveSettingsUiTextSafe("Common.DefaultResetApplied", APIShared.Localization.UiText.English("Common.DefaultResetApplied")));
                }
                RaiseAccessProperties();
            }
        }

        private static bool ConfigurationValuesEqual(Dictionary<string, object> left, Dictionary<string, object> right) =>
            left != null && right != null && left.Count == right.Count &&
            left.All(pair => right.TryGetValue(pair.Key, out var value) && Equals(pair.Value, value));

        /// <summary>Records whether mission data supplied explicit settings, controlling the direct-launch notice.</summary>
        public void System_SetExplicitMissionSettings(bool hasExplicitSettings)
        {
            missionPresetHasExplicitSettings = hasExplicitSettings;
            RaiseAccessProperties();
        }

        /// <summary>Enters a temporary mission snapshot with the supplied label and editability; personal presets are preserved.</summary>
        public void System_EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
        {
            if (presetController == null)
                return;

            missionPresetContext = true;
            missionPresetEditable = editable;
            presetController.EnterMissionPreset(snapshot, label, editable);
            RaiseAccessProperties();
        }

        /// <summary>Leaves temporary mission settings and restores the personal preset context.
        /// If restoration throws, the context remains active so Exit can be retried.</summary>
        public void System_ExitMissionPreset()
        {
            if (!missionPresetContext || presetController == null)
                return;

            bool wasEditable = missionPresetEditable;
            bool hadExplicitSettings = missionPresetHasExplicitSettings;
            missionPresetContext = false;
            missionPresetEditable = false;
            missionPresetHasExplicitSettings = false;
            try { presetController.ExitMissionPreset(); }
            catch
            {
                // The controller needs personal scope during restoration. A failed restore
                // must remain reachable by a later Exit rather than looking already complete.
                missionPresetContext = true;
                missionPresetEditable = wasEditable;
                missionPresetHasExplicitSettings = hadExplicitSettings;
                throw;
            }
            RaiseAccessProperties();
        }
    }
}

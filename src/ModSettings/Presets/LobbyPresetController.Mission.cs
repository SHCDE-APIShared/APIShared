using APIShared.Internal;
using BepInEx.Logging;
using MessagePack;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace APIShared.ModSettings
{
    /// <summary>Transient mission attribution and property-change ownership; remote and mission values stay out of personal storage.</summary>
    internal sealed partial class LobbyPresetController
    {
#if API_SHARED_PRESET_TESTS
        public void SwitchTo(int selected)
        {
            selected = NormalizeSelection(selected, owner.HasMissionContext);
            if (!active || owner.SelectedPreset == selected)
                return;
            if (owner.HasMissionContext && !owner.MissionPresetEditable)
                return;

            if (owner.HasMissionContext && selected == MissionPresetIndex)
            {
                activePublishedPreset = null;
                ApplySnapshot(missionPreset, MissionPresetIndex, writeLocalStorage: false);
                LogRoutine($"[{modName}] Restored the active mission preset.");
                return;
            }

            ApplySnapshot(preset1, 0, writeLocalStorage: true);
        }
#endif

        public void EnterMissionPreset(Dictionary<string, byte[]> snapshot, string label, bool editable)
        {
            suspendedPublishedPreset = activePublishedPreset;
            suspendedBasedOnStableId = basedOnStableId;
            suspendedBasedOnName = basedOnName;
            suspendedBasedOnSource = basedOnSource;
            suspendedPresetDirty = presetDirty;
            activePublishedPreset = null;
            missionPresetLabel = label ?? string.Empty;
            missionBasedOnPreset = null;
            missionPresetDirty = false;
            missionPreset = Clone(preset1 ?? defaults);
            Dictionary<string, byte[]> supplied = snapshot ?? CreateDisabledSnapshot();
            foreach (KeyValuePair<string, byte[]> entry in supplied)
                missionPreset[entry.Key] = entry.Value == null ? null : (byte[])entry.Value.Clone();
            ApplySnapshot(missionPreset, MissionPresetIndex, writeLocalStorage: false);
            LogRoutine($"[{modName}] Entered {(editable ? "editable" : "read-only")} mission preset.");
        }

        public void ExitMissionPreset()
        {
            missionPreset = null;
            missionBasedOnPreset = null;
            missionPresetDirty = false;
            basedOnStableId = suspendedBasedOnStableId;
            basedOnName = suspendedBasedOnName;
            basedOnSource = suspendedBasedOnSource;
            presetDirty = suspendedPresetDirty;
            activePublishedPreset = suspendedPublishedPreset != null && publishedPresets.Contains(suspendedPublishedPreset)
                ? suspendedPublishedPreset
                : null;
            ApplySnapshot(preset1, 0, writeLocalStorage: true);
            suspendedPublishedPreset = null;
            missionPresetLabel = string.Empty;
            LogRoutine($"[{modName}] Left mission preset and restored the previous normal preset.");
        }

        public void AfterPropertyChanged(string propertyName)
        {
            if (!active || applying || string.IsNullOrEmpty(propertyName))
                return;

            persistedPropertiesByName.TryGetValue(
                propertyName,
                out PresetPropertyAccessor property);

            // Keep verified host state in the transient Trail snapshot as well.
            // Otherwise switching to a local preset and back would restore the
            // client's stale local Trail value. Never write this branch to disk.
            if (IsNetworkSyncInProgress())
            {
                if (property != null &&
                    owner.IsMissionPresetSelected &&
                    IsHostProperty(property))
                {
                    StoreProperty(missionPreset, property);
                }
                return;
            }

            if (property == null)
                return;

            if (owner.IsMissionPresetSelected)
            {
                if (owner.MissionPresetEditable &&
                    (owner.IsLocalHost || IsClientProperty(property)))
                {
                    StoreProperty(missionPreset, property);
                    if (missionBasedOnPreset != null)
                        missionPresetDirty = true;
                }
                // Mission-owned values remain in memory until the normal preset is restored.
                return;
            }

            // Incoming host values are runtime-only on clients.
            if (IsHostProperty(property) && !owner.IsLocalHost)
            {
                // A local client edit cannot replace the locally owned host preset.
                return;
            }

            if (owner.IsLocalHost || IsClientProperty(property))
            {
                StoreProperty(preset1, property);
                if (basedOnStableId.Length != 0)
                    presetDirty = true;
                WriteCombinedPayload();
            }
        }

    }
}

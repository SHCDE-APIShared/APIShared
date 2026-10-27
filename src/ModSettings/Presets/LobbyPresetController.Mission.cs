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
        private bool personalHostRestorePending;
        // A restoration whose setter rejects even the code default is retried by its owner.
        // The final attempt completes with the other values, so no context stays stuck.
        internal const int MaximumRestoreAttempts = 3;
        private int missionExitFailures, hostRestoreFailures;
        // Set by ApplySnapshot when a retryable cleanup throws because of a failed setter.
        private bool retryableSetterFailure;

        internal void RestorePersonalHostIfPending()
        {
            if (!personalHostRestorePending || applying || owner.HasMissionContext ||
                !owner.CanRestorePersonalHostSettings ||
                GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin == LobbyModSettingsChangeOrigin.InitialLoad) return;
            bool retryable = hostRestoreFailures + 1 < MaximumRestoreAttempts;
            SnapshotApplicationResult result;
            try { result = ApplySnapshot(preset1, 0, writeLocalStorage: false, restoreHost: true, publish: false, retryableCleanup: retryable); }
            catch { if (retryableSetterFailure) hostRestoreFailures++; retryableSetterFailure = false; throw; }
            ReportFinalRestoreAttempt(result, hostRestoreFailures, "personal host settings");
            hostRestoreFailures = 0;
            personalHostRestorePending = false;
        }

        private void ReportFinalRestoreAttempt(SnapshotApplicationResult result, int failedAttempts, string scope)
        {
            if (result.Failed.Count == 0) return;
            DebugLogHelper.LogError(log,
                $"[{modName}] Restored {scope} after {failedAttempts + 1} attempts without " +
                $"[{string.Join(", ", result.Failed.Select(property => property.Name))}]; those settings keep their current values.");
        }

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
            missionContextLabel = missionPresetLabel;
            missionBasedOnPreset = null;
            missionPresetDirty = false;
            missionExitFailures = 0;
            missionPreset = Clone(preset1 ?? defaults);
            Dictionary<string, byte[]> supplied = snapshot ?? CreateDisabledSnapshot();
            foreach (KeyValuePair<string, byte[]> entry in supplied)
                missionPreset[entry.Key] = entry.Value == null ? null : (byte[])entry.Value.Clone();
            ApplySnapshot(missionPreset, MissionPresetIndex, writeLocalStorage: false);
            DebugLogHelper.LogInfo(log,
                $"SETTINGS_CONTEXT: mod={targetGuid}, action=enter, context={SanitizeLogValue(missionContextLabel)}, " +
                $"source={SanitizeLogValue(ModSettingsApplication.ContextId)}, editable={editable}, " +
                $"snapshotProperties={missionPreset.Count}, host={owner.IsLocalHost}; persistence=temporary.");
        }

        public void ExitMissionPreset()
        {
            bool restoreHost = owner.CanRestorePersonalHostSettings;
            string leavingPreset = missionBasedOnPreset?.StableId ?? "";
            string leavingSourceLabel = missionPresetLabel;
            bool leavingModified = missionPresetDirty;
            // Keep the entire mission and its source metadata until restoration
            // succeeds. The session retains failed owners and calls Exit again.
            // A setter that rejects even its default keeps the mission for a bounded number of
            // retries; callback and publication failures stay retryable without a bound.
            int previousSelection = owner.SelectedPreset;
            bool retryable = missionExitFailures + 1 < MaximumRestoreAttempts;
            SnapshotApplicationResult result;
            try
            {
                try { result = ApplySnapshot(preset1, 0, writeLocalStorage: false, restoreHost: restoreHost, publish: false, retryableCleanup: retryable); }
                catch { if (retryableSetterFailure) missionExitFailures++; retryableSetterFailure = false; throw; }
                owner.PublishSettingsSnapshot(clientProperties.Select(property => property.Name).ToArray());
            }
            catch
            {
                LobbySettingsRestoreScope.Execute(() => owner.SetSelectedPresetCore(previousSelection));
                throw;
            }
            ReportFinalRestoreAttempt(result, missionExitFailures, "the normal preset");
            missionExitFailures = 0;
            personalHostRestorePending = !restoreHost;
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
            suspendedPublishedPreset = null;
            DebugLogHelper.LogInfo(log,
                $"SETTINGS_CONTEXT: mod={targetGuid}, action=exit, context={SanitizeLogValue(missionContextLabel)}, " +
                $"source={SanitizeLogValue(ModSettingsApplication.ContextId)}, restoreHost={restoreHost}, " +
                $"leavingPreset={SanitizeLogValue(leavingPreset)}, leavingSourceLabel={SanitizeLogValue(leavingSourceLabel)}, " +
                $"leavingModified={leavingModified}, hostRestorePending={personalHostRestorePending}, " +
                $"personalPreset={SanitizeLogValue(basedOnStableId)}, personalModified={presetDirty}; persistence=personal-values-preserved.");
            missionPresetLabel = missionContextLabel = string.Empty;
        }

        public void AfterPropertyChanged(string propertyName)
        {
            if (!active || applying || string.IsNullOrEmpty(propertyName))
                return;
            if (GameXAMLManagerAPI.Instance.CurrentLobbyModSettingsChangeOrigin == LobbyModSettingsChangeOrigin.InitialLoad)
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

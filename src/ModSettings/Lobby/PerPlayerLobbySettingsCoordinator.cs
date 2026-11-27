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
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
#if !API_SHARED_PRESET_TESTS
using MonoMod.RuntimeDetour;
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
    internal sealed class PerPlayerLobbySettingsCoordinator
    {
        private readonly PresetLobbyModSettingsViewModel owner;
        private readonly ManualLogSource log;
        private readonly string modName;
        private readonly string ownerGuid;
        private readonly PerPlayerLobbySettingsContract contract;
        private readonly HostLobbySettingsPublisher hostPublisher;
        private readonly bool routineLoggingEnabled;
        // Built once from the immutable contract for per-notification lookups; the first
        // setting with a name wins, as in an ordered search.
        private readonly Dictionary<string, PerPlayerLobbySettingContract> settingsByProperty =
            new Dictionary<string, PerPlayerLobbySettingContract>(StringComparer.Ordinal);
        private readonly HashSet<string> dataPropertyNames = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<int, ulong> playersById = new Dictionary<int, ulong>();
        private ulong lobbyId;
        private bool hasLobby;
        private bool publishPending;
        private bool rosterHasUnresolvedPlayers;
        private int resolvedLocalPlayerId;
        private bool isResettingSlots;
        private bool isMirroringLocalSetting;
        private bool isReady = true;
        private string readinessError = string.Empty;
        private bool active;
        internal Func<int, bool> TransportReady { get; set; } = TransportPlayerMatches;
        internal Func<SettingsBroadcastLobby> CaptureBroadcastLobby { get; set; } = CaptureCurrentLobby;
        internal Func<bool> HasHostAuthority { get; set; } = IsLocalHost;
#if !API_SHARED_PRESET_TESTS
        private IDisposable mapStartSubscription;
        private string lastIdentityDiagnostic = string.Empty;
#endif

        internal PerPlayerLobbySettingsCoordinator(
            PresetLobbyModSettingsViewModel owner,
            ManualLogSource log,
            string modName,
            string ownerGuid,
            PerPlayerLobbySettingsContract contract,
            bool logRoutineActivity)
        {
            this.owner = owner;
            this.log = log;
            this.modName = modName;
            this.ownerGuid = ownerGuid;
            this.contract = contract;
            foreach (PerPlayerLobbySettingContract setting in contract.Settings)
            {
                if (!settingsByProperty.ContainsKey(setting.Property.Name))
                    settingsByProperty.Add(setting.Property.Name, setting);
                dataPropertyNames.Add(setting.DataProperty.Name);
            }
            routineLoggingEnabled = logRoutineActivity;
            hostPublisher = new HostLobbySettingsPublisher(owner.GetType(), () => CaptureBroadcastLobby(),
                () => HasHostAuthority(), id => TransportReady(id), owner.System_TriggerUpdate);
        }

        internal bool IsReady => isReady;
        internal string ReadinessError => readinessError;

        internal void Activate()
        {
            if (contract.Settings.Count == 0 && !hostPublisher.HasSettings)
                return;
            if (active)
                return;

            try
            {
                owner.PropertyChanged += OnOwnerPropertyChanged;
#if !API_SHARED_PRESET_TESTS
                // SaveLifecycle: normal multiplayer starts and Platform_Multiplayer.StartSave
                // preserve the lobby snapshot and remap Steam identities to final game slots.
                // Only single-player save loads have no multiplayer lobby convergence.
                mapStartSubscription = APIShared.Internal.MissionEvents.NativeStart.Subscribe(args =>
                {
                    if (args.IsBeforeInitialization)
                    {
                        hostPublisher.Suspend();
                        if (contract.Settings.Count != 0)
                            FinalizeRosterForMapTransition(args.Context.IsSave && args.Context.Mode.IsRealMultiplayer);
                    }
                });
                if (mapStartSubscription == null)
                    throw new InvalidOperationException("The persistent map-start subscription could not be created.");
#endif
                active = true;
                hostPublisher.Activate();
                RequestPublish();
#if !API_SHARED_PRESET_TESTS
#if API_SHARED_LOBBY_OBSERVER
                IApiShared api = ApiShared.Current;
                if (!api.TryGetLobbyState(
                        ownerGuid,
                        out ILobbyStateCapability lobbyState,
                        out NativeCapabilityDiagnostic diagnostic))
                {
                    throw new InvalidOperationException(
                        $"The process-wide lobby-state capability is unavailable: " +
                        $"state={diagnostic?.State}, reason={diagnostic?.Reason}");
                }
                if (!lobbyState.TryRegisterObserver(
                        "per-player-settings",
                        OnLobbyStateChanged,
                        out diagnostic))
                {
                    throw new InvalidOperationException(
                        $"The per-player lobby observer could not be registered: " +
                        $"state={diagnostic?.State}, reason={diagnostic?.Reason}");
                }
#else
                throw new InvalidOperationException(
                    "Per-player lobby settings require the APIShared lobby-state bridge.");
#endif
#endif
#if API_SHARED_PRESET_TESTS || API_SHARED_LOBBY_OBSERVER
                LogRoutine(
                    $"[{modName}] Shared per-player lobby convergence activated: " +
                    $"settings=[{string.Join(",", contract.Settings.Select(item => item.Property.Name))}], " +
                    $"required=[{string.Join(",", contract.Settings.Where(item => item.IsReportRequired).Select(item => item.Property.Name))}].");
#endif
            }
            catch (Exception ex)
            {
                Deactivate();
                NativeApiLog.Error(
                    log,
                    $"[{modName}] Shared per-player lobby convergence activation failed: {ex}");
                throw;
            }
        }

        internal void Deactivate()
        {
            owner.PropertyChanged -= OnOwnerPropertyChanged;
#if !API_SHARED_PRESET_TESTS
            mapStartSubscription?.Dispose();
            mapStartSubscription = null;
#endif
            active = false;
            publishPending = false;
            hostPublisher.Deactivate();
        }

        internal void RequestPublish()
        {
            if (contract.Settings.Count == 0) return;
            publishPending = true;
            if (hasLobby && !rosterHasUnresolvedPlayers &&
                PlayerSlots.IsValidPlayerId(resolvedLocalPlayerId) &&
                playersById.ContainsKey(resolvedLocalPlayerId) && TransportReady(resolvedLocalPlayerId))
            {
                PublishLocalSettings(resolvedLocalPlayerId);
                RequestReadinessRefresh();
            }
        }

        internal bool DeferBroadcast(string propertyName)
        {
            if (hostPublisher.TryDefer(propertyName, out bool defer)) return defer;
            if (propertyName == null || !settingsByProperty.ContainsKey(propertyName)) return false;
            if (active && hasLobby && !rosterHasUnresolvedPlayers &&
                PlayerSlots.IsValidPlayerId(resolvedLocalPlayerId) && playersById.ContainsKey(resolvedLocalPlayerId) &&
                TransportReady(resolvedLocalPlayerId)) return false;
            // Keep the scalar and its ordinary PropertyChanged/persistence path. Only the
            // outbound call waits; Observe republishes all scalars after roster convergence.
            publishPending = true;
            return true;
        }

        private static bool TransportPlayerMatches(int localPlayerId)
        {
#if API_SHARED_PRESET_TESTS
            return true;
#else
            return GameNetworkAPI.IsNetworkedEnvironment() &&
                GameNetworkAPI.GetPlayerIdForSteamId(Steamworks.SteamUser.GetSteamID()) == localPlayerId;
#endif
        }

        private static SettingsBroadcastLobby CaptureCurrentLobby()
        {
#if API_SHARED_PRESET_TESTS
            return default;
#else
            var lobby = Platform_Multiplayer.Instance?.activeLobby;
            return lobby == null ? default : new SettingsBroadcastLobby(lobby,
                lobby.id.m_SteamID == 0 ? (ulong?)null : lobby.id.m_SteamID);
#endif
        }

        private static bool IsLocalHost()
        {
#if API_SHARED_PRESET_TESTS
            return true;
#else
            return GameNetworkAPI.IsLocalHost();
#endif
        }

        internal bool ArePlayersReady(IEnumerable<int> playerIds, out string error)
        {
            int[] supplied = (playerIds ?? Enumerable.Empty<int>()).ToArray();
            if (supplied.Any(id => !PlayerSlots.IsValidPlayerId(id)))
            {
                error = "At least one supplied human player ID is invalid.";
                return false;
            }
            int[] expected = supplied
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
            if (expected.Length == 0)
            {
                error = "No valid human player IDs were supplied.";
                return false;
            }
            if (contract.Settings.Count == 0)
            {
                error = string.Empty;
                return true;
            }
            if (rosterHasUnresolvedPlayers)
            {
                error = "At least one human lobby member has no stable player ID yet.";
                return false;
            }
            if (hasLobby && !expected.SequenceEqual(playersById.Keys.OrderBy(id => id)))
            {
                error = $"The requested human players [{string.Join(",", expected)}] do not match the converged lobby roster [{string.Join(",", playersById.Keys.OrderBy(id => id))}].";
                return false;
            }
            if (hasLobby && (!PlayerSlots.IsValidPlayerId(resolvedLocalPlayerId) || !playersById.ContainsKey(resolvedLocalPlayerId)))
            {
                error = "The local human player ID is not part of the converged lobby roster.";
                return false;
            }

            foreach (PerPlayerLobbySettingContract setting in contract.Settings.Where(item => item.IsReportRequired))
            {
                Array data = setting.GetData();
                foreach (int playerId in expected)
                {
                    object value = data.GetValue(playerId);
                    if (!setting.HasReport(value))
                    {
                        error = $"Player {playerId} has not reported [{setting.Property.Name}].";
                        return false;
                    }
                }
            }

            error = string.Empty;
            return true;
        }

        internal bool RemapForMapTransition(
            IReadOnlyDictionary<int, ulong> finalPlayers,
            int finalLocalPlayerId,
            out string error)
        {
            var normalized = new Dictionary<int, ulong>();
            foreach (KeyValuePair<int, ulong> player in finalPlayers ?? new Dictionary<int, ulong>())
            {
                if (!PlayerSlots.IsValidPlayerId(player.Key) || player.Value == 0 ||
                    normalized.ContainsKey(player.Key) || normalized.ContainsValue(player.Value))
                {
                    error = "The final human roster contains an invalid or duplicate player identity.";
                    SetReadiness(false, error);
                    return false;
                }
                normalized[player.Key] = player.Value;
            }

            ulong[] previousSteamIds = playersById.Values.OrderBy(value => value).ToArray();
            ulong[] finalSteamIds = normalized.Values.OrderBy(value => value).ToArray();
            if (!hasLobby || previousSteamIds.Length == 0 ||
                !previousSteamIds.SequenceEqual(finalSteamIds))
            {
                error = $"The final human roster [{FormatRoster(normalized)}] does not match the " +
                    $"converged lobby identities [{FormatRoster(playersById)}].";
                SetReadiness(false, error);
                return false;
            }
            if (!PlayerSlots.IsValidPlayerId(finalLocalPlayerId) || !normalized.ContainsKey(finalLocalPlayerId))
            {
                error = "The final local player ID is not part of the final human roster.";
                SetReadiness(false, error);
                return false;
            }

            bool changed = normalized.Count != playersById.Count ||
                normalized.Any(player =>
                    !playersById.TryGetValue(player.Key, out ulong previousSteamId) ||
                    previousSteamId != player.Value);
            if (changed)
            {
                foreach (PerPlayerLobbySettingContract setting in contract.Settings)
                {
                    Array data = setting.GetData();
                    var valuesBySteamId = new Dictionary<ulong, object>();
                    foreach (KeyValuePair<int, ulong> previousPlayer in playersById)
                        valuesBySteamId[previousPlayer.Value] = CloneValue(data.GetValue(previousPlayer.Key));
                    for (int playerId = PlayerSlots.FirstPlayerId; playerId <= PlayerSlots.LastPlayerId; playerId++)
                        data.SetValue(CloneValue(setting.CreateResetValue()), playerId);
                    foreach (KeyValuePair<int, ulong> finalPlayer in normalized)
                        data.SetValue(CloneValue(valuesBySteamId[finalPlayer.Value]), finalPlayer.Key);
                }

                playersById.Clear();
                foreach (KeyValuePair<int, ulong> player in normalized)
                    playersById[player.Key] = player.Value;
            }

            rosterHasUnresolvedPlayers = false;
            resolvedLocalPlayerId = finalLocalPlayerId;
            contract.LocalPlayerResolved?.Invoke(finalLocalPlayerId);
            contract.LobbyChanged?.Invoke(new PerPlayerLobbySnapshot(
                lobbyId,
                new Dictionary<int, ulong>(playersById),
                false,
                finalLocalPlayerId));
            if (!ArePlayersReady(playersById.Keys, out error))
            {
                SetReadiness(false, error);
                return false;
            }

            SetReadiness(true, string.Empty);
            if (changed)
            {
                LogRoutine(
                    $"[{modName}] Shared personal settings remapped to final game slots: " +
                    $"players=[{FormatRoster(playersById)}], localPlayerId={finalLocalPlayerId}.");
            }
            error = string.Empty;
            return true;
        }

        internal void Observe(
            ulong? currentLobbyId,
            IReadOnlyDictionary<int, ulong> currentPlayers,
            bool hasUnresolvedPlayers,
            int localPlayerId,
            bool preserveForMapTransition)
        {
            owner.System_RefreshSettingsAccess();
            hostPublisher.InvalidateObservation();
            if (!currentLobbyId.HasValue)
            {
                hostPublisher.Observe(null, 0, false);
                if (preserveForMapTransition)
                    return;

                if (hasLobby || playersById.Count != 0)
                {
                    ResetSlots(Enumerable.Range(PlayerSlots.FirstPlayerId, PlayerSlots.Count));
                    hasLobby = false;
                    lobbyId = 0;
                    playersById.Clear();
                    rosterHasUnresolvedPlayers = false;
                    resolvedLocalPlayerId = 0;
                    publishPending = false;
                    contract.LobbyChanged?.Invoke(PerPlayerLobbySnapshot.Empty);
                }
                SetReadiness(true, string.Empty);
                return;
            }

            // Domain observers run in the lobby only. Settings are immutable once
            // the map starts, so no file/status refresh may publish into a match.
            contract.Observe?.Invoke();

            var normalized = new Dictionary<int, ulong>();
            foreach (KeyValuePair<int, ulong> player in currentPlayers ?? new Dictionary<int, ulong>())
            {
                if (PlayerSlots.IsValidPlayerId(player.Key) && player.Value != 0)
                    normalized[player.Key] = player.Value;
            }

            bool sessionChanged = !hasLobby || lobbyId != currentLobbyId.Value;
            bool membershipChanged = sessionChanged ||
                normalized.Count != playersById.Count ||
                normalized.Any(player =>
                    !playersById.TryGetValue(player.Key, out ulong previousSteamId) ||
                    previousSteamId != player.Value);
            bool resolutionChanged = rosterHasUnresolvedPlayers != hasUnresolvedPlayers ||
                resolvedLocalPlayerId != localPlayerId;
            if (membershipChanged)
            {
                int[] slotsToReset = sessionChanged
                    ? Enumerable.Range(PlayerSlots.FirstPlayerId, PlayerSlots.Count).ToArray()
                    : Enumerable.Range(PlayerSlots.FirstPlayerId, PlayerSlots.Count)
                        .Where(id =>
                            playersById.TryGetValue(id, out ulong previousSteamId) &&
                            (!normalized.TryGetValue(id, out ulong currentSteamId) ||
                             currentSteamId != previousSteamId))
                        .ToArray();
                ResetSlots(slotsToReset);
                hasLobby = true;
                lobbyId = currentLobbyId.Value;
                playersById.Clear();
                foreach (KeyValuePair<int, ulong> player in normalized)
                    playersById[player.Key] = player.Value;
                publishPending = true;
                LogRoutine(
                    $"[{modName}] Shared per-player lobby roster changed: lobby={currentLobbyId.Value}, " +
                    $"sessionChanged={sessionChanged}, players=[{string.Join(",", normalized.Keys.OrderBy(id => id))}], " +
                    $"unresolved={hasUnresolvedPlayers}, resetSlots=[{string.Join(",", slotsToReset)}].");
            }

            bool localResolved = PlayerSlots.IsValidPlayerId(localPlayerId) && normalized.ContainsKey(localPlayerId);
            rosterHasUnresolvedPlayers = hasUnresolvedPlayers;
            resolvedLocalPlayerId = localPlayerId;
            if (membershipChanged || resolutionChanged)
            {
                contract.LobbyChanged?.Invoke(new PerPlayerLobbySnapshot(
                    currentLobbyId,
                    new Dictionary<int, ulong>(normalized),
                    hasUnresolvedPlayers,
                    localPlayerId));
            }
            hostPublisher.Observe(currentLobbyId, localPlayerId, localResolved);
            if (contract.Settings.Count == 0) return;
            if (publishPending && localResolved && !hasUnresolvedPlayers && TransportReady(localPlayerId))
                PublishLocalSettings(localPlayerId);

            if (hasUnresolvedPlayers)
                SetReadiness(false, "At least one human lobby member has no stable player ID yet.");
            else if (!localResolved)
                SetReadiness(false, "The local human player ID is not part of the resolved lobby roster yet.");
            else if (!ArePlayersReady(normalized.Keys, out string error))
                SetReadiness(false, error);
            else
                SetReadiness(true, string.Empty);
        }

        private void PublishLocalSettings(int localPlayerId)
        {
            publishPending = false;
            contract.BeforePublish?.Invoke();
            contract.LocalPlayerResolved?.Invoke(localPlayerId);
            foreach (PerPlayerLobbySettingContract setting in contract.Settings)
            {
                Array data = setting.GetData();
                data.SetValue(CloneValue(setting.Property.GetValue(owner)), localPlayerId);
                owner.System_TriggerUpdate(setting.Property.Name);
            }
            contract.Published?.Invoke();
            LogRoutine(
                $"[{modName}] Shared personal settings advertised for playerId={localPlayerId}, " +
                $"properties={contract.Settings.Count}.");
        }

        private void ResetSlots(IEnumerable<int> playerIds)
        {
            int[] slots = (playerIds ?? Enumerable.Empty<int>())
                .Where(PlayerSlots.IsValidPlayerId)
                .Distinct()
                .ToArray();
            if (slots.Length == 0)
                return;

            foreach (PerPlayerLobbySettingContract setting in contract.Settings)
            {
                Array data = setting.GetData();
                foreach (int playerId in slots)
                    data.SetValue(CloneValue(setting.CreateResetValue()), playerId);
                isResettingSlots = true;
                try
                {
                    owner.System_TriggerUpdate(setting.DataProperty.Name);
                }
                finally
                {
                    isResettingSlots = false;
                }
            }
        }

        private void SetReadiness(bool value, string error)
        {
            error = error ?? string.Empty;
            if (isReady == value && string.Equals(readinessError, error, StringComparison.Ordinal))
                return;
            isReady = value;
            readinessError = error;
            owner.System_TriggerUpdate(nameof(PresetLobbyModSettingsViewModel.IsPerPlayerLobbySettingsReady));
            owner.System_TriggerUpdate(nameof(PresetLobbyModSettingsViewModel.PerPlayerLobbySettingsReadinessError));
        }

        private void OnOwnerPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (string.IsNullOrEmpty(args?.PropertyName))
                return;
            if (dataPropertyNames.Contains(args.PropertyName))
            {
                if (isResettingSlots || isMirroringLocalSetting)
                    return;
                contract.RemoteDataChanged?.Invoke(args.PropertyName);
                RequestReadinessRefresh();
                return;
            }

            if (settingsByProperty.TryGetValue(args.PropertyName, out PerPlayerLobbySettingContract localSetting) && hasLobby &&
                PlayerSlots.IsValidPlayerId(resolvedLocalPlayerId) &&
                playersById.ContainsKey(resolvedLocalPlayerId))
            {
                // The transport does not echo a sender's packet back to itself. Keep
                // the local companion slot authoritative in Shared so individual mods
                // never need to resolve or guess their own player ID in a setter.
                localSetting.GetData().SetValue(
                    CloneValue(localSetting.Property.GetValue(owner)),
                    resolvedLocalPlayerId);
                isMirroringLocalSetting = true;
                try
                {
                    owner.System_TriggerUpdate(localSetting.DataProperty.Name);
                }
                finally
                {
                    isMirroringLocalSetting = false;
                }
                RequestReadinessRefresh();
            }
        }

        private void RequestReadinessRefresh()
        {
            if (!hasLobby || contract.Settings.Count == 0)
                return;
            if (!ArePlayersReady(playersById.Keys, out string error))
                SetReadiness(false, error);
            else
                SetReadiness(true, string.Empty);
        }

        internal void ObserveError(string error)
        {
            hostPublisher.InvalidateObservation();
            if (contract.Settings.Count != 0) SetReadiness(false, error);
        }

#if !API_SHARED_PRESET_TESTS
        private void FinalizeRosterForMapTransition(bool multiplayerSave)
        {
            if (!hasLobby || !GameModeHelper.IsRealMultiplayer(multiplayerSave))
                return;

            if (!PlayerIdentityHelper.TryCaptureHumanRoster(
                out Dictionary<int, ulong> finalPlayers,
                out string rosterError))
            {
                SetReadiness(false, rosterError);
                NativeApiLog.Error(
                    log,
                    $"[{modName}] Shared final player roster resolution failed; dependent gameplay must remain blocked: {rosterError}");
                return;
            }

            PlayerIdentityResolution localIdentity =
                PlayerIdentityHelper.CaptureLocalPlayerId(realMultiplayer: true);
            ReportIdentityDiagnostic(localIdentity);
            if (!localIdentity.IsResolved)
            {
                SetReadiness(false, localIdentity.Error);
                NativeApiLog.Error(
                    log,
                    $"[{modName}] Shared final local player resolution failed; dependent gameplay must remain blocked: {localIdentity.Error}");
                return;
            }

            if (!RemapForMapTransition(finalPlayers, localIdentity.PlayerId, out string remapError))
            {
                NativeApiLog.Error(
                    log,
                    $"[{modName}] Shared per-player map-transition remap failed; dependent gameplay must remain blocked: {remapError}");
            }
        }

#if API_SHARED_LOBBY_OBSERVER
        private void OnLobbyStateChanged(LobbyStateSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            if (!string.IsNullOrEmpty(snapshot.Error))
            {
                ObserveError(snapshot.Error);
                ReportIdentityDiagnostic(new PlayerIdentityResolution(
                    0,
                    false,
                    snapshot.Error,
                    snapshot.Diagnostic));
                return;
            }
            if (!string.IsNullOrEmpty(snapshot.Diagnostic))
            {
                ReportIdentityDiagnostic(new PlayerIdentityResolution(
                    snapshot.LocalPlayerId,
                    true,
                    string.Empty,
                    snapshot.Diagnostic));
            }
            Observe(
                snapshot.LobbyId,
                snapshot.Players,
                snapshot.HasUnresolvedPlayers,
                snapshot.LocalPlayerId,
                snapshot.PreserveForMapTransition);
        }
#endif

        private void ReportIdentityDiagnostic(PlayerIdentityResolution identity)
        {
            string diagnostic = identity.IsResolved ? identity.Diagnostic : identity.Error;
            if (string.IsNullOrEmpty(diagnostic) ||
                string.Equals(lastIdentityDiagnostic, diagnostic, StringComparison.Ordinal))
                return;
            lastIdentityDiagnostic = diagnostic;
            NativeApiLog.Error(
                log,
                $"[{modName}] Shared player identity source mismatch: {diagnostic}");
        }

#endif

        private void LogRoutine(string message)
        {
            if (routineLoggingEnabled)
                NativeApiLog.Debug(log, message);
        }

        private static string FormatRoster(IReadOnlyDictionary<int, ulong> players) =>
            string.Join(",", (players ?? new Dictionary<int, ulong>())
                .OrderBy(player => player.Key)
                .Select(player => $"{player.Key}:{player.Value}"));

        internal static object CloneValue(object value)
        {
            if (!(value is Array source))
                return value;
            Array clone = (Array)source.Clone();
            for (int index = 0; index < clone.Length; index++)
            {
                if (clone.GetValue(index) is Array nested)
                    clone.SetValue(CloneValue(nested), index);
            }
            return clone;
        }
    }
}

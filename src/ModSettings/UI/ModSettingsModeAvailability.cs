using APIShared.GameModes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace APIShared.ModSettings
{
    /// <summary>Presentation decision only. Unknown must not be presented as permission to run gameplay code.</summary>
    public enum SettingModeStatus
    {
        /// <summary>No unambiguous target, or possible contexts disagree.</summary>
        Unknown,
        /// <summary>Every possible target permits this setting.</summary>
        Active,
        /// <summary>Every possible target excludes this setting.</summary>
        Inactive
    }

    /// <summary>Observable per-setting presentation state; only the owning availability collection changes it.</summary>
    public sealed class SettingModeState : INotifyPropertyChanged
    {
        internal SettingModeState() { }
        /// <summary>Current presentation status. This does not include the configured value or editing authority.</summary>
        public SettingModeStatus Status { get; private set; }
        /// <summary>Whether a setting should receive the inactive-mode background.</summary>
        public bool IsInactive => Status == SettingModeStatus.Inactive;
        /// <summary>Raised synchronously on the UI thread after a real status change.</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        internal void Set(SettingModeStatus status)
        {
            if (Status == status) return;
            Status = status;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsInactive)));
        }
    }

    /// <summary>Optional UI-thread collection of stable, owner-local setting/group keys and profiles. No GUID whitelist, game writes, persistence or editing restrictions.</summary>
    public sealed class ModSettingsModeAvailability : INotifyPropertyChanged
    {
        private readonly Dictionary<string, GameplayModActivationProfile?> rules = new Dictionary<string, GameplayModActivationProfile?>(StringComparer.Ordinal);
        private readonly Dictionary<string, SettingModeState> states = new Dictionary<string, SettingModeState>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> networkVariants = new Dictionary<string, bool>(StringComparer.Ordinal);
        private GameplayModActivationProfile? defaultProfile;
        private GameModeSnapshot? mission;
        private GameplayModAllowedContext candidates;
        private bool? multiplayer;

        /// <summary>Raised after presentation refresh on the calling UI thread; no implicit dispatch.</summary>
        public event PropertyChangedEventHandler PropertyChanged;
        /// <summary>Whether any materialized setting state is known to be inactive.</summary>
        public bool HasInactive => states.Values.Any(s => s.IsInactive);

        /// <summary>Sets the profile used for otherwise unregistered keys. Null makes such keys neutral. Configure before binding the page.</summary>
        public void ConfigureDefault(GameplayModActivationProfile? profile) { defaultProfile = profile; Refresh(); }
        /// <summary>Assigns an explicit setting/group rule, replacing a prior rule for this key. Null excludes local comfort/presentation settings from mode marking. Nonblank keys are required.</summary>
        public void Configure(string key, GameplayModActivationProfile? profile)
        {
            ValidateKey(key); rules[key] = profile; Refresh();
        }
        /// <summary>Marks a separately configured singleplayer/multiplayer value as applicable only to that network variant. Null removes this additional UI condition; the consumer still owns runtime selection of the value.</summary>
        public void ConfigureNetworkVariant(string key, bool? requiresRealMultiplayer)
        {
            ValidateKey(key);
            if (requiresRealMultiplayer.HasValue) networkVariants[key] = requiresRealMultiplayer.Value;
            else networkVariants.Remove(key);
            Refresh();
        }
        /// <summary>Gets a stable observable state for a key, creating it if necessary. UI-thread only; all keys are local to this collection.</summary>
        public SettingModeState GetState(string key)
        {
            ValidateKey(key);
            if (!states.TryGetValue(key, out SettingModeState state))
            {
                states.Add(key, state = new SettingModeState()); state.Set(Evaluate(key));
                if (state.IsInactive) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasInactive)));
            }
            return state;
        }
        /// <summary>Uses authoritative mission evidence, including unknown/conflicting evidence. Call on the UI thread; no gameplay activation occurs.</summary>
        public void SetMission(GameModeSnapshot snapshot) { mission = snapshot; Refresh(); }
        /// <summary>Sets possible front-end launch contexts. None means no selected target. Null multiplayer keeps network identity undecided. Mixed allowed/blocked candidates remain neutral.</summary>
        public void SetPreview(GameplayModAllowedContext possibleContexts, bool? isRealMultiplayer)
        { mission = null; candidates = possibleContexts; multiplayer = isRealMultiplayer; Refresh(); }

        private SettingModeStatus Evaluate(string key)
        {
            GameplayModActivationProfile? rule = rules.TryGetValue(key, out var explicitRule) ? explicitRule : defaultProfile;
            if (!rule.HasValue) return SettingModeStatus.Unknown;
            SettingModeStatus status;
            if (mission.HasValue)
            {
                if (mission.Value.Kind == GameModeKind.Unknown) return SettingModeStatus.Unknown;
                status = GameplayModModePolicy.IsAllowed(rule.Value, mission.Value, out _) ? SettingModeStatus.Active : SettingModeStatus.Inactive;
            }
            else
            {
                if (candidates == GameplayModAllowedContext.None) return SettingModeStatus.Unknown;
                bool anyAllowed = (rule.Value.AllowedContexts & candidates) != 0;
                if (!anyAllowed || (multiplayer == true && !rule.Value.AllowRealMultiplayer)) return SettingModeStatus.Inactive;
                status = (rule.Value.AllowedContexts & candidates) != candidates || (!multiplayer.HasValue && !rule.Value.AllowRealMultiplayer)
                    ? SettingModeStatus.Unknown : SettingModeStatus.Active;
            }
            // A known mode exclusion stays authoritative even if network identity is unavailable.
            if (status == SettingModeStatus.Inactive) return status;
            bool? network = mission.HasValue ? mission.Value.IsRealMultiplayer : multiplayer;
            if (networkVariants.TryGetValue(key, out bool required))
            {
                if (!network.HasValue) return SettingModeStatus.Unknown;
                if (network.Value != required) return SettingModeStatus.Inactive;
            }
            return status;
        }
        private void Refresh()
        {
            foreach (var entry in states.ToArray()) entry.Value.Set(Evaluate(entry.Key));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasInactive)));
        }
        private static void ValidateKey(string key)
        { if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("A nonblank owner-local setting key is required.", nameof(key)); }
    }
}

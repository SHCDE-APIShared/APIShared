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
        /// <summary>Localized XAML caption/help for Common.HostOptions; English fallback: HOST OPTIONS</summary>
        public string HostOptionsText =>
            ResolveSettingsUiTextSafe("Common.HostOptions", APIShared.Localization.UiText.English("Common.HostOptions"));

        /// <summary>Localized XAML caption/help for Common.ClientOptions; English fallback: LOCAL CLIENT OPTIONS</summary>
        public string ClientOptionsText =>
            ResolveSettingsUiTextSafe("Common.ClientOptions", APIShared.Localization.UiText.English("Common.ClientOptions"));

        /// <summary>Localized XAML caption/help for Common.Preset; English fallback: Preset</summary>
        public string PresetText =>
            ResolveSettingsUiTextSafe("Common.Preset", APIShared.Localization.UiText.English("Common.Preset"));

        /// <summary>Localized XAML caption/help for Common.EnableMod; English fallback: Enable Mod</summary>
        public string ModEnabledText =>
            ResolveSettingsUiTextSafe("Common.EnableMod", APIShared.Localization.UiText.English("Common.EnableMod"));

        /// <summary>Localized XAML caption/help for Common.HostActivationLabel; English fallback: (Host-)</summary>
        public string HostActivationLabelText =>
            ResolveSettingsUiTextSafe("Common.HostActivationLabel", APIShared.Localization.UiText.English("Common.HostActivationLabel"));

        /// <summary>Localized XAML caption/help for Common.ClientActivationLabel; English fallback: (Client settings)</summary>
        public string ClientActivationLabelText =>
            ResolveSettingsUiTextSafe("Common.ClientActivationLabel", APIShared.Localization.UiText.English("Common.ClientActivationLabel"));

        /// <summary>Localized XAML caption/help for Common.ActionsScopeHost; English fallback: Loading a preset or resetting settings affects host settings and your local client settings.</summary>
        public string ActionsScopeNoticeText =>
            HasHostSettings && isLocalHost
                ? ResolveSettingsUiTextSafe("Common.ActionsScopeHost", APIShared.Localization.UiText.English("Common.ActionsScopeHost"))
                : ResolveSettingsUiTextSafe("Common.ActionsScopeClient", APIShared.Localization.UiText.English("Common.ActionsScopeClient"));

        /// <summary>Localized XAML caption/help for Common.HostReadOnly; English fallback: Values from host - read-only</summary>
        public string HostReadOnlyNoticeText =>
            ResolveSettingsUiTextSafe("Common.HostReadOnly", APIShared.Localization.UiText.English("Common.HostReadOnly"));

        /// <summary>Localized XAML caption/help for Common.EnableModHelp; English fallback: Enables or disables this mod for the match.</summary>
        public string EnableModHelpText =>
            ResolveSettingsUiTextSafe("Common.EnableModHelp", APIShared.Localization.UiText.English("Common.EnableModHelp"));

        /// <summary>Localized XAML caption/help for Common.HostSettingsActivationHelp; English fallback: Enables or disables all host-controlled settings of this mod.</summary>
        public string HostSettingsActivationHelpText =>
            ResolveSettingsUiTextSafe("Common.HostSettingsActivationHelp", APIShared.Localization.UiText.English("Common.HostSettingsActivationHelp"));

        /// <summary>Localized XAML caption/help for Common.ClientSettingsActivationHelp; English fallback: Enables or disables all local and personal client settings of this mod.</summary>
        public string ClientSettingsActivationHelpText =>
            ResolveSettingsUiTextSafe("Common.ClientSettingsActivationHelp", APIShared.Localization.UiText.English("Common.ClientSettingsActivationHelp"));

        /// <summary>Localized XAML caption/help for Common.PresetHelp; English fallback: Loads saved settings as an editable working copy.</summary>
        public string PresetHelpText =>
            ResolveSettingsUiTextSafe("Common.PresetHelp", APIShared.Localization.UiText.English("Common.PresetHelp"));

        /// <summary>Override to translate common UI keys; return fallback for unknown keys. Called by Unity-thread bindings; the safe wrapper tolerates empty results and exceptions.</summary>
        protected virtual string ResolveSettingsUiText(string key, string fallback) => APIShared.Localization.UiText.GetOrDefault(key, fallback);

        private string ResolveSettingsUiTextSafe(string key, string fallback)
        {
            string resolved;
            try { resolved = ResolveSettingsUiText(key, fallback); }
            catch { resolved = null; } // A consumer's broken translation must not break the shared UI.
            return string.IsNullOrWhiteSpace(resolved) || string.Equals(resolved, key, StringComparison.Ordinal)
                ? APIShared.Localization.UiText.GetOrDefault(key, fallback)
                : resolved;
        }
    }
}

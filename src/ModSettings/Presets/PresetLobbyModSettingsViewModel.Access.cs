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
        /// <summary>Whether the prepared schema contains host-owned settings.</summary>
        public bool HasHostSettings => presetController?.HasHostSettings ?? false;

        /// <summary>Whether the prepared schema contains per-player settings.</summary>
        public bool HasClientSettings => presetController?.HasClientSettings ?? false;

        /// <summary>Whether the schema exposes a host activation property.</summary>
        public bool HasHostSettingsActivation => presetController?.HasHostSettingsActivation ?? false;

        /// <summary>Whether the schema exposes a per-player activation property.</summary>
        public bool HasClientSettingsActivation => presetController?.HasClientSettingsActivation ?? false;

        /// <summary>Host activation value managed by the controller; unavailable before preparation.</summary>
        public bool HostSettingsEnabled
        {
            get => presetController?.HostSettingsEnabled ?? false;
            set => presetController?.SetHostSettingsEnabled(value);
        }

        /// <summary>Local player activation value managed by the controller; unavailable before preparation.</summary>
        public bool ClientSettingsEnabled
        {
            get => presetController?.ClientSettingsEnabled ?? false;
            set => presetController?.SetClientSettingsEnabled(value);
        }

        /// <summary>Opacity for the host options content: 0.65 when its activation switch is off, otherwise 1. Bind only the content container, leaving activation and preset actions outside it.</summary>
        /// <remarks>Presentation only; does not change editing rights, setting values or runtime activation. A schema without a host activation switch remains fully opaque. Read on the Unity UI thread.</remarks>
        public float HostSettingsOpacity => HasHostSettingsActivation && !HostSettingsEnabled ? 0.65f : 1f;

        /// <summary>Opacity for the personal options content: 0.65 when its activation switch is off, otherwise 1. Bind only the content container, leaving activation and preset actions outside it.</summary>
        /// <remarks>Presentation only; does not change editing rights, setting values or runtime activation. A schema without a client activation switch remains fully opaque. Read on the Unity UI thread.</remarks>
        public float ClientSettingsOpacity => HasClientSettingsActivation && !ClientSettingsEnabled ? 0.65f : 1f;

        /// <summary>Whether the latest observed context identifies this peer as settings host.</summary>
        public bool IsLocalSettingsHost => isLocalHost;

        /// <summary>Whether the observed context is a real multiplayer session.</summary>
        public bool IsRealMultiplayerContext => isRealMultiplayer;

        /// <summary>Whether the active mission snapshot permits editing.</summary>
        public bool MissionPresetEditable => missionPresetEditable;

        /// <summary>Whether the mission snapshot is the currently selected preset.</summary>
        public bool IsMissionPresetSelected => missionPresetContext && selectedPreset == (presetController?.MissionPresetIndex ?? 2);

        /// <summary>Shows a configured consumer notice for direct campaign/trail entry when no explicit trail settings supersede it.</summary>
        public Visibility System_DirectLaunchNoticeVisibility =>
            directLaunchNoticeProvider != null &&
            (settingsMenuContext == SettingsMenuContext.Campaign ||
             (settingsMenuContext == SettingsMenuContext.DirectTrail &&
              (!missionPresetContext || !missionPresetHasExplicitSettings)))
                ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Invokes the configured notice provider on the reader thread; empty results or errors preserve the last successful message.</summary>
        public string System_DirectLaunchNoticeText
        {
            get
            {
                try
                {
                    string message = directLaunchNoticeProvider?.Invoke();
                    if (!string.IsNullOrWhiteSpace(message)) lastDirectLaunchNotice = message;
                }
                catch { /* A consumer's localization failure preserves the last readable notice. */ }
                return lastDirectLaunchNotice;
            }
        }

        /// <summary>Shows the read-only trail-source explanation when mission settings cannot be edited.</summary>
        public Visibility System_TrailSourceNoticeVisibility =>
            settingsMenuContext == SettingsMenuContext.DirectTrail &&
            missionPresetContext && !missionPresetEditable && missionPresetHasExplicitSettings
                ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Localized XAML caption/help for Common.TrailSourceReadOnlyNotice; English fallback: These settings come from the selected Trail and are read-only here. Use Customize to change them.</summary>
        public string System_TrailSourceNoticeText =>
            ResolveSettingsUiTextSafe("Common.TrailSourceReadOnlyNotice", APIShared.Localization.UiText.English("Common.TrailSourceReadOnlyNotice"));

        /// <summary>Whether host-owned fields may be edited in the current authority and mission context; use as an XAML gate as well as guarding setters.</summary>
        public bool CanEditHostSettings =>
            isLocalHost && (!IsMissionPresetSelected || missionPresetEditable);

        /// <summary>Personal fields are independent of mission-source editability; normal setter authority still applies.</summary>
        public bool CanEditClientSettings => true;

        /// <summary>Whether this peer owns mission-independent Host properties. Use outside a mission-disabled host container.</summary>
        public bool CanEditMissionIndependentHostSettings => isLocalHost;

        /// <summary>Whether host activation controls are available under the current editing policy.</summary>
        public bool CanToggleHostSettings =>
            HasHostSettings && HasHostSettingsActivation &&
            (presetController?.HostActivationMissionIndependent == true ? CanEditMissionIndependentHostSettings : CanEditHostSettings);

        /// <summary>Whether per-player activation controls are available under the current editing policy.</summary>
        public bool CanToggleClientSettings =>
            HasClientSettings && HasClientSettingsActivation && CanEditClientSettings;

        /// <summary>Whether preset selection is allowed in the current mission context.</summary>
        public bool CanChangePreset =>
            (!IsMissionPresetSelected || missionPresetEditable) && (isLocalHost || HasClientSettings);

        /// <summary>Whether at least one editable host or player settings scope can be reset.</summary>
        public bool CanResetSettings => (!IsMissionPresetSelected || missionPresetEditable) &&
            (CanEditHostSettings || (HasClientSettings && CanEditClientSettings));

        /// <summary>Shows the preset selector when settings exist and the current context permits it.</summary>
        public Visibility PresetVisibility =>
            missionPresetContext || isLocalHost || HasClientSettings
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>Shows separate client activation controls when the schema and context require them.</summary>
        public Visibility ClientSettingsActivationVisibility =>
            HasClientSettingsActivation
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>Shows the explanation when this peer cannot edit host fields.</summary>
        public Visibility HostReadOnlyNoticeVisibility =>
            HasHostSettings && isRealMultiplayer && !isLocalHost
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>Shows the notice that shared actions affect only the locally editable scope.</summary>
        public Visibility ActionsScopeNoticeVisibility =>
            isRealMultiplayer && HasClientSettings
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Compatibility alias for older views. New XAML binds host and client
        // sections separately so multiplayer and Trail locks remain independent.
        /// <summary>Compatibility XAML alias for CanEditHostSettings; per-player controls should use CanEditClientSettings.</summary>
        public bool AreSettingsEditable => CanEditHostSettings;

        /// <summary>Whether a temporary mission preset context is active, independently of which preset is selected.</summary>
        public bool IsMissionPresetActive => missionPresetContext;

        /// <summary>
        /// Authorizes a settings mutation before any backing state is changed.
        /// Preset and Trail snapshots are trusted internal applications; all other
        /// writes use the Script Extender's ownership gate.
        /// </summary>
        protected bool CanMutateSetting([CallerMemberName] string propertyName = null)
        {
            if (presetController?.IsApplyingSnapshot == true)
                return true;

            // The Extender reaches the setter only after it has verified the packet's
            // sender and opened its authorised-update scope. A read-only Trail locks
            // local edits, but must not reject that authoritative host state.
            if (LobbyPresetController.IsNetworkSyncInProgress())
                return CanEdit(propertyName);

            System_RefreshSettingsAccess();
            if (IsMissionPresetSelected && !missionPresetEditable &&
                presetController?.IsMissionIndependentPropertyName(propertyName) != true)
            {
                NotifyRejectedProperty(propertyName);
                return false;
            }

            return CanEdit(propertyName);
        }

        /// <summary>
        /// Also refreshes editable proxy properties after a rejected write. The
        /// Extender's private revert path keeps these notifications out of sync
        /// and storage just like the primary property notification.
        /// </summary>
        protected bool CanMutateSettingWithDependents(
            string propertyName,
            params string[] dependentPropertyNames)
        {
            if (CanMutateSetting(propertyName))
                return true;

            if (dependentPropertyNames == null)
                return false;

            foreach (string dependentPropertyName in dependentPropertyNames)
            {
                if (!string.IsNullOrEmpty(dependentPropertyName) &&
                    !string.Equals(propertyName, dependentPropertyName, StringComparison.Ordinal))
                {
                    NotifyRejectedProperty(dependentPropertyName);
                }
            }

            return false;
        }

        private void NotifyRejectedProperty(string propertyName)
        {
            if (NotifyRevertMethod != null && !string.IsNullOrEmpty(propertyName))
                NotifyRevertMethod.Invoke(this, new object[] { propertyName });
        }

        // Access refresh rule. The refresh reads only game state (network role, real multiplayer,
        // front-end menu), the pending personal host restore and mode availability; no setting value
        // of this model is an input. Every notification still refreshes, because the refresh also
        // detects role and menu changes, except a derived output that this model raises itself
        // (access gates, mode and status notices). Those follow a refresh or an explicit context
        // change, and the raising scope refreshes once after its last output. A refresh requested
        // while one runs is dropped: it would re-read the same state the running one owns.
        private bool refreshingSettingsAccess;
        private bool settingsAccessObserved;
        private int derivedAccessOutputScope;
        private string raisingDerivedAccessOutput;
        private int completedSettingsAccessRefreshes;
#if API_SHARED_PRESET_TESTS
        // The harness cannot read the Extender's network singleton; null keeps the production read.
        internal Func<bool> System_TestObservedLocalHost;
#endif

        private void OnOwnPropertyChangedForAccess(object sender, PropertyChangedEventArgs args)
        {
            // Notifications nested in an observer of a derived output still refresh.
            if (raisingDerivedAccessOutput == null ||
                !string.Equals(args?.PropertyName, raisingDerivedAccessOutput, StringComparison.Ordinal))
                System_RefreshSettingsAccess();
        }

        private void RaiseDerivedAccessOutput(string propertyName)
        {
            string previous = raisingDerivedAccessOutput;
            raisingDerivedAccessOutput = propertyName;
            try { base.OnPropertyChanged(propertyName); }
            finally { raisingDerivedAccessOutput = previous; }
        }

        // Ends a derived-output scope. Outside a running refresh one refresh follows, taking the
        // place of the refreshes the individual notifications started before.
        private void RefreshAfterDerivedAccessOutputs()
        {
            if (derivedAccessOutputScope == 0 && settingsAccessObserved)
                System_RefreshSettingsAccess();
        }

        /// <summary>Refreshes authority/context-dependent UI gates from the current settings context.</summary>
        public void System_RefreshSettingsAccess()
        {
            if (refreshingSettingsAccess)
                return;
            refreshingSettingsAccess = true;
            try
            {
                RefreshSettingsAccessCore();
            }
            finally
            {
                refreshingSettingsAccess = false;
            }
        }

        private void RefreshSettingsAccessCore()
        {
            bool currentIsRealMultiplayer;
            bool currentIsHost;
            SettingsMenuContext currentMenuContext;
            try
            {
                currentIsRealMultiplayer = GameModeHelper.IsRealMultiplayer();
                // Authority and game-mode presentation are independent. The Extender
                // correctly reports local Skirmish and Trail lobbies as local host.
#if API_SHARED_PRESET_TESTS
                currentIsHost = System_TestObservedLocalHost != null ? System_TestObservedLocalHost() : GameNetworkAPI.IsLocalHost();
#else
                currentIsHost = GameNetworkAPI.IsLocalHost();
#endif
                currentMenuContext = CaptureSettingsMenuContext();
            }
            catch
            {
                // Registration can precede the network singleton. Preserve the last
                // confirmed role so a transient failure never unlocks a client.
                return;
            }

            if (isLocalHost == currentIsHost && isRealMultiplayer == currentIsRealMultiplayer &&
                settingsMenuContext == currentMenuContext)
            {
                presetController?.RestorePersonalHostIfPending();
                RefreshModeAvailability();
                completedSettingsAccessRefreshes++;
                return;
            }

            isLocalHost = currentIsHost;
            isRealMultiplayer = currentIsRealMultiplayer;
            settingsMenuContext = currentMenuContext;
            presetController?.RestorePersonalHostIfPending();
            RaiseAccessProperties();
            completedSettingsAccessRefreshes++;
        }

        private static SettingsMenuContext CaptureSettingsMenuContext()
        {
#if API_SHARED_PRESET_TESTS
            return SettingsMenuContext.Other;
#else
            // Instance lazily constructs the entire game ViewModel. Settings are registered
            // before Vanilla has finished creating it, so never trigger that work here.
            if (!CrusaderDE.MainViewModel.viewModelLoaded)
                return SettingsMenuContext.Other;
            try
            {
                CrusaderDE.MainViewModel viewModel = CrusaderDE.MainViewModel.Instance;
                if (viewModel == null)
                    return SettingsMenuContext.Other;
                bool campaign = viewModel.Show_Historical1CampaignMenu || viewModel.Show_Historical2CampaignMenu ||
                    viewModel.Show_Historical3CampaignMenu || viewModel.Show_Historical4CampaignMenu ||
                    viewModel.Show_Historical5CampaignMenu || viewModel.Show_Historical6CampaignMenu ||
                    viewModel.Show_Historical7CampaignMenu;
                bool trail = viewModel.Show_TrailCampaignMenu || viewModel.Show_Trail2CampaignMenu ||
                    viewModel.Show_Trail3CampaignMenu || viewModel.Show_SandsTrail1Menu ||
                    viewModel.Show_SandsTrail2Menu || viewModel.Show_SandsTrail3Menu ||
                    viewModel.Show_SandsTrail4Menu || viewModel.Show_SandsTrail5Menu ||
                    viewModel.Show_SandsTrail6Menu || viewModel.Show_SandsTrail7Menu ||
                    viewModel.Show_SandsTrail8Menu;
                bool coopTrail = viewModel.Show_CoopTrail1 || viewModel.Show_CoopTrail2 ||
                    viewModel.Show_CoopTrail3 || viewModel.Show_CoopTrail4;
                return ResolveSettingsMenuContext(viewModel.Show_MultiplayerSetup, campaign, trail, coopTrail);
            }
            catch
            {
                // An unavailable front-end view never proves that a direct launch is active.
            }
            return SettingsMenuContext.Other;
#endif
        }

        private static SettingsMenuContext ResolveSettingsMenuContext(
            bool customizeSetup, bool campaign, bool trail, bool coopTrail)
        {
            // Vanilla keeps MultiplayerSetup visible behind a direct Coop Trail page.
            if (coopTrail && !campaign) return SettingsMenuContext.DirectTrail;
            if (customizeSetup) return SettingsMenuContext.CustomizeSetup;
            if (campaign && !trail) return SettingsMenuContext.Campaign;
            if (trail && !campaign) return SettingsMenuContext.DirectTrail;
            return SettingsMenuContext.Other;
        }

#if API_SHARED_PRESET_TESTS
        public void System_TestSetSettingsMenuContext(
            bool customizeSetup, bool campaign, bool trail, bool coopTrail = false)
        {
            settingsMenuContext = ResolveSettingsMenuContext(customizeSetup, campaign, trail, coopTrail);
            RaiseAccessProperties();
        }
#endif

        /// <summary>Configures an optional notice for direct campaign/Trail launches. Pass null to hide it.</summary>
        /// <remarks>Call on the Unity UI thread before settings registration. The view model retains the provider and invokes it on the UI caller's thread when its text is read; no dispatch occurs. Empty results and exceptions preserve the last valid text. The caller supplies policy and localization; no plugin GUID is inspected.</remarks>
        public void System_ConfigureDirectLaunchNotice(Func<string> messageProvider)
        {
            directLaunchNoticeProvider = messageProvider;
            lastDirectLaunchNotice = string.Empty;
            RaiseAccessProperties();
        }

        private void RaiseAccessProperties()
        {
            derivedAccessOutputScope++;
            try { RaiseAccessPropertiesCore(); }
            finally { derivedAccessOutputScope--; }
            RefreshAfterDerivedAccessOutputs();
        }

        private void RaiseAccessPropertiesCore()
        {
            RefreshModeAvailability();
            RaiseDerivedAccessOutput(nameof(IsLocalSettingsHost));
            RaiseDerivedAccessOutput(nameof(IsRealMultiplayerContext));
            RaiseDerivedAccessOutput(nameof(HasHostSettings));
            RaiseDerivedAccessOutput(nameof(HasClientSettings));
            RaiseDerivedAccessOutput(nameof(HasHostSettingsActivation));
            RaiseDerivedAccessOutput(nameof(HasClientSettingsActivation));
            RaiseDerivedAccessOutput(nameof(HostSettingsEnabled));
            RaiseDerivedAccessOutput(nameof(ClientSettingsEnabled));
            RaiseDerivedAccessOutput(nameof(HostSettingsOpacity));
            RaiseDerivedAccessOutput(nameof(ClientSettingsOpacity));
            RaiseDerivedAccessOutput(nameof(MissionPresetEditable));
            RaiseDerivedAccessOutput(nameof(IsMissionPresetSelected));
            RaiseDerivedAccessOutput(nameof(CanEditHostSettings));
            RaiseDerivedAccessOutput(nameof(CanEditClientSettings));
            RaiseDerivedAccessOutput(nameof(CanEditMissionIndependentHostSettings));
            RaiseDerivedAccessOutput(nameof(CanToggleHostSettings));
            RaiseDerivedAccessOutput(nameof(CanToggleClientSettings));
            RaiseDerivedAccessOutput(nameof(CanChangePreset));
            RaiseDerivedAccessOutput(nameof(CanResetSettings));
            RaiseDerivedAccessOutput(nameof(PresetVisibility));
            RaiseDerivedAccessOutput(nameof(ClientSettingsActivationVisibility));
            RaiseDerivedAccessOutput(nameof(HostReadOnlyNoticeVisibility));
            RaiseDerivedAccessOutput(nameof(ActionsScopeNoticeVisibility));
            RaiseDerivedAccessOutput(nameof(ActionsScopeNoticeText));
            RaiseDerivedAccessOutput(nameof(AreSettingsEditable));
            RaiseDerivedAccessOutput(nameof(IsMissionPresetActive));
            RaiseDerivedAccessOutput(nameof(System_DirectLaunchNoticeVisibility));
            RaiseDerivedAccessOutput(nameof(System_DirectLaunchNoticeText));
            RaiseDerivedAccessOutput(nameof(System_TrailSourceNoticeVisibility));
            RaiseDerivedAccessOutput(nameof(System_TrailSourceNoticeText));
#if !API_SHARED_PRESET_TESTS
            RaiseDerivedAccessOutput(nameof(System_PresetStatusText));
            RaiseDerivedAccessOutput(nameof(System_PresetStatusVisibility));
            RaiseDerivedAccessOutput(nameof(System_CanLoadSettingsSource));
#endif
        }
    }
}

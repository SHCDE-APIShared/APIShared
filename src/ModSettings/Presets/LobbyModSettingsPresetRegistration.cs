using APIShared.GameModes;
using APIShared.ModSettings;
using APIShared.Internal;
#pragma warning disable 1591 // XAML and integration surface is documented by the APIShared preset guide.
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
    public static class LobbyModSettingsPresetRegistration
    {
        /// <summary>Attaches shared presets to an existing foreign mod page without a second network registration.</summary>
        public static void AttachExternalWorkingCopy(
            ManualLogSource log, string storageAssemblyLocation, string modName, string targetGuid,
            Version targetVersion, PresetLobbyModSettingsViewModel viewModel, object view)
        {
            if (viewModel == null || view == null) throw new ArgumentNullException();
            RegisterExternalWorkingCopy(log, storageAssemblyLocation, modName, targetGuid, targetVersion, viewModel);
            ModSettingsHorizontalFocusScrollGuard.Attach(view, log, modName);
        }

        /// <summary>Attaches shared focus scrolling to a separately registered participant's page.</summary>
        public static void AttachExternalView(object view, ManualLogSource log, string modName) =>
            ModSettingsHorizontalFocusScrollGuard.Attach(view, log, modName);

        /// <summary>Registers an external configuration participant independently of its optional page.</summary>
        public static void RegisterExternalWorkingCopy(
            ManualLogSource log, string storageAssemblyLocation, string modName, string targetGuid,
            Version targetVersion, PresetLobbyModSettingsViewModel viewModel)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            PrepareExternalWorkingCopy(log, storageAssemblyLocation, modName, targetGuid, targetVersion, viewModel)
                .Activate(() => { });
        }

        /// <summary>Prepares an unpublished candidate. Activation failures cannot leak a participant.</summary>
        public static PreparedExternalSettings PrepareExternalWorkingCopy(
            ManualLogSource log, string storageAssemblyLocation, string modName, string targetGuid,
            Version targetVersion, PresetLobbyModSettingsViewModel viewModel)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            ModSettingsApplication.CheckRegistration(targetGuid);
            viewModel.PreparePresets(log, storageAssemblyLocation, modName, targetGuid, targetVersion, publishParticipant: false);
            viewModel.ActivatePresets();
            return new PreparedExternalSettings(targetGuid, storageAssemblyLocation, viewModel);
        }

        public sealed class PreparedExternalSettings
        {
            private readonly string id, path;
            private readonly PresetLobbyModSettingsViewModel model;
            private bool attempted;
            internal PreparedExternalSettings(string id, string path, PresetLobbyModSettingsViewModel model)
            { this.id = id; this.path = path; this.model = model; }
            public void Activate(Action enable, Func<Exception, bool> definitelyRejected = null)
            {
                if (attempted) throw new InvalidOperationException("External registration activation already attempted.");
                attempted = true;
                ModSettingsApplication.CheckRegistration(id);
                ModSettingsApplication.SetActivationFailure(id, "Configuration integration is being activated.");
                bool enabled = false;
                try
                {
                    enable();
                    enabled = true;
                    model.PublishParticipant(id, path);
#if !API_SHARED_PRESET_TESTS
                    Plugin.ModSettingsHubViewModel.PropertyChanged += (_, __) => model.System_RefreshSettingsAccess();
#endif
                    ModSettingsApplication.SetActivationFailure(id, null);
                }
                catch (Exception ex)
                {
                    ModSettingsApplication.SetActivationFailure(id,
                        !enabled && definitelyRejected?.Invoke(ex) == true ? null : ex.GetBaseException().Message);
                    throw;
                }
            }
        }
        public static void Register(
            BaseUnityPlugin plugin,
            ManualLogSource log,
            string modName,
            PresetLobbyModSettingsViewModel viewModel,
            string xamlSourceFile)
        {
            Register(
                plugin,
                log,
                modName,
                viewModel,
                xamlSourceFile,
                true);
        }

        public static void Register(
            BaseUnityPlugin plugin,
            ManualLogSource log,
            string modName,
            PresetLobbyModSettingsViewModel viewModel,
            string xamlSourceFile,
            bool logRoutineActivity)
        {
            Register(plugin, log, modName, viewModel, xamlSourceFile, logRoutineActivity,
                enableScrollDiagnostics: false);
        }

        /// <summary>Registers presets and the lobby page, optionally enabling shared scroll diagnostics for this caller.</summary>
        /// <remarks>Register on the Unity thread. Diagnostics use the supplied logger and mod name; no plugin GUID is inspected. Registrations and UI event handlers remain rooted for the process lifetime.</remarks>
        public static void Register(
            BaseUnityPlugin plugin,
            ManualLogSource log,
            string modName,
            PresetLobbyModSettingsViewModel viewModel,
            string xamlSourceFile,
            bool logRoutineActivity,
            bool enableScrollDiagnostics)
        {
            if (plugin == null)
                throw new ArgumentNullException(nameof(plugin));
            if (viewModel == null)
                throw new ArgumentNullException(nameof(viewModel));

#if !API_SHARED_PRESET_TESTS
            if (GameAssetManagerAPI.Instance.GetModifiedFilePath(
                xamlSourceFile,
                out string absoluteXamlSourceFile))
            {
                // The catalog is read from XAML and is therefore available before Noesis has
                // materialized an unselected tab's controls. This avoids touching native layout.
                ModSettingsSearch.RegisterSource(viewModel, absoluteXamlSourceFile, log, modName);
            }
#endif
            viewModel.PreparePresets(
                log,
                plugin.Info.Location,
                modName,
                plugin.Info.Metadata.GUID,
                plugin.Info.Metadata.Version,
                logRoutineActivity);
            // Structural validation must happen before the ViewModel can enter the
            // Extender registry. An invalid personal setting therefore fails closed.
            viewModel.PreparePerPlayerLobbySettings(
                log,
                modName,
                plugin.Info.Metadata.GUID,
                logRoutineActivity);
            object registeredView = null;
            try
            {
                // Preserve the old pre-binding load (including legacy files), while keeping
                // subsequent writes under the preset controller's ownership.
                try
                {
                    new LobbyModSettingsStorage(plugin.Info.Location, modName).Load(viewModel);
                }
                catch (Exception exception)
                {
                    DebugLogHelper.LogError(log,
                        $"[{modName}] Initial lobby-settings load failed; keeping ViewModel defaults: {exception}");
                }
                GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(
                    plugin,
                    modName,
                    viewModel,
                    xamlSourceFile,
                    useBuiltInPersistence: false);
                var registration = GameXAMLManagerAPI.Instance.RegisteredModSettings
                    .FirstOrDefault(entry => ReferenceEquals(entry.ViewModel, viewModel));
#if API_SHARED_PRESET_TESTS
                // The classic test harness deliberately does not load Noesis.NoesisGUI.
                // Reflection keeps the registration semantics under test without
                // introducing a runtime-only FrameworkElement assembly dependency.
                registeredView = registration?.GetType()
                    .GetProperty("View", BindingFlags.Instance | BindingFlags.Public)
                    ?.GetValue(registration);
#else
                registeredView = registration?.View;
#endif
            }
            catch
            {
                viewModel.DeactivatePerPlayerLobbySettings();
                throw;
            }
            if (registeredView == null)
            {
                viewModel.DeactivatePerPlayerLobbySettings();
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Presets were not activated because lobby-settings registration failed.");
                return;
            }

            ModSettingsHorizontalFocusScrollGuard.Attach(
                registeredView,
                log,
                modName,
                enableScrollDiagnostics);
            viewModel.ActivatePresets();
            viewModel.ActivatePerPlayerLobbySettings();
#if !API_SHARED_PRESET_TESTS
            // Views are created before a lobby exists. Refresh the cached role whenever
            // the persistent settings hub opens or changes its selected tab.
            Plugin.ModSettingsHubViewModel.PropertyChanged += (_, __) =>
                viewModel.System_RefreshSettingsAccess();
#endif
        }
    }
}

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
        /// <summary>
        /// Declares the few domain-specific parts of personal settings. Transport,
        /// player-slot ownership, lobby convergence and readiness stay in APIShared.
        /// </summary>
        protected virtual void ConfigurePerPlayerLobbySettings(
            PerPlayerLobbySettingsBuilder settings)
        {
        }

        /// <summary>Whether required per-player reports have converged; true when no coordinator is configured.</summary>
        public bool IsPerPlayerLobbySettingsReady =>
            perPlayerSettingsCoordinator?.IsReady ?? true;

        /// <summary>Explanation of unresolved required reports, or empty when ready.</summary>
        public string PerPlayerLobbySettingsReadinessError =>
            perPlayerSettingsCoordinator?.ReadinessError ?? string.Empty;

        /// <summary>Requests publication through the existing coordinator; no-op before preparation.</summary>
        public void System_RequestPerPlayerSettingsPublish()
        {
            perPlayerSettingsCoordinator?.RequestPublish();
        }

        /// <summary>Checks required reports for the supplied game player slots and returns a reason on failure; true without a coordinator.</summary>
        public bool System_ArePerPlayerSettingsReady(
            IEnumerable<int> playerIds,
            out string error)
        {
            if (perPlayerSettingsCoordinator == null)
            {
                error = string.Empty;
                return true;
            }

            return perPlayerSettingsCoordinator.ArePlayersReady(playerIds, out error);
        }
    }
}

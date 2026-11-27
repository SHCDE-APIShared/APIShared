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
#if !API_SHARED_PRESET_TESTS
        /// <summary>Editable free-text filter; changing it clears exact-key targeting.</summary>
        public string System_ModSettingsSearchText
        {
            get => modSettingsSearchText;
            set
            {
                string normalized = value ?? string.Empty;
                if (string.Equals(modSettingsSearchText, normalized, StringComparison.Ordinal) &&
                    modSettingsSearchExactKey.Length == 0)
                {
                    return;
                }
                modSettingsSearchText = normalized;
                modSettingsSearchExactKey = string.Empty;
                RaiseModSettingsSearchProperties();
            }
        }

        /// <summary>Whether free-text search includes tooltip content as well as visible titles.</summary>
        public bool System_ModSettingsSearchIncludeToolTips
        {
            get => modSettingsSearchIncludeToolTips;
            set
            {
                if (modSettingsSearchIncludeToolTips == value)
                    return;
                modSettingsSearchIncludeToolTips = value;
                RaiseModSettingsSearchProperties();
            }
        }

        /// <summary>Exact target key used by external search navigation, independent of free-text matching.</summary>
        public string System_ModSettingsSearchExactKey => modSettingsSearchExactKey;

        private ModSettingsSearchStateFilter searchStateFilter;
        private int searchStateRevision;
        /// <summary>Session-only configured-value filter; changing it clears external exact targeting.</summary>
        public ModSettingsSearchStateFilter System_ModSettingsSearchStateFilter
        {
            get => searchStateFilter;
            set
            {
                if (searchStateFilter == value || !Enum.IsDefined(typeof(ModSettingsSearchStateFilter), value)) return;
                searchStateFilter = value;
                modSettingsSearchExactKey = string.Empty;
                RaiseModSettingsSearchProperties();
            }
        }
        /// <summary>Index binding for the All/Active/Inactive selector.</summary>
        public int System_ModSettingsSearchStateFilterIndex
        {
            get => (int)searchStateFilter;
            set { if (value >= 0 && value <= 2) System_ModSettingsSearchStateFilter = (ModSettingsSearchStateFilter)value; }
        }
        /// <summary>Localized status choices, in All/Active/Inactive order.</summary>
        public string[] System_ModSettingsSearchStateFilters => new[] {
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchStateAll", APIShared.Localization.UiText.English("Common.ModSettingsSearchStateAll")),
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchStateActive", APIShared.Localization.UiText.English("Common.ModSettingsSearchStateActive")),
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchStateInactive", APIShared.Localization.UiText.English("Common.ModSettingsSearchStateInactive")) };
        /// <summary>Localized label for the configured-value filter.</summary>
        public string System_ModSettingsSearchStateLabelText => ResolveSettingsUiTextSafe("Common.ModSettingsSearchStateLabel", APIShared.Localization.UiText.English("Common.ModSettingsSearchStateLabel"));
        /// <summary>Localized explanation of per-slider neutral values and mixed rows.</summary>
        public string System_ModSettingsSearchStateHelpText => ResolveSettingsUiTextSafe("Common.ModSettingsSearchStateHelp", APIShared.Localization.UiText.English("Common.ModSettingsSearchStateHelp"));
        /// <summary>UI refresh token for observed value changes. Not a saved setting.</summary>
        public int System_ModSettingsSearchStateRevision => searchStateRevision;

        private void OnModSettingsSearchStateChanged(object model)
        {
            if (!ReferenceEquals(model, this) || searchStateFilter == ModSettingsSearchStateFilter.All) return;
            RaiseModSettingsSearchStateRevision();
        }

        private void OnModSettingsSearchSourceChanged(object model)
        {
            if (ReferenceEquals(model, this)) RaiseModSettingsSearchStateRevision();
        }

        private void RaiseModSettingsSearchStateRevision()
        {
            unchecked { searchStateRevision++; }
            base.OnPropertyChanged(nameof(System_ModSettingsSearchStateRevision));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchNoResultsVisibility));
        }

        /// <summary>Incrementing focus token for the attached FocusRequest binding.</summary>
        public int System_ModSettingsSearchFocusRequest => modSettingsSearchFocusRequest;

        /// <summary>Whether either free-text filtering or exact-key targeting is active.</summary>
        public bool System_ModSettingsSearchHasActiveFilter =>
            searchStateFilter != ModSettingsSearchStateFilter.All ||
            modSettingsSearchExactKey.Length > 0 ||
            !string.IsNullOrWhiteSpace(modSettingsSearchText);

        /// <summary>Shows the expanded search controls.</summary>
        public Visibility System_ModSettingsSearchPanelVisibility =>
            modSettingsSearchExpanded ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>Shows the collapsed search affordance.</summary>
        public Visibility System_ModSettingsSearchInactiveVisibility =>
            System_ModSettingsSearchHasActiveFilter ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>Shows no-results feedback for an active filter with no catalog matches.</summary>
        public Visibility System_ModSettingsSearchNoResultsVisibility =>
            System_ModSettingsSearchHasActiveFilter &&
            !ModSettingsSearch.HasMatches(
                this,
                modSettingsSearchText,
                modSettingsSearchIncludeToolTips,
                modSettingsSearchExactKey,
                searchStateFilter)
                ? Visibility.Visible
                : Visibility.Collapsed;

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchLabel; English fallback: Search</summary>
        public string System_ModSettingsSearchLabelText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchLabel", APIShared.Localization.UiText.English("Common.ModSettingsSearchLabel"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchHelp; English fallback: Search setting titles. Optionally include tooltips.</summary>
        public string System_ModSettingsSearchHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchHelp", APIShared.Localization.UiText.English("Common.ModSettingsSearchHelp"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchToggleHelp; English fallback: Show or hide the settings search.</summary>
        public string System_ModSettingsSearchToggleHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchToggleHelp", APIShared.Localization.UiText.English("Common.ModSettingsSearchToggleHelp"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchIncludeToolTips; English fallback: Search tooltips</summary>
        public string System_ModSettingsSearchIncludeToolTipsText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchIncludeToolTips", APIShared.Localization.UiText.English("Common.ModSettingsSearchIncludeToolTips"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchIncludeToolTipsHelp; English fallback: Also search the explanatory tooltips of settings.</summary>
        public string System_ModSettingsSearchIncludeToolTipsHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchIncludeToolTipsHelp", APIShared.Localization.UiText.English("Common.ModSettingsSearchIncludeToolTipsHelp"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchClearHelp; English fallback: Clear the settings filter.</summary>
        public string System_ModSettingsSearchClearHelpText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchClearHelp", APIShared.Localization.UiText.English("Common.ModSettingsSearchClearHelp"));

        /// <summary>Localized XAML caption/help for Common.ModSettingsSearchNoResults; English fallback: No matching settings found.</summary>
        public string System_ModSettingsSearchNoResultsText =>
            ResolveSettingsUiTextSafe("Common.ModSettingsSearchNoResults", APIShared.Localization.UiText.English("Common.ModSettingsSearchNoResults"));

        /// <summary>Expands or collapses settings search and requests focus when opening.</summary>
        public RelayCommand System_ToggleModSettingsSearchCommand { get; }

        /// <summary>Clears free-text and exact-key targeting.</summary>
        public RelayCommand System_ClearModSettingsSearchCommand { get; }

        /// <summary>Safe reflection bridge used by the optional global search host.</summary>
        public bool System_ApplyModSettingsSearchTarget(string key, string title)
        {
            string normalizedKey = ModSettingsSearchMatcher.Normalize(key);
            if (normalizedKey.Length == 0)
                return false;

            modSettingsSearchText = title ?? string.Empty;
            searchStateFilter = ModSettingsSearchStateFilter.All;
            modSettingsSearchExactKey = normalizedKey;
            modSettingsSearchExpanded = true;
            RaiseModSettingsSearchProperties();
            return true;
        }

        private void ToggleModSettingsSearch()
        {
            modSettingsSearchExpanded = !modSettingsSearchExpanded;
            RaiseModSettingsSearchProperties();
            if (!modSettingsSearchExpanded)
                return;

            unchecked
            {
                modSettingsSearchFocusRequest++;
                if (modSettingsSearchFocusRequest <= 0)
                    modSettingsSearchFocusRequest = 1;
            }
            base.OnPropertyChanged(nameof(System_ModSettingsSearchFocusRequest));
        }

        private void ClearModSettingsSearch()
        {
            searchStateFilter = ModSettingsSearchStateFilter.All;
            modSettingsSearchText = string.Empty;
            modSettingsSearchExactKey = string.Empty;
            RaiseModSettingsSearchProperties();
        }

        private void RaiseModSettingsSearchProperties()
        {
            base.OnPropertyChanged(nameof(System_ModSettingsSearchStateFilter));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchStateFilterIndex));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchText));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchIncludeToolTips));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchExactKey));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchHasActiveFilter));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchPanelVisibility));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchInactiveVisibility));
            base.OnPropertyChanged(nameof(System_ModSettingsSearchNoResultsVisibility));
        }

        // SerpsModsHost discovers this method by reflection. Keeping the bridge on the
        // common base type lets every mod remain usable without the optional pack host.
        /// <summary>Exports search metadata for this settings page, preferring its registered XAML catalog over traversing realized controls.</summary>
        public IReadOnlyList<ModSettingsSearchEntry> System_GetModSettingsSearchEntries(
            Noesis.FrameworkElement view) =>
            ModSettingsSearch.Export(this, view);
#endif
    }
}

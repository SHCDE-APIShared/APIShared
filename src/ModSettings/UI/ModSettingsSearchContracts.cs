using APIShared.Internal;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace APIShared.ModSettings
{
    /// <summary>Configured states contained in a logical settings row. Unknown has no classified values; mixed rows have both flags.</summary>
    [Flags]
    public enum ModSettingsSearchState
    {
        /// <summary>No safely classified checkbox or slider value.</summary>
        Unknown = 0,
        /// <summary>At least one configured value is active.</summary>
        Active = 1,
        /// <summary>At least one configured value is inactive.</summary>
        Inactive = 2
    }

    /// <summary>Additional search constraint. All preserves the original text-only search.</summary>
    public enum ModSettingsSearchStateFilter
    {
        /// <summary>Include settings regardless of state, including unknown state.</summary>
        All = 0,
        /// <summary>Include rows with at least one active value.</summary>
        Active = 1,
        /// <summary>Include rows with at least one inactive value.</summary>
        Inactive = 2
    }

    /// <summary>Shared state predicate for global and local settings search.</summary>
    public static class ModSettingsSearchStateMatcher
    {
        /// <summary>Tests a state flag; mixed rows match both filters. Unknown only matches All.</summary>
        public static bool IsMatch(ModSettingsSearchStateFilter filter, ModSettingsSearchState state) =>
            filter == ModSettingsSearchStateFilter.All ||
            (filter == ModSettingsSearchStateFilter.Active && (state & ModSettingsSearchState.Active) != 0) ||
            (filter == ModSettingsSearchStateFilter.Inactive && (state & ModSettingsSearchState.Inactive) != 0);
    }

    /// <summary>
    /// Public APIShared export shape used by the optional global settings-search host.
    /// </summary>
    public sealed class ModSettingsSearchEntry
    {
        /// <summary>Creates immutable search metadata; null section fields become empty. Callers should supply non-null setting key/title/tooltip for export consumers.</summary>
        public ModSettingsSearchEntry(string key, string title, string toolTip)
            : this(key, title, toolTip, string.Empty, string.Empty, false)
        {
        }

        /// <summary>Creates immutable search metadata; null section fields become empty. Callers should supply non-null setting key/title/tooltip for export consumers.</summary>
        public ModSettingsSearchEntry(
            string key,
            string title,
            string toolTip,
            string sectionKey,
            string sectionTitle,
            bool isSection)
        {
            Key = key;
            Title = title;
            ToolTip = toolTip;
            SectionKey = sectionKey ?? string.Empty;
            SectionTitle = sectionTitle ?? string.Empty;
            IsSection = isSection;
        }

        /// <summary>Stable exact setting key; may be empty when only visible title matching is available.</summary>
        public string Key { get; }
        /// <summary>Localized visible setting title used for free-text search.</summary>
        public string Title { get; }
        /// <summary>Localized explanatory text searched only when tooltip inclusion is enabled.</summary>
        public string ToolTip { get; }
        /// <summary>Stable enclosing section identity used for exact targeting.</summary>
        public string SectionKey { get; }
        /// <summary>Localized enclosing title included in free-text matching.</summary>
        public string SectionTitle { get; }
        /// <summary>Whether this entry describes a whole section instead of an individual setting.</summary>
        public bool IsSection { get; }
    }
}

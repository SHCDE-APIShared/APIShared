using APIShared.ModSettings;
using APIShared.Internal;
using System;
using System.Globalization;
using System.Linq;
using System.Text;
#if !API_SHARED_PRESET_TESTS
using Noesis;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
#endif

namespace APIShared.ModSettings
{
    // UI-only transaction boundary. Settings setters, persistence and authority retain
    // their original notifications; only derived search refreshes wait for completion.
    internal static class ModSettingsSearchUpdateScope
    {
        [ThreadStatic] private static System.Collections.Generic.Dictionary<object, Batch> batches;

        internal static IDisposable Begin(object model)
        {
            if (batches == null) batches = new System.Collections.Generic.Dictionary<object, Batch>(new IdentityComparer());
            if (!batches.TryGetValue(model, out Batch batch)) batches.Add(model, batch = new Batch());
            batch.Depth++;
            return new Lease(model, batch);
        }

        internal static bool TryDefer(object model, Action refresh)
        {
            if (batches == null || !batches.TryGetValue(model, out Batch batch)) return false;
            if (batch.Refreshes == null) batch.Refreshes = new System.Collections.Generic.HashSet<Action>();
            batch.Refreshes.Add(refresh);
            return true;
        }

        private sealed class Batch
        {
            internal int Depth;
            internal System.Collections.Generic.HashSet<Action> Refreshes;
        }
        private sealed class Lease : IDisposable
        {
            private readonly object model;
            private Batch batch;
            internal Lease(object model, Batch batch) { this.model = model; this.batch = batch; }
            public void Dispose()
            {
                Batch current = batch;
                if (current == null) return;
                batch = null;
                if (--current.Depth != 0) return;
                batches.Remove(model);
                // Remove the scope before callbacks so a reentrant change starts a new operation.
                if (current.Refreshes != null) foreach (Action refresh in current.Refreshes) refresh();
            }
        }
        private sealed class IdentityComparer : System.Collections.Generic.IEqualityComparer<object>
        {
            public new bool Equals(object left, object right) => ReferenceEquals(left, right);
            public int GetHashCode(object value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }

    /// <summary>Pure matching policy shared by runtime filtering and isolated tests.</summary>
    public static class ModSettingsSearchMatcher
    {
        /// <summary>Matches an exact ordinal setting/section key when supplied, otherwise all free-text terms in titles and optionally tooltips. Empty filters match all; text matching folds case (culture-invariant) and diacritics.</summary>
        public static bool IsMatch(
            string filterText,
            bool includeToolTips,
            string exactKey,
            string key,
            string title,
            string toolTip)
        {
            return IsMatch(
                filterText,
                includeToolTips,
                exactKey,
                key,
                title,
                toolTip,
                string.Empty,
                string.Empty);
        }

        /// <summary>Matches an exact ordinal setting/section key when supplied, otherwise all free-text terms in titles and optionally tooltips. Empty filters match all; text matching folds case (culture-invariant) and diacritics.</summary>
        public static bool IsMatch(
            string filterText,
            bool includeToolTips,
            string exactKey,
            string key,
            string title,
            string toolTip,
            string sectionKey,
            string sectionTitle)
        {
            string exact = Normalize(exactKey);
            if (exact.Length > 0)
            {
                return string.Equals(Normalize(key), exact, StringComparison.Ordinal) ||
                    string.Equals(Normalize(sectionKey), exact, StringComparison.Ordinal);
            }

            string filter = Normalize(filterText);
            if (filter.Length == 0)
                return true;

            string visibleText = JoinSearchText(title, sectionTitle);
            if (ContainsAllTerms(visibleText, filter))
                return true;
            return includeToolTips && ContainsAllTerms(JoinSearchText(visibleText, toolTip), filter);
        }

        /// <summary>Tests all free-text terms against a section title using the same case/diacritic folding as entry matching.</summary>
        public static bool IsSectionTitleMatch(string filterText, string sectionTitle) =>
            ContainsAllTerms(sectionTitle, Normalize(filterText));

        /// <summary>Trims search text and maps null to empty; does not itself perform case/diacritic folding.</summary>
        public static string Normalize(string value) => (value ?? string.Empty).Trim();

        private static bool ContainsAllTerms(string value, string filter)
        {
            if (filter.Length == 0)
                return true;
            string haystack = Fold(value);
            string needle = Fold(filter);
            if (haystack.Contains(needle))
                return true;
            return needle.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .All(term => haystack.Contains(term));
        }

        private static string JoinSearchText(params string[] values) =>
            string.Join(" ", (values ?? Array.Empty<string>()).Where(value => !string.IsNullOrWhiteSpace(value)));

        private static string Fold(string value)
        {
            string decomposed = ModSettingsSearchText.RepairNormalizationInput(Normalize(value)).Normalize(NormalizationForm.FormD);
            var result = new StringBuilder(decomposed.Length);
            foreach (char character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                    result.Append(char.ToLowerInvariant(character));
            }
            return result.ToString().Normalize(NormalizationForm.FormC);
        }
    }
    // Kept with the pure matcher so the legacy preset harness tests production text handling too.
    internal static class ModSettingsSearchText
    {
        internal static string RepairNormalizationInput(string value)
        {
            StringBuilder repaired = null;
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (char.IsHighSurrogate(character) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
                {
                    int scalar = char.ConvertToUtf32(character, value[index + 1]);
                    if ((scalar & 0xFFFF) >= 0xFFFE)
                    {
                        if (repaired == null) repaired = new StringBuilder(value.Length).Append(value, 0, index);
                        repaired.Append('\uFFFD');
                    }
                    else if (repaired != null) repaired.Append(character).Append(value[index + 1]);
                    index++;
                    continue;
                }
                // CLR normalization rejects Unicode noncharacters on supported runtime variants.
                // Repair comparison input only; never rewrite displayed text or exact keys.
                if (char.IsSurrogate(character) || character >= '\uFFFE' ||
                    (character >= '\uFDD0' && character <= '\uFDEF'))
                {
                    if (repaired == null) repaired = new StringBuilder(value.Length).Append(value, 0, index);
                    repaired.Append('\uFFFD');
                }
                else if (repaired != null) repaired.Append(character);
            }
            return repaired == null ? value : repaired.ToString();
        }

        internal static string FirstSentence(string text)
        {
            string normalized = (text ?? string.Empty).Trim();
            int end = normalized.IndexOfAny(new[] { '.', '!', '?', '\r', '\n' });
            if (end >= 0) normalized = normalized.Substring(0, end + 1);
            if (normalized.Length <= 120) return normalized;
            int length = 117;
            if (char.IsHighSurrogate(normalized[length - 1]) && char.IsLowSurrogate(normalized[length])) length--;
            return normalized.Substring(0, length) + "...";
        }

        internal static uint StableHash(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return hash;
            }
        }

    }

}

#if !API_SHARED_PRESET_TESTS
namespace APIShared.ModSettings
{
    /// <summary>
    /// Explicit, localized metadata for one logical mod-setting entry. The properties are
    /// attached to the smallest container that represents the logical setting.
    /// </summary>
    public static class ModSettingsSearch
    {
        /// <summary>Optional numeric-state metadata for a slider or a TextBox with StateValue: semicolon-separated invariant numbers or inclusive ranges (e.g. 0..1) classified as inactive, replacing the default -1 and 0. Invalid metadata yields unknown state. Specify on the control itself; no inference from defaults or labels.</summary>
        public static readonly DependencyProperty InactiveValuesProperty = DependencyProperty.RegisterAttached(
            "InactiveValues", typeof(string), typeof(ModSettingsSearch), new PropertyMetadata((object)null));
        /// <summary>Gets optional slider-specific inactive values on the Unity thread.</summary>
        public static string GetInactiveValues(DependencyObject value) => value?.GetValue(InactiveValuesProperty) as string;
        /// <summary>Sets optional slider-specific inactive values on the Unity thread.</summary>
        public static void SetInactiveValues(DependencyObject value, string text) => value?.SetValue(InactiveValuesProperty, text);

        /// <summary>Opt-in TextBox state binding to its authoritative numeric model property, e.g. StateValue="{Binding Amount}". The catalog reads this path on the Unity thread and observes managed PropertyChanged; it never parses display text. Converters, alternate sources, nonnumeric and missing values yield Unknown. TextBoxes without this metadata retain Unknown state. Use InactiveValues on the same TextBox to customize neutral values.</summary>
        public static readonly DependencyProperty StateValueProperty = DependencyProperty.RegisterAttached(
            "StateValue", typeof(object), typeof(ModSettingsSearch), new PropertyMetadata((object)null));
        /// <summary>Gets an opt-in TextBox's authoritative search-state value on the Unity thread.</summary>
        public static object GetStateValue(DependencyObject value) => value?.GetValue(StateValueProperty);
        /// <summary>Sets an opt-in TextBox's authoritative search-state value on the Unity thread; normal XAML usage binds a numeric model property.</summary>
        public static void SetStateValue(DependencyObject value, object state) => value?.SetValue(StateValueProperty, state);

        /// <summary>Registered root settings model inherited by local search rows, including template rows.</summary>
        public static readonly DependencyProperty SourceModelProperty = DependencyProperty.RegisterAttached(
            "SourceModel", typeof(object), typeof(ModSettingsSearch), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
        /// <summary>Gets the inherited root model on the Unity thread.</summary>
        public static object GetSourceModel(DependencyObject value) => value?.GetValue(SourceModelProperty);
        /// <summary>Sets the inherited root model on the Unity thread.</summary>
        public static void SetSourceModel(DependencyObject value, object model) => value?.SetValue(SourceModelProperty, model);
        /// <summary>Additional local status filter; All preserves text-only filtering.</summary>
        public static readonly DependencyProperty StateFilterProperty = DependencyProperty.RegisterAttached(
            "StateFilter", typeof(ModSettingsSearchStateFilter), typeof(ModSettingsSearch),
            new FrameworkPropertyMetadata(ModSettingsSearchStateFilter.All, FrameworkPropertyMetadataOptions.Inherits));
        /// <summary>Gets the inherited status filter on the Unity thread.</summary>
        public static ModSettingsSearchStateFilter GetStateFilter(DependencyObject value) =>
            value?.GetValue(StateFilterProperty) is ModSettingsSearchStateFilter filter ? filter : ModSettingsSearchStateFilter.All;
        /// <summary>Sets the inherited status filter on the Unity thread.</summary>
        public static void SetStateFilter(DependencyObject value, ModSettingsSearchStateFilter filter) => value?.SetValue(StateFilterProperty, filter);
        /// <summary>Local binding refresh token for configured-value changes; not persisted.</summary>
        public static readonly DependencyProperty StateRevisionProperty = DependencyProperty.RegisterAttached(
            "StateRevision", typeof(int), typeof(ModSettingsSearch), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.Inherits));
        /// <summary>Gets the inherited state revision on the Unity thread.</summary>
        public static int GetStateRevision(DependencyObject value) => value?.GetValue(StateRevisionProperty) is int revision ? revision : 0;
        /// <summary>Sets the inherited state revision on the Unity thread.</summary>
        public static void SetStateRevision(DependencyObject value, int revision) => value?.SetValue(StateRevisionProperty, revision);
        /// <summary>Noesis attached property for stable exact setting key; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty KeyProperty =
            DependencyProperty.RegisterAttached(
                "Key",
                typeof(string),
                typeof(ModSettingsSearch),
                new PropertyMetadata(string.Empty));

        /// <summary>Noesis attached property for localized visible setting title; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty TitleProperty =
            DependencyProperty.RegisterAttached(
                "Title",
                typeof(string),
                typeof(ModSettingsSearch),
                new PropertyMetadata(string.Empty));

        /// <summary>Noesis attached property for exclusion from search discovery; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty ExcludeProperty =
            DependencyProperty.RegisterAttached(
                "Exclude",
                typeof(bool),
                typeof(ModSettingsSearch),
                new PropertyMetadata(false));

        /// <summary>Noesis attached property for localized tooltip search text; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty ToolTipTextProperty =
            DependencyProperty.RegisterAttached(
                "ToolTipText",
                typeof(string),
                typeof(ModSettingsSearch),
                new PropertyMetadata(string.Empty));

        /// <summary>Noesis attached property for stable enclosing section key; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty SectionKeyProperty =
            DependencyProperty.RegisterAttached(
                "SectionKey",
                typeof(string),
                typeof(ModSettingsSearch),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Noesis attached property for localized enclosing section title; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty SectionTitleProperty =
            DependencyProperty.RegisterAttached(
                "SectionTitle",
                typeof(string),
                typeof(ModSettingsSearch),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Noesis attached property for whether this container represents a section; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty IsSectionProperty =
            DependencyProperty.RegisterAttached(
                "IsSection",
                typeof(bool),
                typeof(ModSettingsSearch),
                new PropertyMetadata(false));

        /// <summary>Noesis attached property for current free-text filter; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty FilterTextProperty =
            DependencyProperty.RegisterAttached(
                "FilterText",
                typeof(string),
                typeof(ModSettingsSearch),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Noesis attached property for exact setting/section target overriding free-text filtering; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty ExactKeyProperty =
            DependencyProperty.RegisterAttached(
                "ExactKey",
                typeof(string),
                typeof(ModSettingsSearch),
                new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Noesis attached property for whether tooltip text participates in free-text matching; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty IncludeToolTipsProperty =
            DependencyProperty.RegisterAttached(
                "IncludeToolTips",
                typeof(bool),
                typeof(ModSettingsSearch),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        /// <summary>Noesis attached property for incrementing token that focuses and selects the target TextBox text; set on the logical setting container or search control on the Unity thread.</summary>
        public static readonly DependencyProperty FocusRequestProperty =
            DependencyProperty.RegisterAttached(
                "FocusRequest",
                typeof(int),
                typeof(ModSettingsSearch),
                new PropertyMetadata(0, OnFocusRequestChanged));

        /// <summary>Reads the attached stable exact setting key on the Unity thread.</summary>
        public static string GetKey(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(KeyProperty) as string ?? string.Empty;

        /// <summary>Sets the attached stable exact setting key on the Unity thread.</summary>
        public static void SetKey(DependencyObject value, string key) =>
            value?.SetValue(KeyProperty, key ?? string.Empty);

        /// <summary>Reads the attached localized visible setting title on the Unity thread.</summary>
        public static string GetTitle(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(TitleProperty) as string ?? string.Empty;

        /// <summary>Sets the attached localized visible setting title on the Unity thread.</summary>
        public static void SetTitle(DependencyObject value, string title) =>
            value?.SetValue(TitleProperty, title ?? string.Empty);

        /// <summary>Reads the attached exclusion from search discovery on the Unity thread.</summary>
        public static bool GetExclude(DependencyObject value) =>
            value != null && value.GetValue(ExcludeProperty) is bool excluded && excluded;

        /// <summary>Sets the attached exclusion from search discovery on the Unity thread.</summary>
        public static void SetExclude(DependencyObject value, bool excluded) =>
            value?.SetValue(ExcludeProperty, excluded);

        /// <summary>Reads the attached localized tooltip search text on the Unity thread.</summary>
        public static string GetToolTipText(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(ToolTipTextProperty) as string ?? string.Empty;

        /// <summary>Sets the attached localized tooltip search text on the Unity thread.</summary>
        public static void SetToolTipText(DependencyObject value, string text) =>
            value?.SetValue(ToolTipTextProperty, text ?? string.Empty);

        /// <summary>Reads the attached stable enclosing section key on the Unity thread.</summary>
        public static string GetSectionKey(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(SectionKeyProperty) as string ?? string.Empty;

        /// <summary>Sets the attached stable enclosing section key on the Unity thread.</summary>
        public static void SetSectionKey(DependencyObject value, string key) =>
            value?.SetValue(SectionKeyProperty, key ?? string.Empty);

        /// <summary>Reads the attached localized enclosing section title on the Unity thread.</summary>
        public static string GetSectionTitle(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(SectionTitleProperty) as string ?? string.Empty;

        /// <summary>Sets the attached localized enclosing section title on the Unity thread.</summary>
        public static void SetSectionTitle(DependencyObject value, string title) =>
            value?.SetValue(SectionTitleProperty, title ?? string.Empty);

        /// <summary>Reads the attached whether this container represents a section on the Unity thread.</summary>
        public static bool GetIsSection(DependencyObject value) =>
            value != null && value.GetValue(IsSectionProperty) is bool isSection && isSection;

        /// <summary>Sets the attached whether this container represents a section on the Unity thread.</summary>
        public static void SetIsSection(DependencyObject value, bool isSection) =>
            value?.SetValue(IsSectionProperty, isSection);

        /// <summary>Reads the attached current free-text filter on the Unity thread.</summary>
        public static string GetFilterText(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(FilterTextProperty) as string ?? string.Empty;

        /// <summary>Sets the attached current free-text filter on the Unity thread.</summary>
        public static void SetFilterText(DependencyObject value, string text) =>
            value?.SetValue(FilterTextProperty, text ?? string.Empty);

        /// <summary>Reads the attached exact setting/section target overriding free-text filtering on the Unity thread.</summary>
        public static string GetExactKey(DependencyObject value) =>
            value == null ? string.Empty : value.GetValue(ExactKeyProperty) as string ?? string.Empty;

        /// <summary>Sets the attached exact setting/section target overriding free-text filtering on the Unity thread.</summary>
        public static void SetExactKey(DependencyObject value, string key) =>
            value?.SetValue(ExactKeyProperty, key ?? string.Empty);

        /// <summary>Reads the attached whether tooltip text participates in free-text matching on the Unity thread.</summary>
        public static bool GetIncludeToolTips(DependencyObject value) =>
            value != null && value.GetValue(IncludeToolTipsProperty) is bool enabled && enabled;

        /// <summary>Sets the attached whether tooltip text participates in free-text matching on the Unity thread.</summary>
        public static void SetIncludeToolTips(DependencyObject value, bool enabled) =>
            value?.SetValue(IncludeToolTipsProperty, enabled);

        /// <summary>Reads the attached incrementing token that focuses and selects the target TextBox text on the Unity thread.</summary>
        public static int GetFocusRequest(DependencyObject value) =>
            value != null && value.GetValue(FocusRequestProperty) is int request ? request : 0;

        /// <summary>Sets the attached incrementing token that focuses and selects the target TextBox text on the Unity thread.</summary>
        public static void SetFocusRequest(DependencyObject value, int request) =>
            value?.SetValue(FocusRequestProperty, request);

        private static void OnFocusRequestChanged(
            DependencyObject dependencyObject,
            DependencyPropertyChangedEventArgs args)
        {
            if (!(dependencyObject is TextBox textBox) ||
                !(args.NewValue is int request) ||
                request <= 0 ||
                Equals(args.OldValue, args.NewValue) ||
                !textBox.IsVisible ||
                !textBox.IsEnabled)
            {
                return;
            }

            // The request is raised only after the local search row became visible. Keeping the
            // concrete TextBox as the target avoids tree traversal and cross-tab focus attempts.
            textBox.Focus();
            textBox.SelectAll();
        }

        /// <summary>
        /// Registers the immutable XAML catalog used by the optional SerpsModsHost. Each mod
        /// remains standalone. New integrations can use ModSettingsSearchCatalog directly without initializing Noesis attached properties.
        /// </summary>
        public static void RegisterSource(
            object viewModel,
            string absoluteXamlPath,
            BepInEx.Logging.ManualLogSource log,
            string modName)
        {
            if (viewModel == null || string.IsNullOrWhiteSpace(absoluteXamlPath))
                return;
            ModSettingsSearchCatalog.RegisterSourceCore(viewModel, absoluteXamlPath, log, modName);
        }

        /// <summary>Exports registered XAML catalog entries, falling back to a supplied realized view only without a catalog. Read on the Unity thread; a registered catalog avoids native traversal during tab transitions.</summary>
        public static IReadOnlyList<ModSettingsSearchEntry> Export(
            object viewModel,
            FrameworkElement view)
        {
            // Registered settings pages use the immutable XAML catalog exclusively. Traversing a
            // realized Noesis tree during or after a tab transition can crash in native code.
            if (ModSettingsSearchCatalog.TryGetEntries(viewModel, out IReadOnlyList<ModSettingsSearchEntry> catalog))
                return catalog;

            var realizedEntries = new List<ModSettingsSearchEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var visited = new HashSet<DependencyObject>();
            var structuralParents = new Dictionary<DependencyObject, DependencyObject>();
            Visit(view, realizedEntries, keys, identities, visited, structuralParents, null, 0);
            return realizedEntries;
        }

        /// <summary>Checks the registered catalog for a filter match; returns true without a catalog to avoid incorrectly hiding unknown content.</summary>
        public static bool HasMatches(
            object viewModel,
            string filterText,
            bool includeToolTips,
            string exactKey)
        {
            if (!ModSettingsSearchCatalog.TryGetEntries(viewModel, out IReadOnlyList<ModSettingsSearchEntry> catalog))
                return true;
            return catalog.Any(entry => ModSettingsSearchMatcher.IsMatch(
                filterText,
                includeToolTips,
                exactKey,
                entry.Key,
                entry.Title,
                entry.ToolTip,
                entry.SectionKey,
                entry.SectionTitle));
        }

        /// <summary>Checks text/exact-target and live configured state together. With a status constraint only individual settings count; an empty/unsupported catalog cannot manufacture a status match.</summary>
        public static bool HasMatches(object viewModel, string filterText, bool includeToolTips, string exactKey,
            ModSettingsSearchStateFilter stateFilter)
        {
            if (stateFilter == ModSettingsSearchStateFilter.All) return HasMatches(viewModel, filterText, includeToolTips, exactKey);
            return ModSettingsSearchCatalog.TryGetEntries(viewModel, out IReadOnlyList<ModSettingsSearchEntry> catalog) &&
                catalog.Any(entry => !entry.IsSection && ModSettingsSearchStateMatcher.IsMatch(stateFilter,
                    ModSettingsSearchCatalog.GetState(viewModel, entry.Key)) && ModSettingsSearchMatcher.IsMatch(
                    filterText, includeToolTips, exactKey, entry.Key, entry.Title, entry.ToolTip, entry.SectionKey, entry.SectionTitle));
        }

        private static void Visit(
            DependencyObject current,
            List<ModSettingsSearchEntry> entries,
            HashSet<string> keys,
            HashSet<string> identities,
            HashSet<DependencyObject> visited,
            Dictionary<DependencyObject, DependencyObject> structuralParents,
            DependencyObject structuralParent,
            int depth)
        {
            if (current == null || depth > 80 || !visited.Add(current))
                return;

            if (structuralParent != null && !structuralParents.ContainsKey(current))
                structuralParents.Add(current, structuralParent);

            if (current is FrameworkElement excludedElement && GetExclude(excludedElement))
                return;

            if (current is FrameworkElement element)
            {
                string key = GetKey(element).Trim();
                string title = GetTitle(element).Trim();
                string sectionKey = GetSectionKey(element).Trim();
                string sectionTitle = GetSectionTitle(element).Trim();
                bool isSection = GetIsSection(element);
                if (isSection)
                {
                    if (sectionKey.Length > 0 && sectionTitle.Length > 0 && keys.Add(sectionKey))
                    {
                        entries.Add(new ModSettingsSearchEntry(
                            sectionKey,
                            sectionTitle,
                            string.Empty,
                            sectionKey,
                            sectionTitle,
                            true));
                    }
                }
                else if (key.Length > 0 || title.Length > 0)
                {
                    // Explicit metadata is authoritative. Repeated keys deliberately allow a
                    // logical setting with several controls to select its first navigation target.
                    if (key.Length > 0 && title.Length > 0 && keys.Add(key))
                    {
                        string toolTip = GetToolTipText(element).Trim();
                        if (toolTip.Length == 0)
                            toolTip = ReadToolTip(element);
                        entries.Add(new ModSettingsSearchEntry(
                            key,
                            title,
                            toolTip,
                            sectionKey,
                            sectionTitle,
                            false));
                        identities.Add(title + "\u001f" + toolTip);
                    }
                }
                else if (IsInteractive(element))
                {
                    string toolTip = ReadToolTip(element);
                    title = FindAutomaticTitle(element, structuralParents);
                    if (title.Length == 0 && toolTip.Length > 0)
                        title = FirstSentence(toolTip);
                    string identity = title + "\u001f" + toolTip;
                    if (title.Length > 0 && toolTip.Length > 0 && identities.Add(identity))
                    {
                        key = BuildAutomaticKey(element, title, toolTip);
                        int suffix = 2;
                        string uniqueKey = key;
                        while (!keys.Add(uniqueKey))
                            uniqueKey = key + ":" + suffix++;

                        entries.Add(new ModSettingsSearchEntry(uniqueKey, title, toolTip));
                    }
                }
            }

            foreach (DependencyObject child in EnumerateChildren(current))
                Visit(child, entries, keys, identities, visited, structuralParents, current, depth + 1);
        }

        private static IEnumerable<DependencyObject> EnumerateChildren(DependencyObject current)
        {
            // Noesis does not create the visual tree of an unselected mod tab. Its declarative
            // content tree is nevertheless already available after LoadXaml, so inspect both.
            var result = new List<DependencyObject>();
            var seen = new HashSet<DependencyObject>();

            if (current is Panel panel)
            {
                for (int index = 0; index < panel.Children.Count; index++)
                    AddChild(result, seen, panel.Children[index]);
            }
            if (current is Decorator decorator)
                AddChild(result, seen, decorator.Child);
            if (current is ContentControl contentControl)
                AddChild(result, seen, contentControl.Content as DependencyObject);
            int visualCount = VisualTreeHelper.GetChildrenCount(current);
            for (int index = 0; index < visualCount; index++)
                AddChild(result, seen, VisualTreeHelper.GetChild(current, index));
            return result;
        }

        private static void AddChild(
            ICollection<DependencyObject> result,
            ISet<DependencyObject> seen,
            DependencyObject child)
        {
            if (child != null && seen.Add(child))
                result.Add(child);
        }

        private static bool IsInteractive(FrameworkElement element) =>
            element is Button || element is CheckBox || element is ComboBox ||
            element is Slider || element is TextBox;

        private static string BuildAutomaticKey(FrameworkElement element, string title, string toolTip)
        {
            Binding binding = BindingOperations.GetBinding(element, ToolTipService.ToolTipProperty);
            string bindingPath = binding?.Path?.Path ?? string.Empty;
            string seed = bindingPath.Length > 0 ? bindingPath + ":" + title : title + ":" + toolTip;
            return "auto:" + StableHash(seed).ToString("x8", CultureInfo.InvariantCulture);
        }

        internal static uint StableHash(string value) => ModSettingsSearchText.StableHash(value);

        private static string FindAutomaticTitle(
            FrameworkElement element,
            IReadOnlyDictionary<DependencyObject, DependencyObject> structuralParents)
        {
            if (element is ContentControl contentControl)
            {
                string contentText = ReadContentText(contentControl.Content);
                if (contentText.Length > 0)
                    return contentText;
            }

            DependencyObject current = element;
            for (int level = 0; level < 4; level++)
            {
                DependencyObject parent = VisualTreeHelper.GetParent(current);
                if (parent == null)
                    structuralParents.TryGetValue(current, out parent);
                if (parent == null)
                    break;
                int row = Grid.GetRow(current);
                int count = VisualTreeHelper.GetChildrenCount(parent);
                for (int index = 0; index < count; index++)
                {
                    DependencyObject sibling = VisualTreeHelper.GetChild(parent, index);
                    if (ReferenceEquals(sibling, current))
                        continue;
                    if (sibling is TextBlock textBlock &&
                        (!(parent is Grid) || Grid.GetRow(textBlock) == row) &&
                        !string.IsNullOrWhiteSpace(textBlock.Text))
                    {
                        return textBlock.Text.Trim();
                    }
                    if (sibling is FrameworkElement siblingElement &&
                        (!(parent is Grid) || Grid.GetRow(siblingElement) == row))
                    {
                        string nested = ReadContentText(siblingElement);
                        if (nested.Length > 0)
                            return nested;
                    }
                }
                current = parent;
            }
            return string.Empty;
        }

        private static string ReadContentText(object content)
        {
            if (content is string text)
                return text.Trim();
            if (content is TextBlock textBlock)
                return textBlock.Text?.Trim() ?? string.Empty;
            if (content is DependencyObject dependencyObject)
            {
                int count = VisualTreeHelper.GetChildrenCount(dependencyObject);
                for (int index = 0; index < count; index++)
                {
                    string nested = ReadContentText(VisualTreeHelper.GetChild(dependencyObject, index));
                    if (nested.Length > 0)
                        return nested;
                }
            }
            return string.Empty;
        }

        internal static string FirstSentence(string text) => ModSettingsSearchText.FirstSentence(text);

        private static string ReadToolTip(DependencyObject element)
        {
            object toolTip = ToolTipService.GetToolTip(element);
            if (toolTip is ToolTip explicitToolTip)
                toolTip = explicitToolTip.Content;
            return ReadContentText(toolTip);
        }
    }

    /// <summary>One-way XAML converter for setting/section search visibility using the shared matching policy.</summary>
    public sealed class ModSettingsSearchVisibilityConverter : IMultiValueConverter
    {
        /// <summary>Returns Visible or Collapsed from the search bindings; incomplete input (fewer than six values) is Collapsed. Bind filter, tooltip inclusion, exact key, then entry key/title/tooltip and optional section key/title.</summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
            => ConvertCore(values, 0);

        internal static object ConvertCore(object[] values, int offset)
        {
            string filterText = ReadString(values, offset);
            bool includeToolTips = values != null && values.Length > offset + 1 && values[offset + 1] is bool value && value;
            string exactKey = ReadString(values, offset + 2);
            if (values == null || values.Length < offset + 6)
                return Visibility.Collapsed;
            for (int index = offset + 3; index + 2 < values.Length; index += 5)
            {
                if (ModSettingsSearchMatcher.IsMatch(
                    filterText,
                    includeToolTips,
                    exactKey,
                    ReadString(values, index),
                    ReadString(values, index + 1),
                    ReadString(values, index + 2),
                    ReadString(values, index + 3),
                    ReadString(values, index + 4)))
                {
                    return Visibility.Visible;
                }
            }
            return Visibility.Collapsed;
        }

        /// <summary>Reverse conversion is unsupported and throws NotSupportedException.</summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();

        private static string ReadString(object[] values, int index) =>
            values != null && index < values.Length && values[index] != null
                ? values[index].ToString()
                : string.Empty;
    }

    /// <summary>Status-aware local visibility converter. Prefix the existing converter inputs with root model, state filter and state revision; the original converter and tuple layout remain compatible.</summary>
    public sealed class ModSettingsSearchStateVisibilityConverter : IMultiValueConverter
    {
        /// <summary>Combines the original text filter with current checkbox/slider state. Section rows remain visible only when a child setting matches both constraints.</summary>
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 9) return Visibility.Collapsed;
            ModSettingsSearchStateFilter filter = values[1] is ModSettingsSearchStateFilter selected ? selected : ModSettingsSearchStateFilter.All;
            if (filter == ModSettingsSearchStateFilter.All)
                return ModSettingsSearchVisibilityConverter.ConvertCore(values, 3);
            object model = values[0];
            for (int index = 6; index + 2 < values.Length; index += 5)
            {
                if (ModSettingsSearchCatalog.MatchesPresentation(model, values[index]?.ToString(), values[3]?.ToString(),
                    values[4] is bool tips && tips, values[5]?.ToString(), filter)) return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }
        /// <summary>Reverse conversion is unsupported.</summary>
        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

}
#endif

using APIShared.Internal;
using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;

namespace APIShared.ModSettings
{
    /// <summary>Explicit opt-in catalog for settings search. Contains managed metadata only; never loads or traverses a Noesis view.</summary>
    /// <remarks>Register and read on the Unity thread. Registrations retain the ViewModel and optional navigation callback for process lifetime. No SerpsModsHost or preset base class is required.</remarks>
    public static class ModSettingsSearchCatalog
    {
        private static readonly Dictionary<object, SearchSource> Sources =
            new Dictionary<object, SearchSource>(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<object> Publishing = new HashSet<object>(ReferenceEqualityComparer.Instance);
        private static readonly HashSet<object> Navigating = new HashSet<object>(ReferenceEqualityComparer.Instance);

        /// <summary>Raised synchronously on the registering Unity thread after a source is added or replaced. The argument is its ViewModel. Listener failures are isolated and logged.</summary>
        public static event Action<object> SourceChanged;

        /// <summary>Registers or replaces a ViewModel's XAML source and invalidates its cached snapshot. Invalid arguments throw before replacing a source.</summary>
        /// <remarks>Use the resolved absolute XAML filename and the same ViewModel as the registered tab. Register on the Unity thread, preferably before the tab. A replacement refreshes localized/dynamic ItemsSource metadata on the next read. Re-registering the same model from its SourceChanged notification throws InvalidOperationException before replacement, preventing recursive notification loops. Optional navigate receives the exact registered key and current title on the Unity thread; return true when a local filter was applied. It must not traverse, realize, measure or focus other views. Failures are logged once per registration and return false, leaving ordinary tab opening to the host.</remarks>
        public static void RegisterSource(object viewModel, string absoluteXamlPath, ManualLogSource log,
            string modName, Func<string, string, bool> navigate = null)
        {
            if (viewModel == null) throw new ArgumentNullException(nameof(viewModel));
            if (string.IsNullOrWhiteSpace(absoluteXamlPath) || !Path.IsPathRooted(absoluteXamlPath))
                throw new ArgumentException("An absolute XAML filename is required.", nameof(absoluteXamlPath));
            RegisterSourceCore(viewModel, absoluteXamlPath, log, modName, navigate);
        }

        // The original facade accepted relative filenames; preserve that behavior for existing
        // callers while the new API makes resolved source ownership explicit.
        internal static void RegisterSourceCore(object viewModel, string absoluteXamlPath, ManualLogSource log,
            string modName, Func<string, string, bool> navigate = null)
        {
            if (Publishing.Contains(viewModel))
                throw new InvalidOperationException("A source cannot be replaced from its own search-source notification.");
            var source = new SearchSource(absoluteXamlPath, log, modName, navigate);
            Sources[viewModel] = source;
            Action<object> listeners = SourceChanged;
            if (listeners == null) return;
            Publishing.Add(viewModel);
            try
            {
                foreach (Action<object> listener in listeners.GetInvocationList())
                {
                    try { listener(viewModel); }
                    catch (Exception ex) { source.Report($"Mod-settings search source observer failed: {ex.Message}"); }
                }
            }
            finally { Publishing.Remove(viewModel); }
        }

        /// <summary>Checks explicit support without evaluating consumer properties. Read on the Unity thread.</summary>
        public static bool IsRegistered(object viewModel) => viewModel != null && Sources.ContainsKey(viewModel);

        /// <summary>Reads immutable entries on the Unity thread. False means unregistered; true may contain zero entries, including a failed catalog logged once per registration. No UI-tree fallback is performed.</summary>
        public static bool TryGetEntries(object viewModel, out IReadOnlyList<ModSettingsSearchEntry> entries)
        {
            entries = Array.Empty<ModSettingsSearchEntry>();
            if (viewModel == null || !Sources.TryGetValue(viewModel, out SearchSource source)) return false;
            entries = source.GetEntries(viewModel);
            return true;
        }

        /// <summary>Invokes an optional registered navigation callback for a current exact key on the Unity thread. Missing support, stale keys, recursive navigation, no callback or callback failure return false. Titles come from the current catalog, not from the caller.</summary>
        public static bool TryNavigate(object viewModel, string key)
        {
            if (viewModel == null || !Sources.TryGetValue(viewModel, out SearchSource source)) return false;
            if (!Navigating.Add(viewModel)) return false;
            try
            {
                ModSettingsSearchEntry entry = source.GetEntries(viewModel).FirstOrDefault(value =>
                    string.Equals(value.Key, key, StringComparison.Ordinal));
                // Consumer getters can replace their registration while building a catalog.
                return entry != null && Sources.TryGetValue(viewModel, out SearchSource current) &&
                    ReferenceEquals(source, current) && source.TryNavigate(entry.Key, entry.Title);
            }
            finally { Navigating.Remove(viewModel); }
        }
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

    internal sealed class SearchSource
    {
        private readonly string path;
        private readonly BepInEx.Logging.ManualLogSource log;
        private readonly string modName;
        private object cachedViewModel;
        private IReadOnlyList<ModSettingsSearchEntry> cachedEntries;
        private bool building;
        private bool navigationFailureReported;
        internal readonly Func<string, string, bool> Navigate;

        internal void Report(string message) => DebugLogHelper.LogWarning(log, $"[{modName}] {message}");

        public SearchSource(
            string path,
            BepInEx.Logging.ManualLogSource log,
            string modName,
            Func<string, string, bool> navigate)
        {
            Navigate = navigate;
            this.path = path;
            this.log = log;
            this.modName = modName ?? string.Empty;
        }

        public IReadOnlyList<ModSettingsSearchEntry> GetEntries(object viewModel)
        {
            if (ReferenceEquals(cachedViewModel, viewModel) && cachedEntries != null)
                return cachedEntries;
            // A consumer getter may call search again while its catalog is being evaluated.
            if (building)
                return Array.Empty<ModSettingsSearchEntry>();
            building = true;
            try
            {
                cachedEntries = BuildCatalog(path, viewModel);
                cachedViewModel = viewModel;
            }
            catch (Exception ex)
            {
                // Publish the failed snapshot before logging: a log observer may reenter.
                cachedEntries = Array.Empty<ModSettingsSearchEntry>();
                cachedViewModel = viewModel;
                Report($"Could not build mod-settings search catalog from [{Path.GetFileName(path)}]: {ex.Message}");
            }
            finally { building = false; }
            return cachedEntries;
        }

        internal bool TryNavigate(string key, string title)
        {
            if (Navigate == null) return false;
            try { return Navigate(key, title); }
            catch (Exception ex)
            {
                if (!navigationFailureReported)
                {
                    navigationFailureReported = true;
                    Report($"Mod-settings search navigation failed: {ex.Message}");
                }
                return false;
            }
        }

        private static IReadOnlyList<ModSettingsSearchEntry> BuildCatalog(
            string path,
            object rootViewModel)
        {
            XDocument document = XDocument.Load(path, LoadOptions.None);
            var result = new List<ModSettingsSearchEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (XElement element in document.Descendants())
            {
                if (IsExcluded(element))
                    continue;
                string localName = element.Name.LocalName;
                bool interactive = localName == "Button" || localName == "CheckBox" ||
                    localName == "ComboBox" || localName == "Slider" || localName == "TextBox";
                XAttribute keyAttribute = FindAttribute(element, "Key");
                XAttribute titleAttribute = FindAttribute(element, "Title");
                bool isSection = string.Equals(
                    FindAttribute(element, "IsSection")?.Value,
                    "True",
                    StringComparison.OrdinalIgnoreCase);
                bool hasExplicitAncestor = element.Ancestors().Any(value =>
                    FindAttribute(value, "Key") != null || FindAttribute(value, "Title") != null);
                if (interactive && keyAttribute == null && titleAttribute == null && hasExplicitAncestor)
                    continue;
                if (!isSection && !interactive && keyAttribute == null && titleAttribute == null)
                    continue;

                foreach (object dataContext in ResolveDataContexts(element, rootViewModel))
                {
                    XElement sectionOwner = FindSectionOwner(element);
                    object sectionDataContext = IsInsideDataTemplate(sectionOwner)
                        ? dataContext
                        : rootViewModel;
                    string sectionKey = ResolveValue(
                        FindAttribute(sectionOwner, "SectionKey")?.Value,
                        sectionDataContext);
                    string sectionTitle = ResolveValue(
                        FindAttribute(sectionOwner, "SectionTitle")?.Value,
                        sectionDataContext);
                    if (isSection)
                    {
                        if (sectionKey.Length == 0 || sectionTitle.Length == 0 || !keys.Add(sectionKey))
                            continue;
                        result.Add(new ModSettingsSearchEntry(
                            sectionKey,
                            sectionTitle,
                            string.Empty,
                            sectionKey,
                            sectionTitle,
                            true));
                        continue;
                    }

                    string title = ResolveValue(titleAttribute?.Value, dataContext);
                    if (title.Length == 0)
                        title = ResolveElementContent(element, dataContext);
                    string toolTip = ResolveValue(FindAttribute(element, "ToolTipText")?.Value, dataContext);
                    if (toolTip.Length == 0)
                        toolTip = ResolveToolTip(element, dataContext);
                    if (title.Length == 0)
                        title = FindNearbyTitle(element, dataContext);
                    if (title.Length == 0 && toolTip.Length > 0)
                        title = ModSettingsSearchText.FirstSentence(toolTip);
                    if (title.Length == 0 || toolTip.Length == 0)
                        continue;

                    string key = ResolveValue(keyAttribute?.Value, dataContext);
                    if (key.Length == 0)
                    {
                        string toolTipPath = ReadBindingPath(FindAttribute(element, "ToolTip")?.Value);
                        string seed = toolTipPath.Length > 0
                            ? toolTipPath + ":" + title
                            : title + ":" + toolTip;
                        key = "auto:" + ModSettingsSearchText.StableHash(seed).ToString("x8", CultureInfo.InvariantCulture);
                    }
                    string identity = title + "\u001f" + toolTip;
                    if (!identities.Add(identity))
                        continue;
                    string uniqueKey = key;
                    int suffix = 2;
                    while (!keys.Add(uniqueKey))
                        uniqueKey = key + ":" + suffix++;
                    result.Add(new ModSettingsSearchEntry(
                        uniqueKey,
                        title,
                        toolTip,
                        sectionKey,
                        sectionTitle,
                        false));
                }
            }
            return result.AsReadOnly();
        }

        private static IEnumerable<object> ResolveDataContexts(XElement element, object root)
        {
            XElement template = element.Ancestors().FirstOrDefault(value =>
                value.Name.LocalName == "DataTemplate");
            if (template == null)
                return new[] { root };
            XElement itemsControl = template.Ancestors().FirstOrDefault(value =>
                value.Name.LocalName == "ItemsControl");
            string path = ReadBindingPath(FindAttribute(itemsControl, "ItemsSource")?.Value);
            object items = ReadPropertyPath(root, path);
            if (items is System.Collections.IEnumerable enumerable && !(items is string))
                return enumerable.Cast<object>().Where(value => value != null).ToArray();
            return Array.Empty<object>();
        }

        private static XElement FindSectionOwner(XElement element) =>
            element?.AncestorsAndSelf().FirstOrDefault(value =>
                FindAttribute(value, "SectionKey") != null ||
                FindAttribute(value, "SectionTitle") != null);

        private static bool IsInsideDataTemplate(XElement element) =>
            element != null && element.AncestorsAndSelf().Any(value =>
                value.Name.LocalName == "DataTemplate");

        private static bool IsExcluded(XElement element) =>
            element.AncestorsAndSelf().Any(value =>
                string.Equals(FindAttribute(value, "Exclude")?.Value, "True", StringComparison.OrdinalIgnoreCase));

        private static string ResolveToolTip(XElement element, object dataContext)
        {
            string result = ResolveValue(FindAttribute(element, "ToolTip")?.Value, dataContext);
            if (result.Length > 0)
                return result;
            XElement property = element.Elements().FirstOrDefault(value =>
                value.Name.LocalName.EndsWith(".ToolTip", StringComparison.Ordinal));
            if (property == null)
                return string.Empty;
            foreach (XElement value in property.DescendantsAndSelf())
            {
                result = ResolveValue(FindAttribute(value, "Text")?.Value, dataContext);
                if (result.Length > 0)
                    return result;
                result = ResolveValue(FindAttribute(value, "Content")?.Value, dataContext);
                if (result.Length > 0)
                    return result;
            }
            return string.Empty;
        }

        private static string ResolveElementContent(XElement element, object dataContext)
        {
            string result = ResolveValue(FindAttribute(element, "Content")?.Value, dataContext);
            if (result.Length > 0)
                return result;

            IEnumerable<XElement> candidates = element.Elements().Where(value =>
                value.Name.LocalName == "TextBlock" ||
                value.Name.LocalName.EndsWith(".Content", StringComparison.Ordinal));
            foreach (XElement container in candidates)
            {
                foreach (XElement candidate in container.DescendantsAndSelf())
                {
                    if (candidate.Name.LocalName != "TextBlock")
                        continue;
                    result = ResolveValue(FindAttribute(candidate, "Text")?.Value, dataContext);
                    if (result.Length > 0)
                        return result;
                }
            }
            return string.Empty;
        }

        private static string FindNearbyTitle(XElement element, object dataContext)
        {
            string row = FindAttribute(element, "Row")?.Value ?? "0";
            XElement current = element;
            for (int level = 0; level < 4 && current.Parent != null; level++)
            {
                XElement parent = current.Parent;
                foreach (XElement sibling in parent.Elements())
                {
                    if (ReferenceEquals(sibling, current))
                        continue;
                    string siblingRow = FindAttribute(sibling, "Row")?.Value ?? "0";
                    if (parent.Name.LocalName == "Grid" && siblingRow != row)
                        continue;
                    foreach (XElement candidate in sibling.DescendantsAndSelf())
                    {
                        if (candidate.Name.LocalName != "TextBlock")
                            continue;
                        string text = ResolveValue(FindAttribute(candidate, "Text")?.Value, dataContext);
                        if (text.Length > 0)
                            return text;
                    }
                }
                current = parent;
                row = FindAttribute(current, "Row")?.Value ?? "0";
            }
            return string.Empty;
        }

        private static XAttribute FindAttribute(XElement element, string localName) =>
            element?.Attributes().FirstOrDefault(attribute =>
                attribute.Name.LocalName == localName ||
                attribute.Name.LocalName.EndsWith("." + localName, StringComparison.Ordinal));

        private static string ResolveValue(string xamlValue, object dataContext)
        {
            if (string.IsNullOrWhiteSpace(xamlValue))
                return string.Empty;
            string path = ReadBindingPath(xamlValue);
            if (xamlValue.TrimStart().StartsWith("{Binding", StringComparison.Ordinal) &&
                path.Length == 0)
            {
                return string.Empty;
            }
            if (path.Length == 0)
            {
                string text = xamlValue.Trim();
                // "{}" escapes literal text; any other markup extension (TemplateBinding,
                // StaticResource, x:Static ...) is not resolvable here and is no title.
                if (text.StartsWith("{}", StringComparison.Ordinal))
                    return text.Substring(2).Trim();
                return text.StartsWith("{", StringComparison.Ordinal) ? string.Empty : text;
            }
            return ReadPropertyPath(dataContext, path)?.ToString()?.Trim() ?? string.Empty;
        }

        private static string ReadBindingPath(string value)
        {
            string text = value?.Trim() ?? string.Empty;
            if (!text.StartsWith("{Binding", StringComparison.Ordinal) ||
                !text.EndsWith("}", StringComparison.Ordinal))
                return string.Empty;
            text = text.Substring(8, text.Length - 9).Trim();
            foreach (string part in text.Split(','))
            {
                string token = part.Trim();
                if (token.StartsWith("Path=", StringComparison.OrdinalIgnoreCase))
                    return token.Substring(5).Trim();
                if (token.Length > 0 && token.IndexOf('=') < 0)
                    return token;
            }
            return string.Empty;
        }

        private static object ReadPropertyPath(object instance, string path)
        {
            object current = instance;
            foreach (string segment in (path ?? string.Empty).Split('.'))
            {
                if (current == null || segment.Length == 0)
                    return null;
                PropertyInfo property = current.GetType().GetProperty(
                    segment,
                    BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanRead)
                    return null;
                current = property.GetValue(current, null);
            }
            return current;
        }
    }

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}

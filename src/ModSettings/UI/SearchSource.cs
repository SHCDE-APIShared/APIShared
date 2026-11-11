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
    internal sealed class SearchSource
    {
        private readonly string path;
        private readonly BepInEx.Logging.ManualLogSource log;
        private readonly string modName;
        private object cachedViewModel;
        private IReadOnlyList<ModSettingsSearchEntry> cachedEntries;
        private bool building;
        private bool navigationFailureReported;
        private readonly Dictionary<string, List<SearchValueBinding>> stateBindings =
            new Dictionary<string, List<SearchValueBinding>>(StringComparer.Ordinal);
        private readonly Dictionary<object, Dictionary<string, HashSet<SearchValueBinding>>> observed =
            new Dictionary<object, Dictionary<string, HashSet<SearchValueBinding>>>(ReferenceEqualityComparer.Instance);
        private readonly HashSet<SearchValueBinding> rebind = new HashSet<SearchValueBinding>();
        private readonly System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<object, string>> foreignChanges =
            new System.Collections.Concurrent.ConcurrentQueue<KeyValuePair<object, string>>();
        private readonly Action flushState;
        private readonly Action drainForeignChanges;
        private int foreignQueued;
        private bool notificationPending;
        private readonly int threadId = System.Threading.Thread.CurrentThread.ManagedThreadId;
        private bool notifyingState;
        private bool retired;
        private readonly HashSet<string> readingStates = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ModSettingsSearchEntry>> presentationIndex =
            new Dictionary<string, List<ModSettingsSearchEntry>>(StringComparer.Ordinal);
        private readonly Dictionary<string, ModSettingsSearchState> presentationStates =
            new Dictionary<string, ModSettingsSearchState>(StringComparer.Ordinal);
        internal readonly Func<string, string, bool> Navigate;

        internal void Report(string message) => NativeApiLog.Warning(log, $"[{modName}] {message}");

        public SearchSource(
            string path,
            BepInEx.Logging.ManualLogSource log,
            string modName,
            Func<string, string, bool> navigate)
        {
            Navigate = navigate;
            flushState = FlushState;
            drainForeignChanges = DrainForeignChanges;
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
                foreach (ModSettingsSearchEntry entry in cachedEntries)
                {
                    if (entry.IsSection) continue;
                    AddPresentationEntry(entry.Key, entry);
                    if (entry.SectionKey.Length != 0 && entry.SectionKey != entry.Key) AddPresentationEntry(entry.SectionKey, entry);
                }
            }
            catch (Exception ex)
            {
                // Publish the failed snapshot before logging: a log observer may reenter.
                cachedEntries = Array.Empty<ModSettingsSearchEntry>();
                stateBindings.Clear();
                presentationIndex.Clear();
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

        private void AddPresentationEntry(string key, ModSettingsSearchEntry entry)
        {
            if (!presentationIndex.TryGetValue(key, out var entries)) presentationIndex.Add(key, entries = new List<ModSettingsSearchEntry>());
            entries.Add(entry);
        }

        internal bool MatchesPresentation(string key, string text, bool tips, string exact, ModSettingsSearchStateFilter filter)
        {
            if (retired || !presentationIndex.TryGetValue(key, out var entries)) return false;
            foreach (ModSettingsSearchEntry entry in entries)
            {
                if (!ModSettingsSearchMatcher.IsMatch(text, tips, exact, entry.Key, entry.Title, entry.ToolTip, entry.SectionKey, entry.SectionTitle)) continue;
                ModSettingsSearchState state;
                if (!notifyingState || notificationPending || !presentationStates.TryGetValue(entry.Key, out state))
                {
                    state = GetState(entry.Key);
                    if (notifyingState && !notificationPending) presentationStates[entry.Key] = state;
                }
                if (ModSettingsSearchStateMatcher.IsMatch(filter, state)) return true;
            }
            return false;
        }

        private IReadOnlyList<ModSettingsSearchEntry> BuildCatalog(
            string path,
            object rootViewModel)
        {
            XDocument document = XDocument.Load(path, LoadOptions.None);
            var result = new List<ModSettingsSearchEntry>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var identities = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            var identityKeys = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
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
                        key = ModSettingsSearchText.AutomaticKey(
                            ReadBindingPath(FindAttribute(element, "ToolTip")?.Value), title, toolTip);
                    string identity = ModSettingsSearchText.Identity(title, toolTip);
                    if (!identities.Add(identity))
                    {
                        // Automatically grouped slider/textbox or paired controls share one logical hit.
                        if (identityKeys.TryGetValue(identity, out string existingKey))
                            stateBindings[existingKey].AddRange(ReadStateBindings(element, dataContext));
                        continue;
                    }
                    string uniqueKey = ModSettingsSearchText.ReserveUniqueKey(keys, key);
                    result.Add(new ModSettingsSearchEntry(
                        uniqueKey,
                        title,
                        toolTip,
                        sectionKey,
                        sectionTitle,
                        false));
                    stateBindings[uniqueKey] = ReadStateBindings(element, dataContext).ToList();
                    identityKeys[identity] = uniqueKey;
                }
            }
            return result.AsReadOnly();
        }

        // Capture binding descriptions, not values: metadata remains a snapshot while status is live.
        private static IEnumerable<SearchValueBinding> ReadStateBindings(XElement owner, object context)
        {
            foreach (XElement control in owner.DescendantsAndSelf())
            {
                if (IsExcluded(control) || (control != owner && control.Ancestors().TakeWhile(value => value != owner).Any(value =>
                    value != owner && (FindAttribute(value, "Key") != null || FindAttribute(value, "Title") != null)))) continue;
                string name = control.Name.LocalName;
                if (name != "CheckBox" && name != "Slider" && name != "TextBox") continue;
                // A nested logical setting owns its own controls.
                if (control != owner && (FindAttribute(control, "Key") != null || FindAttribute(control, "Title") != null)) continue;
                XElement template = control.Ancestors().FirstOrDefault(value => value.Name.LocalName == "DataTemplate");
                // Template item contexts are resolved by the catalog. Other context overrides
                // are not inferred; never accidentally classify a same-named root property.
                if (control.AncestorsAndSelf().TakeWhile(value => value != template).Any(value => FindAttribute(value, "DataContext") != null)) continue;
                // Text boxes opt in explicitly through the authoritative numeric property;
                // formatted Text bindings keep their previous unknown-state behavior.
                string expression = FindAttribute(control, name == "TextBox" ? "StateValue" : name == "CheckBox" ? "IsChecked" : "Value")?.Value;
                string bindingPath = ReadBindingPath(expression);
                // Source/ElementName/RelativeSource and converters cannot be reproduced by reading the model.
                if (bindingPath.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(expression,
                    @"\b(?:Converter|Source|RelativeSource|ElementName)\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                yield return new SearchValueBinding(context, bindingPath, name == "CheckBox",
                    FindAttribute(control, "InactiveValues")?.Value);
            }
        }

        internal ModSettingsSearchState GetState(string key)
        {
            if (retired || cachedEntries == null ||
                !stateBindings.TryGetValue(key, out List<SearchValueBinding> bindings)) return ModSettingsSearchState.Unknown;
            if (!readingStates.Add(key)) return ModSettingsSearchState.Unknown;
            try
            {
                ModSettingsSearchState state = ModSettingsSearchState.Unknown;
                foreach (SearchValueBinding binding in bindings)
                {
                    try
                    {
                        state |= binding.Classify(binding.ReadValue(this, true));
                    }
                    catch { /* A failed value must not remove other values or poison the metadata catalog. */ }
                }
                return retired ? ModSettingsSearchState.Unknown : state;
            }
            finally { readingStates.Remove(key); }
        }

        private void SetBindingOwner(SearchValueBinding binding, int index, object model)
        {
            if (retired) model = null;
            object previous = binding.Owners[index];
            if (ReferenceEquals(previous, model)) return;
            binding.Owners[index] = model;
            string property = binding.Segments[index];
            if (previous is INotifyPropertyChanged oldNotifier && observed.TryGetValue(previous, out var oldNames))
            {
                bool stillUsed = false;
                for (int slot = 0; slot < binding.Owners.Length; slot++)
                    if (ReferenceEquals(binding.Owners[slot], previous) && binding.Segments[slot] == property) { stillUsed = true; break; }
                if (!stillUsed && oldNames.TryGetValue(property, out var oldBindings))
                {
                    oldBindings.Remove(binding);
                    if (oldBindings.Count == 0) oldNames.Remove(property);
                    if (oldNames.Count == 0) { oldNotifier.PropertyChanged -= OnValueChanged; observed.Remove(previous); }
                }
            }
            if (!(model is INotifyPropertyChanged notifier)) return;
            if (!observed.TryGetValue(model, out var names))
            {
                observed.Add(model, names = new Dictionary<string, HashSet<SearchValueBinding>>(StringComparer.Ordinal));
                notifier.PropertyChanged += OnValueChanged;
            }
            if (!names.TryGetValue(property, out var bindings)) names.Add(property, bindings = new HashSet<SearchValueBinding>());
            bindings.Add(binding);
        }

        internal void DetachStateObservers()
        {
            foreach (INotifyPropertyChanged notifier in observed.Keys) notifier.PropertyChanged -= OnValueChanged;
            observed.Clear();
            foreach (var bindings in stateBindings.Values)
                foreach (SearchValueBinding binding in bindings) Array.Clear(binding.Owners, 0, binding.Owners.Length);
            rebind.Clear();
        }

        internal void Retire() { retired = true; DetachStateObservers(); }

        private void OnValueChanged(object sender, PropertyChangedEventArgs args)
        {
            // Inspect subscriptions only on their owning Unity thread, even if a consumer publishes off-thread.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId != threadId)
            {
                foreignChanges.Enqueue(new KeyValuePair<object, string>(sender, args.PropertyName));
                QueueForeignChanges();
                return;
            }
            if (CollectChange(sender, args.PropertyName)) RequestStateRefresh();
        }

        private bool CollectChange(object sender, string property)
        {
            if (retired || !observed.TryGetValue(sender, out var names)) return false;
            if (string.IsNullOrEmpty(property))
            {
                foreach (var bindings in names.Values) foreach (SearchValueBinding binding in bindings) rebind.Add(binding);
            }
            else
            {
                if (!names.TryGetValue(property, out var bindings)) return false;
                foreach (SearchValueBinding binding in bindings)
                    if (binding.HasIntermediateOwner(sender, property)) rebind.Add(binding);
            }
            notificationPending = true;
            return true;
        }

        private void QueueForeignChanges()
        {
            if (System.Threading.Interlocked.CompareExchange(ref foreignQueued, 1, 0) != 0) return;
            if (!UnityMainThreadDispatch.TryEnqueue(drainForeignChanges))
                System.Threading.Interlocked.Exchange(ref foreignQueued, 0);
        }

        private void DrainForeignChanges()
        {
            try
            {
                while (foreignChanges.TryDequeue(out var change)) CollectChange(change.Key, change.Value);
                if (notificationPending) RequestStateRefresh();
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref foreignQueued, 0);
                if (!foreignChanges.IsEmpty) QueueForeignChanges();
            }
        }

        private void RequestStateRefresh()
        {
            if (!ModSettingsSearchUpdateScope.TryDefer(cachedViewModel, flushState)) FlushState();
        }

        private void FlushState()
        {
            if (retired || notifyingState) return;
            notifyingState = true;
            try
            {
                int passes = 0;
                while (notificationPending && !retired)
                {
                    if (++passes > 8) { notificationPending = false; Report("Reentrant settings state changes did not settle; retrying on the next value change."); break; }
                    notificationPending = false;
                    presentationStates.Clear();
                    if (rebind.Count != 0)
                    {
                        SearchValueBinding[] changedPaths = rebind.ToArray();
                        rebind.Clear();
                        foreach (SearchValueBinding binding in changedPaths)
                        {
                            try { binding.ReadValue(this, false); }
                            catch { /* Failed paths remain retryable through their observable prefix. */ }
                        }
                    }
                    ModSettingsSearchCatalog.PublishState(cachedViewModel, this);
                }
            }
            finally { notifyingState = false; }
        }

        private sealed class SearchValueBinding
        {
            internal readonly object Context;
            internal readonly string Path;
            internal readonly string[] Segments;
            internal readonly object[] Owners;
            private readonly Type[] ownerTypes;
            private readonly PropertyInfo[] properties;
            private readonly bool checkbox;
            private readonly double[][] inactive;
            internal SearchValueBinding(object context, string path, bool checkbox, string inactiveValues)
            {
                Context = context; Path = path; this.checkbox = checkbox;
                Segments = path.Split('.');
                Owners = new object[Segments.Length];
                ownerTypes = new Type[Segments.Length];
                properties = new PropertyInfo[Segments.Length];
                if (inactiveValues == null) { inactive = new[] { new[] { -1d, -1d }, new[] { 0d, 0d } }; return; }
                var numbers = new List<double[]>();
                foreach (string token in inactiveValues.Split(';'))
                {
                    string[] range = token.Split(new[] { ".." }, StringSplitOptions.None);
                    if (range.Length > 2 || !double.TryParse(range[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double low) ||
                        double.IsNaN(low) || double.IsInfinity(low)) return;
                    double high = low;
                    if (range.Length == 2 && (!double.TryParse(range[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out high) ||
                        double.IsNaN(high) || double.IsInfinity(high) || high < low)) return;
                    numbers.Add(new[] { low, high });
                }
                inactive = numbers.ToArray();
            }

            internal bool HasIntermediateOwner(object owner, string property)
            {
                for (int index = 0; index + 1 < Owners.Length; index++)
                    if (ReferenceEquals(Owners[index], owner) && Segments[index] == property) return true;
                return false;
            }

            internal object ReadValue(SearchSource source, bool readLeaf)
            {
                object current = Context;
                int index = 0;
                try
                {
                    for (; index < Segments.Length; index++)
                    {
                        source.SetBindingOwner(this, index, current);
                        if (current == null) return null;
                        Type type = current.GetType();
                        if (ownerTypes[index] != type)
                        {
                            ownerTypes[index] = type;
                            properties[index] = null;
                            PropertyInfo property = type.GetProperty(Segments[index], BindingFlags.Instance | BindingFlags.Public);
                            properties[index] = property != null && property.CanRead && property.GetIndexParameters().Length == 0 ? property : null;
                        }
                        if (!readLeaf && index == Segments.Length - 1) return null;
                        current = properties[index]?.GetValue(current, null);
                    }
                    return current;
                }
                finally
                {
                    // A replaced/null/throwing intermediate must release the old suffix only.
                    for (int suffix = index + 1; suffix < Segments.Length; suffix++) source.SetBindingOwner(this, suffix, null);
                }
            }
            internal ModSettingsSearchState Classify(object value)
            {
                if (checkbox) return value is bool enabled
                    ? (enabled ? ModSettingsSearchState.Active : ModSettingsSearchState.Inactive) : ModSettingsSearchState.Unknown;
                if (inactive == null || value == null || value is bool || value is string || value.GetType().IsEnum || !(value is IConvertible))
                    return ModSettingsSearchState.Unknown;
                TypeCode type = Type.GetTypeCode(value.GetType());
                if (type < TypeCode.SByte || type > TypeCode.Decimal) return ModSettingsSearchState.Unknown;
                double number = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) return ModSettingsSearchState.Unknown;
                return inactive.Any(range => number >= range[0] && number <= range[1]) ? ModSettingsSearchState.Inactive : ModSettingsSearchState.Active;
            }
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
}

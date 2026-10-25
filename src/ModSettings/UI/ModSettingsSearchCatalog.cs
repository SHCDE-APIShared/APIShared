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

        /// <summary>Raised on the Unity thread when an observed checkbox/slider binding changes. The argument is the registered root model; listener failures are isolated. Metadata is not rebuilt.</summary>
        public static event Action<object> StateChanged;

        /// <summary>Reads current configured values for an exact catalog key on the Unity thread. Unknown keys, unsupported controls and failed getters return Unknown. Mixed rows can contain both flags. Does not infer parent activation or editability.</summary>
        public static ModSettingsSearchState GetState(object viewModel, string key)
        {
            if (viewModel == null || key == null || !Sources.TryGetValue(viewModel, out SearchSource source))
                return ModSettingsSearchState.Unknown;
            source.GetEntries(viewModel);
            return Sources.TryGetValue(viewModel, out SearchSource current) && ReferenceEquals(source, current)
                ? source.GetState(key) : ModSettingsSearchState.Unknown;
        }

        internal static void PublishState(object model, SearchSource source)
        {
            if (!Sources.TryGetValue(model, out SearchSource current) || !ReferenceEquals(source, current)) return;
            Action<object> listeners = StateChanged;
            if (listeners == null) return;
            foreach (Action<object> listener in listeners.GetInvocationList())
            {
                try { listener(model); }
                catch (Exception ex) { source.Report($"Mod-settings search state observer failed: {ex.Message}"); }
            }
        }

        internal static bool MatchesPresentation(object model, string key, string text, bool tips, string exact,
            ModSettingsSearchStateFilter filter)
        {
            if (model == null || key == null || !Sources.TryGetValue(model, out SearchSource source)) return false;
            source.GetEntries(model);
            return Sources.TryGetValue(model, out SearchSource current) && ReferenceEquals(source, current) &&
                source.MatchesPresentation(key, text, tips, exact, filter);
        }

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
            if (Sources.TryGetValue(viewModel, out SearchSource previous)) previous.Retire();
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

        internal void Report(string message) => DebugLogHelper.LogWarning(log, $"[{modName}] {message}");

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
                    {
                        string toolTipPath = ReadBindingPath(FindAttribute(element, "ToolTip")?.Value);
                        string seed = toolTipPath.Length > 0
                            ? toolTipPath + ":" + title
                            : title + ":" + toolTip;
                        key = "auto:" + ModSettingsSearchText.StableHash(seed).ToString("x8", CultureInfo.InvariantCulture);
                    }
                    string identity = title + "\u001f" + toolTip;
                    if (!identities.Add(identity))
                    {
                        // Automatically grouped slider/textbox or paired controls share one logical hit.
                        if (identityKeys.TryGetValue(identity, out string existingKey))
                            stateBindings[existingKey].AddRange(ReadStateBindings(element, dataContext));
                        continue;
                    }
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
                if (name != "CheckBox" && name != "Slider") continue;
                // A nested logical setting owns its own controls.
                if (control != owner && (FindAttribute(control, "Key") != null || FindAttribute(control, "Title") != null)) continue;
                XElement template = control.Ancestors().FirstOrDefault(value => value.Name.LocalName == "DataTemplate");
                // Template item contexts are resolved by the catalog. Other context overrides
                // are not inferred; never accidentally classify a same-named root property.
                if (control.AncestorsAndSelf().TakeWhile(value => value != template).Any(value => FindAttribute(value, "DataContext") != null)) continue;
                string expression = FindAttribute(control, name == "CheckBox" ? "IsChecked" : "Value")?.Value;
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

    internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object value) =>
            System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
    }
}

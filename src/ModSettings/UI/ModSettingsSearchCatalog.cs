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
}

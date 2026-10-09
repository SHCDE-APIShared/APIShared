using System;
using System.Collections.Generic;

namespace APIShared.ModSettings
{
    /// <summary>Identifies a read-only source which can be materialized into editable working settings.</summary>
    public enum ModSettingsWorkingSourceKind
    {
        /// <summary>Resolve this value from captured code defaults.</summary>
        ModDefault = 0,
        /// <summary>Read-only settings supplied by the current trail.</summary>
        Trail = 1,
        /// <summary>Read-only settings supplied by the current map.</summary>
        Map = 2,
    }

    /// <summary>One source shown by the common ModSettings source selector.</summary>
    public sealed class ModSettingsWorkingSource
    {
        /// <summary>Stable provider-local identifier, independent of the display label.</summary>
        public string Id { get; set; } = string.Empty;
        /// <summary>Localized display label; persistent identity is independent of this text.</summary>
        public string DisplayName { get; set; } = string.Empty;
        /// <summary>Default, trail or map origin shown by the common source selector.</summary>
        public ModSettingsWorkingSourceKind Kind { get; set; }
        /// <summary>Whether this source represents the provider's current mission context.</summary>
        public bool IsPreferred { get; set; }
        /// <summary>Optional stable identity of the mission context used to distinguish context changes from refreshes.</summary>
        public string PreferenceContextId { get; set; } = string.Empty;
        /// <summary>Returns the display label for selection controls; not a persistent identifier.</summary>
        public override string ToString() => DisplayName;
    }

    /// <summary>Optional provider implemented by mission-data mods such as ExtendedData.</summary>
    public interface IModSettingsWorkingSourceProvider
    {
        /// <summary>Synchronous invalidation on the notifying thread; no replay or dispatch. Subscriber exceptions propagate, so notify on the Unity thread for UI consumers.</summary>
        event Action SourcesChanged;
        /// <summary>Returns available read-only sources for the exact target plugin GUID.</summary>
        IReadOnlyList<ModSettingsWorkingSource> GetSources(string targetGuid);
        /// <summary>Materializes the selected source into the target working copy. Calls the provider outside the registry lock; missing provider or provider errors propagate.</summary>
        void Apply(string targetGuid, string sourceId);
        /// <summary>Materializes one source for several target working copies through the provider; this is not a cross-target atomicity guarantee.</summary>
        void ApplyMany(IEnumerable<string> targetGuids, string sourceId);
    }

    /// <summary>Process-wide bridge between APIShared consumers and the optional mission source provider.</summary>
    public static class ModSettingsWorkingSourceRegistry
    {
        /// <summary>Stable selector ID for code defaults.</summary>
        public const string ModDefaultsId = "mod-default";
        /// <summary>Stable selector ID for trail-owned settings.</summary>
        public const string TrailId = "trail";
        /// <summary>Stable selector ID for map-owned settings.</summary>
        public const string MapId = "map";
        private static readonly object Sync = new object();
        private static IModSettingsWorkingSourceProvider provider;
        /// <summary>Synchronous invalidation on the notifying thread; no replay or dispatch. Subscriber exceptions propagate, so notify on the Unity thread for UI consumers.</summary>
        public static event Action SourcesChanged;

        /// <summary>Retains one process-wide provider and subscribes its change event. Re-registering the same instance is harmless; a different provider throws. Notifies synchronously after publication.</summary>
        public static void Register(IModSettingsWorkingSourceProvider value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            lock (Sync)
            {
                if (provider != null && !ReferenceEquals(provider, value))
                    throw new InvalidOperationException("A ModSettings working-source provider is already registered.");
                if (ReferenceEquals(provider, value)) return;
                provider = value;
                provider.SourcesChanged += ForwardSourcesChanged;
            }
            ForwardSourcesChanged();
        }

        /// <summary>Removes only the matching provider and notifies synchronously; unrelated or null instances are ignored. This does not dispose published runtime hooks.</summary>
        public static void Unregister(IModSettingsWorkingSourceProvider value)
        {
            if (value == null) return;
            lock (Sync)
            {
                if (!ReferenceEquals(provider, value)) return;
                provider.SourcesChanged -= ForwardSourcesChanged;
                provider = null;
            }
            ForwardSourcesChanged();
        }

        /// <summary>Returns provider sources, or an empty list when no provider exists; provider exceptions propagate.</summary>
        public static IReadOnlyList<ModSettingsWorkingSource> GetProviderSources(string targetGuid)
        {
            IModSettingsWorkingSourceProvider current;
            lock (Sync) current = provider;
            return current?.GetSources(targetGuid) ?? Array.Empty<ModSettingsWorkingSource>();
        }

        /// <summary>Whether a source provider is currently registered.</summary>
        public static bool HasProvider
        {
            get { lock (Sync) return provider != null; }
        }

        /// <summary>Materializes the selected source into the target working copy. Calls the provider outside the registry lock; missing provider or provider errors propagate.</summary>
        public static void Apply(string targetGuid, string sourceId)
        {
            IModSettingsWorkingSourceProvider current;
            lock (Sync) current = provider;
            if (current == null) throw new InvalidOperationException("No mission ModSettings source provider is registered.");
            current.Apply(targetGuid, sourceId);
        }

        /// <summary>Materializes one source for several target working copies through the provider; this is not a cross-target atomicity guarantee.</summary>
        public static void ApplyMany(IEnumerable<string> targetGuids, string sourceId)
        {
            IModSettingsWorkingSourceProvider current;
            lock (Sync) current = provider;
            if (current == null) throw new InvalidOperationException("No mission ModSettings source provider is registered.");
            current.ApplyMany(targetGuids, sourceId);
        }

        private static void ForwardSourcesChanged() => SourcesChanged?.Invoke();
    }

}

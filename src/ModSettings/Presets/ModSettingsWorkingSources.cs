using System;
using System.Collections.Generic;
#pragma warning disable 1591 // Public schema members are documented by the APIShared preset guide.

namespace APIShared.ModSettings
{
    /// <summary>Identifies a read-only source which can be materialized into editable working settings.</summary>
    public enum ModSettingsWorkingSourceKind
    {
        ModDefault = 0,
        Trail = 1,
        Map = 2,
    }

    /// <summary>One source shown by the common ModSettings source selector.</summary>
    public sealed class ModSettingsWorkingSource
    {
        public string Id { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public ModSettingsWorkingSourceKind Kind { get; set; }
        /// <summary>Whether this source represents the provider's current mission context.</summary>
        public bool IsPreferred { get; set; }
        /// <summary>Optional stable identity of the mission context used to distinguish context changes from refreshes.</summary>
        public string PreferenceContextId { get; set; } = string.Empty;
        public override string ToString() => DisplayName;
    }

    /// <summary>Optional provider implemented by mission-data mods such as ExtendedData.</summary>
    public interface IModSettingsWorkingSourceProvider
    {
        event Action SourcesChanged;
        IReadOnlyList<ModSettingsWorkingSource> GetSources(string targetGuid);
        void Apply(string targetGuid, string sourceId);
        void ApplyMany(IEnumerable<string> targetGuids, string sourceId);
    }

    /// <summary>Process-wide bridge between APIShared consumers and the optional mission source provider.</summary>
    public static class ModSettingsWorkingSourceRegistry
    {
        public const string ModDefaultsId = "mod-default";
        public const string TrailId = "trail";
        public const string MapId = "map";
        private static readonly object Sync = new object();
        private static IModSettingsWorkingSourceProvider provider;
        public static event Action SourcesChanged;

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

        public static IReadOnlyList<ModSettingsWorkingSource> GetProviderSources(string targetGuid)
        {
            IModSettingsWorkingSourceProvider current;
            lock (Sync) current = provider;
            return current?.GetSources(targetGuid) ?? Array.Empty<ModSettingsWorkingSource>();
        }

        public static bool HasProvider
        {
            get { lock (Sync) return provider != null; }
        }

        public static void Apply(string targetGuid, string sourceId)
        {
            IModSettingsWorkingSourceProvider current;
            lock (Sync) current = provider;
            if (current == null) throw new InvalidOperationException("No mission ModSettings source provider is registered.");
            current.Apply(targetGuid, sourceId);
        }

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

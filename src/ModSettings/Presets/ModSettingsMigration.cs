using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace APIShared.ModSettings
{
    /// <summary>Declares the names under which a setting property was stored by earlier versions of the mod.</summary>
    /// <remarks>Put it on the renamed public instance property of the settings class. Wherever APIShared reads
    /// stored settings of that class by name (saved host settings and Trail author rules in saves, Map and Trail
    /// mission settings, the personal settings file and preset files), a value found under a former name is moved to the current property, unless
    /// that stored data already contains the current name. The former names are then gone from the data and the next
    /// write uses the current name only. List the newest former name first; the first one present wins. The stored
    /// value type must still deserialize as the property type. A former name that is still the name of another
    /// property of the class, or that two properties claim, is ignored and reported once as an error.
    /// A change of meaning or type needs <see cref="IModSettingsSnapshotMigration"/> instead.</remarks>
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class ModSettingRenamedFromAttribute : Attribute
    {
        /// <summary>Declares one or more former stored names, newest first.</summary>
        public ModSettingRenamedFromAttribute(params string[] formerNames)
        {
            FormerNames = Array.AsReadOnly((string[])(formerNames ?? Array.Empty<string>()).Clone());
        }

        /// <summary>Former stored names, newest first.</summary>
        public IReadOnlyList<string> FormerNames { get; }
    }

    /// <summary>Brings stored settings into the form of the installed mod version.</summary>
    /// <remarks>APIShared applies declared renames to saves, Trail author rules, personal settings and preset files.
    /// Its saved mission snapshot preparation also invokes the optional converter.
    /// A mod that reads stored settings of another settings class from its own format (for example mission
    /// files) calls <see cref="MigrateStoredSnapshot"/> on the raw stored values before merging them with
    /// defaults. Pure and thread-safe apart from the endpoint's own converter, which runs on the caller's thread.</remarks>
    public static partial class ModSettingsMigration
    {
        // Former name -> current property name. The array keeps the declared precedence (newest former
        // name of a property first); a dictionary alone does not guarantee that order after removals.
        internal sealed class RenameSet
        {
            internal static readonly RenameSet Empty = new RenameSet(new KeyValuePair<string, string>[0]);
            internal readonly KeyValuePair<string, string>[] Ordered;
            internal readonly Dictionary<string, string> ByFormerName = new Dictionary<string, string>(StringComparer.Ordinal);
            internal RenameSet(KeyValuePair<string, string>[] ordered)
            {
                Ordered = ordered;
                foreach (KeyValuePair<string, string> rename in ordered) ByFormerName[rename.Key] = rename.Value;
            }
        }

        private static readonly ConcurrentDictionary<Type, RenameSet> Renames = new ConcurrentDictionary<Type, RenameSet>();
        // Installed by the plugin; this file stays free of runtime dependencies for the test projects
        // that compile the preset sources directly.
        internal static Action<string> ReportProblem;

        /// <summary>Applies the declared renames of the settings object's class and then its optional
        /// <see cref="IModSettingsSnapshotMigration"/> converter. Without a converter, returns the argument itself
        /// when no names change. A converter receives a detached dictionary and byte buffers with the same
        /// comparer; its result is returned unchanged. This call never modifies the argument or its buffers,
        /// including when the converter throws. Returned values are not guaranteed to remain independent
        /// of the argument when no converter runs.</summary>
        /// <exception cref="InvalidDataException">The converter returned null.</exception>
        public static Dictionary<string, byte[]> MigrateStoredSnapshot(object settings, Dictionary<string, byte[]> stored)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (stored == null) throw new ArgumentNullException(nameof(stored));
            var result = ApplyRenames(settings.GetType(), stored);
            if (settings is IModSettingsSnapshotMigration migration)
            {
                // Converters may update their snapshot in place; even a renamed dictionary still
                // shares its serialized buffers with the caller until those buffers are copied.
                var detached = new Dictionary<string, byte[]>(result.Count, result.Comparer);
                foreach (var entry in result)
                    detached.Add(entry.Key, entry.Value == null ? null : (byte[])entry.Value.Clone());
                result = migration.System_MigrateMissionSnapshot(detached) ??
                    throw new InvalidDataException("Settings migration returned nothing: " + settings.GetType().FullName);
            }
            return result;
        }

        // Moves values stored under former names to their current property names. The same instance is
        // returned when the class declares no rename or the data holds no former name.
        internal static Dictionary<string, T> ApplyRenames<T>(Type settingsType, Dictionary<string, T> stored)
        {
            if (settingsType == null || stored == null || stored.Count == 0) return stored;
            KeyValuePair<string, string>[] renames = Renames.GetOrAdd(settingsType, Build).Ordered;
            if (renames.Length == 0) return stored;
            Dictionary<string, T> result = null;
            foreach (KeyValuePair<string, string> rename in renames)
            {
                if (!stored.TryGetValue(rename.Key, out T value)) continue;
                if (result == null) result = new Dictionary<string, T>(stored, stored.Comparer);
                result.Remove(rename.Key);
                // Data that already uses the current name is newer than its leftover former entry.
                if (!result.ContainsKey(rename.Value)) result[rename.Value] = value;
            }
            return result ?? stored;
        }

        internal static IReadOnlyDictionary<string, T> ApplyRenames<T>(Type settingsType, IReadOnlyDictionary<string, T> stored)
        {
            if (settingsType == null || stored == null || stored.Count == 0 || Renames.GetOrAdd(settingsType, Build).Ordered.Length == 0)
                return stored;
            var copy = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, T> entry in stored) copy[entry.Key] = entry.Value;
            Dictionary<string, T> renamed = ApplyRenames(settingsType, copy);
            return ReferenceEquals(renamed, copy) ? stored : renamed;
        }

        // The declared renames of a settings class; empty for a null type (dynamic settings).
        internal static RenameSet RenamesOf(Type settingsType) =>
            settingsType == null ? RenameSet.Empty : Renames.GetOrAdd(settingsType, Build);

        private static RenameSet Build(Type type)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            var problems = new List<string>();
            PropertyInfo[] properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            var current = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo property in properties) current.Add(property.Name);
            var rejected = new HashSet<string>(StringComparer.Ordinal);
            foreach (PropertyInfo property in properties)
            {
                var declared = property.GetCustomAttribute<ModSettingRenamedFromAttribute>(true);
                if (declared == null) continue;
                foreach (string former in declared.FormerNames)
                {
                    if (string.IsNullOrWhiteSpace(former) || former == property.Name) continue;
                    if (current.Contains(former))
                    {
                        problems.Add(property.Name + " <- " + former + " (still a property of the class)");
                        continue;
                    }
                    if (rejected.Contains(former) || (map.TryGetValue(former, out string other) && other != property.Name))
                    {
                        // Ambiguous for every claimant: stored data cannot say which property it meant.
                        map.Remove(former); rejected.Add(former);
                        problems.Add(property.Name + " <- " + former + " (claimed by several properties)");
                        continue;
                    }
                    if (!map.ContainsKey(former)) order.Add(former);
                    map[former] = property.Name;
                }
            }
            if (problems.Count != 0)
            {
                try
                {
                    ReportProblem?.Invoke("MOD_SETTING_RENAME_IGNORED: " +
                        type.FullName + " declares former setting names that cannot be applied: " +
                        string.Join("; ", problems) + ". Values stored under these names are not migrated.");
                }
                catch { /* A declaration problem must not break reading settings. */ }
            }
            var ordered = new List<KeyValuePair<string, string>>();
            foreach (string former in order)
                if (map.TryGetValue(former, out string currentName))
                    ordered.Add(new KeyValuePair<string, string>(former, currentName));
            return new RenameSet(ordered.ToArray());
        }
    }
}

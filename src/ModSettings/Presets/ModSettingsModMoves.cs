using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using MessagePack;

namespace APIShared.ModSettings
{
    /// <summary>One unchanged stored value transferred between mod owners. Renames and semantic conversion run afterwards.</summary>
    public sealed class ModSettingMove
    {
        /// <summary>Declares the old name, destination name and serialized value type. Names use ordinal identity.</summary>
        public ModSettingMove(string sourceProperty, string targetProperty, Type valueType)
        {
            SourceProperty = RequireName(sourceProperty);
            TargetProperty = RequireName(targetProperty);
            ValueType = valueType ?? throw new ArgumentNullException(nameof(valueType));
            if (!SupportedType(valueType)) throw new ArgumentException("Moves require mission scalar or flat scalar-array types.", nameof(valueType));
        }
        /// <summary>Name stored under the source GUID.</summary>
        public string SourceProperty { get; }
        /// <summary>Name to store under the destination GUID.</summary>
        public string TargetProperty { get; }
        /// <summary>Type used to validate actual stored and fixed author values before transferring them.</summary>
        public Type ValueType { get; }
        internal static string RequireName(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) throw new ArgumentException("A nonempty identity of at most 256 characters without control characters is required.");
            return value;
        }
        private static bool SupportedType(Type type) => type == typeof(string) || type.IsEnum ||
            type == typeof(bool) || type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) ||
            type == typeof(ulong) || type == typeof(float) || type == typeof(double) || type == typeof(decimal) ||
            type.IsArray && type.GetArrayRank() == 1 && !type.GetElementType().IsArray && SupportedType(type.GetElementType());
    }

    /// <summary>Declarative, process-lifetime migration of a group from one mod to another.</summary>
    /// <remarks>Moved names are positive evidence of an old source and are removed from its detached effective entry.
    /// Copied names (such as a shared activation switch) remain with the source and never trigger migration alone.
    /// Target defaults are fixed scalar author values used only for a newly synthesized target. Real source values
    /// and rules override defaults. Player rules refer to the new owner. No personal files or live settings are read.</remarks>
    public sealed class ModSettingsModMove
    {
        internal readonly ModSettingMove[] Moved, Copied;
        internal readonly Dictionary<string, object> Defaults;
        /// <summary>Creates an immutable declaration. Target defaults accept nonnull mission scalars, not mutable objects.</summary>
        public ModSettingsModMove(string sourceModGuid, string targetModGuid, IEnumerable<ModSettingMove> movedSettings,
            IEnumerable<ModSettingMove> copiedSettings = null, IDictionary<string, object> targetDefaults = null)
        {
            SourceModGuid = ModSettingMove.RequireName(sourceModGuid);
            TargetModGuid = ModSettingMove.RequireName(targetModGuid);
            if (SourceModGuid == TargetModGuid) throw new ArgumentException("Source and target must differ.");
            Moved = (movedSettings ?? throw new ArgumentNullException(nameof(movedSettings))).ToArray();
            Copied = (copiedSettings ?? Enumerable.Empty<ModSettingMove>()).ToArray();
            var all = Moved.Concat(Copied).ToArray();
            if (Moved.Length == 0 || all.Length > SavegameModSettings.MaximumPropertiesPerMod || all.Any(item => item == null) ||
                all.Select(item => item.SourceProperty).Distinct(StringComparer.Ordinal).Count() != all.Length ||
                all.Select(item => item.TargetProperty).Distinct(StringComparer.Ordinal).Count() != all.Length)
                throw new ArgumentException("A move needs distinct source/destination settings and at least one moved setting.");
            Defaults = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var entry in targetDefaults ?? new Dictionary<string, object>())
            {
                ModSettingMove.RequireName(entry.Key);
                if (entry.Value == null || !(entry.Value is string || entry.Value.GetType().IsValueType) || !MissionModSettingsJson.IsSupportedValue(entry.Value))
                    throw new ArgumentException("Target defaults must be nonnull mission scalars.", nameof(targetDefaults));
                Defaults.Add(entry.Key, entry.Value);
            }
            if (Defaults.Count + all.Length > SavegameModSettings.MaximumPropertiesPerMod)
                throw new ArgumentException("Too many target defaults.");
            foreach (var item in all)
                if (Defaults.TryGetValue(item.TargetProperty, out var value))
                    ConvertDocumentValue(item, value);
            MovedSettings = Array.AsReadOnly(Moved);
            CopiedSettings = Array.AsReadOnly(Copied);
            TargetDefaults = new ReadOnlyDictionary<string, object>(Defaults);
        }
        /// <summary>Previous stored owner.</summary>
        public string SourceModGuid { get; }
        /// <summary>Current owner; must equal the registering owner GUID.</summary>
        public string TargetModGuid { get; }
        /// <summary>Settings whose presence positively identifies an old entry.</summary>
        public IReadOnlyList<ModSettingMove> MovedSettings { get; }
        /// <summary>Auxiliary settings copied only after positive old-entry identification.</summary>
        public IReadOnlyList<ModSettingMove> CopiedSettings { get; }
        /// <summary>Fixed initial values for the synthesized target, before source values/rules.</summary>
        public IReadOnlyDictionary<string, object> TargetDefaults { get; }
        internal static object ConvertDocumentValue(ModSettingMove item, object value)
        {
            try { return ModSettingsPresetJson.ConvertValue(value, item.ValueType); }
            catch (Exception error) { throw new InvalidDataException("Invalid moved setting " + item.SourceProperty + ".", error); }
        }
    }

    public static partial class ModSettingsMigration
    {
        private static readonly object MoveSync = new object();
        private static readonly Dictionary<string, ModSettingsModMove> ModMoves = new Dictionary<string, ModSettingsModMove>(StringComparer.Ordinal);
        private static ModSettingsModMove[] orderedMoves = Array.Empty<ModSettingsModMove>();

        /// <summary>Registers an immutable mod move before any mission preparation. An identical owner/id declaration is idempotent.</summary>
        /// <remarks>Thread-safe, process-lived, no callbacks or I/O. Owner must be the target GUID. Conflicting declarations,
        /// ambiguous moved names, multiple declarations for a target and GUID cycles throw without publishing the candidate.
        /// Registrations apply to subsequent preparations only; never register while a mission is being prepared.</remarks>
        public static void RegisterModMove(string ownerGuid, string registrationId, ModSettingsModMove definition)
        {
            ModSettingMove.RequireName(ownerGuid); ModSettingMove.RequireName(registrationId);
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (ownerGuid != definition.TargetModGuid) throw new ArgumentException("Only the destination owner may declare a move.");
            string key = ownerGuid + "\n" + registrationId;
            lock (MoveSync)
            {
                if (ModMoves.TryGetValue(key, out var previous))
                {
                    if (!EquivalentMove(previous, definition)) throw new InvalidOperationException("Conflicting mod move registration: " + key);
                    return;
                }
                var candidates = ModMoves.Values.Concat(new[] { definition }).ToArray();
                if (candidates.Length > SavegameModSettings.MaximumMods) throw new InvalidOperationException("Too many mod moves.");
                if (candidates.Select(item => item.TargetModGuid).Distinct(StringComparer.Ordinal).Count() != candidates.Length)
                    throw new InvalidOperationException("Several mod moves claim one destination.");
                var claimed = new HashSet<string>(StringComparer.Ordinal);
                foreach (var move in candidates)
                    foreach (var item in move.Moved)
                    {
                        if (!claimed.Add(move.SourceModGuid + "\n" + item.SourceProperty))
                            throw new InvalidOperationException("Several mod moves claim one source setting.");
                        if (candidates.Any(other => !ReferenceEquals(other, move) && other.SourceModGuid == move.SourceModGuid &&
                            other.Copied.Any(copy => copy.SourceProperty == item.SourceProperty)))
                            throw new InvalidOperationException("A source setting cannot be moved and copied by different declarations.");
                    }
                var sorted = SortMoves(candidates);
                ModMoves.Add(key, definition);
                orderedMoves = sorted;
            }
        }

        private static bool EquivalentMove(ModSettingsModMove a, ModSettingsModMove b)
        {
            bool SameItems(ModSettingMove[] left, ModSettingMove[] right) => left.Length == right.Length &&
                left.OrderBy(item => item.SourceProperty, StringComparer.Ordinal).Zip(right.OrderBy(item => item.SourceProperty, StringComparer.Ordinal),
                    (x, y) => x.SourceProperty == y.SourceProperty && x.TargetProperty == y.TargetProperty && x.ValueType == y.ValueType).All(value => value);
            return a.SourceModGuid == b.SourceModGuid && a.TargetModGuid == b.TargetModGuid && SameItems(a.Moved, b.Moved) && SameItems(a.Copied, b.Copied) &&
                a.Defaults.Count == b.Defaults.Count && a.Defaults.All(item => b.Defaults.TryGetValue(item.Key, out var value) && Equals(item.Value, value));
        }
        private static ModSettingsModMove[] SortMoves(ModSettingsModMove[] moves)
        {
            var remaining = moves.ToList(); var result = new List<ModSettingsModMove>();
            while (remaining.Count != 0)
            {
                var ready = remaining.Where(item => !remaining.Any(other => other.TargetModGuid == item.SourceModGuid))
                    .OrderBy(item => item.SourceModGuid, StringComparer.Ordinal).ThenBy(item => item.TargetModGuid, StringComparer.Ordinal).ToArray();
                if (ready.Length == 0) throw new InvalidOperationException("Mod move declarations contain a cycle.");
                foreach (var item in ready) { result.Add(item); remaining.Remove(item); }
            }
            return result.ToArray();
        }
        private static ModSettingsModMove[] SnapshotMoves() { lock (MoveSync) return orderedMoves; }

        /// <summary>Returns a detached schema-3 Map/Trail document with declared owner moves applied before provider matching.</summary>
        /// <remarks>Call before validation/pruning against installed endpoints and before caching author/reset sources.
        /// Explicit target entries (even empty) win as a whole. Defaults are fixed; moved Player entries remain Player
        /// entries of the new owner. The input and its mutable values are never changed. Invalid required moved values
        /// throw before live settings are applied. No file is rewritten and no missing unrelated mod is enabled.</remarks>
        public static MissionModSettingsDocument MigrateMissionDocument(MissionModSettingsDocument document)
        {
            if (document?.Mods == null || document.SchemaVersion != 3) throw new InvalidDataException("A schema-3 mission document is required.");
            // Copy before NormalizeAndValidate: that API intentionally normalizes its argument in place.
            var result = new MissionModSettingsDocument { SchemaVersion = document.SchemaVersion };
            foreach (var mod in document.Mods)
            {
                var entry = mod.Value ?? new MissionModSettingsEntry();
                result.Mods.Add(mod.Key, new MissionModSettingsEntry {
                    PlayerSettings = (string[])(entry.PlayerSettings ?? Array.Empty<string>()).Clone(),
                    Overrides = (entry.Overrides ?? new Dictionary<string, object>()).ToDictionary(item => item.Key, item => CopyMissionValue(item.Value), StringComparer.Ordinal)
                });
            }
            MissionModSettingsJson.NormalizeAndValidate(result, "mod move input");
            // The wire codec omits empty entries, but an explicit in-memory target is
            // authoritative too. Preserve that evidence across repeated preparation.
            var explicitEmpty = document.Mods.Where(mod => (mod.Value?.PlayerSettings?.Length ?? 0) == 0 &&
                (mod.Value?.Overrides?.Count ?? 0) == 0).Select(mod => mod.Key).ToArray();
            foreach (string id in explicitEmpty) result.Mods[id] = new MissionModSettingsEntry();
            var explicitTargets = new HashSet<string>(document.Mods.Keys, StringComparer.Ordinal);
            foreach (var move in SnapshotMoves())
            {
                if (!result.Mods.TryGetValue(move.SourceModGuid, out var source)) continue;
                var player = new HashSet<string>(source.PlayerSettings, StringComparer.Ordinal);
                if (!move.Moved.Any(item => player.Contains(item.SourceProperty) || source.Overrides.ContainsKey(item.SourceProperty))) continue;
                if (explicitTargets.Contains(move.TargetModGuid))
                {
                    // Retire obsolete source fields even when the target wins. Otherwise
                    // pruning/serializing an empty target could resurrect them on next load.
                    foreach (var item in move.Moved) { source.Overrides.Remove(item.SourceProperty); player.Remove(item.SourceProperty); }
                    source.PlayerSettings = player.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                    if (source.PlayerSettings.Length == 0 && source.Overrides.Count == 0) result.Mods.Remove(move.SourceModGuid);
                    continue;
                }
                var target = new MissionModSettingsEntry { Overrides = new Dictionary<string, object>(move.Defaults, StringComparer.Ordinal) };
                var targetPlayers = new List<string>();
                foreach (var item in move.Copied.Concat(move.Moved))
                {
                    if (source.Overrides.TryGetValue(item.SourceProperty, out var value))
                    {
                        // Validate the declared type, retaining the existing mission wire
                        // representation (for example enum names and JSON doubles for floats).
                        ModSettingsModMove.ConvertDocumentValue(item, value);
                        target.Overrides[item.TargetProperty] = CopyMissionValue(value);
                    }
                    else if (player.Contains(item.SourceProperty))
                    { target.Overrides.Remove(item.TargetProperty); targetPlayers.Add(item.TargetProperty); }
                }
                target.PlayerSettings = targetPlayers.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                result.Mods[move.TargetModGuid] = target;
                foreach (var item in move.Moved) { source.Overrides.Remove(item.SourceProperty); player.Remove(item.SourceProperty); }
                source.PlayerSettings = player.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                if (source.PlayerSettings.Length == 0 && source.Overrides.Count == 0) result.Mods.Remove(move.SourceModGuid);
            }
            MissionModSettingsJson.NormalizeAndValidate(result, "mod move result");
            foreach (string id in explicitEmpty) result.Mods[id] = new MissionModSettingsEntry();
            return result;
        }
        private static object CopyMissionValue(object value)
        {
            if (!MissionModSettingsJson.IsSupportedValue(value)) throw new InvalidDataException("Unsupported mission value.");
            if (value is Array array) return array.Clone();
            if (value is System.Collections.IEnumerable values && !(value is string)) return values.Cast<object>().ToArray();
            return value;
        }

        // Called only after wire validation, outside the reader's invalid-record fallback. A required migration
        // error must refuse preparation, never turn a saved-settings choice into current-host-settings.
        internal static SavegameModSettingsRecord MigrateSavegameRecord(SavegameModSettingsRecord record, bool useStoredValues = true)
        {
            if (record == null) return null;
            var result = new SavegameModSettingsRecord {
                Version = record.Version, Kind = record.Kind, Variant = record.Variant,
                LockedByConflict = record.LockedByConflict, TrailCustomizeAllowed = record.TrailCustomizeAllowed,
                Mods = record.Mods.ToDictionary(mod => mod.Key, mod => mod.Value.ToDictionary(item => item.Key, item => (byte[])item.Value.Clone(), StringComparer.Ordinal), StringComparer.Ordinal),
                CreatorRules = record.CreatorRules.ToDictionary(mod => mod.Key, mod => mod.Value.ToDictionary(item => item.Key, item => CopyRule(item.Value), StringComparer.Ordinal), StringComparer.Ordinal)
            };
            var explicitTargets = new HashSet<string>(record.Mods.Keys.Concat(record.CreatorRules.Keys), StringComparer.Ordinal);
            foreach (var move in SnapshotMoves())
            {
                result.Mods.TryGetValue(move.SourceModGuid, out var source);
                result.CreatorRules.TryGetValue(move.SourceModGuid, out var sourceRules);
                // Author rules survive saves made while their owner is uninstalled,
                // and record size reduction may omit values while retaining rules.
                if (!move.Moved.Any(item => source?.ContainsKey(item.SourceProperty) == true ||
                    sourceRules?.ContainsKey(item.SourceProperty) == true)) continue;
                if (explicitTargets.Contains(move.TargetModGuid))
                {
                    foreach (var item in move.Moved) { source?.Remove(item.SourceProperty); sourceRules?.Remove(item.SourceProperty); }
                    if (source?.Count == 0) result.Mods.Remove(move.SourceModGuid);
                    if (sourceRules?.Count == 0) result.CreatorRules.Remove(move.SourceModGuid);
                    continue;
                }
                var target = move.Defaults.ToDictionary(item => item.Key, item => SerializeDefault(item.Value), StringComparer.Ordinal);
                // Mission defaults may use a JSON representation (enum name, numeric
                // string). Save snapshots instead require the declared property's wire type.
                foreach (var item in move.Copied.Concat(move.Moved))
                    if (move.Defaults.TryGetValue(item.TargetProperty, out var defaultValue))
                        target[item.TargetProperty] = MessagePackSerializer.Serialize(item.ValueType,
                            ModSettingsModMove.ConvertDocumentValue(item, defaultValue));
                var rules = target.ToDictionary(item => item.Key, item => new TrailCreatorRule { Mode = 2, FixedValue = (byte[])item.Value.Clone() }, StringComparer.Ordinal);
                foreach (var item in move.Copied.Concat(move.Moved))
                {
                    if (source != null && source.TryGetValue(item.SourceProperty, out var bytes))
                    {
                        // Current-settings loads do not consume stored values. Do not reject
                        // an unused old value which the existing load choice deliberately ignores.
                        if (useStoredValues) ValidateMovedBytes(item, bytes);
                        target[item.TargetProperty] = (byte[])bytes.Clone();
                        // A real value supersedes the synthetic default. With no author
                        // rule the existing strict-Trail baseline remains authoritative.
                        rules.Remove(item.TargetProperty);
                    }
                    if (sourceRules != null && sourceRules.TryGetValue(item.SourceProperty, out var rule))
                    {
                        if (useStoredValues && rule.Mode == 2) ValidateMovedBytes(item, rule.FixedValue);
                        rules[item.TargetProperty] = CopyRule(rule);
                    }
                }
                result.Mods[move.TargetModGuid] = target;
                result.CreatorRules[move.TargetModGuid] = rules;
                foreach (var item in move.Moved) { source?.Remove(item.SourceProperty); sourceRules?.Remove(item.SourceProperty); }
                if (source?.Count == 0) result.Mods.Remove(move.SourceModGuid);
                if (sourceRules?.Count == 0) result.CreatorRules.Remove(move.SourceModGuid);
            }
            if (result.Mods.Count > SavegameModSettings.MaximumMods || result.CreatorRules.Count > SavegameModSettings.MaximumMods)
                throw new InvalidDataException("Migrated savegame exceeds the mod limit.");
            return result;
        }
        private static byte[] SerializeDefault(object value) => MessagePackSerializer.Serialize(value.GetType(), value);
        private static TrailCreatorRule CopyRule(TrailCreatorRule rule) => new TrailCreatorRule {
            Mode = rule.Mode, FixedValue = rule.FixedValue == null ? null : (byte[])rule.FixedValue.Clone()
        };
        private static void ValidateMovedBytes(ModSettingMove item, byte[] bytes)
        {
            try { if (bytes == null || MessagePackSerializer.Deserialize(item.ValueType, bytes) == null) throw new InvalidDataException("Null moved value."); }
            catch (Exception error) { throw new InvalidDataException("Invalid moved setting " + item.SourceProperty + ".", error); }
        }
    }
}

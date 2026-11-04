using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Noesis;

namespace APIShared.Statistics
{
    /// <summary>Thematic placement; unspecified contributions appear under OtherMods.</summary>
    public enum LordStatisticTheme {
        /// <summary>Unassigned contributions from other mods.</summary>
        OtherMods,
        /// <summary>Gold, resources and production.</summary>
        Economy,
        /// <summary>Armies, recruitment and combat.</summary>
        Military,
        /// <summary>Population, popularity and fear.</summary>
        PopulationMood
    }

    /// <summary>Immutable request. Player IDs are the renderer's authorized subset, not a global roster.
    /// Providers run synchronously on Unity's main thread, outside registry locks, and must not mutate
    /// game or UI state. Copy native data under its appropriate read lock; never retain native pointers.</summary>
    public sealed class LordStatisticRequest
    {
        internal LordStatisticRequest(long sessionId, IEnumerable<int> players)
        { SessionId = sessionId; PlayerIds = Array.AsReadOnly(players.Distinct().OrderBy(p => p).ToArray()); }
        /// <summary>Current APIShared mission session, supplied and revalidated by the renderer.</summary>
        public long SessionId { get; }
        /// <summary>Immutable, distinct, authorized player IDs (1–8).</summary>
        public IReadOnlyList<int> PlayerIds { get; }
    }

    /// <summary>A process-lived column definition. Delegates supply localized metadata and a fresh batch
    /// of nullable decimal values. No callback runs merely because a definition is registered.
    /// Treat every column as extended information; authorization and sampling cadence belong to the renderer.</summary>
    public sealed class LordStatisticColumnDefinition
    {
        /// <summary>Creates a column. All providers except format are required. Icon providers return a
        /// shareable ImageSource, never a parented UI element. Format defaults to invariant decimal text.</summary>
        public LordStatisticColumnDefinition(string columnId, Func<string> name, Func<string> tooltip,
            Func<ImageSource> icon, Func<LordStatisticRequest, IReadOnlyDictionary<int, decimal?>> values,
            LordStatisticTheme theme = LordStatisticTheme.OtherMods, Func<decimal, string> format = null)
        { ColumnId = columnId; Name = name; Tooltip = tooltip; Icon = icon; Values = values; Theme = theme; Format = format; }
        /// <summary>Owner-local stable identifier; case-sensitive. Not a localized label.</summary>
        public string ColumnId { get; }
        /// <summary>Localized display name, evaluated on demand by the UI.</summary>
        public Func<string> Name { get; }
        /// <summary>Localized description, including units and cumulative/current meaning.</summary>
        public Func<string> Tooltip { get; }
        /// <summary>Shareable image source; may return null while assets are not ready.</summary>
        public Func<ImageSource> Icon { get; }
        /// <summary>One snapshot for the authorized IDs; missing IDs/null mean unknown. Extra IDs are discarded.</summary>
        public Func<LordStatisticRequest, IReadOnlyDictionary<int, decimal?>> Values { get; }
        /// <summary>Thematic placement, default OtherMods.</summary>
        public LordStatisticTheme Theme { get; }
        /// <summary>Optional text formatter; sorting always uses the numeric decimal value.</summary>
        public Func<decimal, string> Format { get; }
    }

    /// <summary>Immutable catalog revision. Entries have stable identity and separately controlled activation.</summary>
    public sealed class LordStatisticCatalog
    {
        internal LordStatisticCatalog(long revision, LordStatisticColumn[] columns)
        { Revision = revision; Columns = Array.AsReadOnly(columns); }
        /// <summary>Monotonically increasing registry revision.</summary>
        public long Revision { get; }
        /// <summary>Registrations in ordinal key order, including inactive providers.</summary>
        public IReadOnlyList<LordStatisticColumn> Columns { get; }
    }

    /// <summary>Registration handle and safe reader. UI-thread only. No removal: deactivate instead.
    /// Callback failures return unknown/fallback metadata and produce bounded diagnostics, allowing later recovery.</summary>
    public sealed class LordStatisticColumn
    {
        internal readonly LordStatisticColumnDefinition Definition;
        private readonly StatisticRegistry owner;
        private readonly HashSet<string> failures = new HashSet<string>();
        private bool reading;
        internal LordStatisticColumn(StatisticRegistry owner, string guid, string key, LordStatisticColumnDefinition definition)
        { this.owner = owner; OwnerGuid = guid; Key = key; Definition = definition; IsActive = true; }
        /// <summary>Contributing mod GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable persisted identity: escaped owner GUID, slash, escaped owner-local ID.</summary>
        public string Key { get; }
        /// <summary>Logical activation; changes notify catalog subscribers. Defaults true.</summary>
        public bool IsActive { get; private set; }
        /// <summary>Logical activation, without unloading callbacks or deleting saved selections.</summary>
        public void SetActive(bool active)
        { owner.RequireThread(); if (IsActive == active) return; IsActive = active; owner.Publish(); }
        /// <summary>Notify metadata changes (for example a language change); no values are read.</summary>
        public void RefreshMetadata() { owner.RequireThread(); owner.Publish(); }
        /// <summary>Thematic placement.</summary>
        public LordStatisticTheme Theme => Definition.Theme;
        /// <summary>Localized name with stable-key fallback on provider failure.</summary>
        public string GetName() => Safe("name", Definition.Name, Key);
        /// <summary>Localized tooltip, empty on failure.</summary>
        public string GetTooltip() => Safe("tooltip", Definition.Tooltip, string.Empty);
        /// <summary>Shareable symbol, null on failure or before assets become available.</summary>
        public ImageSource GetIcon() => Safe("icon", Definition.Icon, (ImageSource)null);
        /// <summary>Formats numeric data; failed/null custom output falls back to invariant decimal text.</summary>
        public string Format(decimal value) => Safe("format", () => Definition.Format?.Invoke(value), (string)null)
            ?? value.ToString(CultureInfo.InvariantCulture);
        private T Safe<T>(string operation, Func<T> callback, T fallback)
        { owner.RequireThread(); try { return callback(); } catch (Exception error) { Failed(operation, error); return fallback; } }
        private void Failed(string operation, Exception error)
        { if (failures.Add(operation)) owner.Report(Key + " " + operation, error); }
        /// <summary>Reads only on renderer demand, never from registration/catalog queries. Empty rosters,
        /// inactive entries, invalid sessions and recursive reads do not call the provider. Results are copied
        /// into an immutable dictionary filtered to the request. The caller revalidates its session after return.</summary>
        public IReadOnlyDictionary<int, decimal?> Read(long sessionId, IEnumerable<int> authorizedPlayerIds)
        {
            owner.RequireThread();
            var result = new Dictionary<int, decimal?>();
            if (!IsActive || reading || sessionId <= 0 || authorizedPlayerIds == null) return new ReadOnlyDictionary<int, decimal?>(result);
            var request = new LordStatisticRequest(sessionId, authorizedPlayerIds.Where(p => p >= 1 && p <= 8));
            if (request.PlayerIds.Count == 0) return new ReadOnlyDictionary<int, decimal?>(result);
            reading = true;
            try {
                var snapshot = Definition.Values(request);
                if (IsActive && snapshot != null)
                    foreach (int id in request.PlayerIds) result[id] = snapshot.TryGetValue(id, out var value) ? value : null;
            } catch (Exception error) { result.Clear(); Failed("values", error); }
            finally { reading = false; }
            return new ReadOnlyDictionary<int, decimal?>(result);
        }
    }

    // No polling, game reads or lifecycle hooks. The façade supplies the validated Unity-thread guard.
    internal sealed class StatisticRegistry
    {
        private readonly Func<bool> onThread;
        private readonly Action<string, Exception> report;
        private readonly Dictionary<string, LordStatisticColumn> columns = new Dictionary<string, LordStatisticColumn>(StringComparer.Ordinal);
        private readonly Queue<LordStatisticCatalog> notifications = new Queue<LordStatisticCatalog>();
        private bool publishing;
        private long revision;
        internal event Action<LordStatisticCatalog> Changed;
        internal StatisticRegistry(Func<bool> onThread, Action<string, Exception> report) { this.onThread = onThread; this.report = report; }
        internal void RequireThread() { if (!onThread()) throw new InvalidOperationException("Lord statistics require APIShared initialization and Unity's main thread."); }
        internal void Report(string operation, Exception error) { report?.Invoke(operation, error); }
        internal LordStatisticCatalog Snapshot() { RequireThread(); return new LordStatisticCatalog(revision, columns.Values.OrderBy(c => c.Key, StringComparer.Ordinal).ToArray()); }
        internal bool Register(string guid, LordStatisticColumnDefinition definition, out LordStatisticColumn column, out string reason)
        {
            column = null;
            if (!onThread()) { reason = "Initialize APIShared and register on Unity's main thread."; return false; }
            if (string.IsNullOrWhiteSpace(guid) || definition == null || string.IsNullOrWhiteSpace(definition.ColumnId) ||
                definition.Name == null || definition.Tooltip == null || definition.Icon == null || definition.Values == null || !Enum.IsDefined(typeof(LordStatisticTheme), definition.Theme))
            { reason = "Owner GUID, column ID and all metadata/value providers are required."; return false; }
            string key = Uri.EscapeDataString(guid) + "/" + Uri.EscapeDataString(definition.ColumnId);
            if (columns.ContainsKey(key)) { reason = "Duplicate Lord-statistic column: " + key; return false; }
            column = new LordStatisticColumn(this, guid, key, definition); columns.Add(key, column);
            reason = string.Empty; Publish(); return true;
        }
        internal void Publish()
        {
            revision++; notifications.Enqueue(Snapshot());
            if (publishing) return;
            publishing = true;
            try { while (notifications.Count != 0) {
                var next = notifications.Dequeue(); var handlers = Changed;
                if (handlers == null) continue;
                foreach (Action<LordStatisticCatalog> callback in handlers.GetInvocationList())
                    try { callback(next); } catch (Exception error) { Report("catalog observer", error); }
            }} finally { publishing = false; }
        }
    }
}

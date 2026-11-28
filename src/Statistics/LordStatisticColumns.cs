using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using APIShared.GameModes;
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

    /// <summary>Immutable request. Player IDs are authorized for display or sorting, depending on the column.
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

    /// <summary>Renderer-supplied information about one viewer and one candidate lord in a mission.</summary>
    public readonly struct LordStatisticView
    {
        /// <summary>Creates a view. Use zero for an unknown viewer and Unknown for an unresolved mode.</summary>
        public LordStatisticView(long sessionId, int viewerPlayerId, int playerId, GameModeKind mode,
            bool isRealMultiplayer, bool isSpectator, bool isEliminated, bool isAlly, bool hasEnemyDetailsPermission)
        {
            SessionId = sessionId; ViewerPlayerId = viewerPlayerId; PlayerId = playerId; Mode = mode;
            IsRealMultiplayer = isRealMultiplayer; IsSpectator = isSpectator; IsEliminated = isEliminated;
            IsAlly = isAlly; HasEnemyDetailsPermission = hasEnemyDetailsPermission;
        }
        /// <summary>Current renderer mission session.</summary>
        public long SessionId { get; }
        /// <summary>Viewer's controlled player slot, or zero if unavailable.</summary>
        public int ViewerPlayerId { get; }
        /// <summary>Candidate lord's player slot (1–8).</summary>
        public int PlayerId { get; }
        /// <summary>Current mission or editor category.</summary>
        public GameModeKind Mode { get; }
        /// <summary>Whether this is a real multiplayer session.</summary>
        public bool IsRealMultiplayer { get; }
        /// <summary>Whether the viewer currently has spectator rights.</summary>
        public bool IsSpectator { get; }
        /// <summary>Whether the viewer is an eliminated participant.</summary>
        public bool IsEliminated { get; }
        /// <summary>Whether the candidate is another player on the viewer's team.</summary>
        public bool IsAlly { get; }
        /// <summary>Whether the candidate is the viewer's own controlled player.</summary>
        public bool IsOwn => ViewerPlayerId > 0 && ViewerPlayerId == PlayerId;
        /// <summary>Whether the renderer has confirmed permission to disclose enemy details.</summary>
        public bool HasEnemyDetailsPermission { get; }
        /// <summary>Standard detail rule for own, allied, spectator, eliminated, editor or host-authorized views.</summary>
        public bool CanShowDetails => IsOwn || IsAlly || IsSpectator || IsEliminated ||
            Mode == GameModeKind.MapEditor || HasEnemyDetailsPermission;
    }

    /// <summary>Separate projections of a value for display and numeric ordering.</summary>
    public readonly struct LordStatisticCell
    {
        internal LordStatisticCell(decimal? displayValue, decimal? sortValue)
        { DisplayValue = displayValue; SortValue = sortValue; }
        /// <summary>Visible cell value, or null when hidden or unavailable.</summary>
        public decimal? DisplayValue { get; }
        /// <summary>Numeric ordering key. Use only for ordering, as it may be hidden.</summary>
        public decimal? SortValue { get; }
    }

    /// <summary>A process-lived column definition. Delegates supply localized metadata and a fresh batch
    /// of nullable decimal values. No callback runs merely because a definition is registered.
    /// Sampling cadence and viewer context belong to the renderer.</summary>
    public sealed class LordStatisticColumnDefinition
    {
        /// <summary>Creates a column. All providers except format are required. Icon providers return a
        /// shareable ImageSource, never a parented UI element. Format defaults to invariant decimal text.</summary>
        public LordStatisticColumnDefinition(string columnId, Func<string> name, Func<string> tooltip,
            Func<ImageSource> icon, Func<LordStatisticRequest, IReadOnlyDictionary<int, decimal?>> values,
            LordStatisticTheme theme = LordStatisticTheme.OtherMods, Func<decimal, string> format = null)
            : this(columnId, name, tooltip, icon, values, theme, format, null, false) { }

        /// <summary>Creates a column with its own visibility rule. A null rule uses CanShowDetails.
        /// Sort-only values can reveal relative rank. Visibility runs on the UI thread before sampling.
        /// It must be a read-only decision and must not block on the simulation or call the renderer.</summary>
        public LordStatisticColumnDefinition(string columnId, Func<string> name, Func<string> tooltip,
            Func<ImageSource> icon, Func<LordStatisticRequest, IReadOnlyDictionary<int, decimal?>> values,
            LordStatisticTheme theme, Func<decimal, string> format,
            Func<LordStatisticView, bool> canShowValue, bool sortHiddenValues)
        {
            ColumnId = columnId; Name = name; Tooltip = tooltip; Icon = icon; Values = values;
            Theme = theme; Format = format; CanShowValue = canShowValue; SortHiddenValues = sortHiddenValues;
        }
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
        /// <summary>Optional per-lord display rule. Failure excludes that lord from this sample.</summary>
        public Func<LordStatisticView, bool> CanShowValue { get; }
        /// <summary>Whether a hidden value may be sampled and used for numeric ordering.</summary>
        public bool SortHiddenValues { get; }
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
        /// <summary>Changes when this column publishes new metadata, visibility or activation.</summary>
        public long Revision { get; private set; }
        /// <summary>Logical activation, without unloading callbacks or deleting saved selections.</summary>
        public void SetActive(bool active)
        { owner.RequireThread(); if (IsActive == active) return; IsActive = active; RefreshMetadata(); }
        /// <summary>Notify metadata or visibility changes. The renderer can schedule a fresh sample.</summary>
        public void RefreshMetadata() { owner.RequireThread(); Revision++; owner.Publish(); }
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
        /// into an immutable dictionary filtered to the request. Columns with their own visibility rule
        /// require ReadForView and return no values here. The caller revalidates its session after return.</summary>
        public IReadOnlyDictionary<int, decimal?> Read(long sessionId, IEnumerable<int> authorizedPlayerIds)
        {
            owner.RequireThread();
            var result = new Dictionary<int, decimal?>();
            if (!IsActive || reading || sessionId <= 0 || authorizedPlayerIds == null || Definition.CanShowValue != null)
                return new ReadOnlyDictionary<int, decimal?>(result);
            var request = new LordStatisticRequest(sessionId, authorizedPlayerIds.Where(p => p >= 1 && p <= 8));
            if (request.PlayerIds.Count == 0) return new ReadOnlyDictionary<int, decimal?>(result);
            reading = true;
            try {
                CopyValues(request, result);
            } catch (Exception error) { result.Clear(); Failed("values", error); }
            finally { reading = false; }
            return new ReadOnlyDictionary<int, decimal?>(result);
        }

        /// <summary>Evaluates this column's rule for distinct candidate IDs in the current session and
        /// samples the union of display and sort IDs once. Invalid sessions, empty requests, inactive
        /// columns, recursion and changes to this column during callbacks yield no stale cells. The
        /// renderer must revalidate its session, viewer rights and column revision before publishing.
        /// Use DisplayValue for formatting and tooltips. Use SortValue only in the comparer.</summary>
        public IReadOnlyDictionary<int, LordStatisticCell> ReadForView(long sessionId, IEnumerable<LordStatisticView> candidates)
        {
            owner.RequireThread();
            var result = new Dictionary<int, LordStatisticCell>();
            if (!IsActive || reading || sessionId <= 0 || candidates == null)
                return new ReadOnlyDictionary<int, LordStatisticCell>(result);

            long revision = Revision;
            reading = true;
            try {
                var allowed = new Dictionary<int, bool>();
                var seen = new HashSet<int>();
                foreach (var candidate in candidates) {
                    if (!IsActive || revision != Revision) break;
                    if (candidate.SessionId != sessionId || candidate.PlayerId < 1 || candidate.PlayerId > 8 ||
                        !seen.Add(candidate.PlayerId)) continue;

                    bool show;
                    try { show = Definition.CanShowValue?.Invoke(candidate) ?? candidate.CanShowDetails; }
                    catch (Exception error) { Failed("visibility", error); continue; }
                    if (show || Definition.SortHiddenValues) allowed.Add(candidate.PlayerId, show);
                }
                if (allowed.Count != 0 && IsActive && revision == Revision) {
                    var values = new Dictionary<int, decimal?>();
                    CopyValues(new LordStatisticRequest(sessionId, allowed.Keys), values);
                    if (IsActive && revision == Revision)
                        foreach (var pair in values)
                            result.Add(pair.Key, new LordStatisticCell(allowed[pair.Key] ? pair.Value : null, pair.Value));
                }
            } catch (Exception error) { result.Clear(); Failed("values", error); }
            finally { reading = false; }
            return new ReadOnlyDictionary<int, LordStatisticCell>(result);
        }

        private void CopyValues(LordStatisticRequest request, Dictionary<int, decimal?> result)
        {
            var snapshot = Definition.Values(request);
            if (IsActive && snapshot != null)
                foreach (int id in request.PlayerIds)
                    result[id] = snapshot.TryGetValue(id, out var value) ? value : null;
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

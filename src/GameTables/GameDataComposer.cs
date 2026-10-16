using System;
using System.IO;
using System.Threading;
using APIShared.Events;
using APIShared.Savegames;

namespace APIShared.GameTables
{
    // One Script Extender table as rows of int cells. Must not throw; a false read skips the row.
    internal interface IGameDataTableStore
    {
        int Rows { get; }
        int Columns { get; }
        // The game's own values of a row (for trade prices the default table every map load starts from).
        bool TryReadBase(int row, int[] values);
        bool TryReadLive(int row, int[] values);
        void WriteRow(int row, int[] values);
    }

    // Dependency-free core of one GameDataLayers table: ordered layers, the first-write Base, the
    // composed values of the current mission and their save record.
    //
    // Base: the game's own value of every cell, read once per process before APIShared first writes the
    // table (first write wins), so later compositions never start from a value a mod wrote.
    // Composition: Base, then every active layer in layer, order, owner, ID order; integer arithmetic.
    // Ownership check per cell: the live value must be the Base (the game filled the table) or the value
    // the last composition of this mission produced. Anything else was written by another mod: the cell
    // is reported once per mission and left as that mod wrote it, never overwritten.
    // Write: one row write only when a composed cell differs from its live value, so a layer that changes
    // nothing writes nothing. Compositions serialize on one lock; the facade runs them on the Unity thread.
    // Restore-then-apply: the composed values are kept in the save record; after a reload the table may
    // hold them or the defaults, and both count as this service's own, so the new composition starts from
    // Base and never compounds.
    internal sealed class GameDataTableComposer
    {
        internal const int RecordVersion = 1;
        private static readonly InvalidOperationException WrongLayer =
            new InvalidOperationException("The contribution does not match the registration's layer.");
        private static readonly InvalidOperationException InvalidContribution =
            new InvalidOperationException("Invalid contribution: a ratio needs numerator >= 0 and denominator >= 1, a range minimum <= maximum.");

        internal sealed class Entry
        {
            internal long Failures;
            internal readonly GameDataRegistration Handle;
            internal readonly GameDataLayerHandler Handler;
            internal Entry(GameDataRegistration handle, GameDataLayerHandler handler) { Handle = handle; Handler = handler; }
        }

        private readonly object registrationSync = new object();
        private readonly object compositionSync = new object();
        private readonly GameDataTable table;
        private readonly IGameDataTableStore store;
        private readonly Func<string> activate;
        private readonly Action<string> report;
        private readonly int rows, columns;
        private readonly int[] baseValues, applied, live, target;
        private readonly bool[] baseRead, hasApplied, reported;
        private readonly Trace trace = new Trace();
        private bool activated;
        private Entry[] entries = Array.Empty<Entry>();
        private Entry[] snapshot = Array.Empty<Entry>();
        private long failures, writes, foreignCells;

        // activate runs once under the registration lock before the first registration (null = done).
        // report receives one line per foreign cell per mission.
        internal GameDataTableComposer(GameDataTable table, IGameDataTableStore store, Func<string> activate, Action<string> report)
        {
            this.table = table;
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            this.activate = activate ?? throw new ArgumentNullException(nameof(activate));
            this.report = report;
            rows = store.Rows; columns = store.Columns;
            baseValues = new int[rows * columns]; applied = new int[rows * columns];
            baseRead = new bool[rows]; hasApplied = new bool[rows * columns]; reported = new bool[rows * columns];
            live = new int[columns]; target = new int[columns];
        }

        internal GameDataTable Table => table;
        internal long CallbackFailures => Interlocked.Read(ref failures);
        internal long RowWrites => Interlocked.Read(ref writes);
        internal long ForeignCells => Interlocked.Read(ref foreignCells);

        internal bool TryAdd(string ownerGuid, string registrationId, GameDataLayer layer, int order, GameDataLayerHandler handler,
            out GameDataRegistration registration, out string reason)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || handler == null)
            { reason = "Owner GUID, registration ID and a handler are required."; return false; }
            if (layer < GameDataLayer.Additive || layer > GameDataLayer.Clamp) { reason = "Unknown layer " + layer + "."; return false; }
            lock (registrationSync)
            {
                foreach (Entry existing in entries)
                    if (existing.Handle.OwnerGuid == ownerGuid && existing.Handle.RegistrationId == registrationId)
                    { reason = "This owner already registered that ID for " + table + "."; return false; }
                if (!activated)
                {
                    string refusal;
                    try { refusal = activate(); }
                    catch (Exception ex) { refusal = "GameData layers are unavailable: " + ex.Message; }
                    if (refusal != null) { reason = refusal; return false; }
                    activated = true;
                }
                var handle = new GameDataRegistration(ownerGuid, registrationId, table, layer, order, Remove);
                var next = new Entry[entries.Length + 1];
                Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(handle, handler);
                Array.Sort(next, Compare);
                entries = next;
                Volatile.Write(ref snapshot, next);
                registration = handle;
            }
            reason = string.Empty;
            return true;
        }

        private void Remove(GameDataRegistration handle)
        {
            lock (registrationSync)
            {
                int index = Array.FindIndex(entries, entry => ReferenceEquals(entry.Handle, handle));
                if (index < 0) return;
                var next = new Entry[entries.Length - 1];
                Array.Copy(entries, next, index);
                Array.Copy(entries, index + 1, next, index, next.Length - index);
                entries = next;
                Volatile.Write(ref snapshot, next);
            }
        }

        private static int Compare(Entry left, Entry right)
        {
            int comparison = left.Handle.Layer.CompareTo(right.Handle.Layer);
            if (comparison == 0) comparison = left.Handle.Order.CompareTo(right.Handle.Order);
            if (comparison == 0) comparison = StringComparer.Ordinal.Compare(left.Handle.OwnerGuid, right.Handle.OwnerGuid);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(left.Handle.RegistrationId, right.Handle.RegistrationId);
        }

        // A new mission: foreign cells are reported again. The composed values stay; a load replaces them.
        internal void BeginMission()
        {
            lock (compositionSync) Array.Clear(reported, 0, reported.Length);
        }

        // One composition of the whole table. Returns the number of rows written.
        [System.Runtime.CompilerServices.SkipLocalsInit]
        internal int Apply()
        {
            Entry[] layers = Volatile.Read(ref snapshot);
            int written = 0;
            lock (compositionSync)
            {
                for (int row = 0; row < rows; row++)
                {
                    int at = row * columns;
                    if (!baseRead[row])
                    {
                        if (!store.TryReadBase(row, live)) continue;
                        Array.Copy(live, 0, baseValues, at, columns);
                        baseRead[row] = true;
                    }
                    if (!store.TryReadLive(row, live)) continue;
                    bool changed = false;
                    for (int column = 0; column < columns; column++)
                    {
                        int cell = at + column, now = live[column];
                        if (now != baseValues[cell] && !(hasApplied[cell] && now == applied[cell]))
                        {
                            // Kept as the other mod wrote it; it never becomes this service's own value.
                            Foreign(row, column, now);
                            target[column] = now;
                            continue;
                        }
                        int composed = Compose(layers, new GameDataCell(table, row, column, baseValues[cell]));
                        target[column] = composed;
                        applied[cell] = composed;
                        hasApplied[cell] = true;
                        if (composed != now) changed = true;
                    }
                    if (changed) { store.WriteRow(row, target); written++; }
                }
            }
            if (written != 0) Interlocked.Add(ref writes, written);
            return written;
        }

        // How one cell composes for one registration with the current layer inputs; writes nothing. The handlers
        // run as in a composition, and the live value gets the composition's ownership check.
        internal bool TryDescribe(GameDataRegistration registration, int row, int column, out GameDataCellStatus status, out string reason)
        {
            status = default;
            if (registration == null || !registration.IsActive || registration.Table != table)
            { reason = "The registration is not an active layer of " + table + "."; return false; }
            if ((uint)row >= (uint)rows || (uint)column >= (uint)columns)
            { reason = "Row " + row + " column " + column + " is outside " + table + "."; return false; }
            Entry[] layers = Volatile.Read(ref snapshot);
            lock (compositionSync)
            {
                if (!baseRead[row])
                {
                    if (!store.TryReadBase(row, live)) { reason = "The game's values of " + table + " are not readable yet."; return false; }
                    Array.Copy(live, 0, baseValues, row * columns, columns);
                    baseRead[row] = true;
                }
                if (!store.TryReadLive(row, live)) { reason = "The live values of " + table + " are not readable."; return false; }
                int cell = row * columns + column, now = live[column];
                bool direct = now != baseValues[cell] && !(hasApplied[cell] && now == applied[cell]);
                var subject = new GameDataCell(table, row, column, baseValues[cell]);
                trace.Begin(registration);
                int composed = ComposeTraced(layers, subject, trace);
                GameDataRegistration decidedBy = trace.DecidedBy?.Handle, limitedBy = trace.LimitedBy?.Handle;
                bool overridden = trace.Contributes && (direct ||
                    registration.Layer < GameDataLayer.Override && decidedBy != null ||
                    registration.Layer == GameDataLayer.Override && !ReferenceEquals(decidedBy, registration));
                status = new GameDataCellStatus(subject, trace.Contributes, composed, decidedBy?.OwnerGuid, decidedBy?.RegistrationId,
                    limitedBy?.OwnerGuid, limitedBy?.RegistrationId, overridden, direct, direct ? now : composed);
                trace.Begin(null);
            }
            reason = string.Empty;
            return true;
        }

        // Filled by a status query's composition.
        private sealed class Trace
        {
            internal GameDataRegistration Subject;
            internal bool Contributes;
            internal Entry DecidedBy, LimitedBy;
            internal void Begin(GameDataRegistration subject) { Subject = subject; Contributes = false; DecidedBy = null; LimitedBy = null; }
        }

        private void Foreign(int row, int column, int value)
        {
            int cell = row * columns + column;
            if (reported[cell]) return;
            reported[cell] = true;
            Interlocked.Increment(ref foreignCells);
            try
            {
                report?.Invoke(table + " row " + row + " column " + column + " holds " + value + ", neither the game's value " +
                    baseValues[cell] + (hasApplied[cell] ? " nor the last composed value " + applied[cell] : string.Empty) +
                    "; another mod wrote it directly. Left unchanged; register a GameDataLayers layer instead.");
            }
            catch { }
        }

        [System.Runtime.CompilerServices.SkipLocalsInit]
        internal int Compose(Entry[] layers, GameDataCell cell)
        {
            long value = cell.BaseValue;
            for (int index = 0; index < layers.Length; index++)
            {
                Entry entry = layers[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                GameDataContribution contribution;
                bool applies;
                try { applies = entry.Handler(cell, out contribution); }
                catch (Exception ex) { Fail(entry, ex); continue; }
                if (!applies) continue;
                GameDataLayer layer = entry.Handle.Layer;
                if (contribution.Layer != layer) { Fail(entry, WrongLayer); continue; }
                switch (layer)
                {
                    case GameDataLayer.Additive:
                        value = Clamp32(value + contribution.Value);
                        break;
                    case GameDataLayer.Multiplier:
                        if (contribution.Value < 0 || contribution.Second < 1) { Fail(entry, InvalidContribution); continue; }
                        value = Clamp32(DivideRounded(value * contribution.Value, contribution.Second));
                        break;
                    case GameDataLayer.Override:
                        value = contribution.Value;
                        break;
                    case GameDataLayer.Clamp:
                        if (contribution.Value > contribution.Second) { Fail(entry, InvalidContribution); continue; }
                        value = Math.Min(Math.Max(value, contribution.Value), contribution.Second);
                        break;
                }
            }
            return (int)value;
        }

        // Compose with the trace of a status query. Same steps as Compose (the status tests compare both);
        // kept apart so the table composition carries no tracing.
        private int ComposeTraced(Entry[] layers, GameDataCell cell, Trace tracing)
        {
            long value = cell.BaseValue;
            for (int index = 0; index < layers.Length; index++)
            {
                Entry entry = layers[index];
                if (Volatile.Read(ref entry.Handle.ActiveValue) == 0) continue;
                GameDataContribution contribution;
                bool applies;
                try { applies = entry.Handler(cell, out contribution); }
                catch (Exception ex) { Fail(entry, ex); continue; }
                if (!applies) continue;
                GameDataLayer layer = entry.Handle.Layer;
                if (contribution.Layer != layer) { Fail(entry, WrongLayer); continue; }
                switch (layer)
                {
                    case GameDataLayer.Additive:
                        value = Clamp32(value + contribution.Value);
                        break;
                    case GameDataLayer.Multiplier:
                        if (contribution.Value < 0 || contribution.Second < 1) { Fail(entry, InvalidContribution); continue; }
                        value = Clamp32(DivideRounded(value * contribution.Value, contribution.Second));
                        break;
                    case GameDataLayer.Override:
                        value = contribution.Value;
                        tracing.DecidedBy = entry;
                        break;
                    case GameDataLayer.Clamp:
                        if (contribution.Value > contribution.Second) { Fail(entry, InvalidContribution); continue; }
                        long limited = Math.Min(Math.Max(value, contribution.Value), contribution.Second);
                        if (limited != value) tracing.LimitedBy = entry;
                        value = limited;
                        break;
                }
                if (ReferenceEquals(entry.Handle, tracing.Subject)) tracing.Contributes = true;
            }
            return (int)value;
        }

        private static long Clamp32(long value) => Math.Min(int.MaxValue, Math.Max(int.MinValue, value));

        internal static long DivideRounded(long numerator, long denominator) =>
            numerator >= 0 ? (numerator + denominator / 2) / denominator : -((-numerator + denominator / 2) / denominator);

        private void Fail(Entry entry, Exception error)
        {
            Interlocked.Increment(ref failures);
            long count = Interlocked.Increment(ref entry.Failures);
            if ((count & (count - 1)) != 0) return;
            try { InterceptionDiagnostics.Report(entry.Handle.OwnerGuid + "/" + entry.Handle.RegistrationId + " GameData " + table, error); }
            catch { }
        }

        // Save thread: rows, columns, then per cell a flag and the composed value (little endian).
        internal byte[] Capture()
        {
            lock (compositionSync)
            {
                using (var stream = new MemoryStream(8 + rows * columns * 5))
                using (var output = new BinaryWriter(stream))
                {
                    output.Write(rows); output.Write(columns);
                    for (int cell = 0; cell < rows * columns; cell++)
                    {
                        output.Write(hasApplied[cell]);
                        output.Write(applied[cell]);
                    }
                    output.Flush();
                    return stream.ToArray();
                }
            }
        }

        // Load thread, once per archive load: the save's composed values, or none for every other result.
        internal void Restore(byte[] data)
        {
            lock (compositionSync)
            {
                Array.Clear(hasApplied, 0, hasApplied.Length);
                Array.Clear(applied, 0, applied.Length);
                if (data == null || data.Length != 8 + rows * columns * 5 ||
                    BitConverter.ToInt32(data, 0) != rows || BitConverter.ToInt32(data, 4) != columns) return;
                for (int cell = 0, at = 8; cell < rows * columns; cell++, at += 5)
                {
                    hasApplied[cell] = data[at] != 0;
                    applied[cell] = BitConverter.ToInt32(data, at + 1);
                }
            }
        }
    }

    // The save record of every composed table: one entry per table, named by GameDataLayers.
    internal static class GameDataSaveRecord
    {
        internal const string Identifier = "APIShared.GameData";

        internal static bool Capture(VersionedSaveWriter writer, GameDataTableComposer[] tables, string[] names)
        {
            for (int index = 0; index < tables.Length; index++) writer.Write(names[index], tables[index].Capture());
            return true;
        }

        internal static void Loaded(VersionedSaveLoad load, GameDataTableComposer[] tables, string[] names)
        {
            for (int index = 0; index < tables.Length; index++)
            {
                byte[] data = null;
                if (load != null && load.Status == VersionedSaveLoadStatus.Restored) load.Entries.TryGetValue(names[index], out data);
                tables[index].Restore(data);
            }
        }
    }
}

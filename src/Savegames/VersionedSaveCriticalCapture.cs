using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Savegames
{
    // Internal enrollment only. NativeStorage must separately require its initialized
    // owner, the qualified critical writer and a matching protected SP/editor world.
    // This token grants no native access, hook ownership or public migration switch.
    internal sealed class VersionedSaveCriticalSource
    {
        internal VersionedSaveCriticalSource(VersionedSavePreparedSource source, string entry, int reader)
        { PreparedSource = source; ReservedEntry = entry; MinimumCutoverReader = reader; }
        internal VersionedSavePreparedSource PreparedSource { get; }
        internal string ReservedEntry { get; }
        internal int MinimumCutoverReader { get; }
    }

    internal readonly struct VersionedSaveCriticalRecord
    {
        internal VersionedSaveCriticalRecord(VersionedSaveCriticalSource source,
            VersionedSavePreparedOrigin origin, byte[] marker)
        { Source = source; Origin = origin; Marker = marker; }
        internal VersionedSaveCriticalSource Source { get; }
        internal VersionedSavePreparedOrigin Origin { get; }
        internal byte[] Marker { get; }
    }

    // One same-registry bundle belongs to the already-existing critical save owner.
    // All slots are reserved before any consumer callback executes. Preparation is
    // not transport delivery: only SaveCore may mark a frozen record Written.
    internal sealed class VersionedSaveCriticalCapture : IDisposable
    {
        internal sealed class Record
        {
            internal VersionedSaveCriticalSource Source;
            internal VersionedSavePreparedOrigin Origin;
            internal byte[] Marker, Bytes;
            internal List<KeyValuePair<string, byte[]>> Unknown;
            internal bool Served;
        }

        internal readonly VersionedSaveDataRegistry Registry;
        internal readonly Record[] Records;
        internal readonly VersionedSaveContext Context;
        internal readonly long Generation;
        internal readonly int ThreadId;
        internal bool Ready, Failed, Closed;
        internal string Failure = string.Empty;

        internal VersionedSaveCriticalCapture(VersionedSaveDataRegistry registry,
            Record[] records, VersionedSaveContext context, long generation)
        {
            Registry = registry; Records = records; Context = context; Generation = generation;
            ThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        // The caller reads actual completed-archive bytes outside the registry lock.
        // Matching also requires the one actual transport delivery and current origin.
        internal bool MatchesRecord(VersionedSaveCriticalSource source, byte[] bytes, out string reason) =>
            Registry.CriticalRecordMatches(this, source, bytes, out reason);

        internal bool AllWritten(out string reason) => Registry.CriticalAllWritten(this, out reason);

        // Independent finally cleanup in the critical writer must call Dispose even
        // when path restoration, another record release, capture or verification fails.
        public void Dispose() => Registry.EndCriticalCapture(this);
    }

    internal sealed partial class VersionedSaveDataRegistry
    {
        internal bool TryEnrollCriticalSource(VersionedSavePreparedSource source, string reservedEntry,
            int minimumCutoverReader, out VersionedSaveCriticalSource enrollment, out string reason)
        {
            enrollment = null;
            lock (sync)
            {
                Slot slot = FindPreparedSource(source);
                if (generation != 0 || windowOpen || slot == null || slot.CriticalSource != null)
                { reason = "Enroll one exact tracked source before the first archive load."; return false; }
                if (slot.Scope != VersionedSaveScope.SaveGamesAndMaps || !slot.SimulationRelevant)
                { reason = "Critical sources must cover saves/maps and declare simulation relevance."; return false; }
                if (!VersionedSaveRecordCodec.IsValidName(reservedEntry) || !slot.Known.IsKnown(reservedEntry) ||
                    minimumCutoverReader < slot.MinimumReader || minimumCutoverReader > slot.Current)
                { reason = "A known reserved entry and compatible cutover reader are required."; return false; }
                enrollment = new VersionedSaveCriticalSource(source, reservedEntry, minimumCutoverReader);
                slot.CriticalSource = enrollment;
                reason = string.Empty; return true;
            }
        }

        // Logical SaveContext belongs to the Extender/PlayerViewSaveScope. Its path
        // is not the native temporary destination, and flags must not be inferred
        // from the native writer's map byte. The real owner supplies that context.
        // The aggregate bound is explicit, at most the existing 8-MiB record bound.
        internal bool TryFreezeCriticalRecords(IReadOnlyList<VersionedSaveCriticalRecord> requested,
            VersionedSaveContext context, int maximumTotalBytes,
            out VersionedSaveCriticalCapture capture, out string reason)
        {
            capture = null;
            if (requested == null || requested.Count == 0 || requested.Count > MaximumPreparedSources ||
                maximumTotalBytes <= 0 || maximumTotalBytes > VersionedSaveRecordCodec.MaximumRecordBytes)
            { reason = "A bounded nonempty critical source set and aggregate byte limit are required."; return false; }
            VersionedSaveCriticalCapture bundle;
            lock (sync)
            {
                if (windowOpen)
                { reason = "Critical capture cannot run inside a load window."; return false; }
                var selected = new Dictionary<VersionedSaveCriticalSource, VersionedSaveCriticalCapture.Record>();
                foreach (VersionedSaveCriticalRecord item in requested)
                {
                    Slot slot = FindPreparedSource(item.Source?.PreparedSource);
                    if (slot == null || !ReferenceEquals(slot.CriticalSource, item.Source) ||
                        item.Origin == null || !ReferenceEquals(slot.PreparedOrigin, item.Origin) ||
                        item.Origin.Generation != generation || !item.Origin.Completed || !item.Origin.IsReadable ||
                        slot.KeepLoaded || slot.CriticalCapture != null || slot.ActiveCriticalSourceCaptures != 0 ||
                        item.Marker == null || item.Marker.Length == 0 || item.Marker.Length > VersionedSaveRecordCodec.MaximumEntryBytes ||
                        selected.ContainsKey(item.Source))
                    { reason = "An exact completed readable source, unique marker and idle capture slot are required."; return false; }
                    selected.Add(item.Source, new VersionedSaveCriticalCapture.Record
                    {
                        Source = item.Source, Origin = item.Origin, Marker = (byte[])item.Marker.Clone(),
                        Unknown = slot.Unknown == null ? null : new List<KeyValuePair<string, byte[]>>(slot.Unknown)
                    });
                }
                // Reuse the registry's established owner/identifier ordering. The
                // ordinary Extender capture order remains unspecified and unchanged.
                var records = new VersionedSaveCriticalCapture.Record[selected.Count];
                int next = 0;
                foreach (Slot slot in slots)
                    if (slot.CriticalSource != null && selected.TryGetValue(slot.CriticalSource, out var record))
                        records[next++] = record;
                bundle = new VersionedSaveCriticalCapture(this, records, context, generation);
                foreach (var record in records)
                {
                    Slot slot = FindPreparedSource(record.Source.PreparedSource);
                    slot.CriticalCapture = bundle;
                    slot.Handle.SetLastWrite(VersionedSaveWriteStatus.NotWritten);
                }
            }
            bool success = false;
            try
            {
                int total = 0;
                foreach (var record in bundle.Records)
                {
                    Slot slot;
                    lock (sync)
                    {
                        if (!CriticalCurrent(bundle, out reason)) return false;
                        slot = FindPreparedSource(record.Source.PreparedSource);
                    }
                    // CaptureRecord closes its writer and masks Prepared TLS; it
                    // neither holds sync during callbacks nor marks Written.
                    byte[] bytes = CaptureRecord(slot, context, record.Unknown,
                        record.Source.ReservedEntry, record.Marker, record.Source.MinimumCutoverReader,
                        out VersionedSaveWriteStatus status);
                    lock (sync)
                    {
                        if (!CriticalCurrent(bundle, out reason)) return false;
                        if (bytes == null)
                        {
                            slot.Handle.SetLastWrite(status);
                            reason = "An enrolled consumer did not produce its bounded frozen record.";
                            return false;
                        }
                        if (bytes.Length > maximumTotalBytes - total)
                        { reason = "The enrolled frozen records exceed their aggregate byte limit."; return false; }
                        total += bytes.Length; record.Bytes = bytes;
                    }
                }
                lock (sync)
                {
                    if (!CriticalCurrent(bundle, out reason)) return false;
                    bundle.Ready = true; capture = bundle; success = true;
                    reason = string.Empty; return true;
                }
            }
            finally
            {
                if (!success) EndCriticalCapture(bundle);
            }
        }

        // Caller holds sync. A source load/unload, wrong thread or any attempted
        // nested/duplicate/mismatched transport delivery fails the entire bundle.
        private bool CriticalCurrent(VersionedSaveCriticalCapture bundle, out string reason)
        {
            if (bundle == null || !ReferenceEquals(bundle.Registry, this) || bundle.Closed || bundle.Failed || windowOpen ||
                bundle.Generation != generation || bundle.ThreadId != Thread.CurrentThread.ManagedThreadId)
            { reason = bundle == null || string.IsNullOrEmpty(bundle.Failure) ? "The critical capture lifetime/thread changed." : bundle.Failure; return false; }
            foreach (var record in bundle.Records)
            {
                Slot slot = FindPreparedSource(record.Source.PreparedSource);
                if (slot == null || !ReferenceEquals(slot.CriticalCapture, bundle) || slot.KeepLoaded ||
                    !ReferenceEquals(slot.PreparedOrigin, record.Origin) || !record.Origin.Completed || !record.Origin.IsReadable)
                { reason = "A critical source origin or loaded-record state changed."; return false; }
            }
            reason = string.Empty; return true;
        }

        private byte[] ServeCriticalRecord(Slot slot, VersionedSaveContext context, VersionedSaveCriticalCapture bundle)
        {
            lock (sync)
            {
                string reason = string.Empty;
                bool current = CriticalCurrent(bundle, out reason);
                VersionedSaveCriticalCapture.Record selected = null;
                foreach (var record in bundle.Records)
                    if (ReferenceEquals(record.Source, slot.CriticalSource)) { selected = record; break; }
                if (!current || !bundle.Ready || selected == null || selected.Served ||
                    context.IsSaveFile != bundle.Context.IsSaveFile || context.IsMapEditorSave != bundle.Context.IsMapEditorSave ||
                    !string.Equals(context.FilePath, bundle.Context.FilePath, StringComparison.Ordinal))
                {
                    bundle.Failed = true;
                    if (string.IsNullOrEmpty(bundle.Failure)) bundle.Failure = string.IsNullOrEmpty(reason) ?
                        "Frozen transport delivery must occur once on the exact logical SaveContext after preparation." : reason;
                    slot.Handle.SetLastWrite(VersionedSaveWriteStatus.CaptureFailed);
                    return null;
                }
                byte[] bytes = (byte[])selected.Bytes.Clone();
                selected.Served = true;
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.Written);
                return bytes;
            }
        }

        internal bool CriticalAllWritten(VersionedSaveCriticalCapture bundle, out string reason)
        {
            lock (sync)
            {
                if (!CriticalCurrent(bundle, out reason) || !bundle.Ready) return false;
                foreach (var record in bundle.Records)
                {
                    Slot slot = FindPreparedSource(record.Source.PreparedSource);
                    if (!record.Served || slot.Handle.LastWriteStatus != VersionedSaveWriteStatus.Written || slot.Handle.KeepsLoadedRecord)
                    { reason = "An enrolled frozen record was omitted, failed or kept its old loaded bytes."; return false; }
                }
                reason = string.Empty; return true;
            }
        }

        internal bool CriticalRecordMatches(VersionedSaveCriticalCapture bundle,
            VersionedSaveCriticalSource source, byte[] bytes, out string reason)
        {
            lock (sync)
            {
                if (!CriticalAllWritten(bundle, out reason)) return false;
                foreach (var record in bundle.Records)
                    if (ReferenceEquals(record.Source, source))
                    {
                        if (bytes != null && bytes.Length == record.Bytes.Length)
                        {
                            for (int i = 0; i < bytes.Length; i++)
                                if (bytes[i] != record.Bytes[i]) { reason = "The completed archive differs from the frozen consumer record."; return false; }
                            reason = string.Empty; return true;
                        }
                        reason = "The completed archive is missing or has a different consumer record extent."; return false;
                    }
                reason = "The source does not belong to this frozen bundle."; return false;
            }
        }

        internal void EndCriticalCapture(VersionedSaveCriticalCapture bundle)
        {
            lock (sync)
            {
                if (!ReferenceEquals(bundle.Registry, this) || bundle.Closed) return;
                if (bundle.ThreadId != Thread.CurrentThread.ManagedThreadId)
                    throw new InvalidOperationException("The critical capture must end on its owning save thread.");
                bundle.Closed = true; bundle.Ready = false;
                foreach (var record in bundle.Records)
                {
                    Slot slot = FindPreparedSource(record.Source.PreparedSource);
                    if (slot != null && ReferenceEquals(slot.CriticalCapture, bundle)) slot.CriticalCapture = null;
                    record.Bytes = null; record.Marker = null; record.Unknown = null;
                }
            }
        }
    }
}

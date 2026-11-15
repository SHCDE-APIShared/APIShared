using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        internal bool WriterCohortJoined; // Startup-only; standalone captures stay independent.
    }

    internal readonly struct VersionedSaveCriticalRecord
    {
        internal VersionedSaveCriticalRecord(VersionedSaveCriticalSource source,
            VersionedSavePreparedOrigin origin, byte[] marker)
        { Source = source; Origin = origin; CreatedOrigin = null; Marker = marker; }
        internal VersionedSaveCriticalSource Source { get; }
        internal VersionedSavePreparedOrigin Origin { get; }
        internal VersionedSaveCriticalCreatedWorldOrigin CreatedOrigin { get; }
        internal VersionedSaveCriticalRecord(VersionedSaveCriticalSource source, VersionedSaveCriticalCreatedWorldOrigin origin, byte[] marker, bool createdWorld)
        { Source = source; Origin = null; CreatedOrigin = origin; Marker = marker; }
        internal byte[] Marker { get; }
    }

    // Explicit creation has no archive receiver, original bytes/digest or cold-marker authority.
    // Only the existing native owner may mint this after exact EditorCreated/world validation.
    internal sealed class VersionedSaveCriticalCreatedWorldOrigin
    {
        internal VersionedSaveCriticalCreatedWorldOrigin(VersionedSavePreparedSource source, long generation, object world, long session)
        { Source = source; Generation = generation; World = world; Session = session; }
        internal readonly VersionedSavePreparedSource Source;
        internal readonly long Generation, Session;
        internal readonly object World;
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
            internal VersionedSaveCriticalCreatedWorldOrigin CreatedOrigin;
            internal byte[] Marker, Bytes;
            internal List<KeyValuePair<string, byte[]>> Unknown;
            internal bool Served;
        }

        internal readonly VersionedSaveDataRegistry Registry;
        internal readonly Record[] Records;
        internal readonly VersionedSaveContext Context;
        internal readonly long Generation;
        internal readonly int ThreadId;
        // Null for existing standalone preparation. An optional factory frame
        // stamps only bundles constructed inside its actual prepare callback.
        internal readonly object FactoryPreparationTag;
        internal bool Ready, Failed, Closed;
        internal string Failure = string.Empty;

        internal VersionedSaveCriticalCapture(VersionedSaveDataRegistry registry,
            Record[] records, VersionedSaveContext context, long generation)
        {
            Registry = registry; Records = records; Context = context; Generation = generation;
            ThreadId = Thread.CurrentThread.ManagedThreadId;
            FactoryPreparationTag = VersionedSaveFactoryFrame.PreparationTag;
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

    // Dormant managed handoff for the existing critical writer. Only an explicit
    // internal owner opens a frame; this is not enrollment, logical save-attempt
    // provenance, native Original reachability or an archive-commit certificate.
    internal sealed class VersionedSaveFactoryFrame : IDisposable
    {
        [ThreadStatic] private static VersionedSaveFactoryFrame current;
        [ThreadStatic] private static object preparingTag;
        private readonly VersionedSaveFactoryFrame previous;
        private readonly Func<VersionedSaveContext, VersionedSaveCriticalCapture> prepare;
        private readonly object preparationTag = new object();
        private readonly int threadId;
        private VersionedSaveCriticalCapture capture;
        private Exception failure;
        private bool observed, closed, preparationReady;

        internal static VersionedSaveFactoryFrame Current => current;
        internal static object PreparationTag => preparingTag;
        internal Exception Failure => Volatile.Read(ref failure);

        private VersionedSaveFactoryFrame(Func<VersionedSaveContext, VersionedSaveCriticalCapture> prepare, bool preparationReady)
        {
            this.prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
            this.preparationReady = preparationReady;
            threadId = Thread.CurrentThread.ManagedThreadId;
            previous = current;
            if (previous != null)
            {
                var error = new InvalidOperationException("Nested critical factory frames cannot authorize replacement.");
                previous.RecordFailure(error); RecordFailure(error);
            }
            current = this;
        }

        // Creator-thread stack lifetime. The callback returns ownership of one
        // newly prepared, unserved bundle for the supplied context. If it throws
        // before returning, it owns its own cleanup.
        internal static VersionedSaveFactoryFrame Begin(
            Func<VersionedSaveContext, VersionedSaveCriticalCapture> prepare) => new VersionedSaveFactoryFrame(prepare, true);

        // The native owner opens this before freezing participants. A reentrant
        // factory during preparation must not capture their partially frozen state.
        // Standalone Begin retains its immediate preparation contract.
        internal static VersionedSaveFactoryFrame BeginOwnedPreparation(
            Func<VersionedSaveContext, VersionedSaveCriticalCapture> prepare) => new VersionedSaveFactoryFrame(prepare, false);

        // Called only by the owning transaction after all freezes and readiness
        // checks, before Original or native path access. An earlier factory failure
        // remains exact even if the Extender swallowed the dispatch exception.
        internal void CompleteOwnedPreparation()
        {
            if (!IsActive() || Failure != null) throw Failure;
            if (observed)
            {
                RecordFailure(new InvalidOperationException("The critical factory dispatched before owned preparation completed."));
                throw Failure;
            }
            preparationReady = true;
        }

        internal void RecordFailure(Exception error)
        {
            if (error == null) throw new ArgumentNullException(nameof(error));
            Interlocked.CompareExchange(ref failure, error, null);
        }

        private bool IsActive()
        {
            if (Thread.CurrentThread.ManagedThreadId == threadId && !closed && ReferenceEquals(current, this))
                return true;
            RecordFailure(new InvalidOperationException("The critical factory frame changed thread or lifetime."));
            return false;
        }

        // Called by the existing SaveContext factory owner before real handler
        // dispatch. SE can swallow an error: retain it here instead of treating a
        // thrown callback as native cancellation. No callback is retried.
        internal void Capture(VersionedSaveContext context)
        {
            if (!IsActive() || Failure != null) return;
            if (observed)
            {
                RecordFailure(new InvalidOperationException("The critical factory dispatched more than once."));
                return;
            }
            observed = true;
            if (!preparationReady)
            {
                RecordFailure(new InvalidOperationException("The critical factory dispatched before owned preparation completed."));
                return;
            }
            try
            {
                VersionedSaveCriticalCapture prepared;
                object previousTag = preparingTag;
                preparingTag = preparationTag;
                try { prepared = prepare(context); }
                finally { preparingTag = previousTag; }
                // Consumer capture can reenter internal save code. A frame
                // closed/replaced during that callback cannot acquire ownership
                // after its finally has already run; release the returned bundle.
                if (!IsActive())
                {
                    prepared?.Dispose();
                    return;
                }
                capture = prepared;
                if (capture == null)
                    RecordFailure(new InvalidOperationException("The critical factory did not prepare its enrolled records."));
                else if (!ReferenceEquals(capture.FactoryPreparationTag, preparationTag))
                    RecordFailure(new InvalidOperationException("The critical factory bundle was not prepared by this frame's callback."));
                else if (!capture.Ready || capture.Failed || capture.Closed ||
                    capture.ThreadId != threadId || capture.Context.IsSaveFile != context.IsSaveFile ||
                    capture.Context.IsMapEditorSave != context.IsMapEditorSave ||
                    !string.Equals(capture.Context.FilePath, context.FilePath, StringComparison.Ordinal))
                    RecordFailure(new InvalidOperationException("The critical factory returned a different capture context or lifetime."));
                else
                    foreach (var record in capture.Records)
                        if (record.Served)
                        {
                            RecordFailure(new InvalidOperationException("A critical factory record was already served before dispatch."));
                            break;
                        }
            }
            catch (Exception error) { RecordFailure(error); }
        }

        // The writer calls this after Original, before examining the completed
        // temporary archive. Once-only transport is necessary but not sufficient:
        // every actual archive record must also match the returned bundle.
        internal bool TryGetWrittenCapture(out VersionedSaveCriticalCapture written, out string reason)
        {
            written = null;
            if (!IsActive()) { reason = Failure.Message; return false; }
            if (Failure != null) { reason = Failure.Message; return false; }
            if (!observed || capture == null)
                RecordFailure(new InvalidOperationException("The critical factory dispatch was missing."));
            else if (!capture.AllWritten(out reason))
                RecordFailure(new InvalidOperationException(reason));
            if (Failure != null) { reason = Failure.Message; return false; }
            written = capture; reason = string.Empty; return true;
        }

        public void Dispose()
        {
            if (Thread.CurrentThread.ManagedThreadId != threadId)
            {
                var error = new InvalidOperationException("The critical factory frame must close on its owning thread.");
                RecordFailure(error); throw error;
            }
            if (closed) return;
            if (!ReferenceEquals(current, this))
            {
                var error = new InvalidOperationException("Critical factory frames must close in stack order.");
                RecordFailure(error); throw error;
            }
            closed = true;
            try { capture?.Dispose(); }
            finally { capture = null; current = previous; }
        }
    }

    internal sealed partial class VersionedSaveDataRegistry
    {
        private VersionedSaveCriticalSource[] writerCohort = Array.Empty<VersionedSaveCriticalSource>();
        private bool criticalCreatedWorldStarted;
        internal bool HasCriticalWriterDemand => Volatile.Read(ref writerCohort).Length != 0;

        // Tracking/enrollment alone still adds no writer demand. Joining is an
        // explicit internal process-lifetime startup choice, not consumer import.
        internal bool TryJoinCriticalWriterCohort(VersionedSaveCriticalSource source, out string reason)
        {
            lock (sync)
            {
                Slot slot = FindPreparedSource(source?.PreparedSource);
                if (generation != 0 || windowOpen || criticalCreatedWorldStarted || slot == null || !ReferenceEquals(slot.CriticalSource, source) || source.WriterCohortJoined)
                { reason = "Join one exact enrolled source before the first world/load."; return false; }
                var next = new List<VersionedSaveCriticalSource>();
                foreach (Slot registered in slots)
                    if (registered.CriticalSource?.WriterCohortJoined == true || ReferenceEquals(registered.CriticalSource, source))
                        next.Add(registered.CriticalSource);
                var published = next.ToArray(); // Allocate before irreversible publication.
                source.WriterCohortJoined = true;
                Volatile.Write(ref writerCohort, published);
                reason = string.Empty; return true;
            }
        }

        // Only the exact existing EditorCreated boundary may call this. This is
        // intentionally not a Prepared delivery and never finalizes an archive.
        internal bool TryAuthorizeCriticalCreatedWorld(VersionedSavePreparedSource source, object world,
            long sessionId, out string reason)
        {
            lock (sync)
            {
                Slot slot = FindPreparedSource(source);
                if (world == null || sessionId <= 0 || windowOpen || deliveringLoadGeneration == generation || slot?.CriticalSource?.WriterCohortJoined != true ||
                    slot.CriticalCapture != null || slot.ActiveCriticalSourceCaptures != 0)
                { reason = "An exact idle joined source and explicit created world/session are required."; return false; }
                if (slot.CreatedWorldOrigin != null && ReferenceEquals(slot.CreatedWorldOrigin.World, world) && slot.CreatedWorldOrigin.Session == sessionId)
                { reason = string.Empty; return true; }
                criticalCreatedWorldStarted = true;
                slot.PreparedOrigin?.Invalidate(); slot.PreparedOrigin = null;
                slot.PreparedOutcome = null; slot.PreparedFailed = false;
                slot.LoadedBytes = null; slot.Unknown = null; slot.KeepLoaded = false; slot.Handle.SetKeepsLoaded(false);
                slot.CreatedWorldOrigin = new VersionedSaveCriticalCreatedWorldOrigin(source, generation, world, sessionId);
                reason = string.Empty; return true;
            }
        }

        // Exact cohort identities/floors are stored together. A missing participant
        // must not turn a protected archive into a smaller, falsely certified set.
        private byte[] WriterCohortMarker()
        {
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, Encoding.UTF8, true))
            {
                writer.Write(0x31484343); // CCH1, internal marker format 1.
                writer.Write(writerCohort.Length);
                foreach (VersionedSaveCriticalSource source in writerCohort)
                {
                    Slot slot = FindPreparedSource(source.PreparedSource);
                    WriteCohortName(writer, slot.Owner); WriteCohortName(writer, slot.Identifier);
                    WriteCohortName(writer, source.ReservedEntry); writer.Write(source.MinimumCutoverReader);
                }
                writer.Flush(); return bytes.ToArray();
            }
        }

        private static void WriteCohortName(BinaryWriter writer, string value)
        {
            // Registration identity uses ordinal .NET strings. Persist exact UTF-16
            // code units so replacement encoders cannot alias different owners.
            writer.Write(value.Length);
            for (int i = 0; i < value.Length; i++) writer.Write((ushort)value[i]);
        }

        private static bool SameCohortMarker(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }

        // Read validates original bytes before migration or known-entry partition.
        private bool ValidateWriterCohortRecord(Slot slot, VersionedSaveEntries entries,
            int version, int minimumReader, out string reason)
        {
            lock (sync)
            {
                VersionedSaveCriticalSource source = slot.CriticalSource;
                if (source == null) { reason = string.Empty; return true; }
                bool marked = entries.TryGet(source.ReservedEntry, out byte[] marker);
                bool cohortMarker = marked && marker.Length >= 4 && marker[0] == 0x43 && marker[1] == 0x43 && marker[2] == 0x48 && marker[3] == 0x31;
                if (!source.WriterCohortJoined)
                {
                    reason = cohortMarker ? "The stored protected cohort is unavailable; retain its original bytes." : string.Empty;
                    return !cohortMarker;
                }
                if (!marked && minimumReader < source.MinimumCutoverReader)
                { reason = string.Empty; return true; }
                if (!marked || version < source.MinimumCutoverReader || minimumReader < source.MinimumCutoverReader ||
                    !SameCohortMarker(marker, WriterCohortMarker()))
                { reason = "The stored protected cohort or reader floor differs; retain its original bytes."; return false; }
                reason = string.Empty; return true;
            }
        }

        // One protected archive must contain the same original protection mode for
        // every participant. NoRecord/legacy for one member cannot supply the
        // missing half of another member's already protected checkpoint.
        private string ValidateOriginalWriterCohort(long loadGeneration, Slot[] snapshot, byte[][] pending, Outcome[] prepared)
        {
            lock (sync)
            {
                if (generation != loadGeneration || windowOpen || writerCohort.Length == 0) return null;
                bool anyProtected = false, everyProtected = true;
                foreach (VersionedSaveCriticalSource source in writerCohort)
                {
                    Slot slot = FindPreparedSource(source.PreparedSource);
                    int index = Array.IndexOf(snapshot, slot);
                    byte[] original = prepared[index]?.LoadedBytes ?? pending[index];
                    bool marked = false;
                    if (original != null && VersionedSaveRecordCodec.HasSignature(original))
                    {
                        var decoded = VersionedSaveRecordCodec.Decode(original, slot.Current);
                        marked = decoded.Status == VersionedSaveRecordCodec.DecodeStatus.Valid &&
                            decoded.RecordVersion >= source.MinimumCutoverReader && decoded.MinimumReaderVersion >= source.MinimumCutoverReader &&
                            decoded.Entries.TryGet(source.ReservedEntry, out byte[] marker) && SameCohortMarker(marker, WriterCohortMarker());
                    }
                    anyProtected |= marked; everyProtected &= marked;
                }
                if (!anyProtected || everyProtected) return null;
                foreach (VersionedSaveCriticalSource source in writerCohort)
                {
                    Slot slot = FindPreparedSource(source.PreparedSource);
                    slot.PreparedFailed = true; slot.KeepLoaded = true; slot.Handle.SetKeepsLoaded(true); slot.PreparedOrigin?.Invalidate();
                }
                return "The protected archive omits or mixes a cohort participant; retain every available original record.";
            }
        }

        private bool TryGetWriterRecords(out VersionedSaveCriticalRecord[] records, out string reason)
        {
            records = null;
            lock (sync)
            {
                if (writerCohort.Length == 0 || windowOpen)
                { reason = "An idle joined cohort is required."; return false; }
                byte[] marker = WriterCohortMarker();
                var selected = new VersionedSaveCriticalRecord[writerCohort.Length];
                VersionedSaveCriticalCreatedWorldOrigin created = null;
                bool? createdCohort = null;
                for (int i = 0; i < selected.Length; i++)
                {
                    VersionedSaveCriticalSource source = writerCohort[i];
                    Slot slot = FindPreparedSource(source.PreparedSource);
                    if (slot == null || slot.KeepLoaded)
                    { reason = "A joined source retains unavailable loaded bytes."; return false; }
                    bool isCreated = slot.CreatedWorldOrigin != null;
                    if (createdCohort.HasValue && createdCohort.Value != isCreated)
                    { reason = "Archive and created-world sources cannot be mixed."; return false; }
                    createdCohort = isCreated;
                    if (isCreated)
                    {
                        var origin = slot.CreatedWorldOrigin;
                        if (origin.Generation != generation || (created != null &&
                            (!ReferenceEquals(created.World, origin.World) || created.Session != origin.Session)))
                        { reason = "Created-world sources must share the exact current world/session."; return false; }
                        created = origin; selected[i] = new VersionedSaveCriticalRecord(source, origin, marker, true);
                    }
                    else
                    {
                        if (finalizedLoadGeneration != generation || slot.PreparedOrigin == null ||
                            !slot.PreparedOrigin.Completed || !slot.PreparedOrigin.IsReadable)
                        { reason = "Every archive source needs its complete current readable origin."; return false; }
                        selected[i] = new VersionedSaveCriticalRecord(source, slot.PreparedOrigin, marker);
                    }
                }
                records = selected; reason = string.Empty; return true;
            }
        }

        // State publication is stricter than successful early decoding. Reentrant
        // Prepared/Loaded observers cannot activate one source while another can
        // still reject the same load. A created world has a separate exact token.
        internal bool TryValidateCriticalWorld(VersionedSavePreparedSource source, object world, long sessionId, out string reason)
        {
            lock (sync)
            {
                Slot slot = FindPreparedSource(source);
                if (slot?.CriticalSource?.WriterCohortJoined != true || world == null || sessionId <= 0)
                { reason = "An exact joined source and current world/session are required."; return false; }
                if (slot.CreatedWorldOrigin != null)
                {
                    if (!ReferenceEquals(slot.CreatedWorldOrigin.World, world) || slot.CreatedWorldOrigin.Session != sessionId)
                    { reason = "The created-world origin belongs to another world/session."; return false; }
                }
                else if (slot.PreparedOutcome?.Load.MissionSessionId != sessionId)
                { reason = "The archive origin belongs to another mission attempt."; return false; }
                return TryGetWriterRecords(out _, out reason);
            }
        }

        internal void RequireCriticalWriterReady()
        {
            if (!TryGetWriterRecords(out _, out string reason)) throw new InvalidOperationException(reason);
        }

        // Open after demanded engine/fault/pre-capture guards, before native
        // freezes. Storage freezing can establish a first explicit blank origin;
        // the owner rechecks all origins before Original opens the temporary file.
        internal VersionedSaveFactoryFrame BeginCriticalWriterFrame(VersionedSaveContext pinned)
        {
            if (!HasCriticalWriterDemand || VersionedSaveFactoryFrame.Current != null)
                throw new InvalidOperationException("The demanded critical writer must own its factory frame.");
            return VersionedSaveFactoryFrame.BeginOwnedPreparation(actual =>
            {
                if (actual.IsSaveFile != pinned.IsSaveFile || actual.IsMapEditorSave != pinned.IsMapEditorSave ||
                    !string.Equals(actual.FilePath, pinned.FilePath, StringComparison.Ordinal))
                    throw new InvalidOperationException("The actual factory differs from the owned pre-capture context.");
                if (!TryGetWriterRecords(out var requested, out string reason) ||
                    !TryFreezeCriticalRecordsCore(requested, actual, VersionedSaveRecordCodec.MaximumRecordBytes, true, out var capture, out reason))
                    throw new InvalidOperationException(reason);
                return capture;
            });
        }

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
            out VersionedSaveCriticalCapture capture, out string reason) =>
            TryFreezeCriticalRecordsCore(requested, context, maximumTotalBytes, false, out capture, out reason);

        private bool TryFreezeCriticalRecordsCore(IReadOnlyList<VersionedSaveCriticalRecord> requested,
            VersionedSaveContext context, int maximumTotalBytes, bool allowCreated,
            out VersionedSaveCriticalCapture capture, out string reason)
        {
            capture = null;
            if (requested == null || requested.Count == 0 || requested.Count > MaximumPreparedSources ||
                maximumTotalBytes <= 0 || maximumTotalBytes > VersionedSaveRecordCodec.MaximumRecordBytes)
            { reason = "A bounded nonempty critical source set and aggregate byte limit are required."; return false; }
            VersionedSaveCriticalCapture bundle;
            lock (sync)
            {
                bool created = allowCreated && requested[0].CreatedOrigin != null;
                if (windowOpen || (!created && finalizedLoadGeneration != generation))
                { reason = "Critical capture requires the complete current load pass or an explicit created world."; return false; }
                var selected = new Dictionary<VersionedSaveCriticalSource, VersionedSaveCriticalCapture.Record>();
                foreach (VersionedSaveCriticalRecord item in requested)
                {
                    Slot slot = FindPreparedSource(item.Source?.PreparedSource);
                    if (slot == null || !ReferenceEquals(slot.CriticalSource, item.Source) ||
                        (created ? (item.CreatedOrigin == null || !ReferenceEquals(slot.CreatedWorldOrigin, item.CreatedOrigin) ||
                            item.CreatedOrigin.Generation != generation || !ReferenceEquals(item.CreatedOrigin.World, requested[0].CreatedOrigin.World) ||
                            item.CreatedOrigin.Session != requested[0].CreatedOrigin.Session) :
                            (item.CreatedOrigin != null || item.Origin == null || !ReferenceEquals(slot.PreparedOrigin, item.Origin) ||
                            item.Origin.Generation != generation || !item.Origin.Completed || !item.Origin.IsReadable)) ||
                        slot.KeepLoaded || slot.CriticalCapture != null || slot.ActiveCriticalSourceCaptures != 0 ||
                        item.Marker == null || item.Marker.Length == 0 || item.Marker.Length > VersionedSaveRecordCodec.MaximumEntryBytes ||
                        selected.ContainsKey(item.Source))
                    { reason = "An exact completed readable source, unique marker and idle capture slot are required."; return false; }
                    selected.Add(item.Source, new VersionedSaveCriticalCapture.Record
                    {
                        Source = item.Source, Origin = item.Origin, CreatedOrigin = item.CreatedOrigin, Marker = (byte[])item.Marker.Clone(),
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
                (bundle.Records[0].CreatedOrigin == null && finalizedLoadGeneration != generation) || bundle.Generation != generation ||
                bundle.ThreadId != Thread.CurrentThread.ManagedThreadId)
            { reason = bundle == null || string.IsNullOrEmpty(bundle.Failure) ? "The critical capture lifetime/thread changed." : bundle.Failure; return false; }
            foreach (var record in bundle.Records)
            {
                Slot slot = FindPreparedSource(record.Source.PreparedSource);
                if (slot == null || !ReferenceEquals(slot.CriticalCapture, bundle) || slot.KeepLoaded ||
                    (record.CreatedOrigin != null ? (!ReferenceEquals(slot.CreatedWorldOrigin, record.CreatedOrigin) || record.CreatedOrigin.Generation != generation) :
                        (!ReferenceEquals(slot.PreparedOrigin, record.Origin) || record.Origin == null || !record.Origin.Completed || !record.Origin.IsReadable)))
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

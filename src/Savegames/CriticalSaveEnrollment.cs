using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Savegames
{
    internal sealed class CriticalSaveMember
    {
        internal CriticalSaveParticipant Participant;
        internal CriticalSavePrepare Prepare;
        internal CriticalSaveRelease Release;
    }

    // Everything that can allocate is prepared before the irreversible metadata
    // publication. The exact registry snapshots are checked again at commit.
    internal sealed class CriticalSaveEnrollmentPlan
    {
        internal VersionedSaveDataRegistry Registry;
        internal object Slot, Slots;
        internal int PreparedCount;
        internal VersionedSaveCriticalSource[] PreviousCohort, Cohort;
        internal CriticalSaveMember[] PreviousMembers, Members;
        internal VersionedSavePreparedSource Prepared;
        internal VersionedSaveCriticalSource Critical;
        internal CriticalSaveParticipant Participant;
        internal bool NewPrepared, RequiresStorage, Committed;
    }

    // Internal assignment-only import publication, never a consumer callback
    // scope. Lock order is actual engine -> import metadata -> registry. The
    // registry generation observer runs after releasing this same monitor.
    internal sealed class CriticalImportCommitLease : IDisposable
    {
        private readonly object sync;
        private readonly int thread;
        private bool held;
        internal CriticalImportCommitLease(object sync)
        { this.sync = sync; thread = Thread.CurrentThread.ManagedThreadId; }
        internal void Acquired() { held = true; }
        public void Dispose()
        {
            if (!held) return;
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Import publication must finish on its acquiring thread.");
            held = false; Monitor.Exit(sync);
        }
    }

    // One owning save invocation. No consumer callbacks execute under registry
    // locks. State assignments through ref survive false/throw for paired release.
    internal sealed class CriticalSavePreparation : IDisposable
    {
        private readonly VersionedSaveDataRegistry registry;
        private readonly CriticalSaveMember[] members;
        private readonly VersionedSaveContext context;
        private readonly object[] states;
        private readonly long generation;
        private readonly int thread;
        private int entered;
        private bool prepared, accepted, closed;

        internal CriticalSavePreparation(VersionedSaveDataRegistry registry, CriticalSaveMember[] members,
            VersionedSaveContext context, long generation)
        {
            this.registry = registry; this.members = members; this.context = context; this.generation = generation;
            states = new object[members.Length]; thread = Thread.CurrentThread.ManagedThreadId;
        }

        internal bool Prepare()
        {
            if (closed || prepared || Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Critical participant preparation has changed thread or lifetime.");
            prepared = true;
            for (int i = 0; i < members.Length; i++)
            {
                entered = i + 1;
                if (!members[i].Prepare(context, ref states[i])) return false;
            }
            accepted = true; return true;
        }

        internal void RequireCurrent()
        {
            if (closed || Thread.CurrentThread.ManagedThreadId != thread || !accepted || entered != members.Length)
                throw new InvalidOperationException("The complete owning participant preparation is required.");
            registry.RequireParticipantPreparationCurrent(members, generation);
        }

        public void Dispose()
        {
            if (closed) return;
            if (Thread.CurrentThread.ManagedThreadId != thread)
                throw new InvalidOperationException("Critical participants must release on their preparing thread.");
            closed = true;
            Exception first = null;
            for (int i = entered - 1; i >= 0; i--)
            {
                try { members[i].Release(context, states[i]); }
                catch (Exception error) { if (first == null) first = error; }
                finally { states[i] = null; }
            }
            if (first != null) throw first;
        }
    }

    internal sealed partial class VersionedSaveDataRegistry
    {
        private CriticalSaveMember[] criticalParticipants = Array.Empty<CriticalSaveMember>();
        private bool sourceSaveAttempted, requiresNativeStorage;
        private VersionedSaveCriticalSource nativeStorageCriticalSource;
        private Action<long> generationObserver;

        // The storage owner binds once at startup. Revocation is synchronous with
        // actual registry generations, outside sync, rather than queued lifecycle delivery.
        internal bool TryObserveGeneration(Action<long> observer, out string reason)
        {
            lock (sync)
            {
                if (observer == null || generation != 0 || windowOpen ||
                    (generationObserver != null && generationObserver != observer))
                { reason = "Bind one generation owner before the first archive load."; return false; }
                generationObserver = observer; reason = string.Empty; return true;
            }
        }

        // All native probes and allocations belong before this entry. While the
        // lease is held the importer may only publish its already-built state,
        // receipts and pending-plan snapshot; no user callback or native call.
        internal bool TryAcquireCriticalImportCommit(VersionedSavePreparedSource source, VersionedSavePreparedOrigin origin,
            VersionedSavePreparedSource storageSource, long sessionId, out CriticalImportCommitLease lease, out string reason)
        {
            lease = null;
            var preparedLease = new CriticalImportCommitLease(sync);
            Monitor.Enter(sync);
            try
            {
                Slot slot = FindPreparedSource(source), storage = FindPreparedSource(storageSource);
                if (slot == null || storage == null || origin == null || sessionId <= 0 || generation <= 0 ||
                    windowOpen || finalizedLoadGeneration != generation || origin.Generation != generation ||
                    !ReferenceEquals(slot.PreparedOrigin, origin) || !origin.Completed || !origin.IsReadable ||
                    slot.PreparedOutcome?.Load.MissionSessionId != sessionId ||
                    storage.PreparedOutcome?.Load.MissionSessionId != sessionId ||
                    slot.CriticalSource?.WriterCohortJoined != true ||
                    !ReferenceEquals(storage.CriticalSource, nativeStorageCriticalSource) || nativeStorageCriticalSource == null)
                { reason = "The exact completed joint source generation and session are required for publication."; return false; }
                foreach (VersionedSaveCriticalSource member in writerCohort)
                {
                    Slot item = FindPreparedSource(member.PreparedSource);
                    if (item == null || item.KeepLoaded || item.CriticalCapture != null || item.ActiveCriticalSourceCaptures != 0 ||
                        item.CreatedWorldOrigin != null || item.PreparedOrigin == null ||
                        item.PreparedOrigin.Generation != generation || !item.PreparedOrigin.Completed || !item.PreparedOrigin.IsReadable ||
                        item.PreparedOutcome?.Load.MissionSessionId != sessionId)
                    { reason = "Every joint source must remain idle and readable in the same completed generation."; return false; }
                }
                preparedLease.Acquired(); lease = preparedLease; reason = string.Empty; return true;
            }
            finally { if (lease == null) Monitor.Exit(sync); }
        }

        // Cold world activation publishes only under the same generation monitor as
        // archive import. Both origins are retained preparation evidence; this entry
        // never reacquires a token after a reset. Stable unready calls allocate nothing.
        // Caller lock order remains engine -> import metadata -> registry.
        internal bool TryAcquireCriticalWorldPublication(VersionedSavePreparedSource source,
            VersionedSavePreparedOrigin expectedArchiveOrigin, VersionedSaveCriticalCreatedWorldOrigin expectedCreatedOrigin,
            object world, long sessionId, out CriticalImportCommitLease lease, out string reason)
        {
            lease = null;
            lock (sync)
                if (!ValidateCriticalWorldPublicationLocked(source, expectedArchiveOrigin, expectedCreatedOrigin,
                    world, sessionId, out reason)) return false;
            // Allocation belongs before the assignment-only aperture, after the cheap
            // negative path. Metadata may change here, so repeat the exact predicate.
            var preparedLease = new CriticalImportCommitLease(sync);
            Monitor.Enter(sync);
            try
            {
                if (!ValidateCriticalWorldPublicationLocked(source, expectedArchiveOrigin, expectedCreatedOrigin,
                    world, sessionId, out reason)) return false;
                preparedLease.Acquired(); lease = preparedLease; reason = string.Empty; return true;
            }
            finally { if (lease == null) Monitor.Exit(sync); }
        }

        // Caller holds sync. These owned fields cover the same complete-world/origin
        // readiness as the capture bundle without constructing markers or record arrays.
        // A returned lease permits preallocated assignments only, never consumer code,
        // probes, native calls or additional allocations while this monitor stays held.
        private bool ValidateCriticalWorldPublicationLocked(VersionedSavePreparedSource source,
            VersionedSavePreparedOrigin expectedArchiveOrigin, VersionedSaveCriticalCreatedWorldOrigin expectedCreatedOrigin,
            object world, long sessionId, out string reason)
        {
            Slot slot = FindPreparedSource(source);
            if (slot?.CriticalSource?.WriterCohortJoined != true ||
                (expectedArchiveOrigin == null) == (expectedCreatedOrigin == null))
            { reason = "The exact joined source and one retained archive or created-world origin are required."; return false; }
            if (expectedArchiveOrigin != null)
            {
                if (slot.CreatedWorldOrigin != null || !ReferenceEquals(slot.PreparedOrigin, expectedArchiveOrigin) ||
                    !ReferenceEquals(expectedArchiveOrigin.Source, source) || expectedArchiveOrigin.Generation != generation ||
                    !expectedArchiveOrigin.Completed || !expectedArchiveOrigin.IsReadable)
                { reason = "The retained archive origin was replaced or revoked before world publication."; return false; }
            }
            else if (slot.PreparedOrigin != null || !ReferenceEquals(slot.CreatedWorldOrigin, expectedCreatedOrigin) ||
                !ReferenceEquals(expectedCreatedOrigin.Source, source) || expectedCreatedOrigin.Generation != generation ||
                !ReferenceEquals(expectedCreatedOrigin.World, world) || expectedCreatedOrigin.Session != sessionId)
            { reason = "The retained created-world origin was replaced or revoked before world publication."; return false; }
            if (!TryValidateWriterWorld(world, sessionId, out reason)) return false;
            foreach (Slot member in slots)
                if (member.CriticalSource?.WriterCohortJoined == true &&
                    (member.CriticalCapture != null || member.ActiveCriticalSourceCaptures != 0))
                { reason = "A frozen or capturing critical source cannot publish a world state."; return false; }
            reason = string.Empty; return true;
        }

        internal bool TryPrepareCriticalParticipant(string owner, VersionedSaveDataHandle handle, CriticalSaveOptions options,
            out CriticalSaveEnrollmentPlan plan, out string reason)
        {
            plan = null;
            if (options == null || options.Prepare == null || options.Release == null ||
                options.Modes != CriticalSaveModes.SinglePlayerAndEditor ||
                !VersionedSaveRecordCodec.IsValidName(options.ReservedMarkerEntry))
            { reason = "Valid SP/editor options with Prepare, Release and a reserved entry are required."; return false; }
            // Snapshot the caller-owned options once before locking. Later edits cannot
            // remove demand, replace callbacks, or change the protection floor.
            string marker = options.ReservedMarkerEntry;
            int reader = options.MinimumReaderVersion;
            bool storage = options.RequiresNativeStorage;
            var prepare = options.Prepare; var release = options.Release;
            lock (sync)
            {
                Slot slot = null;
                foreach (Slot item in slots) if (ReferenceEquals(item.Handle, handle)) { slot = item; break; }
                if (slot == null || !string.Equals(owner, slot.Owner, StringComparison.Ordinal))
                { reason = "The exact live handle must belong to this owner and registry."; return false; }
                if (generation != 0 || windowOpen || criticalCreatedWorldStarted || sourceSaveAttempted ||
                    slot.CriticalCapture != null || slot.ActiveCriticalSourceCaptures != 0)
                { reason = "Enroll during startup before any world, archive or source-save attempt."; return false; }
                if (slot.Scope != VersionedSaveScope.SaveGamesAndMaps || !slot.SimulationRelevant || slot.Prepared == null ||
                    !slot.Known.IsKnown(marker) || reader < slot.MinimumReader || reader > slot.Current)
                { reason = "The source must declare saves/maps, simulation relevance, Prepared, its known marker and reader floor."; return false; }
                if (slot.CriticalSource != null)
                { reason = "This source already has a critical enrollment."; return false; }
                bool newPrepared = slot.PreparedSource == null;
                if (newPrepared && preparedSourceCount >= MaximumPreparedSources)
                { reason = "The bounded prepared-source catalogue is full."; return false; }
                var source = slot.PreparedSource ?? new VersionedSavePreparedSource(this, slot.Handle, slot.Scope, slot.Current, slot.MinimumReader);
                var critical = new VersionedSaveCriticalSource(source, marker, reader);
                var participant = new CriticalSaveParticipant(source, critical, slot.Handle, storage);
                var member = new CriticalSaveMember { Participant = participant, Prepare = prepare, Release = release };
                var members = new List<CriticalSaveMember>(criticalParticipants.Length + 1);
                var cohort = new List<VersionedSaveCriticalSource>(writerCohort.Length + 1);
                foreach (Slot item in slots)
                {
                    if (ReferenceEquals(item, slot)) { members.Add(member); cohort.Add(critical); continue; }
                    if (item.CriticalSource?.WriterCohortJoined == true) cohort.Add(item.CriticalSource);
                    foreach (CriticalSaveMember existing in criticalParticipants)
                        if (ReferenceEquals(existing.Participant.Handle, item.Handle)) { members.Add(existing); break; }
                }
                plan = new CriticalSaveEnrollmentPlan { Registry = this, Slot = slot, Slots = slots, PreparedCount = preparedSourceCount,
                    PreviousCohort = writerCohort, PreviousMembers = criticalParticipants, Prepared = source, Critical = critical,
                    Participant = participant, NewPrepared = newPrepared, RequiresStorage = storage, Cohort = cohort.ToArray(), Members = members.ToArray() };
                reason = string.Empty; return true;
            }
        }

        internal bool TryCommitCriticalParticipant(CriticalSaveEnrollmentPlan plan, out CriticalSaveParticipant participant, out string reason)
        {
            participant = null;
            lock (sync)
            {
                if (plan == null || !ReferenceEquals(plan.Registry, this) || plan.Committed ||
                    !ReferenceEquals(plan.Slots, slots) || !ReferenceEquals(plan.PreviousCohort, writerCohort) ||
                    !ReferenceEquals(plan.PreviousMembers, criticalParticipants) || plan.PreparedCount != preparedSourceCount ||
                    generation != 0 || windowOpen || criticalCreatedWorldStarted || sourceSaveAttempted)
                { reason = "Startup metadata changed; no enrollment was published."; return false; }
                var slot = (Slot)plan.Slot;
                if (slot.CriticalSource != null || slot.CriticalCapture != null || slot.ActiveCriticalSourceCaptures != 0 ||
                    (!plan.NewPrepared && !ReferenceEquals(slot.PreparedSource, plan.Prepared)))
                { reason = "The exact source changed; no enrollment was published."; return false; }
                // Assignment-only linearization; no transport or consumer code runs here.
                if (plan.NewPrepared) { slot.PreparedSource = plan.Prepared; preparedSourceCount++; }
                slot.CriticalSource = plan.Critical; plan.Critical.WriterCohortJoined = true;
                requiresNativeStorage |= plan.RequiresStorage;
                Volatile.Write(ref criticalParticipants, plan.Members);
                Volatile.Write(ref writerCohort, plan.Cohort);
                plan.Committed = true; participant = plan.Participant; reason = string.Empty; return true;
            }
        }

        internal bool TryEnrollCriticalParticipant(string owner, VersionedSaveDataHandle handle, CriticalSaveOptions options,
            out CriticalSaveParticipant participant, out string reason)
        {
            participant = null;
            return TryPrepareCriticalParticipant(owner, handle, options, out var plan, out reason) &&
                TryCommitCriticalParticipant(plan, out participant, out reason);
        }

        internal bool TryJoinNativeStorageCriticalSource(VersionedSaveCriticalSource source, out string reason)
        {
            lock (sync)
            {
                if (nativeStorageCriticalSource != null)
                { reason = "The shared storage source is already joined."; return false; }
                if (!TryJoinCriticalWriterCohort(source, out reason)) return false;
                nativeStorageCriticalSource = source; return true;
            }
        }

        internal CriticalSavePreparation BeginParticipantPreparation(VersionedSaveContext context)
        {
            CriticalSaveMember[] members;
            long current;
            lock (sync) { members = criticalParticipants; current = generation; }
            return members.Length == 0 ? null : new CriticalSavePreparation(this, members, context, current);
        }

        internal void RequireParticipantPreparationCurrent(CriticalSaveMember[] members, long expectedGeneration)
        {
            lock (sync)
            {
                if (!ReferenceEquals(criticalParticipants, members) || generation != expectedGeneration)
                    throw new InvalidOperationException("The participant source window changed during preparation.");
                RequireCriticalWriterReady();
            }
        }

        // Metadata readiness alone deliberately accepts readable origins without
        // lifecycle evidence. The demanded native writer additionally binds EVERY
        // source to its current native world/session, including state-only mods.
        internal void RequireCriticalWriterWorld(object world, long sessionId)
        {
            lock (sync)
            {
                if (!TryValidateWriterWorld(world, sessionId, out string reason) ||
                    !TryGetWriterRecords(out _, out reason)) throw new InvalidOperationException(reason);
            }
        }

        // Shared activation/import/save association gate. Run this bounded field-only
        // pass before constructing optional record arrays: incomplete/foreign worlds
        // are common early-load refusals, not exceptional or allocation-worthy work.
        private bool TryValidateWriterWorld(object world, long sessionId, out string reason)
        {
            if (world == null || sessionId <= 0 || writerCohort.Length == 0 || windowOpen ||
                (requiresNativeStorage && nativeStorageCriticalSource == null))
            { reason = "An idle joined cohort and positive current native world/session are required."; return false; }
            int originKind = 0, joined = 0;
            foreach (Slot slot in slots)
            {
                if (slot.CriticalSource?.WriterCohortJoined != true) continue;
                joined++;
                if (slot.KeepLoaded)
                { reason = "A joined source retains unavailable original bytes."; return false; }
                var created = slot.CreatedWorldOrigin;
                if (created != null)
                {
                    if (originKind == 1 || created.Generation != generation ||
                        !ReferenceEquals(created.World, world) || created.Session != sessionId)
                    { reason = "Created sources must share the exact current native world/session."; return false; }
                    originKind = 2;
                }
                else
                {
                    var origin = slot.PreparedOrigin;
                    if (originKind == 2 || finalizedLoadGeneration != generation || origin == null ||
                        origin.Generation != generation || !origin.Completed || !origin.IsReadable ||
                        slot.PreparedOutcome?.Load.MissionSessionId != sessionId)
                    { reason = "Every archived source needs its complete readable current native mission association."; return false; }
                    originKind = 1;
                }
            }
            if (joined != writerCohort.Length)
            { reason = "The complete joined source set is required."; return false; }
            reason = string.Empty; return true;
        }
    }
}

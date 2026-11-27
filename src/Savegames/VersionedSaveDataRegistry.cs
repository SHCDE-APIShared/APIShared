using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using APIShared.Events;

namespace APIShared.Savegames
{
    /// <summary>Immutable logical save context, free of Script Extender and native state.</summary>
    /// <remarks>Flags and path identify the owning factory call. They grant no mutation,
    /// import, thread-dispatch or multiplayer authority. The path may be null.</remarks>
    public readonly struct VersionedSaveContext
    {
        internal VersionedSaveContext(bool isSaveFile, bool isMapEditorSave, string filePath)
        { IsSaveFile = isSaveFile; IsMapEditorSave = isMapEditorSave; FilePath = filePath; }
        /// <summary>Whether this factory is writing a save file.</summary>
        public bool IsSaveFile { get; }
        /// <summary>Whether this factory is an editor map save.</summary>
        public bool IsMapEditorSave { get; }
        /// <summary>The logical factory path, possibly null; never the native temporary path.</summary>
        public string FilePath { get; }
    }

    // Owns the registrations and the load window. The Script Extender binding
    // (VersionedSaveDataRuntime) feeds it OnLoadSave/OnLoadMap Pre (BeginLoad), the per-identifier
    // ModSaveDataAPI deliveries, OnLoadSave/OnLoadMap Post (EndLoad) and OnUnloadMap (Unload).
    // SE 2.14.1 reads the archive and delivers every stored entry in BOTH phases of one load
    // (GameMapArchiveManagerAPI.cs:117-143 -> :324-352 -> ModSaveDataAPI.cs:228-267). Exactly one of
    // those deliveries falls between this binding's Pre and Post handlers whichever subscribed first,
    // so only that one counts and the other is a duplicate. A load without the entry delivers
    // nothing (ModSaveDataAPI.cs:250-254), which EndLoad reports as NoRecord. Map unloads inside the
    // window belong to the load. Kept and unknown entries belong to one mission: they are dropped at
    // the next load and at an unload outside a load, as the Extender drops its archive
    // (GameMapArchiveManagerAPI.cs:105-111).
    internal sealed partial class VersionedSaveDataRegistry
    {
        internal delegate bool Transport(string identifier, Func<VersionedSaveContext, byte[]> save, Action<byte[]> load);

        private readonly object sync = new object();
        private readonly Transport transport;
        private readonly Action<string> warning;
        private readonly Action<string> error;
        private Slot[] slots = new Slot[0];
        private bool windowOpen;
        private long generation;
        // Closed executable-admission stamps read this scalar without taking the registry
        // lock or waiting for observers. Both existing writers publish atomically below.
        internal long CurrentGeneration => Interlocked.Read(ref generation);
        // Closing the transport window precedes Loaded callbacks. Critical cold
        // operations wait for the entire matching EndLoad pass instead.
        private long finalizedLoadGeneration = -1;
        private long deliveringLoadGeneration = -1;
        private VersionedSaveLoadSource windowSource;
        private string windowFile;
        private long windowMissionSessionId;
        private long failures;
        [ThreadStatic] private static AtomicSnapshot atomicSnapshot;

        internal sealed class AtomicSnapshot
        {
            internal VersionedSaveDataRegistry Owner;
            internal object RegistrationIdentity;
            internal VersionedSaveContext Context;
            internal long Generation;
            internal bool Preparing, Failed;
            internal readonly Dictionary<string, byte[]> Records = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, VersionedSaveCriticalCapture> CriticalRecords = new Dictionary<string, VersionedSaveCriticalCapture>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> Delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        internal bool RequiresAtomicWrite(VersionedSaveContext context)
        {
            Slot[] snapshot; lock (sync) snapshot = slots;
            foreach (Slot slot in snapshot) if (slot.RequireAtomicWrite && slot.Writes(context)) return true;
            return false;
        }

        // Caller owns the actual engine monitor. Publish the reentrancy guard before
        // invoking any consumer, and never hold the registry lock across its callback.
        internal AtomicSnapshot BeginAtomicSave(VersionedSaveContext context)
        {
            if (atomicSnapshot != null)
            {
                throw new InvalidOperationException("Nested atomic save capture.");
            }
            Slot[] snapshot;
            AtomicSnapshot prepared;
            lock (sync)
            {
                snapshot = slots;
                prepared = new AtomicSnapshot { Owner = this, Context = context, Generation = generation, RegistrationIdentity = snapshot, Preparing = true };
            }
            atomicSnapshot = prepared;
            try
            {
                foreach (Slot slot in snapshot)
                {
                    if (!slot.RequireAtomicWrite || !slot.Writes(context)) continue;
                    RequireAtomicCurrent(prepared, context, true);
                    byte[] bytes = SaveCore(slot, context, true);
                    RequireAtomicCurrent(prepared, context, true);
                    VersionedSaveCriticalCapture critical;
                    lock (sync) critical = slot.CriticalCapture;
                    // A frozen critical record is only peeked here. Its existing
                    // once-only transport must still mark Written during real dispatch.
                    if (bytes == null || slot.Handle.KeepsLoadedRecord ||
                        critical == null && slot.Handle.LastWriteStatus != VersionedSaveWriteStatus.Written)
                        throw new InvalidDataException("Required fresh save capture failed: " + slot.Identifier);
                    prepared.Records.Add(slot.Identifier, bytes);
                    if (critical != null) prepared.CriticalRecords.Add(slot.Identifier, critical);
                }
                RequireAtomicCurrent(prepared, context, true);
                prepared.Preparing = false;
                return prepared;
            }
            catch
            {
                prepared.Failed = true;
                EndAtomicSave(prepared);
                throw;
            }
        }

        private void RequireAtomicCurrent(AtomicSnapshot prepared, VersionedSaveContext context, bool preparing = false)
        {
            lock (sync)
            {
                if (prepared != null && ReferenceEquals(atomicSnapshot, prepared) && ReferenceEquals(prepared.Owner, this) &&
                    !prepared.Failed && (preparing || !prepared.Preparing) && prepared.Generation == generation && !windowOpen &&
                    AtomicRegistrationsMatch(prepared) &&
                    prepared.Context.IsSaveFile == context.IsSaveFile && prepared.Context.IsMapEditorSave == context.IsMapEditorSave &&
                    string.Equals(prepared.Context.FilePath, context.FilePath, StringComparison.Ordinal)) return;
                if (prepared != null) prepared.Failed = true;
                throw new InvalidDataException("The atomic save context, generation, registrations or lifetime changed.");
            }
        }

        private bool AtomicRegistrationsMatch(AtomicSnapshot prepared)
        {
            var original = prepared.RegistrationIdentity as Slot[];
            if (original == null) return false;
            int remaining = 0;
            foreach (Slot prior in original)
                if (prior.RequireAtomicWrite && prior.Writes(prepared.Context)) remaining++;
            foreach (Slot current in slots)
            {
                if (!current.RequireAtomicWrite || !current.Writes(prepared.Context)) continue;
                bool found = false;
                foreach (Slot prior in original)
                    if (ReferenceEquals(prior, current)) { found = true; break; }
                if (!found) return false;
                remaining--;
            }
            return remaining == 0;
        }

        internal void EndAtomicSave(AtomicSnapshot prepared)
        {
            if (ReferenceEquals(atomicSnapshot, prepared)) atomicSnapshot = null;
        }

        internal static void RequireAtomicDelivered(AtomicSnapshot prepared)
        {
            if (prepared == null) return;
            if (prepared.Owner == null) throw new InvalidDataException("The atomic save has no owning registry.");
            prepared.Owner.RequireAtomicCurrent(prepared, prepared.Context);
            foreach (string identifier in prepared.Records.Keys)
                if (!prepared.Delivered.Contains(identifier))
                    throw new InvalidDataException("Required fresh archive record was omitted: " + identifier);
        }

        internal VersionedSaveDataRegistry(Transport transport, Action<string> warning, Action<string> error)
        {
            this.transport = transport;
            this.warning = warning;
            this.error = error;
        }

        internal long CallbackFailures => Interlocked.Read(ref failures);

        /// <summary>Owner and identifier of every simulation-relevant registration, ordered by identifier
        /// ignoring case (the archive's name comparison).</summary>
        internal List<KeyValuePair<string, string>> SimulationRecords
        {
            get { return GetSimulationRecords(false); }
        }
        internal List<KeyValuePair<string, string>> MapSimulationRecords => GetSimulationRecords(true);
        private List<KeyValuePair<string, string>> GetSimulationRecords(bool map)
        {
            Slot[] snapshot;
            lock (sync) snapshot = slots;
            var result = new List<KeyValuePair<string, string>>();
            foreach (Slot slot in snapshot)
                if (slot.SimulationRelevant && (!map || slot.Scope == VersionedSaveScope.SaveGamesAndMaps))
                    result.Add(new KeyValuePair<string, string>(slot.Owner, slot.Identifier));
            result.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Value, b.Value));
            return result;
        }

        internal bool TryRegister(VersionedSaveDataRegistration registration, out VersionedSaveDataHandle handle, out string reason)
        {
            handle = null;
            if (!TryCreateSlot(registration, out Slot slot, out reason)) return false;
            lock (sync)
            {
                int declared = 0;
                foreach (Slot existing in slots)
                {
                    if (string.Equals(existing.Identifier, slot.Identifier, StringComparison.OrdinalIgnoreCase))
                    { reason = "The save data identifier is already registered."; return false; }
                    if (existing.SimulationRelevant) declared++;
                }
                if (slot.SimulationRelevant && declared >= SavegameRecordComparison.MaximumRecords)
                { reason = "At most " + SavegameRecordComparison.MaximumRecords + " records may be simulation-relevant."; return false; }
                bool accepted;
                try { accepted = transport(slot.Identifier, context => Save(slot, context), data => Deliver(slot, data)); }
                catch (Exception ex) { reason = "Script Extender registration failed: " + ex.Message; return false; }
                if (!accepted)
                { reason = "Script Extender rejected the identifier (already registered by another handler, or invalid)."; return false; }
                slots = OrderedRegistrations.WithAdded(slots, slot, Slot.Key);
            }
            handle = slot.Handle;
            reason = string.Empty;
            return true;
        }

        private static bool TryCreateSlot(VersionedSaveDataRegistration registration, out Slot slot, out string reason)
        {
            slot = null;
            if (registration == null) { reason = "A registration is required."; return false; }
            if (string.IsNullOrWhiteSpace(registration.OwnerGuid) || registration.OwnerGuid.Length > 256)
            { reason = "An owner GUID of at most 256 characters is required."; return false; }
            string identifier = registration.SaveDataIdentifier;
            if (string.IsNullOrWhiteSpace(identifier) || identifier.Length > 200 ||
                identifier.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            { reason = "The save data identifier must be a file-name-safe name of at most 200 characters."; return false; }
            if (registration.CurrentVersion < 1) { reason = "The current version must be at least 1."; return false; }
            if (registration.MinimumReaderVersion < 1 || registration.MinimumReaderVersion > registration.CurrentVersion)
            { reason = "The minimum reader version must be between 1 and the current version."; return false; }
            if (registration.Capture == null || registration.Loaded == null)
            { reason = "Capture and load callbacks are required."; return false; }
            if (registration.Scope != VersionedSaveScope.SaveGames && registration.Scope != VersionedSaveScope.SaveGamesAndMaps)
            { reason = "Unknown save scope."; return false; }
            if (registration.InvalidMigrations.Count != 0) { reason = registration.InvalidMigrations[0]; return false; }
            var migrations = new VersionedSaveMigration[registration.CurrentVersion];
            foreach (KeyValuePair<int, VersionedSaveMigration> step in registration.Migrations)
            {
                if (step.Key < 1 || step.Key >= registration.CurrentVersion)
                { reason = "Migration step " + step.Key + " is outside 1 .. current version - 1."; return false; }
                migrations[step.Key] = step.Value;
            }
            if (!VersionedSaveKnownEntries.TryCreate(registration.KnownEntries, out VersionedSaveKnownEntries known, out reason))
                return false;
            slot = new Slot(registration, known, migrations);
            return true;
        }

        internal void BeginLoad(VersionedSaveLoadSource source, string filePath)
            => BeginLoad(source, filePath, 0);

        internal void BeginLoad(VersionedSaveLoadSource source, string filePath, long missionSessionId)
        {
            Action<long> observer;
            long changedGeneration;
            lock (sync)
            {
                windowOpen = true;
                Interlocked.Increment(ref generation);
                finalizedLoadGeneration = -1;
                windowSource = source;
                windowFile = filePath;
                windowMissionSessionId = missionSessionId > 0 ? missionSessionId : 0;
                foreach (Slot slot in slots) { slot.Pending = null; slot.ResetMission(); }
                observer = generationObserver; changedGeneration = generation;
            }
            observer?.Invoke(changedGeneration);
        }

        internal void Unload()
        {
            Action<long> observer;
            long changedGeneration;
            lock (sync)
            {
                if (windowOpen) return; // The game unloads the previous map while loading the next one.
                Interlocked.Increment(ref generation);
                finalizedLoadGeneration = -1;
                windowMissionSessionId = 0;
                foreach (Slot slot in slots) slot.ResetMission();
                observer = generationObserver; changedGeneration = generation;
            }
            observer?.Invoke(changedGeneration);
        }

        private void Deliver(Slot slot, byte[] data)
        {
            if (data == null || data.Length == 0) return;
            lock (sync)
            {
                if (!windowOpen || slot.Pending != null) return; // The other phase's duplicate.
                slot.Pending = (byte[])data.Clone();
            }
        }

        // Called only after the entire early archive read. Missing entries are now known to be absent.
        // Non-opt-in registrations retain their previous migration/callback timing in EndLoad.
        internal void PrepareLoad()
        {
            Slot[] snapshot;
            long currentGeneration;
            long missionSessionId;
            VersionedSaveLoadSource source;
            string file;
            lock (sync)
            {
                if (!windowOpen) return;
                snapshot = slots; currentGeneration = generation; source = windowSource; file = windowFile;
                missionSessionId = windowMissionSessionId;
            }
            foreach (Slot slot in snapshot)
            {
                byte[] bytes;
                lock (sync)
                {
                    if (!windowOpen || generation != currentGeneration) return;
                    if (slot.Prepared == null || slot.PreparedOutcome != null) continue;
                    bytes = slot.Pending;
                }
                Outcome outcome = Read(slot, bytes, source, file, missionSessionId);
                // Only explicitly tracked sources allocate/hash origin metadata. Read's
                // legacy/migration callbacks already own copies, so bytes stays original.
                var origin = slot.PreparedSource == null ? null : new VersionedSavePreparedOrigin(
                    slot.PreparedSource, currentGeneration, outcome.Load.Status, source, bytes);
                lock (sync)
                {
                    if (!windowOpen || generation != currentGeneration) return;
                    slot.PreparedOutcome = outcome; slot.PreparedOrigin = origin;
                }
                // Preserve the old independent receiver/Post bytes and callback timing.
                // The exact receiver is bound only for this synchronous callback.
                bool completed = false;
                try
                {
                    var receiver = CopyLoad(outcome.Load, origin);
                    lock (sync)
                        if (windowOpen && generation == currentGeneration && ReferenceEquals(slot.PreparedOrigin, origin))
                            origin?.Activate(receiver);
                    using (new PreparedDispatchScope(origin)) slot.Prepared(receiver);
                    completed = true;
                }
                catch (Exception ex)
                {
                    Fail("Versioned save data prepared callback failed for " + slot.Identifier + ": " + ex);
                    lock (sync)
                        if (generation == currentGeneration) slot.PreparedFailed = true;
                }
                finally
                {
                    if (origin != null)
                        lock (sync)
                        {
                            if (windowOpen && generation == currentGeneration && ReferenceEquals(slot.PreparedOrigin, origin))
                                origin.Finish(completed);
                            else origin.Invalidate();
                        }
                }
            }
        }

        private static VersionedSaveLoad CopyLoad(VersionedSaveLoad load, VersionedSavePreparedOrigin origin = null)
        {
            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in load.Entries) entries.Add(entry.Key, (byte[])entry.Value.Clone());
            return new VersionedSaveLoad(load.Status, load.Source, load.FilePath, load.RecordVersion,
                load.MinimumReaderVersion, entries, load.KeptEntryCount, load.Detail, origin, load.MissionSessionId);
        }

        internal void EndLoad()
        {
            Slot[] snapshot;
            byte[][] pending;
            Outcome[] prepared;
            long loadGeneration;
            long missionSessionId;
            VersionedSaveLoadSource source;
            string file;
            lock (sync)
            {
                if (!windowOpen) return;
                windowOpen = false;
                snapshot = slots;
                pending = new byte[snapshot.Length][];
                prepared = new Outcome[snapshot.Length];
                for (int i = 0; i < snapshot.Length; i++)
                {
                    pending[i] = snapshot[i].Pending; snapshot[i].Pending = null;
                    prepared[i] = snapshot[i].PreparedOutcome;
                }
                loadGeneration = generation; deliveringLoadGeneration = loadGeneration;
                source = windowSource;
                file = windowFile;
                missionSessionId = windowMissionSessionId;
            }
            // Archive completeness is known now. Mark a mixed protected cohort
            // passive before any Loaded callback; full readiness still waits for
            // every callback and the final matching generation publication below.
            string cohortRefusal = ValidateOriginalWriterCohort(loadGeneration, snapshot, pending, prepared);
            if (!string.IsNullOrEmpty(cohortRefusal)) Warn(cohortRefusal);
            for (int i = 0; i < snapshot.Length; i++)
            {
                Slot slot = snapshot[i];
                Outcome outcome = prepared[i] ?? Read(slot, pending[i], source, file, missionSessionId);
                lock (sync)
                {
                    if (generation != loadGeneration) return; // A newer load or unload owns the state now.
                    slot.LoadedBytes = outcome.LoadedBytes;
                    slot.KeepLoaded = outcome.Keep || slot.PreparedFailed;
                    slot.Unknown = outcome.Unknown;
                    slot.Handle.SetKeepsLoaded(slot.KeepLoaded);
                }
                if (outcome.Load.Status != VersionedSaveLoadStatus.Restored &&
                    outcome.Load.Status != VersionedSaveLoadStatus.NoRecord &&
                    outcome.Load.Status != VersionedSaveLoadStatus.OutOfScope)
                    Warn("Versioned save data " + slot.Identifier + ": " + outcome.Load.Status + " (" + outcome.Load.Detail + ")" +
                        (outcome.Keep ? "; later saves keep the loaded bytes unchanged." : "; the next save replaces it."));
                try { using (new PreparedDispatchScope(null)) slot.Loaded(outcome.Load); }
                catch (Exception ex)
                {
                    Fail("Versioned save data load callback failed for " + slot.Identifier + ": " + ex);
                    lock (sync)
                        if (generation == loadGeneration) slot.PreparedOrigin?.Invalidate();
                    if (outcome.Load.Status != VersionedSaveLoadStatus.Restored) continue;
                    lock (sync)
                    {
                        // The consumer did not take the record: never overwrite what it could not accept.
                        if (generation != loadGeneration) return;
                        slot.KeepLoaded = true;
                        slot.Handle.SetKeepsLoaded(true);
                    }
                }
            }
            lock (sync)
            {
                // A nested load/unload may already own a replacement generation.
                // Publish only after every Loaded callback in this pass returned.
                if (generation == loadGeneration && !windowOpen)
                { finalizedLoadGeneration = loadGeneration; deliveringLoadGeneration = -1; }
            }
        }

        private Outcome Read(Slot slot, byte[] data, VersionedSaveLoadSource source, string file, long missionSessionId)
        {
            if (!slot.Accepts(source))
                return Outcome.Empty(new VersionedSaveLoad(VersionedSaveLoadStatus.OutOfScope, source, file, 0, 0, null, 0, string.Empty, null, missionSessionId));
            if (data == null)
                return Outcome.Empty(new VersionedSaveLoad(VersionedSaveLoadStatus.NoRecord, source, file, 0, 0, null, 0, string.Empty, null, missionSessionId));
            VersionedSaveEntries entries;
            int version, minimumReader;
            if (!VersionedSaveRecordCodec.HasSignature(data))
            {
                if (slot.Legacy == null) return Kept(VersionedSaveLoadStatus.Unrecognized, source, file, data, 0, 0, "No legacy reader for unversioned data.", missionSessionId);
                entries = new VersionedSaveEntries();
                bool accepted;
                try { using (new PreparedDispatchScope(null)) accepted = slot.Legacy((byte[])data.Clone(), entries, out version); }
                catch (Exception ex)
                {
                    Fail("Versioned save data legacy reader failed for " + slot.Identifier + ": " + ex);
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, 0, 0, "Legacy reader threw: " + ex.Message, missionSessionId);
                }
                if (!accepted) return Kept(VersionedSaveLoadStatus.Unrecognized, source, file, data, 0, 0, "The legacy reader did not recognize the data.", missionSessionId);
                if (version < 1 || version > slot.Current)
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, version, 0, "The legacy reader returned version " + version + ".", missionSessionId);
                minimumReader = 0;
            }
            else
            {
                VersionedSaveRecordCodec.Decoded decoded = VersionedSaveRecordCodec.Decode(data, slot.Current);
                if (decoded.Status == VersionedSaveRecordCodec.DecodeStatus.TooNew)
                    return Kept(VersionedSaveLoadStatus.TooNew, source, file, data, decoded.RecordVersion, decoded.MinimumReaderVersion, decoded.Detail, missionSessionId);
                if (decoded.Status == VersionedSaveRecordCodec.DecodeStatus.Corrupt)
                    return new Outcome(new VersionedSaveLoad(VersionedSaveLoadStatus.Corrupt, source, file, 0, 0, null, 0, decoded.Detail, null, missionSessionId),
                        data, false, null);
                entries = decoded.Entries;
                version = decoded.RecordVersion;
                minimumReader = decoded.MinimumReaderVersion;
            }
            if (!ValidateWriterCohortRecord(slot, entries, version, minimumReader, out string cohortReason))
                return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, version, minimumReader, cohortReason, missionSessionId);
            for (int from = version; from < slot.Current; from++)
            {
                VersionedSaveMigration step = slot.Migrations[from];
                if (step == null) continue;
                try { using (new PreparedDispatchScope(null)) step(entries); }
                catch (Exception ex)
                {
                    Fail("Versioned save data migration " + from + " -> " + (from + 1) + " failed for " + slot.Identifier + ": " + ex);
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, version, minimumReader,
                        "Migration " + from + " -> " + (from + 1) + " threw: " + ex.Message, missionSessionId);
                }
            }
            var known = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            var unknown = new List<KeyValuePair<string, byte[]>>();
            foreach (KeyValuePair<string, byte[]> entry in entries.Items)
            {
                if (slot.Known.IsKnown(entry.Key)) known.Add(entry.Key, (byte[])entry.Value.Clone());
                else unknown.Add(entry);
            }
            return new Outcome(new VersionedSaveLoad(VersionedSaveLoadStatus.Restored, source, file, version, minimumReader,
                known, unknown.Count, string.Empty, null, missionSessionId), data, false, unknown);
        }

        private static Outcome Kept(VersionedSaveLoadStatus status, VersionedSaveLoadSource source, string file,
            byte[] data, int version, int minimumReader, string detail, long missionSessionId) =>
            new Outcome(new VersionedSaveLoad(status, source, file, version, minimumReader, null, 0, detail, null, missionSessionId), data, true, null);

        private byte[] Save(Slot slot, VersionedSaveContext context)
        {
            try { return SaveCore(slot, context); }
            catch (Exception ex)
            {
                // Defensive: the Extender would log and skip this handler (ModSaveDataAPI.cs:206-209).
                Error("Versioned save data save failed for " + slot.Identifier + ": " + ex);
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.CaptureFailed);
                return null;
            }
        }

        private byte[] SaveCore(Slot slot, VersionedSaveContext context, bool freezing = false)
        {
            // An owned transaction also refuses a wrong-scope attempt, even if that
            // context would normally omit this registration before touching transport.
            if (slot.RequireAtomicWrite && !freezing && atomicSnapshot != null && ReferenceEquals(atomicSnapshot.Owner, this))
                RequireAtomicCurrent(atomicSnapshot, context);
            if (!slot.Writes(context))
            {
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.NotWritten);
                return null;
            }
            if (slot.RequireAtomicWrite && !freezing)
            {
                AtomicSnapshot atomic = atomicSnapshot;
                // Serving a prepared critical bundle invokes no consumer callback.
                // Keep generation, exact bytes and delivery publication under one lock.
                lock (sync)
                {
                    RequireAtomicCurrent(atomic, context);
                    if (!atomic.Records.TryGetValue(slot.Identifier, out byte[] frozenBytes))
                    {
                        atomic.Failed = true;
                        throw new InvalidOperationException("Required record bypassed the protected atomic writer: " + slot.Identifier);
                    }
                    byte[] bytes;
                    if (atomic.CriticalRecords.TryGetValue(slot.Identifier, out var critical))
                    {
                        if (!ReferenceEquals(slot.CriticalCapture, critical))
                        {
                            atomic.Failed = true;
                            throw new InvalidDataException("The atomic record lost its exact critical capture.");
                        }
                        bytes = ServeCriticalRecord(slot, context, critical);
                        if (bytes == null || !SameCohortMarker(bytes, frozenBytes))
                        {
                            atomic.Failed = true; critical.Failed = true;
                            if (string.IsNullOrEmpty(critical.Failure)) critical.Failure = "The atomic record differs from its frozen critical capture.";
                            slot.Handle.SetLastWrite(VersionedSaveWriteStatus.CaptureFailed);
                            return null;
                        }
                    }
                    else
                    {
                        if (slot.CriticalCapture != null || slot.CriticalSource?.WriterCohortJoined == true)
                        {
                            atomic.Failed = true;
                            throw new InvalidOperationException("An enrolled atomic record lost its critical capture.");
                        }
                        bytes = (byte[])frozenBytes.Clone();
                        slot.Handle.SetLastWrite(VersionedSaveWriteStatus.Written);
                    }
                    atomic.Delivered.Add(slot.Identifier);
                    return bytes;
                }
            }
            byte[] loaded;
            VersionedSaveCriticalCapture frozen;
            bool trackedCapture;
            bool keep;
            List<KeyValuePair<string, byte[]>> unknown;
            lock (sync)
            {
                sourceSaveAttempted = true;
                // Saving inside a load window is not a game flow; never write a half-loaded state.
                loaded = windowOpen ? slot.Pending : slot.LoadedBytes;
                keep = windowOpen || slot.KeepLoaded;
                unknown = slot.Unknown;
                frozen = slot.CriticalCapture;
                trackedCapture = frozen == null && !keep && slot.CriticalSource != null && !slot.CriticalSource.WriterCohortJoined;
                if (trackedCapture) slot.ActiveCriticalSourceCaptures++;
            }
            if (frozen != null) return ServeCriticalRecord(slot, context, frozen, !freezing);
            if (!keep && slot.CriticalSource?.WriterCohortJoined == true)
            {
                // Never downgrade a joined source through an ordinary callback.
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.CaptureFailed);
                return null;
            }
            if (keep)
            {
                slot.Handle.SetLastWrite(loaded != null ? VersionedSaveWriteStatus.KeptLoadedRecord : VersionedSaveWriteStatus.NotWritten);
                return loaded == null ? null : (byte[])loaded.Clone();
            }
            try
            {
                byte[] result = CaptureRecord(slot, context, unknown, slot.CriticalSource?.ReservedEntry, null, 0, out VersionedSaveWriteStatus status);
                slot.Handle.SetLastWrite(status);
                return result;
            }
            finally
            {
                if (trackedCapture) lock (sync) slot.ActiveCriticalSourceCaptures--;
            }
        }

        // One encoder/callback path serves ordinary saves and explicit preparation.
        // Status is returned to the caller: preparing frozen bytes cannot mark Written.
        private byte[] CaptureRecord(Slot slot, VersionedSaveContext context,
            List<KeyValuePair<string, byte[]>> unknown, string reservedEntry, byte[] reservedMarker,
            int cutoverMinimumReader, out VersionedSaveWriteStatus status)
        {
            status = VersionedSaveWriteStatus.CaptureFailed;
            var writer = new VersionedSaveWriter(slot.Known, context.IsSaveFile, context.IsMapEditorSave, context.FilePath,
                slot.Current, slot.MinimumReader, reservedEntry);
            bool write;
            try { using (new PreparedDispatchScope(null)) write = slot.Capture(writer); }
            catch (Exception ex)
            {
                Fail("Versioned save data capture failed for " + slot.Identifier + ": " + ex);
                status = VersionedSaveWriteStatus.CaptureFailed;
                return null;
            }
            finally { writer.Close(); }
            if (!write)
            {
                status = VersionedSaveWriteStatus.NotWritten;
                return null;
            }
            if (writer.RecordVersion < slot.Current)
            {
                bool hasUnknown = unknown != null && unknown.Count != 0;
                lock (sync) hasUnknown |= slot.Unknown != null && slot.Unknown.Count != 0;
                // Migrations run before unknown entries are partitioned. Relabeling them as an older
                // schema could make a later reader migrate them twice. Include a load completed by
                // a reentrant capture as well as the unknown snapshot this save would merge.
                if (hasUnknown)
                {
                    Fail("Versioned save data capture cannot select an older schema while retaining unknown entries for " + slot.Identifier + ".");
                    status = VersionedSaveWriteStatus.CaptureFailed;
                    return null;
                }
            }
            if (cutoverMinimumReader > writer.RecordVersion)
            {
                Fail("An enrolled consumer selected a record version below its cutover reader for " + slot.Identifier + ".");
                status = VersionedSaveWriteStatus.CaptureFailed;
                return null;
            }
            SortedDictionary<string, byte[]> record = writer.Written;
            if (reservedMarker != null) record.Add(reservedEntry, (byte[])reservedMarker.Clone());
            if (unknown != null)
                foreach (KeyValuePair<string, byte[]> entry in unknown) record[entry.Key] = entry.Value; // Never a known name.
            byte[] bytes = VersionedSaveRecordCodec.Encode(writer.RecordVersion, Math.Max(writer.MinimumReaderVersion, cutoverMinimumReader), record);
            if (bytes == null)
            {
                Error("Versioned save data " + slot.Identifier + " exceeds " + VersionedSaveRecordCodec.MaximumEntries +
                    " entries or " + VersionedSaveRecordCodec.MaximumRecordBytes + " bytes with " + record.Count + " entries; not written.");
                status = VersionedSaveWriteStatus.TooLarge;
                return null;
            }
            status = VersionedSaveWriteStatus.Written;
            return bytes;
        }

        private void Warn(string message)
        {
            try { warning?.Invoke(message); } catch { }
        }

        private void Fail(string message)
        {
            Interlocked.Increment(ref failures);
            Error(message);
        }

        private void Error(string message)
        {
            try { error?.Invoke(message); } catch { }
        }

        private sealed class Outcome
        {
            internal Outcome(VersionedSaveLoad load, byte[] loadedBytes, bool keep, List<KeyValuePair<string, byte[]>> unknown)
            { Load = load; LoadedBytes = loadedBytes; Keep = keep; Unknown = unknown; }
            internal static Outcome Empty(VersionedSaveLoad load) => new Outcome(load, null, false, null);
            internal VersionedSaveLoad Load { get; }
            internal byte[] LoadedBytes { get; }
            internal bool Keep { get; }
            internal List<KeyValuePair<string, byte[]>> Unknown { get; }
        }

        private sealed class Slot
        {
            internal Slot(VersionedSaveDataRegistration registration, VersionedSaveKnownEntries known, VersionedSaveMigration[] migrations)
            {
                Owner = registration.OwnerGuid;
                Identifier = registration.SaveDataIdentifier;
                Current = registration.CurrentVersion;
                MinimumReader = registration.MinimumReaderVersion;
                Known = known;
                Migrations = migrations;
                Capture = registration.Capture;
                Loaded = registration.Loaded;
                Prepared = registration.Prepared;
                Scope = registration.Scope;
                Legacy = registration.LegacyReader;
                SimulationRelevant = registration.SimulationRelevant;
                RequireAtomicWrite = registration.RequireAtomicWrite;
                Handle = new VersionedSaveDataHandle(Owner, Identifier);
            }

            internal readonly string Owner, Identifier;
            internal readonly int Current, MinimumReader;
            internal readonly VersionedSaveKnownEntries Known;
            internal readonly VersionedSaveMigration[] Migrations;
            internal readonly VersionedSaveCapture Capture;
            internal readonly Action<VersionedSaveLoad> Loaded;
            internal readonly Action<VersionedSaveLoad> Prepared;
            internal readonly VersionedSaveScope Scope;
            internal readonly VersionedSaveLegacyReader Legacy;
            internal readonly bool SimulationRelevant, RequireAtomicWrite;
            internal readonly VersionedSaveDataHandle Handle;
            // The save data identifier is the registration ID.
            internal static readonly Func<Slot, RegistrationKey> Key = slot => new RegistrationKey(slot.Owner, slot.Identifier);

            // Guarded by the registry lock.
            internal byte[] Pending;
            internal Outcome PreparedOutcome;
            internal bool PreparedFailed;
            internal VersionedSavePreparedSource PreparedSource;
            internal VersionedSavePreparedOrigin PreparedOrigin;
            internal VersionedSaveCriticalCreatedWorldOrigin CreatedWorldOrigin;
            internal VersionedSaveCriticalSource CriticalSource;
            internal VersionedSaveCriticalCapture CriticalCapture;
            internal int ActiveCriticalSourceCaptures;
            internal byte[] LoadedBytes;
            internal bool KeepLoaded;
            internal List<KeyValuePair<string, byte[]>> Unknown;

            internal void ResetMission()
            {
                PreparedOrigin?.Invalidate(); PreparedOrigin = null; CreatedWorldOrigin = null;
                if (CriticalCapture != null)
                {
                    CriticalCapture.Failed = true;
                    if (string.IsNullOrEmpty(CriticalCapture.Failure)) CriticalCapture.Failure = "A load or unload invalidated the frozen source generation.";
                }
                PreparedOutcome = null;
                PreparedFailed = false;
                LoadedBytes = null;
                KeepLoaded = false;
                Unknown = null;
                Handle.SetKeepsLoaded(false);
            }

            internal bool Accepts(VersionedSaveLoadSource source) => Scope == VersionedSaveScope.SaveGamesAndMaps ||
                source == VersionedSaveLoadSource.SaveGame || source == VersionedSaveLoadSource.MultiplayerSave;

            internal bool Writes(VersionedSaveContext context) => Scope == VersionedSaveScope.SaveGamesAndMaps ||
                context.IsSaveFile && !context.IsMapEditorSave;
        }
    }
}

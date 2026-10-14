using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace APIShared.Savegames
{
    /// <summary>Save context reported by the transport, free of Script Extender types.</summary>
    internal readonly struct VersionedSaveContext
    {
        internal VersionedSaveContext(bool isSaveFile, bool isMapEditorSave, string filePath)
        { IsSaveFile = isSaveFile; IsMapEditorSave = isMapEditorSave; FilePath = filePath; }
        internal bool IsSaveFile { get; }
        internal bool IsMapEditorSave { get; }
        internal string FilePath { get; }
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
    internal sealed class VersionedSaveDataRegistry
    {
        internal delegate bool Transport(string identifier, Func<VersionedSaveContext, byte[]> save, Action<byte[]> load);

        private readonly object sync = new object();
        private readonly Transport transport;
        private readonly Action<string> warning;
        private readonly Action<string> error;
        private Slot[] slots = new Slot[0];
        private bool windowOpen;
        private long generation;
        private VersionedSaveLoadSource windowSource;
        private string windowFile;
        private long failures;

        internal VersionedSaveDataRegistry(Transport transport, Action<string> warning, Action<string> error)
        {
            this.transport = transport;
            this.warning = warning;
            this.error = error;
        }

        internal long CallbackFailures => Interlocked.Read(ref failures);

        internal bool TryRegister(VersionedSaveDataRegistration registration, out VersionedSaveDataHandle handle, out string reason)
        {
            handle = null;
            if (!TryCreateSlot(registration, out Slot slot, out reason)) return false;
            lock (sync)
            {
                foreach (Slot existing in slots)
                    if (string.Equals(existing.Identifier, slot.Identifier, StringComparison.OrdinalIgnoreCase))
                    { reason = "The save data identifier is already registered."; return false; }
                bool accepted;
                try { accepted = transport(slot.Identifier, context => Save(slot, context), data => Deliver(slot, data)); }
                catch (Exception ex) { reason = "Script Extender registration failed: " + ex.Message; return false; }
                if (!accepted)
                { reason = "Script Extender rejected the identifier (already registered by another handler, or invalid)."; return false; }
                var next = new Slot[slots.Length + 1];
                Array.Copy(slots, next, slots.Length);
                next[next.Length - 1] = slot;
                Array.Sort(next, (a, b) =>
                {
                    int comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Identifier, b.Identifier);
                });
                slots = next;
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
        {
            lock (sync)
            {
                windowOpen = true;
                generation++;
                windowSource = source;
                windowFile = filePath;
                foreach (Slot slot in slots) { slot.Pending = null; slot.ResetMission(); }
            }
        }

        internal void Unload()
        {
            lock (sync)
            {
                if (windowOpen) return; // The game unloads the previous map while loading the next one.
                generation++;
                foreach (Slot slot in slots) slot.ResetMission();
            }
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

        internal void EndLoad()
        {
            Slot[] snapshot;
            byte[][] pending;
            long loadGeneration;
            VersionedSaveLoadSource source;
            string file;
            lock (sync)
            {
                if (!windowOpen) return;
                windowOpen = false;
                snapshot = slots;
                pending = new byte[snapshot.Length][];
                for (int i = 0; i < snapshot.Length; i++) { pending[i] = snapshot[i].Pending; snapshot[i].Pending = null; }
                loadGeneration = generation;
                source = windowSource;
                file = windowFile;
            }
            for (int i = 0; i < snapshot.Length; i++)
            {
                Slot slot = snapshot[i];
                Outcome outcome = Read(slot, pending[i], source, file);
                lock (sync)
                {
                    if (generation != loadGeneration) return; // A newer load or unload owns the state now.
                    slot.LoadedBytes = outcome.LoadedBytes;
                    slot.KeepLoaded = outcome.Keep;
                    slot.Unknown = outcome.Unknown;
                    slot.Handle.SetKeepsLoaded(outcome.Keep);
                }
                if (outcome.Load.Status != VersionedSaveLoadStatus.Restored &&
                    outcome.Load.Status != VersionedSaveLoadStatus.NoRecord &&
                    outcome.Load.Status != VersionedSaveLoadStatus.OutOfScope)
                    Warn("Versioned save data " + slot.Identifier + ": " + outcome.Load.Status + " (" + outcome.Load.Detail + ")" +
                        (outcome.Keep ? "; later saves keep the loaded bytes unchanged." : "; the next save replaces it."));
                try { slot.Loaded(outcome.Load); }
                catch (Exception ex)
                {
                    Fail("Versioned save data load callback failed for " + slot.Identifier + ": " + ex);
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
        }

        private Outcome Read(Slot slot, byte[] data, VersionedSaveLoadSource source, string file)
        {
            if (!slot.Accepts(source))
                return Outcome.Empty(new VersionedSaveLoad(VersionedSaveLoadStatus.OutOfScope, source, file, 0, 0, null, 0, string.Empty));
            if (data == null)
                return Outcome.Empty(new VersionedSaveLoad(VersionedSaveLoadStatus.NoRecord, source, file, 0, 0, null, 0, string.Empty));
            VersionedSaveEntries entries;
            int version, minimumReader;
            if (!VersionedSaveRecordCodec.HasSignature(data))
            {
                if (slot.Legacy == null) return Kept(VersionedSaveLoadStatus.Unrecognized, source, file, data, 0, 0, "No legacy reader for unversioned data.");
                entries = new VersionedSaveEntries();
                bool accepted;
                try { accepted = slot.Legacy((byte[])data.Clone(), entries, out version); }
                catch (Exception ex)
                {
                    Fail("Versioned save data legacy reader failed for " + slot.Identifier + ": " + ex);
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, 0, 0, "Legacy reader threw: " + ex.Message);
                }
                if (!accepted) return Kept(VersionedSaveLoadStatus.Unrecognized, source, file, data, 0, 0, "The legacy reader did not recognize the data.");
                if (version < 1 || version > slot.Current)
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, version, 0, "The legacy reader returned version " + version + ".");
                minimumReader = 0;
            }
            else
            {
                VersionedSaveRecordCodec.Decoded decoded = VersionedSaveRecordCodec.Decode(data, slot.Current);
                if (decoded.Status == VersionedSaveRecordCodec.DecodeStatus.TooNew)
                    return Kept(VersionedSaveLoadStatus.TooNew, source, file, data, decoded.RecordVersion, decoded.MinimumReaderVersion, decoded.Detail);
                if (decoded.Status == VersionedSaveRecordCodec.DecodeStatus.Corrupt)
                    return new Outcome(new VersionedSaveLoad(VersionedSaveLoadStatus.Corrupt, source, file, 0, 0, null, 0, decoded.Detail),
                        data, false, null);
                entries = decoded.Entries;
                version = decoded.RecordVersion;
                minimumReader = decoded.MinimumReaderVersion;
            }
            for (int from = version; from < slot.Current; from++)
            {
                VersionedSaveMigration step = slot.Migrations[from];
                if (step == null) continue;
                try { step(entries); }
                catch (Exception ex)
                {
                    Fail("Versioned save data migration " + from + " -> " + (from + 1) + " failed for " + slot.Identifier + ": " + ex);
                    return Kept(VersionedSaveLoadStatus.MigrationFailed, source, file, data, version, minimumReader,
                        "Migration " + from + " -> " + (from + 1) + " threw: " + ex.Message);
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
                known, unknown.Count, string.Empty), data, false, unknown);
        }

        private static Outcome Kept(VersionedSaveLoadStatus status, VersionedSaveLoadSource source, string file,
            byte[] data, int version, int minimumReader, string detail) =>
            new Outcome(new VersionedSaveLoad(status, source, file, version, minimumReader, null, 0, detail), data, true, null);

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

        private byte[] SaveCore(Slot slot, VersionedSaveContext context)
        {
            if (!slot.Writes(context))
            {
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.NotWritten);
                return null;
            }
            byte[] loaded;
            bool keep;
            List<KeyValuePair<string, byte[]>> unknown;
            lock (sync)
            {
                // Saving inside a load window is not a game flow; never write a half-loaded state.
                loaded = windowOpen ? slot.Pending : slot.LoadedBytes;
                keep = windowOpen || slot.KeepLoaded;
                unknown = slot.Unknown;
            }
            if (keep)
            {
                slot.Handle.SetLastWrite(loaded != null ? VersionedSaveWriteStatus.KeptLoadedRecord : VersionedSaveWriteStatus.NotWritten);
                return loaded == null ? null : (byte[])loaded.Clone();
            }
            var writer = new VersionedSaveWriter(slot.Known, context.IsSaveFile, context.IsMapEditorSave, context.FilePath);
            bool write;
            try { write = slot.Capture(writer); }
            catch (Exception ex)
            {
                Fail("Versioned save data capture failed for " + slot.Identifier + ": " + ex);
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.CaptureFailed);
                return null;
            }
            finally { writer.Close(); }
            if (!write)
            {
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.NotWritten);
                return null;
            }
            SortedDictionary<string, byte[]> record = writer.Written;
            if (unknown != null)
                foreach (KeyValuePair<string, byte[]> entry in unknown) record[entry.Key] = entry.Value; // Never a known name.
            byte[] bytes = VersionedSaveRecordCodec.Encode(slot.Current, slot.MinimumReader, record);
            if (bytes == null)
            {
                Error("Versioned save data " + slot.Identifier + " exceeds " + VersionedSaveRecordCodec.MaximumEntries +
                    " entries or " + VersionedSaveRecordCodec.MaximumRecordBytes + " bytes with " + record.Count + " entries; not written.");
                slot.Handle.SetLastWrite(VersionedSaveWriteStatus.TooLarge);
                return null;
            }
            slot.Handle.SetLastWrite(VersionedSaveWriteStatus.Written);
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
                Scope = registration.Scope;
                Legacy = registration.LegacyReader;
                Handle = new VersionedSaveDataHandle(Owner, Identifier);
            }

            internal readonly string Owner, Identifier;
            internal readonly int Current, MinimumReader;
            internal readonly VersionedSaveKnownEntries Known;
            internal readonly VersionedSaveMigration[] Migrations;
            internal readonly VersionedSaveCapture Capture;
            internal readonly Action<VersionedSaveLoad> Loaded;
            internal readonly VersionedSaveScope Scope;
            internal readonly VersionedSaveLegacyReader Legacy;
            internal readonly VersionedSaveDataHandle Handle;

            // Guarded by the registry lock.
            internal byte[] Pending;
            internal byte[] LoadedBytes;
            internal bool KeepLoaded;
            internal List<KeyValuePair<string, byte[]>> Unknown;

            internal void ResetMission()
            {
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

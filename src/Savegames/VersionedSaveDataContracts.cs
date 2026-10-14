using System;
using System.Collections.Generic;

namespace APIShared.Savegames
{
    /// <summary>Which files a versioned record is read from and written into.</summary>
    public enum VersionedSaveScope
    {
        /// <summary>Gameplay saves only. Writes when Script Extender reports a save file that is not a
        /// map editor save; reads from <see cref="VersionedSaveLoadSource.SaveGame"/> and
        /// <see cref="VersionedSaveLoadSource.MultiplayerSave"/> loads. Other loads report
        /// <see cref="VersionedSaveLoadStatus.OutOfScope"/>.</summary>
        SaveGames = 0,
        /// <summary>Gameplay saves, map editor saves and every map load, for data that belongs to the
        /// map itself (for example a map theme chosen in the editor).</summary>
        SaveGamesAndMaps = 1,
    }

    /// <summary>The Script Extender archive load that ended. Values describe the Extender event, not a
    /// verified game menu path; see the save data guide for the load paths still awaiting in-game proof.</summary>
    public enum VersionedSaveLoadSource
    {
        /// <summary>Extender OnLoadSave without its editor-map flag: a save game.</summary>
        SaveGame = 0,
        /// <summary>Extender OnLoadMap during APIShared's confirmed multiplayer save load, or with the
        /// native multiplayer-save flag set.</summary>
        MultiplayerSave = 1,
        /// <summary>Any other Extender OnLoadMap: a map file loaded to be played.</summary>
        MapToPlay = 2,
        /// <summary>Extender OnLoadSave with its editor-map flag: a map opened in the map editor.</summary>
        EditorMap = 3,
    }

    /// <summary>Outcome of one archive load for one registration, reported once at the end of that load.</summary>
    public enum VersionedSaveLoadStatus
    {
        /// <summary>The load carried no record for this registration. Start from a fresh state.</summary>
        NoRecord = 0,
        /// <summary>The record was read and migrated to the current version. Known entries are delivered;
        /// unknown entries are kept and written back unchanged by later saves of the same mission.</summary>
        Restored = 1,
        /// <summary>The loaded file is outside the registration's scope. A record in it is ignored; later
        /// gameplay saves of the mission capture as usual, and map editor saves are not written.</summary>
        OutOfScope = 2,
        /// <summary>The record or its envelope requires a newer reader. Nothing is delivered; later saves
        /// of this mission write the loaded bytes back unchanged.</summary>
        TooNew = 3,
        /// <summary>The data carries the record signature but is damaged (checksum, structure or limits).
        /// Nothing is delivered; the next save replaces it with the current state.</summary>
        Corrupt = 4,
        /// <summary>The data is not a versioned record and no legacy reader accepted it. Nothing is
        /// delivered; later saves of this mission write it back unchanged.</summary>
        Unrecognized = 5,
        /// <summary>The legacy reader or a migration step threw, or the legacy reader returned an invalid
        /// version. Nothing is delivered; later saves of this mission write the loaded bytes back unchanged.</summary>
        MigrationFailed = 6,
    }

    /// <summary>Result of the most recent in-scope save request for one registration.</summary>
    public enum VersionedSaveWriteStatus
    {
        /// <summary>No save has reached this registration since it was registered.</summary>
        None = 0,
        /// <summary>The captured known entries and the kept unknown entries were written.</summary>
        Written = 1,
        /// <summary>The loaded bytes were written back unchanged, because the load reported TooNew,
        /// Unrecognized or MigrationFailed, or the load callback threw for a restored record.</summary>
        KeptLoadedRecord = 2,
        /// <summary>Nothing was returned to Script Extender: the save was out of scope or capture returned
        /// false. The Extender keeps the entry its archive already holds, if any.</summary>
        NotWritten = 3,
        /// <summary>Capture threw or wrote an invalid entry. Nothing was returned; the Extender keeps the
        /// entry its archive already holds, if any.</summary>
        CaptureFailed = 4,
        /// <summary>The record exceeded an entry-count or byte limit. Nothing was returned; the Extender
        /// keeps the entry its archive already holds, if any.</summary>
        TooLarge = 5,
    }

    /// <summary>Writes the current state as named entries. Runs synchronously on the Extender's save
    /// thread for each in-scope save; return false to write nothing for this save.</summary>
    /// <param name="writer">Writer valid only until this callback returns.</param>
    /// <returns>True to write the record; false to leave the Extender's existing entry unchanged.</returns>
    public delegate bool VersionedSaveCapture(VersionedSaveWriter writer);

    /// <summary>Transforms the entries of a record from the step's source version to the next version.
    /// Throw to fail the load with <see cref="VersionedSaveLoadStatus.MigrationFailed"/>.</summary>
    /// <param name="entries">Mutable entries of the record being migrated.</param>
    public delegate void VersionedSaveMigration(VersionedSaveEntries entries);

    /// <summary>Converts data that a consumer stored under the same Extender identifier before it used
    /// versioned records. Return false when the data is not in the legacy format.</summary>
    /// <param name="data">A private copy of the stored bytes.</param>
    /// <param name="entries">Empty entry set to fill.</param>
    /// <param name="recordVersion">Record version the converted entries correspond to, from 1 to the
    /// registration's current version; the remaining migration steps run from there.</param>
    /// <returns>True when the data was recognized and converted.</returns>
    public delegate bool VersionedSaveLegacyReader(byte[] data, VersionedSaveEntries entries, out int recordVersion);

    /// <summary>Mutable named entries used by legacy readers, migration steps and consumer tests.</summary>
    /// <remarks>Names are case-sensitive and compared ordinally. Every operation enforces the limits in
    /// <see cref="VersionedSaveData"/>. Not thread-safe; an instance belongs to one callback.</remarks>
    public sealed class VersionedSaveEntries
    {
        private readonly SortedDictionary<string, byte[]> entries =
            new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Creates an empty entry set, for example to unit test a migration step.</summary>
        public VersionedSaveEntries() { }

        /// <summary>Number of entries.</summary>
        public int Count => entries.Count;

        /// <summary>Snapshot of the entry names in ordinal order.</summary>
        public IReadOnlyList<string> Names => new List<string>(entries.Keys);

        /// <summary>Whether an entry with this exact name exists.</summary>
        public bool Contains(string name) => name != null && entries.ContainsKey(name);

        /// <summary>Gets an entry's stored bytes. Do not modify the returned array; use <see cref="Set"/>.</summary>
        public bool TryGet(string name, out byte[] data)
        {
            if (name != null && entries.TryGetValue(name, out data)) return true;
            data = null;
            return false;
        }

        /// <summary>Adds or replaces an entry with a copy of <paramref name="data"/>.</summary>
        /// <exception cref="ArgumentNullException">The data is null.</exception>
        /// <exception cref="ArgumentException">The name is invalid or the data exceeds
        /// <see cref="VersionedSaveData.MaximumEntryBytes"/>.</exception>
        /// <exception cref="InvalidOperationException">A new entry would exceed
        /// <see cref="VersionedSaveData.MaximumEntries"/>.</exception>
        public void Set(string name, byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            SetOwned(name, (byte[])data.Clone());
        }

        /// <summary>Removes an entry. Returns false when it does not exist.</summary>
        public bool Remove(string name) => name != null && entries.Remove(name);

        internal void SetOwned(string name, byte[] data)
        {
            if (!VersionedSaveRecordCodec.IsValidName(name))
                throw new ArgumentException("Entry names need 1 to " + VersionedSaveRecordCodec.MaximumNameLength +
                    " characters, not only white space, and no unpaired surrogates.", nameof(name));
            if (data.Length > VersionedSaveRecordCodec.MaximumEntryBytes)
                throw new ArgumentException("Entry data exceeds " + VersionedSaveRecordCodec.MaximumEntryBytes + " bytes.", nameof(data));
            if (!entries.ContainsKey(name) && entries.Count >= VersionedSaveRecordCodec.MaximumEntries)
                throw new InvalidOperationException("A record holds at most " + VersionedSaveRecordCodec.MaximumEntries + " entries.");
            entries[name] = data;
        }

        internal IEnumerable<KeyValuePair<string, byte[]>> Items => entries;
    }

    /// <summary>Collects the known entries of one save. Valid only while the capture callback runs.</summary>
    public sealed class VersionedSaveWriter
    {
        private readonly VersionedSaveKnownEntries known;
        private readonly SortedDictionary<string, byte[]> written =
            new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        private bool closed;

        internal VersionedSaveWriter(VersionedSaveKnownEntries known, bool isSaveFile, bool isMapEditorSave, string filePath)
        {
            this.known = known;
            IsSaveFile = isSaveFile;
            IsMapEditorSave = isMapEditorSave;
            FilePath = filePath;
        }

        /// <summary>The Extender's save-file flag for this save (false for a map file).</summary>
        public bool IsSaveFile { get; }
        /// <summary>Whether this save comes from the map editor.</summary>
        public bool IsMapEditorSave { get; }
        /// <summary>Destination path reported by the Extender; may be null.</summary>
        public string FilePath { get; }
        /// <summary>Number of entries written so far.</summary>
        public int Count => written.Count;

        /// <summary>Writes one known entry. The data is copied. Writing nothing produces an empty record,
        /// which replaces older data in the save.</summary>
        /// <exception cref="ArgumentNullException">The data is null.</exception>
        /// <exception cref="ArgumentException">The name is invalid, not covered by the registration's known
        /// entries, already written, or the data exceeds <see cref="VersionedSaveData.MaximumEntryBytes"/>.</exception>
        /// <exception cref="InvalidOperationException">The capture callback has returned, or a new entry
        /// would exceed <see cref="VersionedSaveData.MaximumEntries"/>.</exception>
        public void Write(string name, byte[] data)
        {
            if (closed) throw new InvalidOperationException("The save writer is only valid during capture.");
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (!VersionedSaveRecordCodec.IsValidName(name))
                throw new ArgumentException("Invalid entry name.", nameof(name));
            if (!known.IsKnown(name))
                throw new ArgumentException("Entry '" + name + "' is not one of the registration's known entries.", nameof(name));
            if (written.ContainsKey(name))
                throw new ArgumentException("Entry '" + name + "' was already written.", nameof(name));
            if (data.Length > VersionedSaveRecordCodec.MaximumEntryBytes)
                throw new ArgumentException("Entry data exceeds " + VersionedSaveRecordCodec.MaximumEntryBytes + " bytes.", nameof(data));
            if (written.Count >= VersionedSaveRecordCodec.MaximumEntries)
                throw new InvalidOperationException("A record holds at most " + VersionedSaveRecordCodec.MaximumEntries + " entries.");
            written.Add(name, (byte[])data.Clone());
        }

        internal void Close() => closed = true;
        internal SortedDictionary<string, byte[]> Written => written;
    }

    /// <summary>Immutable result of one archive load for one registration.</summary>
    public sealed class VersionedSaveLoad
    {
        private static readonly IReadOnlyDictionary<string, byte[]> NoEntries =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        internal VersionedSaveLoad(VersionedSaveLoadStatus status, VersionedSaveLoadSource source, string filePath,
            int recordVersion, int minimumReaderVersion, IReadOnlyDictionary<string, byte[]> entries,
            int keptEntryCount, string detail)
        {
            Status = status; Source = source; FilePath = filePath;
            RecordVersion = recordVersion; MinimumReaderVersion = minimumReaderVersion;
            Entries = entries ?? NoEntries; KeptEntryCount = keptEntryCount; Detail = detail ?? string.Empty;
        }

        /// <summary>What happened to this registration's record in this load.</summary>
        public VersionedSaveLoadStatus Status { get; }
        /// <summary>The Extender load that ended.</summary>
        public VersionedSaveLoadSource Source { get; }
        /// <summary>File name reported by the Extender load event; may be null.</summary>
        public string FilePath { get; }
        /// <summary>Version the record was written at (or the legacy reader's version), before migration;
        /// 0 when no record header was read.</summary>
        public int RecordVersion { get; }
        /// <summary>Oldest reader version the writer allowed; 0 for legacy data or no readable header.</summary>
        public int MinimumReaderVersion { get; }
        /// <summary>Known entries after migration, keyed ordinally. Empty unless <see cref="Status"/> is
        /// Restored. The arrays are private copies owned by the receiver.</summary>
        public IReadOnlyDictionary<string, byte[]> Entries { get; }
        /// <summary>Unknown entries kept for write-back by later saves of this mission.</summary>
        public int KeptEntryCount { get; }
        /// <summary>Diagnostic description of a failure; empty on success. Not a stable contract.</summary>
        public string Detail { get; }
    }

    /// <summary>Describes one per-mod versioned record. <see cref="VersionedSaveData.TryRegister"/> validates
    /// it and takes a snapshot; later changes to this object have no effect.</summary>
    public sealed class VersionedSaveDataRegistration
    {
        private readonly Dictionary<int, VersionedSaveMigration> migrations = new Dictionary<int, VersionedSaveMigration>();
        private readonly List<string> invalidMigrations = new List<string>();

        /// <summary>Creates a registration.</summary>
        /// <param name="ownerGuid">The registering plugin's GUID, used for ordering and diagnostics.</param>
        /// <param name="saveDataIdentifier">Script Extender ModSaveDataAPI identifier: unique in the process
        /// and file-name safe. Reuse an identifier that already holds older data only with a
        /// <see cref="LegacyReader"/>.</param>
        /// <param name="currentVersion">Record version this build writes, at least 1.</param>
        /// <param name="minimumReaderVersion">Oldest record version whose reader can read what this build
        /// writes, from 1 to <paramref name="currentVersion"/>. Keep it when a version only adds entries;
        /// raise it when an existing entry's meaning or encoding changes.</param>
        /// <param name="knownEntries">Entry names this build owns. An item ending in '/' owns every name
        /// that starts with it. Entries outside this set are kept unchanged across saves.</param>
        /// <param name="capture">Writes the current state at each in-scope save.</param>
        /// <param name="loaded">Receives the outcome once at the end of every archive load.</param>
        public VersionedSaveDataRegistration(string ownerGuid, string saveDataIdentifier, int currentVersion,
            int minimumReaderVersion, IEnumerable<string> knownEntries, VersionedSaveCapture capture,
            Action<VersionedSaveLoad> loaded)
        {
            OwnerGuid = ownerGuid;
            SaveDataIdentifier = saveDataIdentifier;
            CurrentVersion = currentVersion;
            MinimumReaderVersion = minimumReaderVersion;
            KnownEntries = knownEntries == null ? null : new List<string>(knownEntries);
            Capture = capture;
            Loaded = loaded;
        }

        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Script Extender ModSaveDataAPI identifier.</summary>
        public string SaveDataIdentifier { get; }
        /// <summary>Record version this build writes.</summary>
        public int CurrentVersion { get; }
        /// <summary>Oldest record version whose reader can read what this build writes.</summary>
        public int MinimumReaderVersion { get; }
        /// <summary>Known entry names and '/'-terminated prefixes.</summary>
        public IReadOnlyList<string> KnownEntries { get; }
        /// <summary>Capture callback.</summary>
        public VersionedSaveCapture Capture { get; }
        /// <summary>Load callback.</summary>
        public Action<VersionedSaveLoad> Loaded { get; }
        /// <summary>Files read and written; <see cref="VersionedSaveScope.SaveGames"/> by default.</summary>
        public VersionedSaveScope Scope { get; set; } = VersionedSaveScope.SaveGames;
        /// <summary>Optional converter for pre-existing data stored under the same identifier.</summary>
        public VersionedSaveLegacyReader LegacyReader { get; set; }

        /// <summary>Adds the step that turns a <paramref name="fromVersion"/> record into a
        /// <paramref name="fromVersion"/> + 1 record. Steps run in ascending order up to the current
        /// version; a version without a step needs no change. Invalid steps fail registration.</summary>
        public void AddMigration(int fromVersion, VersionedSaveMigration migration)
        {
            if (migration == null || migrations.ContainsKey(fromVersion))
                invalidMigrations.Add(migration == null ? "Migration step " + fromVersion + " is null." :
                    "Migration step " + fromVersion + " was added twice.");
            else migrations.Add(fromVersion, migration);
        }

        internal IReadOnlyDictionary<int, VersionedSaveMigration> Migrations => migrations;
        internal IReadOnlyList<string> InvalidMigrations => invalidMigrations;
    }

    /// <summary>Process-lived diagnostics of one registration. There is no unregistration.</summary>
    public sealed class VersionedSaveDataHandle
    {
        private int lastWrite;
        private int keepsLoaded;

        internal VersionedSaveDataHandle(string ownerGuid, string saveDataIdentifier)
        {
            OwnerGuid = ownerGuid;
            SaveDataIdentifier = saveDataIdentifier;
        }

        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Script Extender ModSaveDataAPI identifier.</summary>
        public string SaveDataIdentifier { get; }
        /// <summary>Result of the latest in-scope save request.</summary>
        public VersionedSaveWriteStatus LastWriteStatus => (VersionedSaveWriteStatus)System.Threading.Volatile.Read(ref lastWrite);
        /// <summary>Whether saves of the current mission write the loaded bytes back instead of capturing.</summary>
        public bool KeepsLoadedRecord => System.Threading.Volatile.Read(ref keepsLoaded) != 0;

        internal void SetLastWrite(VersionedSaveWriteStatus status) => System.Threading.Volatile.Write(ref lastWrite, (int)status);
        internal void SetKeepsLoaded(bool value) => System.Threading.Volatile.Write(ref keepsLoaded, value ? 1 : 0);
    }

    /// <summary>Versioned per-mod save records on top of Script Extender ModSaveDataAPI: an explicit
    /// record version, consumer-owned migration steps, unknown entries kept across saves, and documented
    /// results for missing, damaged or too-new data. See docs/SAVE_DATA.md for the migration policy.</summary>
    public static partial class VersionedSaveData
    {
        /// <summary>Most entries in one record, on write and on read. Equals the per-mod limit of the
        /// existing savegame settings reader (SavegameModSettingsRecordFormatter); issue #10 governs
        /// raising it.</summary>
        public static int MaximumEntries => VersionedSaveRecordCodec.MaximumEntries;
        /// <summary>Most data bytes in one entry.</summary>
        public static int MaximumEntryBytes => VersionedSaveRecordCodec.MaximumEntryBytes;
        /// <summary>Most bytes in one encoded record.</summary>
        public static int MaximumRecordBytes => VersionedSaveRecordCodec.MaximumRecordBytes;
        /// <summary>Most UTF-16 characters in one entry name.</summary>
        public static int MaximumNameLength => VersionedSaveRecordCodec.MaximumNameLength;
    }

    internal sealed class VersionedSaveKnownEntries
    {
        private readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> prefixes = new List<string>();

        internal static bool TryCreate(IReadOnlyList<string> declared, out VersionedSaveKnownEntries known, out string reason)
        {
            known = null;
            if (declared == null || declared.Count == 0) { reason = "At least one known entry name or prefix is required."; return false; }
            var result = new VersionedSaveKnownEntries();
            foreach (string item in declared)
            {
                if (!VersionedSaveRecordCodec.IsValidName(item) || item == "/")
                { reason = "Invalid known entry '" + item + "'."; return false; }
                bool added;
                if (item.EndsWith("/", StringComparison.Ordinal))
                {
                    added = !result.prefixes.Contains(item);
                    if (added) result.prefixes.Add(item);
                }
                else added = result.names.Add(item);
                if (!added) { reason = "Known entry '" + item + "' is declared twice."; return false; }
            }
            known = result;
            reason = string.Empty;
            return true;
        }

        internal bool IsKnown(string name)
        {
            if (names.Contains(name)) return true;
            foreach (string prefix in prefixes)
                if (name.Length > prefix.Length && name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}

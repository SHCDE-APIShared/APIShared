using System;
using System.IO;
using System.Text;
using SHCDESE.API;

namespace APIShared.ModData
{
    /// <summary>General map/local-sidecar reads and an optional confirmed selected-Lord source. No content selection,
    /// network transport, gameplay application or file writes. Read on the Unity thread after Extender initialization.
    /// Each read returns a detached immutable snapshot; no data is cached across reads or mission transitions.</summary>
    public static class ModDataAccess
    {
        private const string MapEntryName = "modmap.json";
        private const string LordExtension = ".lordjson";
        private static readonly object SourceLock = new object();
        private static SourceRegistration selectedSource;
        [ThreadStatic] private static bool readingSelected;

        /// <summary>Whether a source is registered; this does not establish that the current selection is ready.</summary>
        public static bool HasSelectedLordSource { get { lock (SourceLock) return selectedSource != null; } }

        /// <summary>Registers the single source responsible for confirmed selected-Lord data, with an owning mod GUID
        /// and stable owner-local registration ID. Duplicate/invalid registrations throw. Keep the returned token rooted;
        /// disposing it logically unregisters this instance and does not affect a later registration. The service retains
        /// the callback until disposal. Register/dispose on the Unity thread. The callback runs synchronously outside locks,
        /// must use its current verified selection, enforce sidecar limits, and return a result for the requested GUID.
        /// Null means not ready; exceptions become ReadError; recursive reads or registration changes during a read yield
        /// HostDataUnavailable. The provider owns host authority, lifecycle invalidation and optional local selection.
        /// It must never substitute a local file for unavailable host-confirmed data.</summary>
        public static IDisposable RegisterSelectedLordSource(string ownerGuid, string registrationId,
            Func<int, string, ModDataReadResult> readNamespace)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid)) throw new ArgumentException("An owner GUID is required.", nameof(ownerGuid));
            if (string.IsNullOrWhiteSpace(registrationId)) throw new ArgumentException("A stable registration ID is required.", nameof(registrationId));
            if (readNamespace == null) throw new ArgumentNullException(nameof(readNamespace));
            lock (SourceLock)
            {
                if (selectedSource != null) throw new InvalidOperationException("A selected-Lord data source is already registered.");
                selectedSource = new SourceRegistration(ownerGuid, registrationId, readNamespace);
                return selectedSource;
            }
        }

        /// <summary>Reads a confirmed selection by one-based player ID (1 through Extender MAX_PLAYERS).
        /// Without a ready provider returns HostDataUnavailable; never searches local files as a fallback.
        /// A registered provider may report a specifically verified local single-player selection.</summary>
        public static ModDataReadResult ReadSelectedLordNamespace(int playerId, string modGuid)
        {
            if (string.IsNullOrWhiteSpace(modGuid) || playerId < 1 || playerId > GamePlayerManagerAPI.MAX_PLAYERS)
                return Failure(ModDataReadStatus.InvalidRequest, modGuid, "", "A mod GUID and player ID from 1 to 8 are required.");
            SourceRegistration source;
            lock (SourceLock) source = selectedSource;
            if (source == null || readingSelected)
                return Failure(ModDataReadStatus.HostDataUnavailable, modGuid, "", "The selected Lord-data source is not ready.");
            ModDataReadResult result = null;
            Exception failure = null;
            readingSelected = true;
            try { result = source.Read(playerId, modGuid); }
            catch (Exception error) { failure = error; }
            finally { readingSelected = false; }
            lock (SourceLock)
                if (!ReferenceEquals(source, selectedSource))
                    return Failure(ModDataReadStatus.HostDataUnavailable, modGuid, "", "The selected Lord-data source changed during the read.");
            if (failure != null)
                return Failure(ModDataReadStatus.ReadError, modGuid, source.Identity, "Could not read confirmed Lord data: " + failure.Message);
            if (result == null)
                return Failure(ModDataReadStatus.HostDataUnavailable, modGuid, source.Identity, "The selected Lord-data source is not ready.");
            if (!string.Equals(result.ModGuid, modGuid, StringComparison.OrdinalIgnoreCase))
                return Failure(ModDataReadStatus.ReadError, modGuid, source.Identity, "The source returned a different mod GUID.");
            return result;
        }

        /// <summary>Reads modmap.json from the Extender's currently loaded in-memory archive, including its pending
        /// edits. Does not choose a file, reload an archive or infer a mission from the filename. Missing archive/entry
        /// returns FileNotFound. Invalid UTF-8/JSON returns InvalidDocument; other archive failures return ReadError.
        /// Preserves the existing map reader's unrestricted document-size contract.</summary>
        public static ModDataReadResult ReadCurrentMapNamespace(string modGuid)
        {
            if (string.IsNullOrWhiteSpace(modGuid))
                return Failure(ModDataReadStatus.InvalidRequest, modGuid, MapEntryName, "A non-empty mod GUID is required.");
            string source = MapEntryName;
            try
            {
                var manager = GameMapArchiveManagerAPI.Instance;
                string path = manager.GetCurrentFilePath();
                if (!string.IsNullOrWhiteSpace(path)) source = path + "::" + MapEntryName;
                byte[] bytes = manager.TryReadBinaryFile(MapEntryName, ignoreCase: true);
                if (bytes == null) return Failure(ModDataReadStatus.FileNotFound, modGuid, source, "The mod-data file does not exist.");
                string json = new UTF8Encoding(false, true).GetString(bytes);
                if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
                return ModDataNamespaceReader.Read(json, modGuid, source);
            }
            catch (DecoderFallbackException error)
            { return Failure(ModDataReadStatus.InvalidDocument, modGuid, source, "The mod-data file is not valid UTF-8: " + error.Message); }
            catch (Exception error)
            { return Failure(ModDataReadStatus.ReadError, modGuid, source, "Could not read modmap.json from the current map archive: " + error.Message); }
        }

        /// <summary>Explicitly reads the local .modlord.json sidecar of a .lordjson path, using the existing
        /// 64 KiB document / 16 KiB compact namespace limits. Local provenance is not host confirmation, even in
        /// multiplayer. This method neither resolves a selected Lord nor falls back through a registered provider.</summary>
        public static ModDataReadResult ReadLocalLordNamespace(string lordJsonPath, string modGuid)
        {
            if (string.IsNullOrWhiteSpace(modGuid))
                return Failure(ModDataReadStatus.InvalidRequest, modGuid, lordJsonPath, "A non-empty mod GUID is required.");
            if (string.IsNullOrWhiteSpace(lordJsonPath))
                return Failure(ModDataReadStatus.InvalidRequest, modGuid, lordJsonPath, "A .lordjson path is required.");
            if (!lordJsonPath.EndsWith(LordExtension, StringComparison.OrdinalIgnoreCase))
                return Failure(ModDataReadStatus.InvalidRequest, modGuid, lordJsonPath, "The supplied path must end in .lordjson.");
            string source = lordJsonPath.Substring(0, lordJsonPath.Length - LordExtension.Length) + ".modlord.json";
            try
            {
                if (!File.Exists(source)) return Failure(ModDataReadStatus.FileNotFound, modGuid, source, "The mod-data file does not exist.");
                using (var stream = File.OpenRead(source))
                    return ModDataNamespaceReader.Read(ModDataDocumentJson.ReadUtf8(stream), modGuid, source);
            }
            catch (Exception error) when (error is InvalidDataException || error is DecoderFallbackException || error is FormatException)
            { return Failure(ModDataReadStatus.InvalidDocument, modGuid, source, "Invalid Custom Lord mod-data sidecar: " + error.Message); }
            catch (Exception error)
            { return Failure(ModDataReadStatus.ReadError, modGuid, source, "Could not read the Custom Lord mod-data sidecar: " + error.Message); }
        }

        private static ModDataReadResult Failure(ModDataReadStatus status, string modGuid, string source, string diagnostic) =>
            ModDataReadResult.Failure(status, modGuid, source, diagnostic);

        private sealed class SourceRegistration : IDisposable
        {
            internal readonly string Identity;
            internal readonly Func<int, string, ModDataReadResult> Read;
            internal SourceRegistration(string owner, string id, Func<int, string, ModDataReadResult> read)
            { Identity = owner + ":" + id; Read = read; }
            public void Dispose() { lock (SourceLock) if (ReferenceEquals(selectedSource, this)) selectedSource = null; }
        }
    }
}

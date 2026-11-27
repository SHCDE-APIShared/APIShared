using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using APIShared.Maps;
using APIShared.Savegames;

namespace APIShared.Economy
{
    // The regular versioned save registry compares this record before admitting
    // an MP save. It also ties a completed delivery to the same archive window.
    internal sealed class AutoTradeArchive
    {
        private const string Entry = "allowed";
        private const string Identifier = "APIShared_AutoTrade";
        // The current game's header reader rejects saves above this size.
        private const int MaximumLegacySaveBytes = 9000000;
        private readonly Func<bool?> capture;
        private VersionedSavePreparedSource source;
        private StagedLoad staged;
        private StagedLoad completed;
        private LegacyLoad legacy;

        private AutoTradeArchive(Func<bool?> capture) { this.capture = capture; }

        internal static bool TryCreate(Func<bool?> capture, out AutoTradeArchive archive, out string reason,
            VersionedSaveDataRegistry registry = null)
        {
            archive = null;
            if (capture == null)
            {
                reason = "The AutoTrade rule source is unavailable.";
                return false;
            }

            var candidate = new AutoTradeArchive(capture);
            var registration = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, Identifier, 1, 1,
                new[] { Entry }, candidate.Capture, candidate.Loaded)
            {
                Prepared = candidate.Prepared,
                Scope = VersionedSaveScope.SaveGames,
                SimulationRelevant = true
            };
            VersionedSaveDataHandle handle;
            bool registered = registry == null
                ? VersionedSaveData.TryRegister(registration, out handle, out reason) &&
                    VersionedSaveDataRuntime.TryTrackPreparedSource(APISharedPlugin.PluginGuid, handle, out candidate.source, out reason)
                : registry.TryRegister(registration, out handle, out reason) &&
                    registry.TryTrackPreparedSource(APISharedPlugin.PluginGuid, handle, out candidate.source, out reason);
            if (!registered)
                return false;
            archive = candidate;
            return true;
        }

        private bool Capture(VersionedSaveWriter writer)
        {
            if (source == null) return false;
            bool? allowed = capture();
            if (!allowed.HasValue) return false;
            writer.Write(Entry, new byte[] { allowed.Value ? (byte)1 : (byte)0 });
            return true;
        }

        private void Prepared(VersionedSaveLoad load)
        {
            Volatile.Write(ref completed, null);
            Volatile.Write(ref staged, null);
            if (source == null || load.MissionSessionId <= 0 ||
                (load.Source != VersionedSaveLoadSource.SaveGame && load.Source != VersionedSaveLoadSource.MultiplayerSave) ||
                !source.TryGetCurrent(load, out var origin, out _) || !TryReadValue(load, origin, out bool? allowed))
                return;
            string path = NormalizePath(load.FilePath);
            if (path != null)
                Volatile.Write(ref staged, new StagedLoad(load.MissionSessionId, path, load.Source, origin, allowed));
        }

        private void Loaded(VersionedSaveLoad load)
        {
            var candidate = Volatile.Read(ref staged);
            if (candidate != null && candidate.Matches(load.MissionSessionId, NormalizePath(load.FilePath), load.Source) &&
                TryReadValue(load, candidate.Origin, out bool? allowed) && candidate.Allowed == allowed)
                Volatile.Write(ref completed, candidate);
        }

        // Called by the owning local load before entering the engine monitor.
        // Decode the bounded skirmish restart structure directly. Vanilla's cached
        // setup is usually unset and treats malformed numbers as zero when parsed.
        internal void PrepareLegacy(long sessionId, string filePath)
        {
            Volatile.Write(ref legacy, null);
            string path = NormalizePath(filePath);
            if (sessionId <= 0 || path == null || !Path.IsPathRooted(filePath) ||
                !path.EndsWith(".sav", StringComparison.OrdinalIgnoreCase))
                return;
            try
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var stamp = new FileInfo(path);
                    if (file.Length < 35000 || file.Length > MaximumLegacySaveBytes || stamp.Length != file.Length)
                        return;
                    using (var reader = new BinaryReader(file))
                    {
                        byte[] bytes = reader.ReadBytes((int)file.Length);
                        if (bytes.LongLength == stamp.Length && TryReadLegacyRule(bytes, out bool allowed))
                            Volatile.Write(ref legacy, new LegacyLoad(sessionId, path, stamp, allowed));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.SecurityException) { }
        }

        // Runs after the owning managed loader returned. Legacy files need only
        // a metadata recheck here; their bounded byte read happened before loading.
        internal bool? Resolve(long sessionId, string filePath, bool multiplayer)
        {
            var candidate = Volatile.Read(ref completed);
            var kind = multiplayer ? VersionedSaveLoadSource.MultiplayerSave : VersionedSaveLoadSource.SaveGame;
            if (candidate == null || !candidate.Matches(sessionId, NormalizePath(filePath), kind) ||
                !source.TryValidateCompleted(candidate.Origin, out _))
                return null;
            if (candidate.Allowed.HasValue || multiplayer) return candidate.Allowed;
            var local = Volatile.Read(ref legacy);
            return local != null && local.SessionId == sessionId &&
                string.Equals(local.Path, candidate.Path, StringComparison.OrdinalIgnoreCase) && local.StillMatches()
                ? local.Allowed : (bool?)null;
        }

        private static bool TryReadValue(VersionedSaveLoad load, VersionedSavePreparedOrigin origin, out bool? allowed)
        {
            allowed = null;
            if (load.Status == VersionedSaveLoadStatus.NoRecord) return !origin.HasRecord;
            if (load.Status != VersionedSaveLoadStatus.Restored || load.RecordVersion != 1 ||
                !load.Entries.TryGetValue(Entry, out byte[] value) || value.Length != 1 || value[0] > 1)
                return false;
            allowed = value[0] == 1;
            return true;
        }

        internal static bool TryReadLegacyRule(byte[] bytes, out bool allowed)
        {
            allowed = false;
            if (bytes == null) return false;
            try
            {
                var layout = TrailContainer.ReadLayout(bytes, MaximumLegacySaveBytes);
                if (!layout.HasRestartData) return false;
                using (var stream = new MemoryStream(bytes, layout.RestartPayloadOffset, layout.RestartSize, false))
                using (var reader = new BinaryReader(stream))
                {
                    // HUD_IngameMenu.RestartSkirmishMapInfo.encode (type 60): each
                    // lord contributes three Int32s, followed by map origin and name.
                    if (reader.ReadByte() != 60) return false;
                    int players = reader.ReadInt32();
                    if (players != 8 && players != 16) return false;
                    stream.Position += players * 12;
                    if (reader.ReadByte() > 2) return false;
                    int nameBytes = reader.ReadInt32();
                    if (nameBytes <= 0 || nameBytes > stream.Length - stream.Position) return false;
                    stream.Position += nameBytes;
                    int setupBytes = reader.ReadInt32();
                    // Current -12 ToString writes 117 Int32 values and a final comma.
                    // A full numeric check avoids FromString quietly translating malformed input to 0.
                    if (setupBytes <= 0 || setupBytes > 1404 || setupBytes > stream.Length - stream.Position)
                        return false;
                    string[] fields = Encoding.ASCII.GetString(reader.ReadBytes(setupBytes)).Split(',');
                    if (fields.Length != 118 || fields[0] != "-12" || fields[117].Length != 0 ||
                        (fields[5] != "0" && fields[5] != "1"))
                        return false;
                    for (int i = 1; i < 117; i++)
                        if (!int.TryParse(fields[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
                            return false;
                    allowed = fields[5] == "1";
                    return true;
                }
            }
            catch (IOException) { return false; }
            catch (InvalidDataException) { return false; }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Path.GetFullPath(path); }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        private sealed class StagedLoad
        {
            internal readonly long SessionId;
            internal readonly string Path;
            internal readonly VersionedSaveLoadSource Source;
            internal readonly VersionedSavePreparedOrigin Origin;
            internal readonly bool? Allowed;

            internal StagedLoad(long sessionId, string path, VersionedSaveLoadSource source,
                VersionedSavePreparedOrigin origin, bool? allowed)
            { SessionId = sessionId; Path = path; Source = source; Origin = origin; Allowed = allowed; }

            internal bool Matches(long sessionId, string path, VersionedSaveLoadSource source) =>
                sessionId == SessionId && source == Source && string.Equals(path, Path, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class LegacyLoad
        {
            internal readonly long SessionId;
            internal readonly string Path;
            internal readonly bool Allowed;
            private readonly long length;
            private readonly DateTime creationTime, lastWriteTime;

            internal LegacyLoad(long sessionId, string path, FileInfo stamp, bool allowed)
            {
                SessionId = sessionId; Path = path; Allowed = allowed;
                length = stamp.Length; creationTime = stamp.CreationTimeUtc; lastWriteTime = stamp.LastWriteTimeUtc;
            }

            internal bool StillMatches()
            {
                try
                {
                    var stamp = new FileInfo(Path);
                    return stamp.Length == length && stamp.LastWriteTimeUtc == lastWriteTime && stamp.CreationTimeUtc == creationTime;
                }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
        }
    }
}

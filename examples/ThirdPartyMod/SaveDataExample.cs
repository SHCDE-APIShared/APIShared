using System;
using System.Collections.Generic;
using APIShared;
using APIShared.Savegames;

namespace ThirdPartyMod
{
    // Keeps a per-mission level for each building in the save. Version 1 packed every building into
    // one "levels" entry; version 2 writes one "building/<id>" entry per building. Version 2 changed
    // the encoding, so its records require reader version 2.
    internal static class SaveDataExample
    {
        private const string BuildingPrefix = "building/";
        private const string CriticalMarker = "critical-save";
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, int> BuildingLevels = new Dictionary<int, int>();
        private static Action<string> log;
        private static CriticalSaveParticipant protectedParticipant;
        private static Dictionary<int, int> protectedSnapshot;

        internal static void Register(string ownerGuid, Action<string> logger)
        {
            log = logger;
            var registration = new VersionedSaveDataRegistration(ownerGuid, ownerGuid + ".SaveData",
                currentVersion: 2, minimumReaderVersion: 2, knownEntries: new[] { BuildingPrefix },
                capture: Capture, loaded: OnLoaded);
            registration.AddMigration(1, MigrateFromVersion1);
            // Building levels drive the simulation, so every player's saved copy must match before a
            // multiplayer save loads. View or preference data would go into a separate, undeclared record.
            registration.SimulationRelevant = true;
            if (!VersionedSaveData.TryRegister(registration, out _, out string reason))
                logger("Example save data unavailable: " + reason);
        }

        // Optional alternative to Register, called once on initialized Unity startup before a world/load.
        // The separate identifier does not silently migrate the ordinary example's existing saves.
        // A real consumer must design its complete migration before retiring its old provider or ledger.
        internal static bool TryRegisterProtected(ModApiClient client, Action<string> logger,
            out CriticalSaveParticipant participant, out string reason)
        {
            log = logger;
            participant = null;
            var registration = new VersionedSaveDataRegistration(client.OwnerGuid,
                client.OwnerGuid + ".ProtectedSaveData", currentVersion: 3, minimumReaderVersion: 3,
                knownEntries: new[] { BuildingPrefix, CriticalMarker },
                capture: CaptureProtected, loaded: OnLoaded)
            {
                Scope = VersionedSaveScope.SaveGamesAndMaps,
                SimulationRelevant = true,
                Prepared = OnProtectedPrepared
            };
            if (!VersionedSaveData.TryRegister(registration, out var handle, out reason)) return false;
            var options = new CriticalSaveOptions(CriticalMarker, 3, PrepareProtected, ReleaseProtected);
            // State-only example. A storage-dependent consumer sets RequiresNativeStorage = true here,
            // before enrollment AND before its first NativeStorage registration. No native access is granted.
            if (!client.CriticalSaves.TryEnroll(handle, options, out participant, out reason)) return false;
            protectedParticipant = participant;
            return true;
        }

        // Runs on the Extender's save thread. Known entries left out are deleted from the save.
        private static bool Capture(VersionedSaveWriter writer)
        {
            lock (Sync)
                foreach (KeyValuePair<int, int> building in BuildingLevels)
                    writer.Write(BuildingPrefix + building.Key, BitConverter.GetBytes(building.Value));
            return true;
        }

        private static bool PrepareProtected(VersionedSaveContext context, ref object state)
        {
            state = null;
            lock (Sync)
            {
                if (protectedSnapshot != null) return false;
                protectedSnapshot = new Dictionary<int, int>(BuildingLevels);
                state = protectedSnapshot;
                return true;
            }
        }

        private static bool CaptureProtected(VersionedSaveWriter writer)
        {
            lock (Sync)
            {
                // Failed enrollment cannot fall back to an ordinary, unprotected capture.
                if (protectedParticipant == null || protectedSnapshot == null) return false;
                foreach (KeyValuePair<int, int> building in protectedSnapshot)
                    writer.Write(BuildingPrefix + building.Key, BitConverter.GetBytes(building.Value));
            }
            // Do not write CriticalMarker: enrollment reserves it for framework injection.
            return true;
        }

        private static void ReleaseProtected(VersionedSaveContext context, object state)
        {
            // Called once for every entered Prepare, also on false/throw/null; no Dispose is registered.
            lock (Sync)
                if (ReferenceEquals(protectedSnapshot, state)) protectedSnapshot = null;
        }

        private static void OnProtectedPrepared(VersionedSaveLoad load)
        {
            if (protectedParticipant == null)
                throw new InvalidOperationException("Protected example enrollment is unavailable.");
            if (!protectedParticipant.TryReadPreparedProtection(load, out bool protectedRecord, out string reason))
                throw new InvalidOperationException(reason);
            // Observe only in this exact callback. This does not confirm the complete cohort, initialize
            // native state, certify a live commit or authorize import/retirement of another provider.
            if (protectedRecord) log("Example archive contains an original protection marker.");
        }

        // Runs once at the end of every archive load, also without a record: reset, then take the entries.
        // Early state can use MissionSessionId when requiring a provider build exporting it;
        // see docs/SAVE_DATA.md before coupling Prepared data to queued lifecycle notifications.
        private static void OnLoaded(VersionedSaveLoad load)
        {
            lock (Sync)
            {
                BuildingLevels.Clear();
                foreach (KeyValuePair<string, byte[]> entry in load.Entries)
                    if (entry.Key.StartsWith(BuildingPrefix, StringComparison.Ordinal) && entry.Value.Length == 4 &&
                        int.TryParse(entry.Key.Substring(BuildingPrefix.Length), out int id))
                        BuildingLevels[id] = BitConverter.ToInt32(entry.Value, 0);
            }
            if (load.Status != VersionedSaveLoadStatus.Restored && load.Status != VersionedSaveLoadStatus.NoRecord &&
                load.Status != VersionedSaveLoadStatus.OutOfScope)
                log("Saved example data not used (" + load.Status + "): " + load.Detail);
        }

        // Version 1 stored (id, level) pairs of 32-bit integers in "levels". An entry that already exists
        // came from a newer version through an older one's save and is left as it is.
        private static void MigrateFromVersion1(VersionedSaveEntries entries)
        {
            if (!entries.TryGet("levels", out byte[] packed)) return;
            for (int offset = 0; offset + 8 <= packed.Length; offset += 8)
            {
                string name = BuildingPrefix + BitConverter.ToInt32(packed, offset);
                if (!entries.Contains(name)) entries.Set(name, BitConverter.GetBytes(BitConverter.ToInt32(packed, offset + 4)));
            }
            entries.Remove("levels");
        }
    }
}

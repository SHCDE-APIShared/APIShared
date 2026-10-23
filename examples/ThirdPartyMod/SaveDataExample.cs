using System;
using System.Collections.Generic;
using APIShared.Savegames;

namespace ThirdPartyMod
{
    // Keeps a per-mission level for each building in the save. Version 1 packed every building into
    // one "levels" entry; version 2 writes one "building/<id>" entry per building. Version 2 changed
    // the encoding, so its records require reader version 2.
    internal static class SaveDataExample
    {
        private const string BuildingPrefix = "building/";
        private static readonly object Sync = new object();
        private static readonly Dictionary<int, int> BuildingLevels = new Dictionary<int, int>();
        private static Action<string> log;

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

        // Runs on the Extender's save thread. Known entries left out are deleted from the save.
        private static bool Capture(VersionedSaveWriter writer)
        {
            lock (Sync)
                foreach (KeyValuePair<int, int> building in BuildingLevels)
                    writer.Write(BuildingPrefix + building.Key, BitConverter.GetBytes(building.Value));
            return true;
        }

        // Runs once at the end of every archive load, also without a record: reset, then take the entries.
        private static void OnLoaded(VersionedSaveLoad load)
        {
            lock (Sync)
            {
                BuildingLevels.Clear();
                foreach (KeyValuePair<string, byte[]> entry in load.Entries)
                    if (entry.Value.Length == 4 && int.TryParse(entry.Key.Substring(BuildingPrefix.Length), out int id))
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

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using APIShared.Savegames;

namespace APIShared.Economy
{
    // The actual early-load controller is independent of native world access. Runtime.Prepare
    // applies only its fully validated candidate. Throwing for restored invalid data tells the
    // versioned registry to retain the original record, not a partially decoded replacement.
    internal sealed class NativeStoragePreparedLoad
    {
        internal NativeStorageState State { get; private set; }
        internal bool Blocked { get; private set; }
        internal bool MayContainCargo { get; private set; }
        internal string Reason { get; private set; }

        internal void Prepare(IReadOnlyList<NativeStorageGoodRegistration> definitions, VersionedSaveLoad load)
        {
            State = null; Blocked = false; MayContainCargo = true; Reason = string.Empty;
            try
            {
                if (load == null || definitions == null) throw new InvalidDataException("Storage preparation has no load or registered catalogue.");
                if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope)
                {
                    var empty = new NativeStorageState();
                    foreach (var entry in definitions)
                        if (!empty.TryRegister(entry.Good, entry.Kind, out string error)) throw new InvalidDataException(error);
                    MayContainCargo = false; State = empty; return;
                }
                // A readable schema-1 stores header proves this pair has no cargo tail even if
                // its body is malformed. Unknown/TooNew/corrupt envelopes hide their entries and
                // cannot provide that proof; their passive save behavior must be conservative.
                MayContainCargo = !HasLegacyStoresHeader(load);
                if (load.Status != VersionedSaveLoadStatus.Restored)
                { Blocked = true; Reason = "The native-storage save record cannot be read."; return; }
                if (!load.Entries.TryGetValue("ledger", out byte[] ledger) || !load.Entries.TryGetValue("stores", out byte[] stores) ||
                    !NativeStorageState.TryRestore(definitions, ledger, stores, out NativeStorageState candidate, out string detail))
                    throw new InvalidDataException("Invalid native-storage ledger/allocation pair.");
                if (candidate.HasCargoHistory && (load.RecordVersion < 2 || load.MinimumReaderVersion < 2))
                    throw new InvalidDataException("Cargo history requires record version 2 and minimum reader 2 or later.");
                State = candidate;
            }
            catch (Exception error)
            {
                State = null; Blocked = true; Reason = error.Message;
                throw;
            }
        }
        private static bool HasLegacyStoresHeader(VersionedSaveLoad load) =>
            load.Status == VersionedSaveLoadStatus.Restored && load.Entries.TryGetValue("stores", out byte[] stores) && stores != null && stores.Length >= 8 &&
            BinaryPrimitives.ReadInt32LittleEndian(stores) == 0x3153474E && BinaryPrimitives.ReadInt32LittleEndian(stores.AsSpan(4)) == 1;
    }
}

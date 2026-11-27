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
        internal long MissionSessionId { get; private set; }
        internal bool Blocked { get; private set; }
        internal bool MayContainCargo { get; private set; }
        internal string Reason { get; private set; }

        // Decoder-only callers acquire no world ownership. The runtime supplies the current
        // executing attempt explicitly; a ready/global context cannot lend its ID to an archive.
        internal void Prepare(IReadOnlyList<NativeStorageGoodRegistration> definitions, VersionedSaveLoad load) =>
            Prepare(definitions, load, null);

        internal void Prepare(IReadOnlyList<NativeStorageGoodRegistration> definitions, VersionedSaveLoad load,
            long? executingSession)
        {
            MissionSessionId = load?.MissionSessionId ?? 0;
            State = null; Blocked = false; MayContainCargo = true; Reason = string.Empty;
            try
            {
                if (load == null || definitions == null) throw new InvalidDataException("Storage preparation has no load or registered catalogue.");
                // Reject before interpreting even NoRecord: an unassociated archive cannot
                // establish absence for the active world or downgrade conservative save demand.
                if (executingSession.HasValue && (MissionSessionId <= 0 || MissionSessionId != executingSession.Value))
                    throw new InvalidDataException("The storage archive has no matching executing mission attempt.");
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
        // Only Runtime's existing explicit EditorCreated boundary uses this path. It records
        // that creator's session without pretending that a versioned archive was delivered.
        internal static NativeStoragePreparedLoad ForBlankWorld(NativeStorageState state, long missionSessionId)
        {
            if (state == null || missionSessionId <= 0) throw new ArgumentException("A confirmed blank world is required.");
            return new NativeStoragePreparedLoad { State = state, MissionSessionId = missionSessionId,
                MayContainCargo = false, Reason = string.Empty };
        }

        internal void ReleaseState() => State = null;

        private static bool HasLegacyStoresHeader(VersionedSaveLoad load) =>
            load.Status == VersionedSaveLoadStatus.Restored && load.Entries.TryGetValue("stores", out byte[] stores) && stores != null && stores.Length >= 8 &&
            BinaryPrimitives.ReadInt32LittleEndian(stores) == 0x3153474E && BinaryPrimitives.ReadInt32LittleEndian(stores.AsSpan(4)) == 1;
    }
}

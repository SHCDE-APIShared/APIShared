using System;
using System.IO;
using System.Threading;
using APIShared.Savegames;
using SHCDESE.API.Components.Archive;

namespace APIShared.Economy
{
    // One frozen pair accompanies the existing critical production writer. This is a save
    // checkpoint, not a second ledger. Reentrant storage mutations are refused until End.
    internal sealed class NativeStorageProductionSave
    {
        private readonly NativeStorageState state;
        private readonly object world;
        private readonly object publication;
        private readonly byte[] goods, stores;
        internal bool Captured { get; private set; }
        internal NativeStorageProductionSave(NativeStorageState state, object world)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.world = world ?? throw new ArgumentNullException(nameof(world));
            publication = state.PublicationIdentity;
            state.Capture(out goods, out stores);
        }
        internal bool Matches(NativeStorageState current, object currentWorld) =>
            ReferenceEquals(state, current) && ReferenceEquals(world, currentWorld) && ReferenceEquals(publication, current.PublicationIdentity);
        internal bool IsWritten(VersionedSaveWriteStatus status, bool keepsLoaded) =>
            Captured && status == VersionedSaveWriteStatus.Written && !keepsLoaded;
        internal void Write(VersionedSaveWriter writer)
        { writer.Write("ledger", goods); writer.Write("stores", stores); Captured = true; }
        internal bool MatchesRecord(byte[] record)
        {
            if (record == null) return false;
            var decoded = VersionedSaveRecordCodec.Decode(record, 1);
            return decoded.Status == VersionedSaveRecordCodec.DecodeStatus.Valid &&
                decoded.Entries.TryGet("ledger", out byte[] savedGoods) && decoded.Entries.TryGet("stores", out byte[] savedStores) &&
                Same(goods, savedGoods) && Same(stores, savedStores);
        }
        private static bool Same(byte[] left, byte[] right)
        {
            if (right == null || left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }

    internal sealed unsafe partial class NativeStorageRuntime
    {
        private const string SaveIdentifier = "APIShared.NativeStorage.v1";
        private VersionedSaveDataHandle saveHandle;
        private NativeStorageProductionSave productionSave;

        internal void FreezeProductionSave()
        {
            if (engine == null || !Monitor.IsEntered(engine) || !ready)
                throw new InvalidOperationException("Custom production saving needs the active protected storage owner.");
            if (productionSave != null) throw new InvalidOperationException("A production storage checkpoint is already frozen.");
            if (!Context(true, out object world, out string reason)) throw new InvalidDataException(reason);
            if (MissionLifecycleService.NativeClock?.Context.Mode.IsRealMultiplayer != false)
                throw new InvalidOperationException("Custom production saving is not qualified for real multiplayer repair.");
            if (!EnsureWorld(out world, out reason) || !Reconcile(out reason)) throw new InvalidDataException(reason);
            var frozen = new NativeStorageProductionSave(state, world);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld) || blocked)
                throw new InvalidDataException("The native world changed during storage save preparation.");
            productionSave = frozen;
        }

        internal void VerifyProductionSaveWritten(string path)
        {
            NativeStorageProductionSave frozen = productionSave;
            if (frozen == null || !frozen.Matches(state, MissionLifecycleService.NativeWorld) || blocked ||
                saveHandle == null || !frozen.IsWritten(saveHandle.LastWriteStatus, saveHandle.KeepsLoadedRecord))
                throw new InvalidDataException("The current native-storage save checkpoint was not written.");
            if (string.IsNullOrEmpty(path) || !MapArchive.TryLoad(path, out MapArchive archive))
                throw new InvalidDataException("The completed production save archive cannot be read.");
            using (archive)
            {
                byte[] record = archive.TryReadBinaryFile("_SE_ModData_" + SaveIdentifier + ".msgpack", true);
                if (!frozen.MatchesRecord(record)) throw new InvalidDataException("The saved native-storage pair differs from the frozen checkpoint.");
            }
        }
        internal void EndProductionSave() { productionSave = null; }
    }
}

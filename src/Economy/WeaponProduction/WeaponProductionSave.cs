using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using APIShared.Savegames;
using SHCDESE.API.Components.Archive;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        private const string SaveIdentifier = "APIShared_WeaponProduction";
        private const string SaveEntry = "workshops";
        private const uint SaveMagic = 0x31575057;
        private const int MaxBuildingId = WeaponProductionNativeDefinition.MaxBuildingId;
        private const int MaxUnitId = WorkerProductionStateStore.RowCapacity - 1;
        private const int SaveHeaderBytes = 76;
        private const int MaximumSaveBytes = SaveHeaderBytes + MaxBuildingId * BuildingWireBytes + MaxUnitId * WorkerWireBytes;
        private static VersionedSaveDataHandle saveHandle;
        private static byte[] preparedSave;
        private static bool failedPreparation, restoredSave;
        [ThreadStatic] private static byte[] frozenSave;
        [ThreadStatic] private static bool capturedSave;

        private static void InitializeSave()
        {
            var registration = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, SaveIdentifier, 1, 1,
                new[] { SaveEntry }, CaptureSave, _ => { })
            {
                Scope = VersionedSaveScope.SaveGamesAndMaps,
                SimulationRelevant = true,
                Prepared = PrepareSave
            };
            if (!VersionedSaveData.TryRegister(registration, out saveHandle, out string reason))
                throw new InvalidOperationException("Weapon-workshop save registration failed: " + reason);
            NativeWorldRestoreParticipants.Register(RestoreSave);
        }

        internal static string SavedDigest(byte[] payload)
        {
            if (payload == null) return "";
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "");
        }

        internal static string CombineDigest(string first, string second) =>
            SavedDigest(Encoding.UTF8.GetBytes("weapon-workshop/1\n" + first + "\n" + second));

        internal static byte[] InspectSave(string path)
        {
            if (string.IsNullOrEmpty(path) || !MapArchive.TryLoad(path, out MapArchive archive)) return null;
            using (archive)
            {
                byte[] record = archive.TryReadBinaryFile("_SE_ModData_" + SaveIdentifier + ".msgpack", true);
                if (record == null) return null;
                var decoded = VersionedSaveRecordCodec.Decode(record, 1);
                if (decoded.Status != VersionedSaveRecordCodec.DecodeStatus.Valid)
                    throw new InvalidDataException("Weapon-workshop save record is invalid: " + decoded.Detail);
                if (!decoded.Entries.TryGet(SaveEntry, out byte[] bytes)) return null;
                ValidateSave(bytes, out _, out _);
                return bytes;
            }
        }

        internal static bool MatchesSavedCatalog(byte[] payload, WeaponProductionCatalog catalog) =>
            payload == null || StringComparer.Ordinal.Equals(ReadCatalog(payload), catalog.Digest);

        private static void PrepareSave(VersionedSaveLoad load)
        {
            preparedSave = null;
            failedPreparation = false;
            try
            {
                if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope) return;
                if (load.Status != VersionedSaveLoadStatus.Restored)
                    throw new InvalidDataException("Weapon-workshop save cannot be prepared: " + load.Status);
                if (!load.Entries.TryGetValue(SaveEntry, out byte[] payload)) return;
                ValidateSave(payload, out _, out _);
                preparedSave = (byte[])payload.Clone();
            }
            catch { failedPreparation = true; }
        }

        private static bool RestoreSave()
        {
            if (failedPreparation)
            { WorkerProductionWorldSafety.Fault("Weapon-workshop save data cannot be prepared."); return false; }
            if (preparedSave == null) return true;
            if (!MatchesSavedCatalog(preparedSave, frozen))
            { WorkerProductionWorldSafety.Fault("The saved weapon-workshop catalogue is unavailable or differs."); return false; }

            ValidateSave(preparedSave, out int buildingCount, out int workerCount);
            int workerStart = SaveHeaderBytes + BuildingWireBytes * buildingCount;
            for (int i = 0; i < buildingCount; i++)
            {
                var row = preparedSave.AsSpan(SaveHeaderBytes + i * BuildingWireBytes, BuildingWireBytes);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(row);
                uint gid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2));
                if (!TryBuilding(id, gid, out var building) || !ValidateBuildingWire(id, gid, building->r_BuildingType, row))
                    throw new InvalidDataException("A saved weapon workshop does not match its native building and recipe.");
            }
            for (int i = 0; i < workerCount; i++)
            {
                var row = preparedSave.AsSpan(workerStart + i * WorkerWireBytes, WorkerWireBytes);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(row);
                uint gid = BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2));
                if (!TryUnitKind(id, gid, out var kind) || !ValidateWorkerWire(id, gid, kind, row))
                    throw new InvalidDataException("A saved workshop batch does not match its native worker and recipe.");
            }
            for (int i = 0; i < workerCount; i++)
            {
                var row = preparedSave.AsSpan(workerStart + i * WorkerWireBytes, WorkerWireBytes);
                ReadWorkerWire(BinaryPrimitives.ReadUInt16LittleEndian(row), row);
            }
            for (int i = 0; i < buildingCount; i++)
            {
                var row = preparedSave.AsSpan(SaveHeaderBytes + i * BuildingWireBytes, BuildingWireBytes);
                ReadBuildingWire(BinaryPrimitives.ReadUInt16LittleEndian(row), row);
            }
            restoredSave = true;
            return true;
        }

        internal static bool RestoreVerified(byte[] selected) => selected == null
            ? !failedPreparation && preparedSave == null
            : !failedPreparation && restoredSave && preparedSave != null && preparedSave.AsSpan().SequenceEqual(selected);

        private static void ResetSaveLoad()
        { preparedSave = null; failedPreparation = false; restoredSave = false; }

        internal static void FreezeSave()
        {
            frozenSave = null;
            capturedSave = false;
            if (!HasHudDemand) return;
            var savedBuildings = buildings.OrderBy(pair => pair.Key)
                .Where(pair => TryBuilding(pair.Key, pair.Value.GlobalId, out _) &&
                    (pair.Value.Next != null || HasCurrentBatch(pair.Value))).ToArray();
            var savedWorkers = workers.OrderBy(pair => pair.Key).Where(pair => IsBatchActorCurrent(pair.Value)).ToArray();
            if (savedBuildings.Length == 0 && savedWorkers.Length == 0) return;
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(SaveMagic);
                writer.Write(Encoding.ASCII.GetBytes(active.Digest));
                writer.Write(savedBuildings.Length);
                writer.Write(savedWorkers.Length);
                var buildingRow = new byte[BuildingWireBytes];
                foreach (var pair in savedBuildings)
                {
                    WriteBuildingWire(pair.Key, buildingRow);
                    writer.Write(buildingRow);
                }
                var workerRow = new byte[WorkerWireBytes];
                foreach (var pair in savedWorkers)
                {
                    WriteWorkerWire(pair.Key, workerRow);
                    writer.Write(workerRow);
                }
                frozenSave = stream.ToArray();
            }
            ValidateSave(frozenSave, out _, out _);
        }

        private static int OptionIndex(WeaponProductionOption option)
        {
            if (option == null) return 0;
            int index = frozen.IndexOf(option.Id);
            if (index <= 0) throw new InvalidDataException("A weapon-workshop option left the active catalogue.");
            return index;
        }

        private static bool CaptureSave(VersionedSaveWriter writer)
        {
            if (frozenSave == null) return true;
            writer.Write(SaveEntry, frozenSave);
            capturedSave = true;
            return true;
        }

        internal static void VerifySaveWritten(string path)
        {
            if (frozenSave == null) return;
            if (!capturedSave || saveHandle.LastWriteStatus != VersionedSaveWriteStatus.Written || saveHandle.KeepsLoadedRecord ||
                !frozenSave.AsSpan().SequenceEqual(InspectSave(path)))
                throw new InvalidDataException("The weapon-workshop save was not written from the current snapshot.");
        }

        internal static void EndSaveCapture() { frozenSave = null; capturedSave = false; }

        private static bool TryUnitKind(int id, uint gid, out eChimps kind)
        {
            kind = default;
            if (id <= 0 || id > MaxUnitId || gid == 0 || !UnitAccess.TryGetById(id, out GameUnit* unit, out _) ||
                unit->r_GlobalId != gid) return false;
            kind = unit->r_UnitChimp;
            return true;
        }

        private static string ReadCatalog(byte[] bytes) => Encoding.ASCII.GetString(bytes, 4, 64);

        private static void ValidateSave(byte[] bytes, out int buildingCount, out int workerCount)
        {
            if (bytes == null || bytes.Length < SaveHeaderBytes || bytes.Length > MaximumSaveBytes)
                throw new InvalidDataException("Invalid weapon-workshop save length.");
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != SaveMagic)
                throw new InvalidDataException("Unknown weapon-workshop save schema.");
            string catalog = ReadCatalog(bytes);
            if (catalog.Any(c => !(c >= '0' && c <= '9') && !(c >= 'A' && c <= 'F')))
                throw new InvalidDataException("Invalid weapon-workshop catalogue digest.");
            buildingCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(68));
            workerCount = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(72));
            if (buildingCount < 0 || buildingCount > MaxBuildingId || workerCount < 0 || workerCount > MaxUnitId ||
                SaveHeaderBytes + BuildingWireBytes * buildingCount + WorkerWireBytes * workerCount != bytes.Length)
                throw new InvalidDataException("Invalid weapon-workshop row counts.");
            int workerStart = SaveHeaderBytes + BuildingWireBytes * buildingCount;
            ValidateSaveRows(bytes.AsSpan(SaveHeaderBytes, workerStart - SaveHeaderBytes), BuildingWireBytes, MaxBuildingId);
            ValidateSaveRows(bytes.AsSpan(workerStart), WorkerWireBytes, MaxUnitId);
        }

        private static void ValidateSaveRows(ReadOnlySpan<byte> bytes, int stride, int maxId)
        {
            int previous = 0;
            for (int offset = 0; offset < bytes.Length; offset += stride)
            {
                var row = bytes.Slice(offset, stride);
                int id = BinaryPrimitives.ReadUInt16LittleEndian(row);
                if (id <= previous || id > maxId || BinaryPrimitives.ReadUInt32LittleEndian(row.Slice(2)) == 0)
                    throw new InvalidDataException("Invalid or unordered weapon-workshop identity.");
                previous = id;
            }
        }
    }
}

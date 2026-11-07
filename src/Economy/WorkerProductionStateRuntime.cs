using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Collections.Generic;
using BepInEx.Logging;
using APIShared.Savegames;
using SHCDESE.API.Components.Archive;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using SHCDESE.IO;

namespace APIShared.Economy
{
    internal sealed class WorkerProductionStateRequirement
    {
        internal WorkerProductionStateRequirement(bool valid, bool extended, int minimumReader, string digest, string diagnostic)
        { IsValid = valid; RequiresExtended = extended; MinimumReaderSchema = minimumReader; Digest = digest ?? ""; Diagnostic = diagnostic ?? ""; }
        internal bool IsValid { get; }
        internal bool RequiresExtended { get; }
        internal int MinimumReaderSchema { get; }
        internal string Digest { get; }
        internal string Diagnostic { get; }
    }

    // Lifetime: statically rooted registry, lifecycle observer and native restore participant.
    // All native reads/writes happen under EngineMonitor. Prepared only decodes private bytes.
    internal static unsafe class WorkerProductionStateRuntime
    {
        internal const string RecordIdentifier = "APIShared_WorkerProduction";
        internal const string EntryName = "state";
        internal const string CapabilityKey = "worker-production-sidecar/1/FBCB9319/row32";
        internal const int WireSchema = 1, HeaderSize = 16;
        private static readonly WorkerProductionStateRequirement Legacy = new WorkerProductionStateRequirement(true, false, 0, "", "");
        private static object engine;
        private static long session;
        private static byte[] prepared;
        private static bool preparedFailed, rawRestored, initialized, connected;
        internal static bool PreparedThroughSaveGameEntry { get; private set; }
        private static WorkerProductionStateRequirement preparedRequirement = Legacy;
        private static VersionedSaveDataHandle saveHandle;
        [ThreadStatic] private static byte[] frozenPayload;
        [ThreadStatic] private static bool frozenCaptured;
        internal static event Action RequirementChanged;
        internal static bool StateReady => initialized && connected && WorkerProductionStateNativeRuntime.IsAvailable &&
            WorkerProductionCriticalSaveRuntime.IsAvailable && WorkerProductionSaveUiRuntime.IsAvailable &&
            Units.AiMilitary.Native.AiMilitaryNativeObservers.IsReady && WorkerProductionWorldSafety.IsAvailable;
        internal static bool PreparedRequiresExtended => preparedRequirement.RequiresExtended;
        internal static bool LoadedRequiresExtended => preparedRequirement.RequiresExtended;
        internal static bool IsAvailable => StateReady;
        internal static string Diagnostic => StateReady ? "" : "Production persistence, wire, save transaction or restore owner is unavailable.";
        internal static int RequiredSchema => preparedRequirement.MinimumReaderSchema;
        internal static string SavedRecordDigest => preparedRequirement.Digest;
        internal static bool WorldHasData => IsExtended;
        internal static event Action ConfigurationChanged
        { add { RequirementChanged += value; } remove { RequirementChanged -= value; } }
        internal static string PreparedDiagnostic => preparedRequirement.Diagnostic;
        internal static string PreparedRecordDigest => preparedRequirement.Digest;
        internal static bool IsExtended => WorkerProductionStateStore.Ready &&
            WorkerProductionStateStore.WorldMode == WorkerProductionWorldMode.Extended;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (initialized) return;
            if (!string.Equals(hash, WorkerProductionBonusNativeDefinition.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production sidecar requires the audited native image.");
            if (!WorkerProductionWorldSafety.IsAvailable)
                throw new InvalidOperationException("Production persistence requires the validated world-safety boundary.");
            engine = Internal.EngineMonitor.Current ?? throw new InvalidOperationException("Production state requires the real engine monitor.");
            var registration = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, RecordIdentifier, 1, 1,
                new[] { EntryName }, Capture, Loaded) {
                Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = Prepared
            };
            if (!VersionedSaveData.TryRegister(registration, out saveHandle, out string reason))
                throw new InvalidOperationException("Production persistence registration failed: " + reason);
            WorkerProductionModifiers.SetSavedDenominatorProvider(WorkerProductionStateStore.CaptureDenominators);
            // Preparation of every dependent site precedes world admission. A partial publication
            // remains logically dormant; global production availability also requires StateReady.
            WorkerProductionSaveUiRuntime.Initialize(log);
            WorkerProductionCriticalSaveRuntime.Initialize(context, hash, log);
            WorkerProductionStateNativeRuntime.Initialize(context, hash, log);
            initialized = true;
        }

        // The root owner calls this only after its lifecycle observer has bound. An
        // optional integration failure must not leave a participant rejecting vanilla
        // loads despite having no mission owner capable of admitting their world.
        internal static void CompleteIntegration()
        {
            if (connected) return;
            // Persistence is an optional cohort. Its failure must leave the root's
            // fixed-only/Vanilla lifecycle usable; StateReady still rejects base demand.
            if (!initialized) return;
            NativeWorldRestoreParticipants.Register(Restore);
            connected = true;
        }

        internal static void BeginWorld(long sessionId, bool hasBaseDemand)
        {
            lock (engine)
            {
                if (session == sessionId) return;
                session = sessionId; prepared = null; preparedFailed = rawRestored = false;
                PreparedThroughSaveGameEntry = false;
                preparedRequirement = Legacy;
                WorkerProductionStateStore.Clear();
                // Admission is frozen before the native loader can perform its first fold.
                bool extended = hasBaseDemand;
                if (extended && !StateReady) { preparedFailed = true; WorkerProductionWorldSafety.Fault("Production state service is unavailable at mission admission."); return; }
                WorkerProductionStateStore.Admit(extended ? WorkerProductionWorldMode.Extended : WorkerProductionWorldMode.Legacy);
            }
        }
        internal static void ValidateStarted(long sessionId)
        {
            lock (engine)
                if (session == sessionId && (preparedFailed ||
                    (preparedRequirement.RequiresExtended && !rawRestored)))
                    WorkerProductionWorldSafety.Fault("The production sidecar was not restored before native initialization.");
        }
        internal static void EndWorld(long sessionId)
        {
            lock (engine)
                if (session == sessionId)
                {
                    WorkerProductionStateStore.Clear(); session = 0; prepared = null;
                    preparedFailed = rawRestored = false; preparedRequirement = Legacy;
                    PreparedThroughSaveGameEntry = false;
                }
        }

        private static void Prepared(VersionedSaveLoad load)
        {
            // Deliberately no native access. Invalid candidate stays sticky until next attempt;
            // the registry's callback exception isolation must never turn this into a legacy load.
            try
            {
                // Only these two event sources enter native853F0. A multiplayer save
                // arrives through84AC0 and cannot borrow853F0's scene40 proof.
                PreparedThroughSaveGameEntry = load.Source == VersionedSaveLoadSource.SaveGame ||
                    load.Source == VersionedSaveLoadSource.EditorMap;
                prepared = null; preparedRequirement = Requirement(load, out byte[] payload);
                if (!preparedRequirement.IsValid) throw new InvalidDataException(preparedRequirement.Diagnostic);
                prepared = payload;
            }
            catch (Exception error)
            {
                preparedFailed = true;
                preparedRequirement = new WorkerProductionStateRequirement(false, true, WireSchema, "", error.Message);
            }
            var callbacks = RequirementChanged;
            if (callbacks != null) foreach (Action callback in callbacks.GetInvocationList())
                try { callback(); } catch { /* observational agreement callbacks cannot change the candidate */ }
        }
        private static void Loaded(VersionedSaveLoad load)
        {
            if (preparedFailed || (preparedRequirement.RequiresExtended && !rawRestored))
                WorkerProductionWorldSafety.Fault("Production save restoration failed: " + preparedRequirement.Diagnostic);
        }

        private static bool Restore()
        {
            lock (engine)
            {
                if (session == 0)
                {
                    if (preparedFailed || preparedRequirement.RequiresExtended)
                        return RejectRestore("Required production data cannot restore without an admitted mission.");
                    return true; // Noninteractive native preview with no gameplay sidecar.
                }
                if (preparedFailed) return false;
                // BeginWorld already froze the mode-filtered admitted bundle. Raw desired
                // profiles may be inactive in this mission and cannot renegotiate its wire.
                bool extended = WorkerProductionStateStore.WorldMode == WorkerProductionWorldMode.Extended || preparedRequirement.RequiresExtended;
                if (extended && !StateReady) return RejectRestore("Required production sidecar support is unavailable.");
                // Validate the whole candidate against the now-restored native world BEFORE writing
                // any row. A failed candidate never installs a prefix of the saved table.
                if (prepared != null)
                    for (int id = 1; id < WorkerProductionStateStore.RowCapacity; id++)
                    {
                        WorkerProductionStateRow row = ReadRow(prepared, HeaderSize + id * WorkerProductionStateStore.RowSize);
                        if (!row.IsEmpty && !MatchesNative(id, in row)) return RejectRestore("Saved production identity does not match unit " + id + ".");
                    }
                WorkerProductionStateStore.Clear();
                if (prepared != null)
                    Marshal.Copy(prepared, HeaderSize, WorkerProductionStateStore.RowsAddress,
                        WorkerProductionStateStore.RowCapacity * WorkerProductionStateStore.RowSize);
                WorkerProductionStateStore.Admit(extended ? WorkerProductionWorldMode.Extended : WorkerProductionWorldMode.Legacy);
                if (!WorkerProductionRuntime.ValidateRestoredState(out string diagnostic)) return RejectRestore(diagnostic);
                if (extended) WorkerProductionWorldSafety.RequireGuard();
                rawRestored = true;
                return true;
            }
        }
        private static bool RejectRestore(string reason)
        { preparedFailed = true; WorkerProductionWorldSafety.Fault(reason); return false; }

        internal static bool MatchesNative(int id, in WorkerProductionStateRow row)
        {
            if (!UnitAccess.TryGetById(id, out GameUnit* unit, out _) || unit->r_GlobalId != row.GlobalId ||
                unit->r_UnitChimp != row.ProducerKind) return false;
            // A paused or retiring native record may still be saved/transferred verbatim.
            // Lifetime is the actual GID/type pair, not a guessed live-state cutoff.
            return true;
        }
        internal static WorkerProductionStateRow LiveRow(int id)
        {
            WorkerProductionStateRow* pointer = WorkerProductionStateStore.RowPointer(id);
            var row = *pointer;
            if (!WorkerProductionStateStore.Validate(in row, out string reason))
                throw new InvalidDataException(reason);
            // Capture/pack are observations. Do not mutate live state merely because one
            // peer saves; native identity checks retire stale rows in the simulation path.
            if (!row.IsEmpty)
            {
                if (!UnitAccess.TryGetById(id, out GameUnit* unit, out _) || unit->r_GlobalId != row.GlobalId) return default;
                return WorkerProductionStateStore.ForCurrentKind(in row, unit->r_UnitChimp);
            }
            return row;
        }

        // This is the only producer of save bytes. The common 52B0 transaction freezes it
        // under EngineMonitor BEFORE native saving; the Extender capture reads only this copy.
        internal static byte[] FreezeForSave()
        {
            if (frozenPayload != null) throw new InvalidOperationException("Nested critical production save.");
            if (WorkerProductionWorldSafety.IsFaulted) throw new InvalidOperationException("The production world is faulted.");
            if (!IsExtended) return null;
            if (!StateReady || !System.Threading.Monitor.IsEntered(engine))
                throw new InvalidOperationException("Production capture requires the available service and engine monitor.");
            var bytes = new byte[HeaderSize + WorkerProductionStateStore.RowCapacity * WorkerProductionStateStore.RowSize];
            bytes[0] = (byte)'W'; bytes[1] = (byte)'P'; bytes[2] = (byte)'S'; bytes[3] = (byte)'1';
            WriteU32(bytes, 4, WireSchema); WriteU32(bytes, 8, WorkerProductionStateStore.RowCapacity); WriteU32(bytes, 12, WorkerProductionStateStore.RowSize);
            for (int id = 1; id < WorkerProductionStateStore.RowCapacity; id++)
            {
                var row = LiveRow(id);
                fixed (byte* target = &bytes[HeaderSize + id * WorkerProductionStateStore.RowSize]) *(WorkerProductionStateRow*)target = row;
            }
            frozenCaptured = false; frozenPayload = bytes; return bytes;
        }
        internal static void EndSaveCapture() { frozenPayload = null; frozenCaptured = false; }
        internal static void RequireCurrentCaptureWritten()
        {
            if (frozenPayload == null || !frozenCaptured || saveHandle == null ||
                saveHandle.LastWriteStatus != VersionedSaveWriteStatus.Written || saveHandle.KeepsLoadedRecord)
                throw new InvalidDataException("The current production save capture did not complete successfully.");
        }
        private static bool Capture(VersionedSaveWriter writer)
        {
            if (!IsExtended) return true; // An explicit empty record retires previous sidecar data.
            if (frozenPayload == null) throw new InvalidOperationException("Production save did not enter its critical transaction.");
            writer.Write(EntryName, frozenPayload); frozenCaptured = true; return true;
        }

        internal static WorkerProductionStateRequirement InspectRequirement(string path) => InspectRequirement(path, out _);
        private static WorkerProductionStateRequirement InspectRequirement(string path, out byte[] payload)
        {
            payload = null;
            try
            {
                if (!MapArchive.TryLoad(path, out MapArchive archive))
                {
                    if (ZipUtil.FindEocdHeaderIndex(path) >= 0 || ZipUtil.FindFirstZipHeaderIndex(path) >= 0)
                        return new WorkerProductionStateRequirement(false, true, WireSchema, "", "The mod archive is damaged.");
                    return Legacy;
                }
                using (archive)
                {
                    byte[] record = archive.TryReadBinaryFile("_SE_ModData_" + RecordIdentifier + ".msgpack", true);
                    if (record == null) return Legacy;
                    var decoded = VersionedSaveRecordCodec.Decode(record, WireSchema);
                    if (decoded.Status != VersionedSaveRecordCodec.DecodeStatus.Valid)
                        return new WorkerProductionStateRequirement(false, true, WireSchema, "", decoded.Detail);
                    if (!decoded.Entries.TryGet(EntryName, out byte[] bytes)) return Legacy;
                    var requirement = ValidatePayload(bytes);
                    if (requirement.IsValid) payload = bytes;
                    return requirement;
                }
            }
            catch (Exception error) { return new WorkerProductionStateRequirement(false, true, WireSchema, "", error.Message); }
        }
        internal static WorkerProductionStateRequirement InspectSelectedSaveRequirement(string path) => InspectRequirement(path);
        internal static ulong[] InspectSelectedSaveDenominators(string path)
        {
            // A selected file is rare admission work, not a tick path. Re-read rather than
            // trust timestamp/length caches: an equal-size replacement must not reuse credits.
            var requirement = InspectRequirement(path, out byte[] payload);
            if (!requirement.IsValid) throw new InvalidDataException(requirement.Diagnostic);
            if (payload == null) return Array.Empty<ulong>();
            var denominators = new SortedSet<ulong>();
            for (int id = 1; id < WorkerProductionStateStore.RowCapacity; id++)
            {
                var row = ReadRow(payload, HeaderSize + id * WorkerProductionStateStore.RowSize);
                if (!row.IsEmpty) denominators.Add(row.Denominator);
            }
            var result = new ulong[denominators.Count]; denominators.CopyTo(result); return result;
        }
        private static WorkerProductionStateRequirement Requirement(VersionedSaveLoad load, out byte[] payload)
        {
            payload = null;
            if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope) return Legacy;
            if (load.Status != VersionedSaveLoadStatus.Restored)
                return new WorkerProductionStateRequirement(false, true, WireSchema, "", "Production record cannot be read: " + load.Status);
            if (!load.Entries.TryGetValue(EntryName, out byte[] bytes)) return Legacy;
            var requirement = ValidatePayload(bytes);
            if (requirement.IsValid) payload = (byte[])bytes.Clone();
            return requirement;
        }
        internal static WorkerProductionStateRequirement ValidatePayload(byte[] bytes)
        {
            if (bytes == null || bytes.Length != HeaderSize + WorkerProductionStateStore.RowCapacity * WorkerProductionStateStore.RowSize ||
                bytes[0] != 'W' || bytes[1] != 'P' || bytes[2] != 'S' || bytes[3] != '1' ||
                ReadU32(bytes, 4) != WireSchema || ReadU32(bytes, 8) != WorkerProductionStateStore.RowCapacity ||
                ReadU32(bytes, 12) != WorkerProductionStateStore.RowSize)
                return new WorkerProductionStateRequirement(false, true, WireSchema, "", "Production state header or length differs.");
            bool hasData = false;
            for (int id = 0; id < WorkerProductionStateStore.RowCapacity; id++)
            {
                var row = ReadRow(bytes, HeaderSize + id * WorkerProductionStateStore.RowSize);
                if ((id == 0 && !row.IsEmpty) || !WorkerProductionStateStore.Validate(in row, out string reason))
                    return new WorkerProductionStateRequirement(false, true, WireSchema, "", "Invalid production row " + id + ".");
                hasData |= !row.IsEmpty;
            }
            using (var hash = SHA256.Create())
                return new WorkerProductionStateRequirement(true, hasData, WireSchema,
                    BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""), "");
        }
        internal static WorkerProductionStateRow ReadRow(byte[] bytes, int offset)
        { fixed (byte* pointer = &bytes[offset]) return *(WorkerProductionStateRow*)pointer; }
        internal static uint ReadU32(byte[] bytes, int offset) => (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);
        internal static void WriteU32(byte[] bytes, int offset, uint value)
        { for (int n = 0; n < 4; n++) bytes[offset + n] = (byte)(value >> (8 * n)); }
    }
}

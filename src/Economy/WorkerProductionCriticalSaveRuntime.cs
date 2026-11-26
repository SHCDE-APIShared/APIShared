using System;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Savegames;
using APIShared.NativeSites;
using APIShared.PlayerControl;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.Components.Archive;
using SHCDESE.API.LowLevel;

namespace APIShared.Economy
{
    // All three native save callers (normal/editor, synchronized Chore39 and trail
    // restart-info) enter the sole PlayerViewSaveScope writer. This transaction runs
    // inside its engine/participant-identity scope; SE logical SaveContext is untouched.
    internal static unsafe class WorkerProductionCriticalSaveRuntime
    {
        internal const string SiteGroup = "Economy.WorkerProductionCriticalSave";
        internal const int PathSlotRva = 0x31C74B6, SelectorRva = 0x30D1EE0, PathCapacity = 1001;
        internal const int WriteRva = 0x1DDB94, CloseRva = 0x1DBB20;
        internal static readonly byte[] IoEntry = WorkerProductionBonusNativeDefinition.Hex("48895C240848897C241055488BEC4883EC60");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WriteCall(int descriptor, IntPtr buffer, uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CloseCall(int descriptor);
        private static readonly WriteCall WriteCallback = OnWrite;
        private static readonly CloseCall CloseCallback = OnClose;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<WriteCall> write;
        private static DetourHandle<CloseCall> close;
        private static HookTransaction transaction;
        private static IntPtr module;
        private static ManualLogSource log;
        private static readonly Action endProductionCapture = EndProductionCapture;
        private static readonly Action endWorkshopCapture = EndWorkshopCapture;
        private static readonly Action endStorageCapture = EndStorageCapture;
        [ThreadStatic] private static long sequence;
        [ThreadStatic] private static bool lastFailed;
        [ThreadStatic] private static bool saving;
        [ThreadStatic] private static bool nativeIoActive;
        [ThreadStatic] private static WorkerProductionSaveIoState nativeIo;
        internal static bool IsAvailable => publication.IsReady && PlayerViewSaveScope.IsReady;
        internal static long OutcomeToken => sequence;
        internal static bool FailedSince(long token) => sequence != token && lastFailed;
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(WriteRva, 10, NativeSiteMode.Exclusive),
            new NativeSiteSpan(WriteRva + 10, IoEntry.Length - 10, NativeSiteMode.Observe),
            new NativeSiteSpan(CloseRva, 10, NativeSiteMode.Exclusive),
            new NativeSiteSpan(CloseRva + 10, IoEntry.Length - 10, NativeSiteMode.Observe)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            publication.ThrowIfUnavailable(); if (publication.IsReady) return;
            if (!string.Equals(hash, WorkerProductionBonusNativeDefinition.ReferenceHash, StringComparison.OrdinalIgnoreCase) ||
                !context.Memory.Slice(WriteRva, IoEntry.Length).SequenceEqual(IoEntry) ||
                !context.Memory.Slice(CloseRva, IoEntry.Length).SequenceEqual(IoEntry))
                throw new InvalidOperationException("Critical production save writer contract differs.");
            if (!PlayerViewSaveScope.IsReady)
                throw new InvalidOperationException("Critical production saves require the published shared participant-identity writer scope.");
            module = context.ModuleHandle; log = logger;
            NativeIndirectEntry.VerifyBytes(module + WriteRva, IoEntry);
            NativeIndirectEntry.VerifyBytes(module + CloseRva, IoEntry);
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup, Spans(), m => new InvalidOperationException(m));
            try
            {
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions {
                            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false })) });
                write = new DetourHandle<WriteCall>(); close = new DetourHandle<CloseCall>();
                transaction.AddDetour(write, HookTarget.FromAddress(unchecked((ulong)(module + WriteRva).ToInt64())), WriteCallback);
                transaction.AddDetour(close, HookTarget.FromAddress(unchecked((ulong)(module + CloseRva).ToInt64())), CloseCallback);
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !write.Success || !close.Success)
                    throw new InvalidOperationException("Critical save I/O observers could not prepare.");
                ValidateInstalled(false);
                NativeHookPublication.Enable(transaction, result, publication, typeof(WorkerProductionCriticalSaveRuntime),
                    () => ValidateInstalled(true));
                NativeApiLog.Info(log, "PRODUCTION_SAVE_READY: shared participant-identity writer52B0; frozen snapshot, checked native writes/close, verified sibling temporary file, atomic replacement; legacy saves retain their original path.");
            }
            catch
            {
                publication.RollbackUnpublished(transaction, () => ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup));
                throw;
            }
        }

        private static void ValidateInstalled(bool installed)
        {
            NativeIndirectEntry.Validate(write.Hook, module + WriteRva, IoEntry, installed);
            NativeIndirectEntry.Validate(close.Hook, module + CloseRva, IoEntry, installed);
        }
        private static int OnWrite(int descriptor, IntPtr buffer, uint count)
        {
            int result = write.Original(descriptor, buffer, count);
            if (nativeIoActive) nativeIo.ObserveWrite(descriptor, count, result);
            return result;
        }
        private static int OnClose(int descriptor)
        {
            int result = close.Original(descriptor);
            if (nativeIoActive) nativeIo.ObserveClose(descriptor, result);
            return result;
        }

        // Called only by PlayerViewSaveScope's protected writer. Preserve its exact
        // ulong native return ABI and never install another52B0 detour.
        internal static ulong RunWriter(IntPtr archive, IntPtr sections, byte map, PlayerViewSaveScope.WriterCall original)
        {
            // A nested writer must not bypass an active transaction merely because its
            // own map/save scope has no required records. Preserve the outer path/I/O proof.
            if (saving) return Failed("Nested protected save rejected; the previous save remains untouched.");
            bool requiredRecords = VersionedSaveDataRuntime.RequiresAtomicWrite(map != 0);
            bool externalCargo = NativeStorageRuntime.Instance.HasExternalCargoSaveDemand;
            if (!IsAvailable)
            {
                if (!externalCargo && !requiredRecords) return original(archive, sections, map);
                if (!externalCargo) return Failed("Required atomic save support is unavailable.");
                sequence++; lastFailed = false;
                return Failed("Cargo saving requires the available verified critical writer.");
            }
            var requirements = new WorkerProductionSaveRequirements(WorkerProductionStateRuntime.IsExtended,
                WorkerProductionWorldSafety.IsFaulted, WeaponProductionRuntime.HasStoredOutputDemand, externalCargo,
                requiredRecords);
            sequence++; lastFailed = false;
            if (!requirements.Transaction) return original(archive, sections, map);
            return RunTransaction(archive, sections, map, original, requirements);
        }

        // Keep closure construction wholly behind the existing no-demand guards.
        // The protected writer still owns this single transaction and native ABI.
        private static ulong RunTransaction(IntPtr archive, IntPtr sections, byte map,
            PlayerViewSaveScope.WriterCall original, WorkerProductionSaveRequirements requirements)
        {
            string temporary = null;
            byte[] oldPath = null;
            int selector = 0;
            bool swapped = false;
            byte[] payload = null;
            bool committed = false;
            ulong actualWriterResult = 0;
            VersionedSaveDataRegistry.AtomicSnapshot required = null;
            Exception primary = null;
            WorkerProductionSaveCleanupReport cleanup = default;
            object protectedWorld = MissionLifecycleService.NativeWorld;
            long protectedGeneration = WorkerProductionWorldSafety.Generation;
            Action restore = () =>
            {
                if (!swapped) return;
                Marshal.Copy(oldPath, 0, module + PathSlotRva, oldPath.Length);
                Marshal.WriteInt32(module + SelectorRva, selector);
            };
            saving = true;
            try
            {
                try
                {
                    object engineMonitor = Internal.EngineMonitor.Current;
                    if (engineMonitor == null || !System.Threading.Monitor.IsEntered(engineMonitor))
                        throw new InvalidOperationException("Critical saving requires the actual engine monitor.");
                    // A fault is rejected before capture or ANY native file open/truncation.
                    if (requirements.Faulted) throw new InvalidOperationException("The production world is faulted.");
                    if (requirements.Production)
                    {
                        payload = WorkerProductionStateRuntime.FreezeForSave();
                        if (payload == null) throw new InvalidOperationException("The extended world has no production snapshot.");
                    }
                    if (requirements.Workshops) WeaponProductionRuntime.FreezeSave();
                    if (requirements.Storage) NativeStorageRuntime.Instance.FreezeProductionSave();
                    selector = Marshal.ReadInt32(module + SelectorRva);
                    if (selector != 1) throw new InvalidDataException("The native save path selector differs from the audited save callers.");
                    var chars = new ReadOnlySpan<char>((void*)(module + PathSlotRva), PathCapacity);
                    int end = chars.IndexOf('\0');
                    if (end <= 0) throw new InvalidDataException("The native destination path is missing or unterminated.");
                    string destination = Path.GetFullPath(new string(chars.Slice(0, end).ToArray()));
                    if (requirements.AtomicRecords)
                    {
                        required = VersionedSaveDataRuntime.BeginAtomicSave(map != 0, destination);
                        if (required == null) throw new InvalidOperationException("The required atomic record registry is unavailable.");
                    }
                    string folder = Path.GetDirectoryName(destination);
                    temporary = Path.Combine(folder, ".wp-" + Guid.NewGuid().ToString("N") + ".tmp");
                    if (temporary.Length >= PathCapacity) throw new PathTooLongException("The native temporary save path exceeds its UTF-16 slot.");
                    if (File.Exists(temporary)) throw new IOException("Temporary production save path already exists.");
                    oldPath = new byte[PathCapacity * 2]; Marshal.Copy(module + PathSlotRva, oldPath, 0, oldPath.Length);
                    var target = new Span<char>((void*)(module + PathSlotRva), PathCapacity);
                    target.Clear(); temporary.AsSpan().CopyTo(target); swapped = true;
                    // All46 native writes and the SE ZIP append call1DDB94. The first write
                    // is this52B0 invocation's four-byte header, after its sole successful
                    // file open; helpers before it do not write files. Bind that descriptor
                    // and ignore unrelated descriptors used by foreign save callbacks.
                    nativeIo = default;
                    ulong result;
                    nativeIoActive = true;
                    try { result = original(archive, sections, map); }
                    finally { nativeIoActive = false; }
                    actualWriterResult = result;
                    if (result != 1 || lastFailed) throw new IOException("Native saving returned failure or attempted a nested save.");
                    if (requirements.Production) WorkerProductionStateRuntime.RequireCurrentCaptureWritten();
                    VersionedSaveDataRegistry.RequireAtomicDelivered(required);
                    if (requirements.Workshops) WeaponProductionRuntime.VerifySaveWritten(temporary);
                    if (requirements.Storage) NativeStorageRuntime.Instance.VerifyProductionSaveWritten(temporary);
                    // MoveFileExW replacement commits only the verified complete file. Failed capture,
                    // omitted/stale archive records and append errors leave the old destination intact.
                    CommitSavedFile(temporary, destination, payload, in nativeIo, requirements, required);
                    committed = true;
                    temporary = null;
                }
                catch (Exception error)
                {
                    primary = error;
                    Failed((requirements.Production || requirements.Storage ? "Production save failed: " :
                        "Required simulation save failed: ") + DescribeCleanupError(error));
                }
                finally
                {
                    cleanup = CompleteSaveCleanup(actualWriterResult, committed, primary, restore,
                        requirements.Production ? endProductionCapture : null,
                        requirements.Workshops ? endWorkshopCapture : null,
                        required == null && !requirements.Storage ? null : (Action)(() =>
                        {
                            try { VersionedSaveDataRuntime.EndAtomicSave(required); }
                            finally { if (requirements.Storage) EndStorageCapture(); }
                        }));
                    if (temporary != null)
                        try { if (File.Exists(temporary)) File.Delete(temporary); }
                        catch (Exception error)
                        {
                            // Failure is already returned to every caller; additionally make a retained
                            // temporary file explicit instead of pretending cleanup succeeded.
                            try { NativeApiLog.Error(log, "Failed-save temporary file retained: " + temporary + "; " + error.Message); } catch { }
                        }
                }
                if (cleanup.CleanupFailed) ReportCleanupProtectionFailure(cleanup, protectedWorld, protectedGeneration);
                if (committed)
                {
                    // Fault notifications may synchronously call consumers. This
                    // completing transaction still committed its verified file.
                    lastFailed = false;
                    return cleanup.ReturnValue;
                }
                if (primary != null) { lastFailed = true; return 0; }
                return Failed("Production save failed: " + DescribeCleanupError(cleanup.Primary));
            }
            finally { saving = false; }
        }
        private static void EndProductionCapture() => WorkerProductionStateRuntime.EndSaveCapture();
        private static void EndWorkshopCapture() => WeaponProductionRuntime.EndSaveCapture();
        private static void EndStorageCapture() => NativeStorageRuntime.Instance.EndProductionSave();

        // This is the actual transaction's cleanup entry, also exercised by the
        // maintained cargo-save fixture. It calls no native APIs by itself.
        internal static WorkerProductionSaveCleanupReport CompleteSaveCleanup(ulong actualWriterResult, bool committed,
            Exception primary, Action restore, Action production, Action workshops, Action storage)
        {
            Exception restoreError = null, productionError = null, workshopError = null, storageError = null;
            try { restore?.Invoke(); } catch (Exception error) { restoreError = error; }
            try { production?.Invoke(); } catch (Exception error) { productionError = error; }
            try { workshops?.Invoke(); } catch (Exception error) { workshopError = error; }
            try { storage?.Invoke(); } catch (Exception error) { storageError = error; }
            return new WorkerProductionSaveCleanupReport(actualWriterResult, committed,
                primary ?? restoreError ?? productionError ?? workshopError ?? storageError,
                restoreError, productionError, workshopError, storageError);
        }

        private static void ReportCleanupProtectionFailure(WorkerProductionSaveCleanupReport report,
            object protectedWorld, long protectedGeneration)
        {
            // An archive may already be committed. Do not set lastFailed and
            // make UI/publication hooks pretend that write never happened.
            string detail = report.Committed ? "Critical save committed; protection cleanup failed." :
                "Critical save did not commit; protection cleanup failed.";
            try { detail = "Critical save cleanup protection failed; committed=" + report.Committed +
                "; primary=" + DescribeCleanupError(report.Primary) +
                "; restore=" + DescribeCleanupError(report.RestoreError) +
                "; production=" + DescribeCleanupError(report.ProductionError) +
                "; workshops=" + DescribeCleanupError(report.WorkshopError) +
                "; storage=" + DescribeCleanupError(report.StorageError); } catch { }
            try { NativeApiLog.Error(log, detail); } catch { }
            try
            {
                // A reentrant load must not charge the old save's cleanup
                // failure to a replacement world or admission generation.
                if (protectedWorld != null && ReferenceEquals(protectedWorld, MissionLifecycleService.NativeWorld) &&
                    protectedGeneration == WorkerProductionWorldSafety.Generation)
                    WorkerProductionWorldSafety.Fault(detail);
            }
            catch (Exception error)
            { try { NativeApiLog.Error(log, "Critical save cleanup fault reporting failed: " + DescribeCleanupError(error)); } catch { } }
        }

        private static string DescribeCleanupError(Exception error)
        {
            if (error == null) return "none";
            try
            {
                string kind = error.GetType().Name ?? "Exception";
                string message = error.Message ?? string.Empty;
                if (kind.Length > 128) kind = kind.Substring(0, 128);
                if (message.Length > 382) message = message.Substring(0, 382);
                return kind + ": " + message;
            }
            catch { return "exception diagnostics unavailable"; }
        }

        private static ulong Failed(string reason)
        {
            lastFailed = true;
            try { WorkerProductionSaveUiRuntime.ReportFailure(reason); } catch { }
            try { NativeApiLog.Error(log, reason); } catch { }
            return 0;
        }
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io)
            => CommitSavedFile(temporary, destination, payload, in io, new WorkerProductionSaveRequirements(true, false, false, false));

        // Preserve the upstream atomic-record test/verification entry without changing
        // the existing four-argument production-payload contract.
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            VersionedSaveDataRegistry.AtomicSnapshot required)
            => CommitSavedFile(temporary, destination, payload, in io,
                new WorkerProductionSaveRequirements(payload != null, false, false, false, required != null), required);

        // Cargo-only worlds intentionally have no extended worker-production sidecar. Storage
        // verification has already checked its frozen pair; require a production payload only
        // when that source participated in this transaction. Never synthesize an empty record.
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            WorkerProductionSaveRequirements requirements, VersionedSaveDataRegistry.AtomicSnapshot required = null)
        {
            if (!io.IsComplete) throw new IOException("A native save write was incomplete or the save file could not close successfully.");
            if (!requirements.Transaction || requirements.Faulted) throw new InvalidDataException("No valid frozen save sources authorize file replacement.");
            if (requirements.Production)
            {
                if (payload == null) throw new InvalidDataException("The required production checkpoint is missing.");
                VerifySavedPayload(temporary, payload);
            }
            else if (payload != null) throw new InvalidDataException("An unexpected production payload cannot label a cargo-only checkpoint.");
            VersionedSaveDataRegistry.RequireAtomicDelivered(required);
            VerifyRequiredRecords(temporary, required);
            Internal.AtomicFileReplacement.Replace(temporary, destination);
        }
        internal static void VerifyRequiredRecords(string path, VersionedSaveDataRegistry.AtomicSnapshot required)
        {
            if (required == null || required.Records.Count == 0) return;
            if (!MapArchive.TryLoad(path, out MapArchive archive))
                throw new InvalidDataException("The required mod archive is missing or damaged.");
            using (archive)
                foreach (var record in required.Records)
                {
                    byte[] actual = archive.TryReadBinaryFile("_SE_ModData_" + record.Key + ".msgpack", true);
                    if (actual == null || !actual.AsSpan().SequenceEqual(record.Value))
                        throw new InvalidDataException("The required save record is missing or stale: " + record.Key);
                }
        }
        internal static void VerifySavedPayload(string path, byte[] expected)
        {
            if (!MapArchive.TryLoad(path, out MapArchive archive)) throw new InvalidDataException("The saved mod archive is missing or damaged.");
            using (archive)
            {
                byte[] record = archive.TryReadBinaryFile("_SE_ModData_" + WorkerProductionStateRuntime.RecordIdentifier + ".msgpack", true);
                if (record == null) throw new InvalidDataException("The required production record was omitted.");
                var decoded = VersionedSaveRecordCodec.Decode(record, WorkerProductionStateRuntime.WireSchema);
                if (decoded.Status != VersionedSaveRecordCodec.DecodeStatus.Valid ||
                    !decoded.Entries.TryGet(WorkerProductionStateRuntime.EntryName, out byte[] actual) || !actual.AsSpan().SequenceEqual(expected))
                    throw new InvalidDataException("The production record was not written from the current snapshot.");
            }
        }
    }

    // Source selection is frozen at entry. External cargo, including passive unknown/staged
    // records and terminal history, needs the same verified native file transaction even when
    // the independent extended worker-production source is absent.
    // Distinguish a committed file/result from later protection cleanup faults.
    // Exceptions are retained exactly, without wrapping or secondary masking.
    internal readonly struct WorkerProductionSaveCleanupReport
    {
        internal readonly ulong ActualWriterResult;
        internal readonly bool Committed;
        internal readonly Exception Primary, RestoreError, ProductionError, WorkshopError, StorageError;
        internal WorkerProductionSaveCleanupReport(ulong actual, bool committed, Exception primary,
            Exception restore, Exception production, Exception workshop, Exception storage)
        {
            ActualWriterResult = actual; Committed = committed; Primary = primary;
            RestoreError = restore; ProductionError = production; WorkshopError = workshop; StorageError = storage;
        }
        internal bool CleanupFailed => RestoreError != null || ProductionError != null || WorkshopError != null || StorageError != null;
        internal ulong ReturnValue => Committed ? ActualWriterResult : 0;
    }

    internal readonly struct WorkerProductionSaveRequirements
    {
        internal readonly bool Production, Workshops, Storage, Faulted, AtomicRecords;
        internal bool Transaction => Production || Storage || Faulted || AtomicRecords;
        internal WorkerProductionSaveRequirements(bool extended, bool faulted, bool storedOutput, bool externalCargo)
            : this(extended, faulted, storedOutput, externalCargo, false) { }
        internal WorkerProductionSaveRequirements(bool extended, bool faulted, bool storedOutput, bool externalCargo,
            bool atomicRecords)
        {
            AtomicRecords = atomicRecords;
            Production = extended; Workshops = extended || storedOutput || atomicRecords;
            Storage = storedOutput || externalCargo; Faulted = faulted;
        }
    }

    // Only the critical save thread feeds this allocation-free observer. Failure is sticky:
    // a later successful ZIP append must never conceal an earlier short native write.
    internal struct WorkerProductionSaveIoState
    {
        private int descriptor;
        private bool bound, closed, failed;
        internal bool IsComplete => bound && closed && !failed;
        internal void ObserveWrite(int file, uint requested, int actual)
        {
            if (!bound) { descriptor = file; bound = true; }
            if (file != descriptor) return;
            if (closed || actual < 0 || unchecked((uint)actual) != requested) failed = true;
        }
        internal void ObserveClose(int file, int result)
        {
            if (!bound || file != descriptor) return;
            if (closed || result != 0) failed = true;
            closed = true;
        }
    }
}

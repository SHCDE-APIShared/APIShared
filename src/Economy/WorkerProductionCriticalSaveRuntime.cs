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
        private static PreCaptureSaveContextReader preCaptureContext;
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
            // Resolve optional managed metadata once, never on a native save path.
            preCaptureContext = PreCaptureSaveContextReader.Create(out _);
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
            bool externalCargo = NativeStorageRuntime.Instance.HasExternalCargoSaveDemand ||
                NativeStorageRuntime.Instance.HasUnreadyArchiveSaveDemand;
            bool criticalCohort = VersionedSaveDataRuntime.HasCriticalWriterDemand;
            if (!IsAvailable)
            {
                if (!externalCargo && !criticalCohort) return original(archive, sections, map);
                sequence++; lastFailed = false;
                return Failed("Enrolled storage/cargo saving requires the available verified critical writer.");
            }
            var requirements = new WorkerProductionSaveRequirements(WorkerProductionStateRuntime.IsExtended,
                WorkerProductionWorldSafety.IsFaulted, WeaponProductionRuntime.HasStoredOutputDemand, externalCargo,
                criticalCohort, criticalCohort && NativeStorageRuntime.Instance.HasCriticalWriterEnrollment);
            sequence++; lastFailed = false;
            if (saving) return Failed("Nested production save rejected; the previous save remains untouched.");
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
            // Retain the exact optional owner across Original. Looking up TLS
            // only afterwards would let a callback-pop hide a failed frame.
            VersionedSaveFactoryFrame factoryFrame = VersionedSaveFactoryFrame.Current;
            CriticalSavePreparation participants = null;
            bool ownsFactoryFrame = false;
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
                    if (!System.Threading.Monitor.IsEntered(WorkerProductionWorldSafety.EngineGate))
                        throw new InvalidOperationException("Critical saving requires the actual engine monitor.");
                    // A fault is rejected before capture or ANY native file open/truncation.
                    if (requirements.Faulted) throw new InvalidOperationException("The production world is faulted.");
                    if (requirements.CriticalCohort)
                    {
                        CriticalSaveRuntime.RequireCurrentSaveWorld(protectedWorld, protectedGeneration);
                        if (preCaptureContext == null) throw new InvalidOperationException("The pre-capture reader is unavailable.");
                        if (!preCaptureContext.TryReadSnapshot(out var logical, out _, out string contextReason))
                            throw new InvalidOperationException(contextReason);
                        factoryFrame = VersionedSaveDataRuntime.BeginCriticalWriterFrame(logical);
                        ownsFactoryFrame = true;
                        participants = VersionedSaveDataRuntime.BeginParticipantPreparation(logical);
                    }
                    if (requirements.Production)
                    {
                        payload = WorkerProductionStateRuntime.FreezeForSave();
                        if (payload == null) throw new InvalidOperationException("The extended world has no production snapshot.");
                    }
                    if (requirements.Workshops) WeaponProductionRuntime.FreezeSave();
                    if (requirements.Storage) NativeStorageRuntime.Instance.FreezeProductionSave(requirements.CriticalCohort);
                    if (requirements.CriticalCohort)
                    {
                        CriticalSaveRuntime.RequireCurrentSaveWorld(protectedWorld, protectedGeneration);
                        if (participants != null && !participants.Prepare())
                            throw new InvalidOperationException("A critical participant refused preparation.");
                        participants?.RequireCurrent();
                        CriticalSaveRuntime.RequireCurrentSaveWorld(protectedWorld, protectedGeneration, true);
                        // Reentrant factory dispatch while freezing is an earlier
                        // failure, never permission to enter Original/open a file.
                        factoryFrame.CompleteOwnedPreparation();
                    }
                    selector = Marshal.ReadInt32(module + SelectorRva);
                    if (selector != 1) throw new InvalidDataException("The native save path selector differs from the audited save callers.");
                    var chars = new ReadOnlySpan<char>((void*)(module + PathSlotRva), PathCapacity);
                    int end = chars.IndexOf('\0');
                    if (end <= 0) throw new InvalidDataException("The native destination path is missing or unterminated.");
                    string destination = Path.GetFullPath(new string(chars.Slice(0, end).ToArray()));
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
                    if (requirements.Workshops) WeaponProductionRuntime.VerifySaveWritten(temporary);
                    if (requirements.Storage) NativeStorageRuntime.Instance.VerifyProductionSaveWritten(temporary);
                    // MoveFileExW replacement commits only the verified complete file. Failed capture,
                    // omitted/stale archive records and append errors leave the old destination intact.
                    CommitSavedFile(temporary, destination, payload, in nativeIo, requirements, factoryFrame,
                        protectedWorld, protectedGeneration, participants);
                    committed = true;
                    temporary = null;
                }
                catch (Exception error)
                {
                    if (ownsFactoryFrame)
                    {
                        factoryFrame.RecordFailure(error);
                        primary = factoryFrame.Failure;
                    }
                    else primary = error;
                    Failed("Production save failed: " + DescribeCleanupError(primary));
                }
                finally
                {
                    cleanup = CompleteSaveCleanup(actualWriterResult, committed, primary, restore,
                        requirements.Production ? endProductionCapture : null,
                        requirements.Workshops ? endWorkshopCapture : null,
                        requirements.Storage ? endStorageCapture : null,
                        ownsFactoryFrame ? (Action)factoryFrame.Dispose : null,
                        participants == null ? null : (Action)participants.Dispose);
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
            Exception primary, Action restore, Action production, Action workshops, Action storage) =>
            CompleteSaveCleanup(actualWriterResult, committed, primary, restore, production, workshops, storage, null);

        internal static WorkerProductionSaveCleanupReport CompleteSaveCleanup(ulong actualWriterResult, bool committed,
            Exception primary, Action restore, Action production, Action workshops, Action storage, Action frame)
            => CompleteSaveCleanup(actualWriterResult, committed, primary, restore, production, workshops, storage, frame, null);

        internal static WorkerProductionSaveCleanupReport CompleteSaveCleanup(ulong actualWriterResult, bool committed,
            Exception primary, Action restore, Action production, Action workshops, Action storage, Action frame, Action participants)
        {
            Exception restoreError = null, productionError = null, workshopError = null, storageError = null, frameError = null, participantError = null;
            try { restore?.Invoke(); } catch (Exception error) { restoreError = error; }
            try { participants?.Invoke(); } catch (Exception error) { participantError = error; }
            try { production?.Invoke(); } catch (Exception error) { productionError = error; }
            try { workshops?.Invoke(); } catch (Exception error) { workshopError = error; }
            try { storage?.Invoke(); } catch (Exception error) { storageError = error; }
            // Frame release is independent of every earlier protection cleanup.
            try { frame?.Invoke(); } catch (Exception error) { frameError = error; }
            return new WorkerProductionSaveCleanupReport(actualWriterResult, committed,
                primary ?? restoreError ?? participantError ?? productionError ?? workshopError ?? storageError ?? frameError,
                restoreError, productionError, workshopError, storageError, frameError, participantError);
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
                "; storage=" + DescribeCleanupError(report.StorageError) +
                "; participants=" + DescribeCleanupError(report.ParticipantError) +
                "; factoryFrame=" + DescribeCleanupError(report.FactoryFrameError); } catch { }
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

        // Cargo-only worlds intentionally have no extended worker-production sidecar. Storage
        // verification has already checked its frozen pair; require a production payload only
        // when that source participated in this transaction. Never synthesize an empty record.
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            WorkerProductionSaveRequirements requirements)
            => CommitSavedFile(temporary, destination, payload, in io, requirements, VersionedSaveFactoryFrame.Current);

        // An enrolling owner must retain/pass its frame from before Original;
        // a later TLS lookup alone cannot detect an early close or thread hop.
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            WorkerProductionSaveRequirements requirements, VersionedSaveFactoryFrame factoryFrame)
            => CommitSavedFileCore(temporary, destination, payload, in io, requirements, factoryFrame, false, null, 0, null);

        // Production retains its pre-Original owner through the sink. Archive
        // bytes alone cannot authorize replacement after callback-driven world
        // invalidation which occurred before the archive provider saw a new load.
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            WorkerProductionSaveRequirements requirements, VersionedSaveFactoryFrame factoryFrame,
            object world, long generation, CriticalSavePreparation participants)
            => CommitSavedFileCore(temporary, destination, payload, in io, requirements, factoryFrame,
                requirements.CriticalCohort, world, generation, participants);

        private static void CommitSavedFileCore(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io,
            WorkerProductionSaveRequirements requirements, VersionedSaveFactoryFrame factoryFrame,
            bool validateOwner, object world, long generation, CriticalSavePreparation participants)
        {
            if (!io.IsComplete) throw new IOException("A native save write was incomplete or the save file could not close successfully.");
            if (!requirements.Transaction || requirements.Faulted) throw new InvalidDataException("No valid frozen save sources authorize file replacement.");
            if (requirements.Production)
            {
                if (payload == null) throw new InvalidDataException("The required production checkpoint is missing.");
                VerifySavedPayload(temporary, payload);
            }
            else if (payload != null) throw new InvalidDataException("An unexpected production payload cannot label a cargo-only checkpoint.");
            VerifyFactoryCapture(temporary, factoryFrame);
            if (validateOwner)
            {
                participants?.RequireCurrent();
                CriticalSaveRuntime.RequireCurrentSaveWorld(world, generation, true);
            }
            Internal.AtomicFileReplacement.Replace(temporary, destination);
        }

        // Explicit cohort demand opens the owning frame; absent enrollment this
        // verifier preserves the existing frame-free path.
        // Missing dispatch and SE-swallowed failures must refuse replacement;
        // a successful native return alone cannot certify the frozen records.
        internal static void VerifyFactoryCapture(string temporary, VersionedSaveFactoryFrame frame)
        {
            var current = VersionedSaveFactoryFrame.Current;
            if (!ReferenceEquals(frame, current))
            {
                // Includes expected=null/current!=null: a frame opened during
                // Original cannot masquerade as the dormant no-owner path.
                Exception error = frame?.Failure ?? current?.Failure ??
                    new InvalidOperationException("The retained critical factory frame does not match the current save scope.");
                frame?.RecordFailure(error); current?.RecordFailure(error);
                throw error;
            }
            if (frame == null) return;
            try
            {
                if (!frame.TryGetWrittenCapture(out var capture, out string reason))
                    throw frame.Failure ?? new InvalidDataException(reason);
                if (!MapArchive.TryLoad(temporary, out MapArchive archive))
                    throw new InvalidDataException("The critical factory archive is missing or unreadable.");
                using (archive)
                    foreach (var record in capture.Records)
                    {
                        string identifier = record.Source.PreparedSource.Handle.SaveDataIdentifier;
                        byte[] bytes = archive.TryReadBinaryFile("_SE_ModData_" + identifier + ".msgpack", true);
                        if (!capture.MatchesRecord(record.Source, bytes, out reason))
                            throw new InvalidDataException(reason);
                    }
            }
            catch (Exception error) { frame.RecordFailure(error); throw; }
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
        internal readonly Exception Primary, RestoreError, ProductionError, WorkshopError, StorageError, FactoryFrameError, ParticipantError;
        internal WorkerProductionSaveCleanupReport(ulong actual, bool committed, Exception primary,
            Exception restore, Exception production, Exception workshop, Exception storage, Exception factoryFrame = null, Exception participant = null)
        {
            ActualWriterResult = actual; Committed = committed; Primary = primary;
            RestoreError = restore; ProductionError = production; WorkshopError = workshop; StorageError = storage; FactoryFrameError = factoryFrame;
            ParticipantError = participant;
        }
        internal bool CleanupFailed => RestoreError != null || ParticipantError != null || ProductionError != null || WorkshopError != null || StorageError != null || FactoryFrameError != null;
        internal ulong ReturnValue => Committed ? ActualWriterResult : 0;
    }

    internal readonly struct WorkerProductionSaveRequirements
    {
        internal readonly bool Production, Workshops, Storage, Faulted, CriticalCohort;
        internal bool Transaction => Production || Storage || Faulted || CriticalCohort;
        internal WorkerProductionSaveRequirements(bool extended, bool faulted, bool storedOutput, bool externalCargo)
            : this(extended, faulted, storedOutput, externalCargo, false, false) { }
        internal WorkerProductionSaveRequirements(bool extended, bool faulted, bool storedOutput, bool externalCargo,
            bool criticalCohort, bool criticalStorage)
        {
            Production = extended; Workshops = extended || storedOutput;
            Storage = storedOutput || externalCargo || criticalStorage; Faulted = faulted; CriticalCohort = criticalCohort;
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

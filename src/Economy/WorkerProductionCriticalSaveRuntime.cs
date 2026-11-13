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
            if (!IsAvailable) return original(archive, sections, map);
            sequence++; lastFailed = false;
            if (!WorkerProductionStateRuntime.IsExtended && !WorkerProductionWorldSafety.IsFaulted)
                return original(archive, sections, map);
            if (saving) return Failed("Nested production save rejected; the previous save remains untouched.");
            string temporary = null;
            byte[] oldPath = null;
            int selector = 0;
            bool swapped = false;
            bool storageCheckpoint = WeaponProductionRuntime.HasStoredOutputDemand;
            saving = true;
            try
            {
                if (!System.Threading.Monitor.IsEntered(WorkerProductionWorldSafety.EngineGate))
                    throw new InvalidOperationException("Critical saving requires the actual engine monitor.");
                // A fault is rejected before capture or ANY native file open/truncation.
                byte[] payload = WorkerProductionStateRuntime.FreezeForSave();
                if (payload == null) throw new InvalidOperationException("The extended world has no production snapshot.");
                WeaponProductionRuntime.FreezeSave();
                if (storageCheckpoint) NativeStorageRuntime.Instance.FreezeProductionSave();
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
                if (result != 1 || lastFailed) throw new IOException("Native saving returned failure or attempted a nested save.");
                WorkerProductionStateRuntime.RequireCurrentCaptureWritten();
                WeaponProductionRuntime.VerifySaveWritten(temporary);
                if (storageCheckpoint) NativeStorageRuntime.Instance.VerifyProductionSaveWritten(temporary);
                // MoveFileExW replacement commits only the verified complete file. Failed capture,
                // omitted/stale archive records and append errors leave the old destination intact.
                CommitSavedFile(temporary, destination, payload, in nativeIo);
                temporary = null;
                return 1;
            }
            catch (Exception error) { return Failed("Production save failed: " + error.Message); }
            finally
            {
                if (swapped)
                {
                    Marshal.Copy(oldPath, 0, module + PathSlotRva, oldPath.Length);
                    Marshal.WriteInt32(module + SelectorRva, selector);
                }
                WorkerProductionStateRuntime.EndSaveCapture();
                WeaponProductionRuntime.EndSaveCapture();
                if (storageCheckpoint) NativeStorageRuntime.Instance.EndProductionSave();
                saving = false;
                if (temporary != null)
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception error)
                    {
                        // Failure is already returned to every caller; additionally make a retained
                        // temporary file explicit instead of pretending cleanup succeeded.
                        try { NativeApiLog.Error(log, "Failed-save temporary file retained: " + temporary + "; " + error.Message); } catch { }
                    }
            }
        }
        private static ulong Failed(string reason)
        {
            lastFailed = true;
            try { WorkerProductionSaveUiRuntime.ReportFailure(reason); } catch { }
            try { NativeApiLog.Error(log, reason); } catch { }
            return 0;
        }
        internal static void CommitSavedFile(string temporary, string destination, byte[] payload, in WorkerProductionSaveIoState io)
        {
            if (!io.IsComplete) throw new IOException("A native save write was incomplete or the save file could not close successfully.");
            VerifySavedPayload(temporary, payload);
            Internal.AtomicFileReplacement.Replace(temporary, destination);
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

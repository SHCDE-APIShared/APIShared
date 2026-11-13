using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.PlayerControl
{
    // Common writer entry, covering export, synchronized autosave and Trail saves.
    // This optional service is separate from spectator single-owner input/control.
    // Initialize only in the startup patch window with the runtime-owned guard;
    // never bind an active-human view until this whole publication is ready.
    internal static class PlayerViewSaveScope
    {
        internal const int Rva = 0x52B0, DisplacedLength = 10;
        internal static readonly byte[] Entry = { 0x48, 0x89, 0x5C, 0x24, 0x10,
            0x48, 0x89, 0x74, 0x24, 0x18, 0x55, 0x57 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate ulong WriterCall(IntPtr manager, IntPtr sections, byte mapSave);
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static readonly Func<WriterArguments, ulong> writeOriginal = WriteOriginal;
        private static DetourHandle<WriterCall> handle;
        private static WriterCall cachedWriterCall;
        private static PlayerViewIdentity identity;

        private readonly struct WriterArguments
        {
            internal readonly IntPtr Manager, Sections;
            internal readonly byte MapSave;
            internal readonly WriterCall Original;
            internal WriterArguments(IntPtr manager, IntPtr sections, byte mapSave, WriterCall original)
            { Manager = manager; Sections = sections; MapSave = mapSave; Original = original; }
        }

        internal static bool IsReady => publication.IsReady;
        internal static bool IsBoundTo(PlayerViewIdentity guard) => publication.IsReady && ReferenceEquals(identity, guard);

        internal static void ValidateIdentityBinding(PlayerViewIdentity existing, PlayerViewIdentity requested)
        {
            if (requested == null) throw new ArgumentNullException(nameof(requested));
            if (existing != null && !ReferenceEquals(existing, requested))
                throw new InvalidOperationException("PLAYER_CONTROL_SAVE: the published writer belongs to a different identity core.");
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash,
            PlayerViewIdentity guard, ManualLogSource log)
        {
            publication.ThrowIfUnavailable();
            ValidateIdentityBinding(identity, guard);
            if (publication.IsReady) return;
            if (!NativeStartupPatchWindow.IsOpen)
                throw new InvalidOperationException("PLAYER_CONTROL_SAVE: initialize only in the startup patch window; no late native installation is allowed.");
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            if (guard.ProtectedHumanId != 0)
                throw new InvalidOperationException("PLAYER_CONTROL_SAVE: publish the save guard before acquiring a protected view.");
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("PLAYER_CONTROL_SAVE: unsupported native writer/identity section layout; active-human view leases remain unavailable.");
            for (int i = 0; i < Entry.Length; i++)
                if (context.Memory[Rva + i] != Entry[i])
                    throw new InvalidOperationException("PLAYER_CONTROL_SAVE: pristine writer entry differs at RVA 0x" + (Rva + i).ToString("X"));
            IntPtr target = context.ModuleHandle + Rva;
            NativeSites.NativeIndirectEntry.VerifyBytes(target, Entry);
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.PlayerViewIdentitySite,
                ApiSharedNativeSites.PlayerViewIdentitySpans(), reason => new InvalidOperationException(reason));
            HookTransaction pending = null;
            try
            {
                pending = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                var candidate = new DetourHandle<WriterCall>();
                pending.AddDetour(candidate, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), (WriterCall)OnWriter);
                var result = pending.Commit();
                if (!result.IsCompleteSuccess || !candidate.Success)
                    throw new InvalidOperationException("PLAYER_CONTROL_SAVE: writer hook could not prepare.");
                ValidatePrepared(candidate.Hook, target);
                handle = candidate; cachedWriterCall = candidate.Original; identity = guard;
                NativeHookPublication.Enable(pending, result, publication, handle,
                    () => NativeSites.NativeIndirectEntry.Validate(candidate.Hook, target, Entry));
                NativeApiLog.Info(log, "PLAYER_CONTROL_SAVE: method=reference-rva, rva=0x52B0, " +
                    "span=[0x52B0,0x52BA), instructions=5+5, continuation=push rbp, backend=Indirect/10; process lifetime.");
            }
            catch
            {
                publication.RollbackUnpublished(pending, () => {
                    handle = null; cachedWriterCall = null; identity = null;
                    ApiSharedNativeSites.Release(sites, ApiSharedNativeSites.PlayerViewIdentitySite);
                });
                throw;
            }
        }

        internal static void ValidatePrepared(IDetour<WriterCall> hook, IntPtr target)
            => NativeSites.NativeIndirectEntry.Validate(hook, target, Entry, false);

        // The existing sole52B0 owner enters production only after acquiring the same
        // engine monitor and restoring the participant identity. Cache the exact ulong
        // native ABI delegate once; no per-save delegate allocation or competing detour.
        private static ulong WriteOriginal(WriterArguments args) => Economy.WorkerProductionCriticalSaveRuntime.RunWriter(
            args.Manager, args.Sections, args.MapSave, args.Original);

        private static ulong OnWriter(IntPtr manager, IntPtr sections, byte mapSave)
        {
            var guard = identity;
            if (!publication.IsReady || guard == null)
                return handle.Original(manager, sections, mapSave);
            return RunWriter(guard, manager, sections, mapSave, cachedWriterCall);
        }

        // Tests substitute only the raw native writer, retaining the actual identity ->
        // production -> native ordering and the outer nonthrowing ABI boundary.
        internal static ulong RunWriter(PlayerViewIdentity guard, IntPtr manager, IntPtr sections, byte mapSave,
            WriterCall original, PlayerViewIdentityRuntime runtime = null) =>
            RunProtectedWriter(guard, new WriterArguments(manager, sections, mapSave, original), writeOriginal, runtime);

        // The native error boundary is also exercised with a throwing world
        // adapter. If a post-write check throws, the file has already been written;
        // preserve the writer's result rather than reporting a false write failure.
        internal static ulong RunProtectedWriter<TState>(PlayerViewIdentity guard, TState state, Func<TState, ulong> writer,
            PlayerViewIdentityRuntime runtime = null)
        {
            ulong result = 0;
            PlayerViewIdentity.Lease lease = null;
            var coordinator = runtime ?? PlayerViewIdentityRuntime.Instance;
            string protectionFailure = null;
            try
            {
                if (!ReferenceEquals(coordinator.Identity, guard))
                    throw new InvalidOperationException("The save adapter and coordinator must use the same identity core.");
                // Known Vanilla callers already hold this monitor. Retain the
                // boundary here as well, including concurrent unbound acquisition.
                lock (coordinator.EngineGate)
                {
                    lease = guard.CurrentLease;
                    bool completed = guard.TryRunWriter(state, writer, out result, out string reason);
                    if (!completed) protectionFailure = reason;
                }
                // A restoration failure can follow a successful file write. Keep
                // Vanilla's actual writer result; report/fault the dependent view
                // separately. Before-writer rejection leaves result at zero.
                return result;
            }
            catch (Exception error)
            {
                // No managed exception crosses the native writer boundary. The
                // scope's finally has already attempted to restore the leased view.
                protectionFailure = "Protected writer failed: " + error.GetType().Name + ": " + error.Message;
            }
            finally
            {
                if (protectionFailure != null) guard.ReportFailure(lease, protectionFailure);
                coordinator.ObserveFailure(guard);
            }
            return result;
        }
    }
}

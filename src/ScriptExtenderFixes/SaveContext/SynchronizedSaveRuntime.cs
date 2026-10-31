using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.ScriptExtenderFixes
{
    internal static class SynchronizedSaveRuntime
    {
        internal const int Rva = 0x13030, DisplacedLength = 6;
        internal static readonly byte[] Entry = { 0x48, 0x89, 0x5C, 0x24, 0x08, 0x57,
            0xB8, 0x40, 0x1D, 0x00, 0x00, 0xE8, 0x50, 0x39, 0x19, 0x00, 0x48, 0x2B, 0xE0 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SaveCall();
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static readonly Action callOriginal = CallOriginal;
        private static DetourHandle<SaveCall> handle;
        private static HookTransaction transaction;

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            publication.ThrowIfUnavailable();
            if (publication.IsReady) return;
            if (!SaveContextRuntime.ManagedReady)
                throw new InvalidOperationException("SE_SAVE_CONTEXT: managed factory correction unavailable; native save scope was not installed.");
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SE_SAVE_CONTEXT: synchronized-save contract unsupported; nativeHash=" + hash +
                    ",expected=" + ApiSharedRuntime.SupportedHash + "; original saving remains active, save-only records may be omitted.");
            for (int i = 0; i < Entry.Length; i++)
                if (context.Memory[Rva + i] != Entry[i])
                    throw new InvalidOperationException("SE_SAVE_CONTEXT: pristine Chore save entry differs at RVA 0x" + (Rva + i).ToString("X"));
            IntPtr target = context.ModuleHandle + Rva;
            NativeSites.NativeIndirectEntry.VerifyBytes(target, Entry);
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.SynchronizedSave,
                ApiSharedNativeSites.SynchronizedSaveSpans(), reason => new InvalidOperationException(reason));
            HookTransaction pending = null;
            try
            {
                pending = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                var candidate = new DetourHandle<SaveCall>();
                pending.AddDetour(candidate, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), (SaveCall)OnSave);
                var result = pending.Commit();
                if (!result.IsCompleteSuccess || !candidate.Success)
                    throw new InvalidOperationException("SE_SAVE_CONTEXT: synchronized-save hook could not prepare.");
                Validate(candidate.Hook, target, false);
                handle = candidate;
                transaction = pending;
                NativeHookPublication.Enable(pending, result, publication, handle, () => Validate(candidate.Hook, target, true));
                NativeApiLog.Info(log, "SE_SAVE_CONTEXT: synchronized saves corrected; method=reference-rva, rva=0x13030, " +
                    "span=[0x13030,0x13036), instructions=5+1, continuation=mov eax,0x1D40, backend=Indirect/6; process lifetime.");
            }
            catch
            {
                publication.RollbackUnpublished(pending, () => {
                    handle = null; transaction = null; ApiSharedNativeSites.Release(sites, ApiSharedNativeSites.SynchronizedSave);
                });
                throw;
            }
        }

        internal static void Validate(IDetour<SaveCall> hook, IntPtr target, bool installed)
        {
            var native = hook as NativeDetour<SaveCall>;
            if (native == null || native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != DisplacedLength || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("SE_SAVE_CONTEXT: expected NativeX64 Indirect/6 backend at " + target);
            for (int i = 0; i < DisplacedLength; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != Entry[i])
                    throw new InvalidOperationException("SE_SAVE_CONTEXT: displaced save prologue differs.");
            // These six bytes have no relative operand; decode the following trampoline jump.
            byte[] bytes = new byte[15];
            Marshal.Copy(native.TrampolineAddress + DisplacedLength, bytes, 0, bytes.Length);
            var decoder = Iced.Intel.Decoder.Create(64, new Iced.Intel.ByteArrayCodeReader(bytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + DisplacedLength).ToInt64());
            decoder.Decode(out var jump);
            if (jump.Mnemonic != Iced.Intel.Mnemonic.Jmp || jump.Op0Kind != Iced.Intel.OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + DisplacedLength).ToInt64()))
                throw new InvalidOperationException("SE_SAVE_CONTEXT: trampoline does not resume at the +6 boundary.");
            for (int i = DisplacedLength; i < Entry.Length; i++)
                if (Marshal.ReadByte(target, i) != Entry[i])
                    throw new InvalidOperationException("SE_SAVE_CONTEXT: save continuation differs.");
            if (installed && (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress))
                throw new InvalidOperationException("SE_SAVE_CONTEXT: published pointer-slot contract differs.");
        }

        private static void CallOriginal() => handle.Original();
        private static void OnSave()
        {
            if (!publication.IsReady) { CallOriginal(); return; }
            SaveContextRuntime.RunSynchronizedSave(callOriginal);
        }
    }
}

using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace APIShared
{
    public static unsafe partial class AssassinPathAPI
    {
        private static void EnsureInstalled()
        {
            publication.ThrowIfUnavailable();
            if (IsInstalled) return;
            if (!supported || module == IntPtr.Zero || region == null)
                throw new InvalidOperationException("Shared Assassin hooks require the audited native hash and loaded library.");
            var memory = new ReadOnlySpan<byte>(module.ToPointer(), memoryLength);
            AssassinPathNativeDefinition.Validate(memory, unchecked((ulong)module.ToInt64()), log);
            NativeOwnershipRegistry sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.AssassinPath, ApiSharedNativeSites.AssassinPathSpans(),
                reason => new InvalidOperationException(reason));
            var pendingBuilder = new DetourHandle<AssassinPathBuilder>();
            var pendingEndpoints = new HookHandle<X64InlineHook>[AssassinPathNativeDefinition.Sites.Length];
            HookTransaction pending = null;
            try
            {
                // InstallOnCommit = false: builderHook is published with its original before any patch
                // is written, so Build never runs before RunVanillaBuilder can (NativeHookPublication).
                pending = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow,
                        OwnsHooks = true, InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                ulong nativeBase = unchecked((ulong)module.ToInt64());
                for (int index = 0; index < pendingEndpoints.Length; index++)
                {
                    int site = index;
                    pendingEndpoints[index] = new HookHandle<X64InlineHook>();
                    pending.AddInline(pendingEndpoints[index],
                        HookTarget.FromAddress(nativeBase + (uint)AssassinPathNativeDefinition.Sites[index]),
                        (assembler, instructions, returnAddress) =>
                        {
                            ulong flagsAddress = unchecked((ulong)policyFlags.ToInt64());
                            if (site == AssassinPathNativeDefinition.ReverseConnectionSite)
                                AssassinEndpointEmitter.EmitReverseConnection(assembler, instructions, nativeBase, flagsAddress);
                            else
                                AssassinEndpointEmitter.Emit(assembler, instructions, nativeBase, flagsAddress, site);
                        },
                        hookSize: 14);
                }
                pending.AddDetour(pendingBuilder, HookTarget.FromAddress(nativeBase + 0xD9C40), (AssassinPathBuilder)Build);
                CommitResult result = pending.Commit();
                if (!result.IsCompleteSuccess || !pendingBuilder.Success)
                    throw new InvalidOperationException("Shared Assassin hook transaction failed.");
                for (int index = 0; index < pendingEndpoints.Length; index++)
                    if (!pendingEndpoints[index].Success || pendingEndpoints[index].IsInstalled ||
                        pendingEndpoints[index].Hook.DisplacedByteCount != AssassinPathNativeDefinition.Lengths[index])
                        throw new InvalidOperationException("Prepared Assassin endpoint displacement mismatch at site " + index);
                Volatile.Write(ref builderHook, pendingBuilder);
                transaction = pending;
                endpointHooks = pendingEndpoints;
                NativeHookPublication.Enable(pending, result, publication, null, () =>
                {
                    AssassinPathNativeDefinition.ValidateInstalledDetour(pendingBuilder.Hook, nativeBase);
                    for (int index = 0; index < pendingEndpoints.Length; index++)
                        if (!pendingEndpoints[index].Success || !pendingEndpoints[index].IsInstalled ||
                            pendingEndpoints[index].Hook.DisplacedByteCount != AssassinPathNativeDefinition.Lengths[index])
                            throw new InvalidOperationException("Shared Assassin endpoint displacement mismatch at site " + index);
                });
            }
            catch (Exception ex)
            {
                publication.RollbackUnpublished(pending, () =>
                {
                    Volatile.Write(ref builderHook, null);
                    transaction = null; endpointHooks = null;
                    ApiSharedNativeSites.Release(sites, ApiSharedNativeSites.AssassinPath);
                });
                try { NativeApiLog.Error(log, "Shared Assassin hooks unavailable; Vanilla retained: " + ex); } catch { }
                throw;
            }
            try { NativeApiLog.Debug(log, "Assassin shared hooks installed; owner=APIShared, builder=0xD9C40, " +
                "scheme=Indirect/displaced=10, endpoints=0xD9F0C/16,0xD9F1C/15,0xE19D8/18,0xE19F9/23, reconstruction-connection=0xE19C0/14."); } catch { }
        }
    }
}

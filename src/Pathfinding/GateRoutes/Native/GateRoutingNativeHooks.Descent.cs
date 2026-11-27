using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
namespace APIShared.GateRouting.Native
{
    public sealed partial class GateRoutingNativeHooks
    {
        /// <summary>Installs the audited pre-step route-clear adapter once. Activation pointers refer to
        /// permanently allocated Int32 data; zero preserves the complete original path. The decision callback
        /// returns a verified descent exception in R11 (0/1). Before/after are optional diagnostic callbacks
        /// gated by diagnosticActivation; they must be supplied and must preserve register state.</summary>
        public void InstallDescent(CapturerComparisonCallback decision,CapturerComparisonCallback beforeCallback,
            CapturerComparisonCallback afterCallback,IntPtr activation,IntPtr diagnosticActivation)
        {
            if (decision==null||beforeCallback==null||afterCallback==null||activation==IntPtr.Zero||diagnosticActivation==IntPtr.Zero)throw new ArgumentException("Callbacks and activation data required");
            descentPublication.ThrowIfUnavailable();
            if (descentAttempted) throw new InvalidOperationException("Gate descent installation already attempted");
            descentAttempted=true;
            const int Site=DescentSiteRva,Length=DescentLength,Continuation=DescentContinuationRva;
            ulong module=libraryBase;
            var hook=new HookHandle<X64InlineHook>();
            byte[] expected = { 0x8b, 0xd3, 0x48, 0x8b, 0xcf, 0xe8, 0x71, 0x31, 0x01, 0x00, 0x8b, 0xd3, 0x48, 0x8b, 0xcf, 0xe8, 0xe7, 0x1b, 0x01, 0x00 };
            for (int i = 0; i < Length; i++) if (context.Memory[Site + i] != expected[i]) throw new InvalidOperationException("Gate descent abort bytes changed");
            // Continuation begins by checking the unchanged special-step register R9w.
            byte[] next = { 0x66, 0x45, 0x85, 0xc9 };
            for (int i = 0; i < next.Length; i++) if (context.Memory[Continuation + i] != next[i]) throw new InvalidOperationException("Gate descent continuation changed");
            using (var probe = new X64InlineHook(module + Site, Length))
                if (probe.DisplacedByteCount != Length) throw new InvalidOperationException("Gate descent displacement changed");
            ApiSharedNativeSites.Claim(Sites, ApiSharedNativeSites.GateDescent, ApiSharedNativeSites.GateDescentSpans(),
                reason => new InvalidOperationException(reason));
            HookTransaction transaction = null;
            CapturerComparisonCallback guardedDecision = cpu => { if (descentPublication.IsReady) decision(cpu); };
            CapturerComparisonCallback guardedBefore = cpu => { if (descentPublication.IsReady) beforeCallback(cpu); };
            CapturerComparisonCallback guardedAfter = cpu => { if (descentPublication.IsReady) afterCallback(cpu); };
            roots.Add(guardedDecision); roots.Add(guardedBefore); roots.Add(guardedAfter);
            try
            {
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false });
                transaction.AddInline(hook, HookTarget.FromAddress(module + Site), (a, original, ret) =>
                    AssassinGateDescentAdapter.Emit(a, original, Address(guardedDecision), (ulong)activation.ToInt64(), module + Continuation,
                        Address(guardedBefore), Address(guardedAfter), (ulong)diagnosticActivation.ToInt64(), module), Length);
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !hook.Success) throw new InvalidOperationException("Gate descent hook failed");
                if (hook.Hook.DisplacedByteCount != Length)
                    throw new InvalidOperationException("Prepared gate descent hook mismatch");
                NativeHookPublication.Enable(transaction, result, descentPublication, this, () =>
                {
                    if (!hook.IsInstalled || hook.Hook.DisplacedByteCount != Length)
                        throw new InvalidOperationException("Gate descent hook failed its written contract.");
                });
            }
            catch
            {
                descentPublication.RollbackUnpublished(transaction,
                    () => ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateDescent));
                throw;
            }
        }
    }
}

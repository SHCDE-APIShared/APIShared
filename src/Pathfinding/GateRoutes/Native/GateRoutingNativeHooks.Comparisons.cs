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
        /// <summary>Prepares both capturer-comparison adapters before publication. Any published part
        /// survives installation failure; callbacks remain inactive until both hooks validate. The callback context is
        /// X64SmartCPUContext; R11b contains secured native inequality, never use Rflags as input/output.
        /// Clear R11b only to add rejection; retain native rejection. All other live state must be preserved.</summary>
        public void InstallComparisons(CapturerComparisonCallback pcl,CapturerComparisonCallback builder)
        {
            if (pcl==null||builder==null)throw new ArgumentNullException("comparison callbacks");
            comparisonPublication.ThrowIfUnavailable();
            if (comparisonsAttempted) throw new InvalidOperationException("Gate comparison installation already attempted");
            comparisonsAttempted=true;
            var memory=context.Memory;
            int pclGraphFilterRva=EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterRva;
            int builderPrecheckFilterRva=EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterRva;
            // Our adapter executes CMP once, saves inequality in R11b, and TESTs
            // that Boolean after callback cleanup. The real R11 is preserved.
            rootedPclComparison = cpu => { if (comparisonPublication.IsReady) pcl(cpu); };
            rootedBuilderComparison = cpu => { if (comparisonPublication.IsReady) builder(cpu); };
            ulong pclCallback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(rootedPclComparison).ToInt64());
            ulong builderCallback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(rootedBuilderComparison).ToInt64());
            CapturerComparisonAdapterEmitter.AssembleAndValidate(
                memory.Slice(pclGraphFilterRva, 20).ToArray(), libraryBase + (ulong)pclGraphFilterRva,
                pclCallback, libraryBase + 0x02200000UL);
            CapturerComparisonAdapterEmitter.AssembleAndValidate(
                memory.Slice(builderPrecheckFilterRva, 20).ToArray(), libraryBase + (ulong)builderPrecheckFilterRva,
                builderCallback, libraryBase + 0x02200000UL);
            ApiSharedNativeSites.Claim(Sites, ApiSharedNativeSites.GateComparisons, ApiSharedNativeSites.GateComparisonSpans(),
                reason => new InvalidOperationException(reason));
            try { InstallComparisonHooks(pclGraphFilterRva, builderPrecheckFilterRva, pclCallback, builderCallback); }
            catch
            {
                if (!comparisonPublication.HasStarted) ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateComparisons);
                throw;
            }
        }

        private void InstallComparisonHooks(int pclGraphFilterRva, int builderPrecheckFilterRva, ulong pclCallback, ulong builderCallback)
        {
            var transaction = new HookTransaction(
                context.Region,
                SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions
                {
                    FailureMode = TransactionFailureMode.RollbackAndThrow,
                    OwnsHooks = true,
                    InstallOnCommit = false
                });
            try
            {
                transaction.AddInline(
                    pclGraphCapturedByFilterHook,
                    HookTarget.FromAddress(libraryBase + unchecked((ulong)pclGraphFilterRva)),
                    (asm, original, returnAddress) => CapturerComparisonAdapterEmitter.Emit(asm, original, pclCallback),
                    hookSize: EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterHookLength);
                transaction.AddInline(
                    builderPrecheckCapturedByFilterHook,
                    HookTarget.FromAddress(libraryBase + unchecked((ulong)builderPrecheckFilterRva)),
                    (asm, original, returnAddress) => CapturerComparisonAdapterEmitter.Emit(asm, original, builderCallback),
                    hookSize: EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength);
                CommitResult commitResult = transaction.Commit();
                if (!commitResult.IsCompleteSuccess ||
                    !pclGraphCapturedByFilterHook.Success ||
                    !builderPrecheckCapturedByFilterHook.Success)
                    throw new InvalidOperationException(
                        $"both snapshot-based captured-player filters were not installed atomically: {commitResult}");
                if (pclGraphCapturedByFilterHook.Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterHookLength ||
                    builderPrecheckCapturedByFilterHook.Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength)
                {
                    throw new InvalidOperationException(
                        "RedBird prepared an unexpected captured-player hook span.");
                }
                NativeHookPublication.Enable(transaction, commitResult, comparisonPublication, this, () =>
                {
                    if (!pclGraphCapturedByFilterHook.IsInstalled || !builderPrecheckCapturedByFilterHook.IsInstalled ||
                        pclGraphCapturedByFilterHook.Hook.DisplacedByteCount != EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterHookLength ||
                        builderPrecheckCapturedByFilterHook.Hook.DisplacedByteCount != EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength)
                        throw new InvalidOperationException("Captured-player hooks were prepared but not written.");
                });
            }
            catch
            {
                comparisonPublication.RollbackUnpublished(transaction, null);
                throw;
            }
        }
    }
}

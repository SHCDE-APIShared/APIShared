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
        /// <summary>Installs the audited edge/cursor/tactical adapters once. Buffers follow
        /// GateNativeSearchScopes and must remain allocated until process exit. Each native callback
        /// owns exactly one original invocation; callbacks and transactions are retained permanently.
        /// This includes partial publication followed by an exception. Provider callbacks stay inactive
        /// until validation succeeds; originals are already available if a call arrives before return.
        /// Existing builder/attack/building/cursor-command function detours are not installed here.</summary>
        public void InstallMovement(IntPtr slots,IntPtr tactical, BuildingConsumerDelegate alternate,
            CandidateSearchDelegate candidates,AiTacticalTargetDelegate ai,DirectTileSearchDelegate direct,CursorPclDecisionDelegate cursor)
        {
            if(slots==IntPtr.Zero||tactical==IntPtr.Zero||alternate==null||candidates==null||ai==null||direct==null||cursor==null)throw new ArgumentException("Buffers and callbacks required");
            movementPublication.ThrowIfUnavailable();
            if(movementAttempted)throw new InvalidOperationException("Gate movement installation already attempted");
            movementAttempted=true;
            threadSlots=slots;tacticalThreadSlots=tactical;rootedAlternateConsumer=alternate;rootedCandidateSearch=candidates;
            rootedAiTacticalTarget=ai;
            // These are the same audited original functions called by the provider. Wrappers must
            // bypass provider policy while the transaction is incomplete or permanently failed.
            originalDirectCursorSearch = Marshal.GetDelegateForFunctionPointer<DirectTileSearchDelegate>(
                new IntPtr(unchecked((long)(libraryBase + EnemyGatePathfindingNativeDefinition.DirectTileSearchRva))));
            originalPclReachability = Marshal.GetDelegateForFunctionPointer<PclReachabilityDelegate>(
                new IntPtr(unchecked((long)(libraryBase + EnemyGatePathfindingNativeDefinition.PclReachabilityRva))));
            rootedDirectCursorSearch = (manager, x, y, a4, a5, a6) =>
                movementPublication.IsReady ? direct(manager, x, y, a4, a5, a6) : originalDirectCursorSearch(manager, x, y, a4, a5, a6);
            rootedCursorPclDecision = (manager, player, target, source, mode, unit) =>
                movementPublication.IsReady ? cursor(manager, player, target, source, mode, unit) : originalPclReachability(manager, player, target, source, mode);
            // Claimed for APIShared before the transaction; released when it was not published.
            ApiSharedNativeSites.Claim(Sites, ApiSharedNativeSites.GateMovement, ApiSharedNativeSites.GateMovementSpans(),
                reason => new InvalidOperationException(reason));
            try { InstallMovementHooks(); }
            catch
            {
                movementPublication.RollbackUnpublished(transaction, () =>
                {
                    System.Threading.Volatile.Write(ref originalAlternateConsumer, null);
                    System.Threading.Volatile.Write(ref originalCandidateSearch, null);
                    System.Threading.Volatile.Write(ref originalAiTacticalTarget, null);
                    ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateMovement);
                });
                throw;
            }
        }
        private sealed class DetourCandidate<T> where T : Delegate
        {
            internal readonly DetourHandle<T> Handle = new DetourHandle<T>();
            internal readonly ulong Address;
            internal DetourCandidate(ulong address) { Address = address; }
            internal bool Prepared => Handle.Success &&
                Handle.Failure == null && Handle.ResolvedAddress == Address;
        }

        private DetourCandidate<T> AddDetour<T>(int rva, T callback, ulong libraryBase) where T : Delegate
        {
            ulong address = libraryBase + unchecked((ulong)rva);
            var candidate = new DetourCandidate<T>(address);
            GateNativeDetourContract.Probe(address,rva,callback);
            transaction.AddDetour(candidate.Handle, HookTarget.FromAddress(address), callback);
            return candidate;
        }

        private void InstallMovementHooks()
        {
            // InstallOnCommit = false: the originals are published before any patch is written, so the
            // provider's callbacks always find them (NativeHookPublication).
            transaction = new HookTransaction(region,
                SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                    InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(GateNativeDetourContract.Backend) });
            alternateConsumer = AddDetour<BuildingConsumerDelegate>(
                EnemyGatePathfindingNativeDefinition.AlternateBuildingConsumerRva,
                (manager, tribe, variant) =>
                {
                    if (movementPublication.IsReady) rootedAlternateConsumer(manager, tribe, variant);
                    else originalAlternateConsumer(manager, tribe, variant);
                }, libraryBase);
            candidateSearch = AddDetour<CandidateSearchDelegate>(
                EnemyGatePathfindingNativeDefinition.PlayerAwareCandidateSearchRva,
                (manager, start, count, target, player, mode, candidateClass) =>
                {
                    if (movementPublication.IsReady) rootedCandidateSearch(manager, start, count, target, player, mode, candidateClass);
                    else originalCandidateSearch(manager, start, count, target, player, mode, candidateClass);
                }, libraryBase);
            aiTacticalTarget = AddDetour<AiTacticalTargetDelegate>(
                EnemyGatePathfindingNativeDefinition.AiTacticalTargetSelectionRva,
                (manager, tribe) =>
                {
                    if (movementPublication.IsReady) rootedAiTacticalTarget(manager, tribe);
                    else originalAiTacticalTarget(manager, tribe);
                }, libraryBase);
            ulong directCursorWrapper = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(
                rootedDirectCursorSearch).ToInt64());
            DirectCursorCallAdapterEmitter.AssembleAndValidate(
                EnemyGatePathfindingNativeDefinition.GetDirectCursorSearchBlockBytes(),
                libraryBase + EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockRva,
                directCursorWrapper, libraryBase + 0x02100000UL);
            using (var probe = new X64InlineHook(
                libraryBase + EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockRva,
                EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength))
                if (probe.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength)
                    throw new InvalidOperationException(
                        $"RedBird direct-cursor span was {probe.DisplacedByteCount}, expected " +
                        EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength + ".");
            transaction.AddInline(directCursorHook,
                HookTarget.FromAddress(libraryBase +
                    EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockRva),
                (asm, original, returnAddress) => DirectCursorCallAdapterEmitter.Emit(
                    asm, original, directCursorWrapper),
                hookSize: EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength);
            ulong cursorPclWrapper = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(
                rootedCursorPclDecision).ToInt64());
            CursorPclCallAdapterEmitter.AssembleAndValidate(
                EnemyGatePathfindingNativeDefinition.GetCursorPclDecisionBytes(),
                libraryBase + EnemyGatePathfindingNativeDefinition.CursorPclDecisionRva,
                cursorPclWrapper, libraryBase + 0x02200000UL);
            using (var probe = new X64InlineHook(
                libraryBase + EnemyGatePathfindingNativeDefinition.CursorPclDecisionRva,
                EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength))
                if (probe.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength)
                    throw new InvalidOperationException(
                        $"RedBird cursor-PCL span was {probe.DisplacedByteCount}, expected " +
                        EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength + ".");
            transaction.AddInline(cursorPclDecisionHook,
                HookTarget.FromAddress(libraryBase +
                    EnemyGatePathfindingNativeDefinition.CursorPclDecisionRva),
                (asm, original, returnAddress) => CursorPclCallAdapterEmitter.Emit(
                    asm, original, cursorPclWrapper),
                hookSize: EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength);
            for (int index = 0; index < edgeHooks.Length; index++)
            {
                edgeHooks[index] = new HookHandle<X64InlineHook>();
                int captured = index;
                int rva = EnemyGatePathfindingNativeDefinition.DirectionFilterRvas[index];
                int length = EnemyGatePathfindingNativeDefinition.DirectionFilterLengths[index];
                DirectionFilterAdapterEmitter.AssembleAndValidate(
                    EnemyGatePathfindingNativeDefinition.GetDirectionFilterBytes(index),
                    libraryBase + unchecked((ulong)rva), index,
                    unchecked((ulong)threadSlots.ToInt64()),
                    libraryBase + 0x02000000UL + unchecked((ulong)(index * 0x1000)));
                using (var probe = new X64InlineHook(libraryBase + unchecked((ulong)rva), 14))
                    if (probe.DisplacedByteCount != length)
                        throw new InvalidOperationException(
                            $"RedBird direction-filter span {index} was {probe.DisplacedByteCount}, expected {length}.");
                transaction.AddInline(edgeHooks[index],
                    HookTarget.FromAddress(libraryBase + unchecked((ulong)rva)),
                    (asm, original, returnAddress) => DirectionFilterAdapterEmitter.Emit(
                        asm, original, captured, unchecked((ulong)threadSlots.ToInt64())), hookSize: 14);
            }
            for (int index = 0; index < tacticalEdgeHooks.Length; index++)
            {
                tacticalEdgeHooks[index] = new HookHandle<X64InlineHook>();
                int captured = index;
                int rva = EnemyGatePathfindingNativeDefinition.AiTacticalFilterRvas[index];
                int length = EnemyGatePathfindingNativeDefinition.AiTacticalFilterLengths[index];
                ulong reject = libraryBase + unchecked((ulong)
                    EnemyGatePathfindingNativeDefinition.AiTacticalRejectRvas[index]);
                AiTacticalTargetAdapterEmitter.AssembleAndValidate(
                    EnemyGatePathfindingNativeDefinition.GetAiTacticalFilterBytes(index),
                    libraryBase + unchecked((ulong)rva), index,
                    unchecked((ulong)tacticalThreadSlots.ToInt64()), reject,
                    libraryBase + 0x02300000UL + unchecked((ulong)(index * 0x1000)));
                using (var probe = new X64InlineHook(libraryBase + unchecked((ulong)rva), length))
                    if (probe.DisplacedByteCount != length)
                        throw new InvalidOperationException(
                            $"RedBird AI tactical-filter span {index} was " +
                            $"{probe.DisplacedByteCount}, expected {length}.");
                transaction.AddInline(tacticalEdgeHooks[index],
                    HookTarget.FromAddress(libraryBase + unchecked((ulong)rva)),
                    (asm, original, returnAddress) => AiTacticalTargetAdapterEmitter.Emit(
                        asm, original, captured,
                        unchecked((ulong)tacticalThreadSlots.ToInt64()),
                        libraryBase + unchecked((ulong)
                            EnemyGatePathfindingNativeDefinition.AiTacticalRejectRvas[captured])),
                    hookSize: length);
            }
            CommitResult result = transaction.Commit();
            if (!result.IsCompleteSuccess)
                throw new InvalidOperationException($"Vanilla Gate-Filter was not installed atomically: {result}");
            ValidateMovement(result, false);
            System.Threading.Volatile.Write(ref originalAlternateConsumer, alternateConsumer.Handle.Original);
            System.Threading.Volatile.Write(ref originalCandidateSearch, candidateSearch.Handle.Original);
            System.Threading.Volatile.Write(ref originalAiTacticalTarget, aiTacticalTarget.Handle.Original);
            NativeHookPublication.Enable(transaction, result, movementPublication, this,
                () => ValidateMovement(result, true));
        }

        private void ValidateMovement(CommitResult result, bool installed)
        {
            if (!result.IsCompleteSuccess ||
                !alternateConsumer.Prepared || !candidateSearch.Prepared || !aiTacticalTarget.Prepared ||
                !directCursorHook.Success || directCursorHook.IsInstalled != installed ||
                directCursorHook.Failure != null ||
                !cursorPclDecisionHook.Success || cursorPclDecisionHook.IsInstalled != installed ||
                cursorPclDecisionHook.Failure != null ||
                directCursorHook.Hook.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength ||
                cursorPclDecisionHook.Hook.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength)
            {
                throw new InvalidOperationException($"Vanilla Gate-Filter was not installed atomically: {result}");
            }
            for (int index = 0; index < edgeHooks.Length; index++)
            {
                if (!edgeHooks[index].Success || edgeHooks[index].IsInstalled != installed ||
                    edgeHooks[index].Failure != null || edgeHooks[index].Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.DirectionFilterLengths[index])
                {
                    throw new InvalidOperationException($"Direction-filter hook {index} failed its committed contract.");
                }
            }
            for (int index = 0; index < tacticalEdgeHooks.Length; index++)
            {
                if (!tacticalEdgeHooks[index].Success || tacticalEdgeHooks[index].IsInstalled != installed ||
                    tacticalEdgeHooks[index].Failure != null ||
                    tacticalEdgeHooks[index].Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.AiTacticalFilterLengths[index])
                {
                    throw new InvalidOperationException(
                        $"AI tactical-filter hook {index} failed its committed contract.");
                }
            }
            GateNativeDetourContract.Validate(alternateConsumer.Handle.Hook,alternateConsumer.Address,6,installed);
            GateNativeDetourContract.Validate(candidateSearch.Handle.Hook,candidateSearch.Address,6,installed);
            GateNativeDetourContract.Validate(aiTacticalTarget.Handle.Hook,aiTacticalTarget.Address,10,installed);
        }
    }
}

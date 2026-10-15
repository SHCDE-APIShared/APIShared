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
    // Sole permanent native owner. Requested explicitly; legacy policy registration stays passive.
    /// <summary>Advanced native gate-routing backend, acquired by one process-lived provider.
    /// Ordinary rule contributors use <see cref="APIShared.GateAccessRules"/> instead.
    /// Acquisition validates the installed native contract but does not install hooks.
    /// Installation is explicit, permanent, and requires synchronous ABI-compatible callbacks.
    /// Providers retain their data buffers until process exit and disable through those buffers.
    /// This backend contains no owner/alliance/capture rule, world scan or scheduling.</summary>
    public sealed class GateRoutingNativeHooks
    {
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PathBuilderDelegate(IntPtr manager, int playerId, int profile);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void AttackApproachDelegate(IntPtr manager, int tribeId,
            int targetContext, uint x, uint y, int resultCount, int region, int movementClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void BuildingApproachDelegate(IntPtr manager, int tribeId,
            int buildingId, int resultCount, int region, int movementClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void BuildingConsumerDelegate(IntPtr tribeManager, int tribeId, int variant);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CursorMoveDelegate(IntPtr unitManager, int tribeId,
            int targetX, int targetY, int targetContext, int actionFlags);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void CandidateSearchDelegate(IntPtr manager, int startTileId,
            int resultCount, int targetPcl, int playerId, int mode, int candidateClass);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int DirectTileSearchDelegate(IntPtr manager, int x, int y,
            int argument4, int argument5, int argument6);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int PclReachabilityDelegate(IntPtr manager, int player,
            int targetPcl, int sourcePcl, int mode);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate int CursorPclDecisionDelegate(IntPtr manager, int player,
            int targetPcl, int sourcePcl, int mode, int unitId);
        /// <summary>Exact audited native ABI; callbacks execute synchronously on the calling native thread. No exception may escape.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        public delegate void AiTacticalTargetDelegate(IntPtr tribeManager, int tribeId);


        internal const int DescentSiteRva = 0x185D55, DescentLength = 20;
        internal const int DescentContinuationRva = 0x185934, DescentContinuationLength = 4;
        private static NativeOwnershipRegistry Sites => ApiSharedRuntime.ProcessInstance.NativeOwnership;
        private static GateRoutingNativeHooks instance;
        private static string owner, registration;
        private readonly CrusaderLibraryLoadContext context;
        private readonly ScanRegion region;
        private readonly ulong libraryBase;
        private readonly ManualLogSource log;
        private HookTransaction transaction;
        private readonly List<object> roots = new List<object>();
        private IntPtr threadSlots, tacticalThreadSlots;
        private bool hooksInstalled, movementAttempted, comparisonsAttempted, descentAttempted;
        private BuildingConsumerDelegate rootedAlternateConsumer;
        private CandidateSearchDelegate rootedCandidateSearch;
        private AiTacticalTargetDelegate rootedAiTacticalTarget;
        private DirectTileSearchDelegate rootedDirectCursorSearch;
        private CursorPclDecisionDelegate rootedCursorPclDecision;
        private DetourCandidate<BuildingConsumerDelegate> alternateConsumer;
        private DetourCandidate<CandidateSearchDelegate> candidateSearch;
        private DetourCandidate<AiTacticalTargetDelegate> aiTacticalTarget;
        internal BuildingConsumerDelegate originalAlternateConsumer;
        internal CandidateSearchDelegate originalCandidateSearch;
        internal AiTacticalTargetDelegate originalAiTacticalTarget;
        private readonly HookHandle<X64InlineHook> directCursorHook = new HookHandle<X64InlineHook>();
        private readonly HookHandle<X64InlineHook> cursorPclDecisionHook = new HookHandle<X64InlineHook>();
        private readonly HookHandle<X64InlineHook>[] edgeHooks = new HookHandle<X64InlineHook>[EnemyGatePathfindingNativeDefinition.DirectionFilterRvas.Length];
        private readonly HookHandle<X64InlineHook>[] tacticalEdgeHooks = new HookHandle<X64InlineHook>[EnemyGatePathfindingNativeDefinition.AiTacticalFilterRvas.Length];
        private GateRoutingNativeHooks(CrusaderLibraryLoadContext c, ManualLogSource logger) { context=c;region=c.Region;libraryBase=(ulong)c.ModuleHandle.ToInt64();log=logger; }
        /// <summary>Requests the exclusive backend without installing hooks. Same-owner retries return the
        /// same backend only for the same registration and native module. Registration never replaces an owner.
        /// Call after native initialization; inspect failure text before using the returned backend.</summary>
        public static bool TryRequest(string ownerGuid,string registrationId,CrusaderLibraryLoadContext context,
            ManualLogSource logger,out GateRoutingNativeHooks backend,out string reason)
        {
            backend=null;reason=null;
            if(string.IsNullOrWhiteSpace(ownerGuid)||string.IsNullOrWhiteSpace(registrationId)||context==null)
            {reason="Owner, registration ID and native context required";return false;}
            lock(typeof(GateRoutingNativeHooks))
            {
                if(instance!=null)
                {
                    if(owner!=ownerGuid||registration!=registrationId||instance.libraryBase!=(ulong)context.ModuleHandle.ToInt64())
                    {reason="Gate native backend already owned by "+owner+"/"+registration;return false;}
                    backend=instance;return true;
                }
                try
                {
                    EnemyGatePathfindingNativeDefinition.ValidateNativeHookContracts(context.Memory);
                    EnemyGatePathfindingNativeDefinition.ValidateSamePclNativeFilterContracts(context.Memory,true);
                    owner=ownerGuid;registration=registrationId;
                    backend=instance=new GateRoutingNativeHooks(context,logger);return true;
                }
                catch(Exception error){reason=error.Message;return false;}
            }
        }
        // The three originals are set before the first movement patch is written, so a native callback
        // can always call them, and cleared again if the installation fails (issue #28).
        /// <summary>Original alternate building consumer, available after successful movement installation.</summary>
        public BuildingConsumerDelegate OriginalAlternateConsumer => System.Threading.Volatile.Read(ref originalAlternateConsumer);
        /// <summary>Original candidate search, available after successful movement installation.</summary>
        public CandidateSearchDelegate OriginalCandidateSearch => System.Threading.Volatile.Read(ref originalCandidateSearch);
        /// <summary>Original tactical search, available after successful movement installation.</summary>
        public AiTacticalTargetDelegate OriginalAiTacticalTarget => System.Threading.Volatile.Read(ref originalAiTacticalTarget);
        /// <summary>Installs the audited edge/cursor/tactical adapters once. Buffers follow
        /// GateNativeSearchScopes and must remain allocated until process exit. Each native callback
        /// owns exactly one original invocation; callbacks and transactions are retained permanently.
        /// Existing builder/attack/building/cursor-command function detours are not installed here.</summary>
        public void InstallMovement(IntPtr slots,IntPtr tactical, BuildingConsumerDelegate alternate,
            CandidateSearchDelegate candidates,AiTacticalTargetDelegate ai,DirectTileSearchDelegate direct,CursorPclDecisionDelegate cursor)
        {
            if(slots==IntPtr.Zero||tactical==IntPtr.Zero||alternate==null||candidates==null||ai==null||direct==null||cursor==null)throw new ArgumentException("Buffers and callbacks required");
            if(movementAttempted)throw new InvalidOperationException("Gate movement installation already attempted");
            movementAttempted=true;
            threadSlots=slots;tacticalThreadSlots=tactical;rootedAlternateConsumer=alternate;rootedCandidateSearch=candidates;
            rootedAiTacticalTarget=ai;rootedDirectCursorSearch=direct;rootedCursorPclDecision=cursor;
            // Claimed for APIShared before the transaction; released when it was not published.
            ApiSharedNativeSites.Claim(Sites, ApiSharedNativeSites.GateMovement, ApiSharedNativeSites.GateMovementSpans(),
                reason => new InvalidOperationException(reason));
            try { InstallMovementHooks(); }
            catch { ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateMovement); throw; }
            roots.Add(transaction);
        }
        private sealed class DetourCandidate<T> where T : Delegate
        {
            internal readonly DetourHandle<T> Handle = new DetourHandle<T>();
            internal readonly ulong Address;
            internal DetourCandidate(ulong address) { Address = address; }
            internal bool Committed => Handle.Success && Handle.IsInstalled &&
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
                new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = false,
                    InstallOnCommit = false, Backend = GateNativeDetourContract.Backend });
            alternateConsumer = AddDetour(
                EnemyGatePathfindingNativeDefinition.AlternateBuildingConsumerRva,
                rootedAlternateConsumer, libraryBase);
            candidateSearch = AddDetour(
                EnemyGatePathfindingNativeDefinition.PlayerAwareCandidateSearchRva,
                rootedCandidateSearch, libraryBase);
            aiTacticalTarget = AddDetour(
                EnemyGatePathfindingNativeDefinition.AiTacticalTargetSelectionRva,
                rootedAiTacticalTarget, libraryBase);
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
            System.Threading.Volatile.Write(ref originalAlternateConsumer, alternateConsumer.Handle.Original);
            System.Threading.Volatile.Write(ref originalCandidateSearch, candidateSearch.Handle.Original);
            System.Threading.Volatile.Write(ref originalAiTacticalTarget, aiTacticalTarget.Handle.Original);
            try { NativeHookPublication.Enable(transaction, result); }
            catch { RollBackMovement(); throw; }
            if (!result.IsCompleteSuccess ||
                !alternateConsumer.Committed || !candidateSearch.Committed || !aiTacticalTarget.Committed ||
                !directCursorHook.Success || !directCursorHook.IsInstalled ||
                directCursorHook.Failure != null ||
                !cursorPclDecisionHook.Success || !cursorPclDecisionHook.IsInstalled ||
                cursorPclDecisionHook.Failure != null ||
                directCursorHook.Hook.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength ||
                cursorPclDecisionHook.Hook.DisplacedByteCount !=
                    EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength)
            {
                RollBackMovement();
                throw new InvalidOperationException($"Vanilla Gate-Filter was not installed atomically: {result}");
            }
            for (int index = 0; index < edgeHooks.Length; index++)
            {
                if (!edgeHooks[index].Success || !edgeHooks[index].IsInstalled ||
                    edgeHooks[index].Failure != null || edgeHooks[index].Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.DirectionFilterLengths[index])
                {
                    RollBackMovement();
                    throw new InvalidOperationException($"Direction-filter hook {index} failed its committed contract.");
                }
            }
            for (int index = 0; index < tacticalEdgeHooks.Length; index++)
            {
                if (!tacticalEdgeHooks[index].Success || !tacticalEdgeHooks[index].IsInstalled ||
                    tacticalEdgeHooks[index].Failure != null ||
                    tacticalEdgeHooks[index].Hook.DisplacedByteCount !=
                        EnemyGatePathfindingNativeDefinition.AiTacticalFilterLengths[index])
                {
                    RollBackMovement();
                    throw new InvalidOperationException(
                        $"AI tactical-filter hook {index} failed its committed contract.");
                }
            }
            try
            {
                GateNativeDetourContract.Validate(alternateConsumer.Handle.Hook,alternateConsumer.Address,6,true);
                GateNativeDetourContract.Validate(candidateSearch.Handle.Hook,candidateSearch.Address,6,true);
                GateNativeDetourContract.Validate(aiTacticalTarget.Handle.Hook,aiTacticalTarget.Address,10,true);
            }
            catch { RollBackMovement(); throw; } // Initialization candidate is not installed.
            hooksInstalled = true;
        }
        // Failed movement installation: removes every patch first, then unpublishes the originals.
        private void RollBackMovement()
        {
            transaction.DisableAll();
            System.Threading.Volatile.Write(ref originalAlternateConsumer, null);
            System.Threading.Volatile.Write(ref originalCandidateSearch, null);
            System.Threading.Volatile.Write(ref originalAiTacticalTarget, null);
        }
        private CapturerComparisonCallback rootedPclComparison,rootedBuilderComparison;
        private readonly HookHandle<X64InlineHook> pclGraphCapturedByFilterHook=new HookHandle<X64InlineHook>();
        private readonly HookHandle<X64InlineHook> builderPrecheckCapturedByFilterHook=new HookHandle<X64InlineHook>();
        /// <summary>Installs both capturer-comparison adapters atomically. The callback context is
        /// X64SmartCPUContext; R11b contains secured native inequality, never use Rflags as input/output.
        /// Clear R11b only to add rejection; retain native rejection. All other live state must be preserved.</summary>
        public void InstallComparisons(CapturerComparisonCallback pcl,CapturerComparisonCallback builder)
        {
            if (pcl==null||builder==null)throw new ArgumentNullException("comparison callbacks");
            if (comparisonsAttempted) throw new InvalidOperationException("Gate comparison installation already attempted");
            comparisonsAttempted=true;
            var memory=context.Memory;
            int pclGraphFilterRva=EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterRva;
            int builderPrecheckFilterRva=EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterRva;
            // Our adapter executes CMP once, saves inequality in R11b, and TESTs
            // that Boolean after callback cleanup. The real R11 is preserved.
            rootedPclComparison = pcl;
            rootedBuilderComparison = builder;
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
            catch { ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateComparisons); throw; }
        }

        private void InstallComparisonHooks(int pclGraphFilterRva, int builderPrecheckFilterRva, ulong pclCallback, ulong builderCallback)
        {
            var transaction = new HookTransaction(
                context.Region,
                SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions
                {
                    FailureMode = TransactionFailureMode.RollbackAndThrow,
                    OwnsHooks = false
                });
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
                // This object has not been published yet, so rollback is safe and mandatory.
                transaction.DisableAll();
                throw new InvalidOperationException(
                    "RedBird committed an unexpected captured-player hook span; both hooks were rolled back.");
            }

            roots.Add(rootedPclComparison);roots.Add(rootedBuilderComparison);roots.Add(transaction);
        }
        /// <summary>Installs the audited pre-step route-clear adapter once. Activation pointers refer to
        /// permanently allocated Int32 data; zero preserves the complete original path. The decision callback
        /// returns a verified descent exception in R11 (0/1). Before/after are optional diagnostic callbacks
        /// gated by diagnosticActivation; they must be supplied and must preserve register state.</summary>
        public void InstallDescent(CapturerComparisonCallback decision,CapturerComparisonCallback beforeCallback,
            CapturerComparisonCallback afterCallback,IntPtr activation,IntPtr diagnosticActivation)
        {
            if (decision==null||beforeCallback==null||afterCallback==null||activation==IntPtr.Zero||diagnosticActivation==IntPtr.Zero)throw new ArgumentException("Callbacks and activation data required");
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
            HookTransaction transaction;
            try
            {
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = false });
                transaction.AddInline(hook, HookTarget.FromAddress(module + Site), (a, original, ret) =>
                    AssassinGateDescentAdapter.Emit(a, original, Address(decision), (ulong)activation.ToInt64(), module + Continuation,
                        Address(beforeCallback), Address(afterCallback), (ulong)diagnosticActivation.ToInt64(), module), Length);
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !hook.Success) throw new InvalidOperationException("Gate descent hook failed");
                if (hook.Hook.DisplacedByteCount != Length)
                { transaction.DisableAll(); throw new InvalidOperationException("Unpublished gate descent hook mismatch"); }
            }
            catch { ApiSharedNativeSites.Release(Sites, ApiSharedNativeSites.GateDescent); throw; }
            roots.Add(transaction);roots.Add(decision);roots.Add(beforeCallback);roots.Add(afterCallback);
        }
        private static ulong Address(Delegate d) => (ulong)Marshal.GetFunctionPointerForDelegate(d).ToInt64();
    }
}

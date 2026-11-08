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
    public sealed partial class GateRoutingNativeHooks
    {
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
        private readonly NativeHookPublicationState movementPublication = new NativeHookPublicationState();
        private readonly NativeHookPublicationState comparisonPublication = new NativeHookPublicationState();
        private readonly NativeHookPublicationState descentPublication = new NativeHookPublicationState();
        private readonly List<object> roots = new List<object>();
        private IntPtr threadSlots, tacticalThreadSlots;
        private bool movementAttempted, comparisonsAttempted, descentAttempted;
        private BuildingConsumerDelegate rootedAlternateConsumer;
        private CandidateSearchDelegate rootedCandidateSearch;
        private AiTacticalTargetDelegate rootedAiTacticalTarget;
        private DirectTileSearchDelegate rootedDirectCursorSearch;
        private CursorPclDecisionDelegate rootedCursorPclDecision;
        private DirectTileSearchDelegate originalDirectCursorSearch;
        private PclReachabilityDelegate originalPclReachability;
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
        // can always call them. They remain available even if a later publication step fails.
        /// <summary>Original alternate building consumer, available before movement publication and retained after a publication failure.</summary>
        public BuildingConsumerDelegate OriginalAlternateConsumer => System.Threading.Volatile.Read(ref originalAlternateConsumer);
        /// <summary>Original candidate search, available before movement publication and retained after a publication failure.</summary>
        public CandidateSearchDelegate OriginalCandidateSearch => System.Threading.Volatile.Read(ref originalCandidateSearch);
        /// <summary>Original tactical search, available before movement publication and retained after a publication failure.</summary>
        public AiTacticalTargetDelegate OriginalAiTacticalTarget => System.Threading.Volatile.Read(ref originalAiTacticalTarget);
        private CapturerComparisonCallback rootedPclComparison,rootedBuilderComparison;
        private readonly HookHandle<X64InlineHook> pclGraphCapturedByFilterHook=new HookHandle<X64InlineHook>();
        private readonly HookHandle<X64InlineHook> builderPrecheckCapturedByFilterHook=new HookHandle<X64InlineHook>();
        private static ulong Address(Delegate d) => (ulong)Marshal.GetFunctionPointerForDelegate(d).ToInt64();
    }
}

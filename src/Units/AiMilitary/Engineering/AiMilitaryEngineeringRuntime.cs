using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using APIShared.NativeSites;

namespace APIShared.Units.AiMilitary.Engineering
{
    // Provenance is cumulative over every real participant in every builder tick.
    // A mixed/unknown build can never become additional by changing its final crew.
    internal static unsafe partial class AiMilitaryEngineeringRuntime
    {
        internal const string SiteName = "ai-military-engineering-provenance";
        private const int BuildingManager = 0x64CCBB0, BuildingStride = 0x32C;
        private const int UnitManager = 0x67E8400, UnitStride = 0x490;
        private static readonly int[] Rvas = { 0xA65A0, 0xA8E40, 0xA93C0, 0xA9A30, 0xAA030, 0xAA630 };
        private static readonly eChimps[] Types = { eChimps.CHIMP_TYPE_ARAB_BALLISTA, eChimps.CHIMP_TYPE_CATAPULT,
            eChimps.CHIMP_TYPE_TREBUCHET, eChimps.CHIMP_TYPE_SIEGE_TOWER, eChimps.CHIMP_TYPE_BATTERING_RAM, eChimps.CHIMP_TYPE_PORTABLE_SHIELD };
        private static readonly int[] Crew = { 2, 2, 3, 4, 4, 1 };
        private static readonly string[] EntryHex = { "40535641544157", "40535641544157", "405356574155", "48895C242056", "48895C242056", "48895C242056" };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Builder();
        private static readonly Builder[] Callbacks = { () => Invoke(0), () => Invoke(1), () => Invoke(2), () => Invoke(3), () => Invoke(4), () => Invoke(5) };
        private static readonly DetourHandle<Builder>[] Hooks = new DetourHandle<Builder>[6];
        private static readonly AiEngineeringNativeGate[] Gates = new AiEngineeringNativeGate[6];
        private static readonly NativeDetourBackend Backend = new NativeDetourBackend(new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static readonly Dictionary<BuildingRef, AiBuildProvenance> Builds = new Dictionary<BuildingRef, AiBuildProvenance>();
        private static readonly Dictionary<AiUnitRef, ProductHistory> Products = new Dictionary<AiUnitRef, ProductHistory>();
        private static readonly Dictionary<AiUnitRef, ReplacementCandidate> Candidates = new Dictionary<AiUnitRef, ReplacementCandidate>();
        private static readonly List<AiUnitRef> RemoveCandidates = new List<AiUnitRef>();
        private static readonly List<AiUnitRef> RemoveProducts = new List<AiUnitRef>();
        private static readonly List<BuildingRef> RemoveBuilds = new List<BuildingRef>();
        private static IntPtr module;
        private static IntPtr trackedBuildings;
        private static Func<AiUnitRef, bool> isAdditional;
        private static Action<AiUnitRef, bool> markProduct;
        private static ManualLogSource logger;
        private static HookTransaction transaction;
        [ThreadStatic] private static BuildScope scope;
        [ThreadStatic] private static ReplacementScope replacement;
        private static long generation;
        internal static bool IsReady => Publication.IsReady;
        internal static NativeSiteSpan[] Spans()
        {
            var result = new NativeSiteSpan[Rvas.Length];
            for (int i = 0; i < result.Length; i++) result[i] = new NativeSiteSpan(Rvas[i], EntryHex[i].Length / 2, NativeSiteMode.Exclusive);
            return result;
        }
        private readonly struct BuildingRef : IEquatable<BuildingRef>
        {
            internal BuildingRef(int id, uint global) { Id = id; Global = global; }
            internal int Id { get; } internal uint Global { get; }
            public bool Equals(BuildingRef other) => Id == other.Id && Global == other.Global;
            public override bool Equals(object other) => other is BuildingRef value && Equals(value);
            public override int GetHashCode() => unchecked(Id * 397 ^ (int)Global);
        }
        private readonly struct ProductHistory
        {
            internal ProductHistory(int type, int owner, bool additional, AiUnitRef[] crew)
            { Type = type; Owner = owner; Additional = additional; Crew = crew; }
            internal int Type { get; } internal int Owner { get; } internal bool Additional { get; }
            internal AiUnitRef[] Crew { get; }
        }
        private struct BuildScope
        {
            internal int Index; internal BuildingRef Building; internal AiBuildProvenance History; internal AiUnitRef Product;
            internal long Generation;
        }
        internal sealed class ReplacementScope
        {
            internal ReplacementScope Previous;
            internal AiUnitRef Source, Created;
            internal int Type, Owner;
            internal long Generation;
            internal AiUnitRef[] Crew;
            internal bool Ambiguous;
        }
        private readonly struct ReplacementCandidate
        {
            internal ReplacementCandidate(AiUnitRef source, int type, int owner, AiUnitRef[] crew)
            { Source = source; Type = type; Owner = owner; Crew = crew; }
            internal AiUnitRef Source { get; }
            internal int Type { get; }
            internal int Owner { get; }
            internal AiUnitRef[] Crew { get; }
        }
        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log,
            Func<AiUnitRef, bool> additional, Action<AiUnitRef, bool> productMarker)
        {
            Publication.ThrowIfUnavailable(); if (Publication.IsReady) return;
            if (!string.Equals(hash, Quota.AiQuotaNativeContract.Hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Engineering provenance requires the audited image.");
            if (additional == null || productMarker == null) throw new ArgumentNullException(nameof(additional));
            module = context.ModuleHandle; logger = log; isAdditional = additional; markProduct = productMarker;
            var spans = new NativeSiteSpan[6];
            for (int i = 0; i < Rvas.Length; i++)
            {
                byte[] bytes = Quota.AiQuotaNativeContract.Hex(EntryHex[i]);
                if (!context.Memory.Slice(Rvas[i], bytes.Length).SequenceEqual(bytes)) throw new InvalidOperationException("Engineering load-image entry differs.");
                NativeIndirectEntry.VerifyBytes(module + Rvas[i], bytes);
                spans[i] = new NativeSiteSpan(Rvas[i], bytes.Length, NativeSiteMode.Exclusive);
            }
            var claim = NativeSiteRegistry.Register(APISharedPlugin.PluginGuid, SiteName, spans);
            if (!claim.Succeeded) throw new InvalidOperationException(claim.Reason);
            try
            {
                trackedBuildings = Marshal.AllocHGlobal(4000 * 4); new Span<byte>((void*)trackedBuildings, 4000 * 4).Clear();
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                for (int i = 0; i < Hooks.Length; i++)
                {
                    Gates[i] = new AiEngineeringNativeGate(Address(0), unchecked((ulong)Quota.AiMilitaryQuotaRuntime.DemandAddress.ToInt64()),
                        unchecked((ulong)trackedBuildings.ToInt64()), unchecked((ulong)Marshal.GetFunctionPointerForDelegate(Callbacks[i]).ToInt64()));
                    Hooks[i] = new DetourHandle<Builder>(); transaction.AddDetour(Hooks[i], HookTarget.FromAddress(Address(Rvas[i])), Callbacks[i], Gates[i]);
                }
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess) throw new InvalidOperationException("Engineering native preparation incomplete.");
                for (int i = 0; i < Hooks.Length; i++)
                {
                    Validate(i, false);
                }
                // Gates are already held by the static owner. The publication rollback
                // guard preserves all of them after even a partial executable write.
                NativeHookPublication.Enable(transaction, result, Publication, typeof(AiMilitaryEngineeringRuntime), () => {
                    for (int i = 0; i < Hooks.Length; i++) Validate(i, true);
                });
                AiFixesReplacementObserver.Initialize();
            }
            catch
            {
                Publication.RollbackUnpublished(transaction, () => {
                    NativeSiteRegistry.Unregister(APISharedPlugin.PluginGuid, SiteName); transaction = null;
                    Marshal.FreeHGlobal(trackedBuildings); trackedBuildings = IntPtr.Zero;
                });
                throw;
            }
        }
        internal static void Clear(long worldGeneration)
        { generation = worldGeneration; Builds.Clear(); Products.Clear(); Candidates.Clear(); RemoveCandidates.Clear(); RemoveProducts.Clear(); RemoveBuilds.Clear(); scope = default; replacement = null;
            if (trackedBuildings != IntPtr.Zero) new Span<byte>((void*)trackedBuildings, 4000 * 4).Clear(); }
        private static void Report(Exception error)
        { try { Internal.RuntimeHealth.Failure(logger, "ai-military-engineering", true, "AI engineering provenance failed: " + error); } catch { } }
        internal static void ReportIntegration(Exception error) => Report(error);
    }
}

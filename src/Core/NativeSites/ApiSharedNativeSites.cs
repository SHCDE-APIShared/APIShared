using System;
using System.Collections.Generic;
using APIShared.Audio;
using APIShared.Economy;
using APIShared.GateRouting.Native;
using APIShared.NativeSites;

namespace APIShared
{
    // APIShared's own native sites, built from each service's native contract constants. Every
    // service claims its group under APIShared_Serp immediately before its hook transaction and
    // releases it only when that transaction failed before publication. A conflict with another
    // owner's registration fails that service closed, exactly like an occupied entry already does.
    internal static class ApiSharedNativeSites
    {
        internal const string Audio = "Audio.UnitContext";
        internal const string GatehousePermanent = "Gatehouse.PermanentHooks";
        internal const string GatehouseAutomation = "Gatehouse.Automation";
        internal const string AivBuildStep = "Diagnostics.AivBuildStep";
        internal const string MarketPrices = "Economy.MarketPrices";
        internal const string PlagueHeight = "Projectiles.PlagueHeight";
        internal const string AiMarketSell = "Economy.AiMarketSell";
        internal const string AssassinPath = "Pathfinding.AssassinPath";
        internal const string AssassinAttack = "Pathfinding.AssassinAttack";
        internal const string AssassinRetarget = "Pathfinding.AssassinRetargetEntry";
        internal const string GateMovement = "GateRouting.Movement";
        internal const string GateComparisons = "GateRouting.Comparisons";
        internal const string GateDescent = "GateRouting.Descent";
        internal const string BuildingRepair = "Buildings.RepairQuote";
        internal const string SynchronizedSave = "Savegames.SynchronizedSaveContext";

        internal static NativeSiteSpan[] SynchronizedSaveSpans() => new[] {
            Exclusive(ScriptExtenderFixes.SynchronizedSaveRuntime.Rva, ScriptExtenderFixes.SynchronizedSaveRuntime.DisplacedLength),
            Observe(ScriptExtenderFixes.SynchronizedSaveRuntime.Rva + ScriptExtenderFixes.SynchronizedSaveRuntime.DisplacedLength,
                ScriptExtenderFixes.SynchronizedSaveRuntime.Entry.Length - ScriptExtenderFixes.SynchronizedSaveRuntime.DisplacedLength)
        };

        // AIV detour: the backend's displacement is not fixed here, so the claim covers the
        // instruction-aligned prologue any patch of up to 14 bytes displaces (2+1+1+1+2+2+2+2+4).
        internal const int AivPrologueLength = 17;

        internal static NativeSiteSpan[] AudioSpans()
        {
            var spans = new List<NativeSiteSpan>
            {
                Exclusive(NativeContract.WrapperRva, NativeContract.WrapperBytes.Length),
                Exclusive(NativeContract.FlagWrapperRva, NativeContract.FlagWrapperBytes.Length),
                Exclusive(NativeContract.EmitRva, NativeContract.EmitBytes.Length)
            };
            // The 91 catalog CALL instructions are decoded and must keep calling the wrappers.
            foreach (SoundSource source in SoundSources.All)
                spans.Add(Observe(source.ReturnRva - 5, 5));
            return spans.ToArray();
        }

        internal static NativeSiteSpan[] GatehousePermanentSpans(bool distance, bool timing, GatehouseBuildTarget target)
        {
            var spans = new List<NativeSiteSpan>();
            if (distance)
                spans.Add(Exclusive(GatehousePermanentRuntimeState.DistanceHookRva, GatehousePermanentRuntimeState.DistanceDisplacedBytes));
            if (timing)
            {
                spans.Add(Exclusive(GatehousePermanentRuntimeState.DecisionHookRva, GatehousePermanentRuntimeState.DecisionDisplacedBytes));
                // Live-validated human-delay prefix; it ends where Fixes' B7C39 hook begins.
                spans.Add(Observe(target.HumanDelayBlockRva, 7));
            }
            return spans.ToArray();
        }

        internal static NativeSiteSpan[] GatehouseAutomationSpans()
        {
            var spans = new List<NativeSiteSpan>();
            for (int site = 0; site < GatehouseAutomationNativeState.HookRvas.Length; site++)
                spans.Add(Exclusive(GatehouseAutomationNativeState.HookRvas[site], GatehouseAutomationNativeState.HookBytes[site].Length));
            spans.Add(Observe(GatehouseAutomationNativeState.CouplingRva, GatehouseAutomationNativeState.CouplingEntry.Length));
            return spans.ToArray();
        }

        internal static NativeSiteSpan[] AivBuildStepSpans() =>
            new[] { Exclusive(AivBuildStepService.FunctionRva, AivPrologueLength) };

        internal static NativeSiteSpan[] PlagueHeightSpans() => new[] {
            Exclusive(PlagueHeightAssembly.Sites[0], 32), Exclusive(PlagueHeightAssembly.Sites[1], 26),
            Exclusive(PlagueHeightAssembly.Sites[2], 26), Exclusive(PlagueHeightAssembly.Sites[3], 26)
        };

        internal static NativeSiteSpan[] AiMarketSellSpans() => new[]
        {
            Exclusive(0x3F1F3, 15), Observe(0x3F202, 5),
            Exclusive(0x29723, 15), Observe(0x29732, 5), Observe(0x2978B, 21)
        };

        internal static NativeSiteSpan[] MarketPriceSpans() => new[]
        {
            Exclusive(MarketPriceNativeRuntime.BuyRva, MarketPriceNativeRuntime.DisplacedLength),
            Observe(MarketPriceNativeRuntime.BuyRva + MarketPriceNativeRuntime.DisplacedLength,
                MarketPriceNativeRuntime.BuyBytes.Length - MarketPriceNativeRuntime.DisplacedLength),
            Exclusive(MarketPriceNativeRuntime.SellRva, MarketPriceNativeRuntime.DisplacedLength),
            Observe(MarketPriceNativeRuntime.SellRva + MarketPriceNativeRuntime.DisplacedLength,
                MarketPriceNativeRuntime.SellBytes.Length - MarketPriceNativeRuntime.DisplacedLength)
        };

        internal static NativeSiteSpan[] AssassinPathSpans()
        {
            var spans = new List<NativeSiteSpan>
            {
                Exclusive(AssassinPathNativeDefinition.BuilderRva, AssassinPathNativeDefinition.BuilderDisplacedLength)
            };
            for (int index = 0; index < AssassinPathNativeDefinition.Sites.Length; index++)
                spans.Add(Exclusive(AssassinPathNativeDefinition.Sites[index], AssassinPathNativeDefinition.Lengths[index]));
            return spans.ToArray();
        }

        internal static NativeSiteSpan[] AssassinAttackSpans() =>
            new[] { Exclusive(AssassinAttackNativeContract.UpdateRva, AssassinAttackNativeContract.UpdateDisplacedLength) };

        internal static NativeSiteSpan[] AssassinRetargetSpans() =>
            new[] { Observe(AssassinAttackNativeContract.RetargetEntryRva, AssassinAttackNativeContract.RetargetEntryLength) };

        internal static NativeSiteSpan[] GateMovementSpans()
        {
            var spans = new List<NativeSiteSpan>();
            foreach (int rva in new[]
            {
                EnemyGatePathfindingNativeDefinition.AlternateBuildingConsumerRva,
                EnemyGatePathfindingNativeDefinition.PlayerAwareCandidateSearchRva,
                EnemyGatePathfindingNativeDefinition.AiTacticalTargetSelectionRva
            })
                spans.Add(Exclusive(rva, GateNativeDetourContract.Length(rva)));
            spans.Add(Exclusive(EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockRva,
                EnemyGatePathfindingNativeDefinition.DirectCursorSearchBlockLength));
            spans.Add(Exclusive(EnemyGatePathfindingNativeDefinition.CursorPclDecisionRva,
                EnemyGatePathfindingNativeDefinition.CursorPclDecisionLength));
            for (int index = 0; index < EnemyGatePathfindingNativeDefinition.DirectionFilterRvas.Length; index++)
                spans.Add(Exclusive(EnemyGatePathfindingNativeDefinition.DirectionFilterRvas[index],
                    EnemyGatePathfindingNativeDefinition.DirectionFilterLengths[index]));
            for (int index = 0; index < EnemyGatePathfindingNativeDefinition.AiTacticalFilterRvas.Length; index++)
                spans.Add(Exclusive(EnemyGatePathfindingNativeDefinition.AiTacticalFilterRvas[index],
                    EnemyGatePathfindingNativeDefinition.AiTacticalFilterLengths[index]));
            return spans.ToArray();
        }

        internal static NativeSiteSpan[] GateComparisonSpans() => new[]
        {
            Exclusive(EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterRva,
                EnemyGatePathfindingNativeDefinition.PclGraphCapturedByFilterHookLength),
            Exclusive(EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterRva,
                EnemyGatePathfindingNativeDefinition.BuilderPrecheckCapturedByFilterHookLength)
        };

        internal static NativeSiteSpan[] GateDescentSpans() => new[]
        {
            Exclusive(GateRoutingNativeHooks.DescentSiteRva, GateRoutingNativeHooks.DescentLength),
            Observe(GateRoutingNativeHooks.DescentContinuationRva, GateRoutingNativeHooks.DescentContinuationLength)
        };

        internal static NativeSiteSpan[] BuildingRepairSpans() =>
            new[] { Observe(BuildingRepairService.CanRepairRva, BuildingRepairService.CanRepairEntry.Length) };

        // Every group in one list, for the catalog test and the documentation table.
        internal static IReadOnlyList<KeyValuePair<string, NativeSiteSpan[]>> All() => new[]
        {
            Group(Audio, AudioSpans()),
            Group(GatehousePermanent, GatehousePermanentSpans(true, true, GatehouseBuildTarget.Supported)),
            Group(GatehouseAutomation, GatehouseAutomationSpans()),
            Group(AivBuildStep, AivBuildStepSpans()),
            Group(MarketPrices, MarketPriceSpans()),
            Group(PlagueHeight, PlagueHeightSpans()),
            Group(AiMarketSell, AiMarketSellSpans()),
            Group(AssassinPath, AssassinPathSpans()),
            Group(AssassinAttack, AssassinAttackSpans()),
            Group(AssassinRetarget, AssassinRetargetSpans()),
            Group(GateMovement, GateMovementSpans()),
            Group(GateComparisons, GateComparisonSpans()),
            Group(GateDescent, GateDescentSpans()),
            Group(BuildingRepair, BuildingRepairSpans()),
            Group(SynchronizedSave, SynchronizedSaveSpans())
        };

        // Claims a group for APIShared before installation. Throws the service's own failure type
        // with the refusal text, so each service keeps its existing fail-closed path and diagnostics.
        internal static void Claim(NativeOwnershipRegistry registry, string name, NativeSiteSpan[] spans,
            Func<string, Exception> failure)
        {
            if (registry == null)
                return;
            NativeSiteRegistrationResult result = registry.RegisterSites(APISharedPlugin.PluginGuid, name, spans);
            if (!result.Succeeded)
                throw failure("Native site registry refused " + name + ": " + result.Reason);
        }

        internal static void Release(NativeOwnershipRegistry registry, string name)
        {
            try { registry?.UnregisterSites(APISharedPlugin.PluginGuid, name); }
            catch { }
        }

        private static KeyValuePair<string, NativeSiteSpan[]> Group(string name, NativeSiteSpan[] spans) =>
            new KeyValuePair<string, NativeSiteSpan[]>(name, spans);

        private static NativeSiteSpan Exclusive(int rva, int length) => new NativeSiteSpan(rva, length, NativeSiteMode.Exclusive);
        private static NativeSiteSpan Observe(int rva, int length) => new NativeSiteSpan(rva, length, NativeSiteMode.Observe);
    }
}

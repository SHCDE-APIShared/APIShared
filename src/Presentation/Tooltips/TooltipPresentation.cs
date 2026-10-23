using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    /// <summary>Process-lived, multi-owner literal text and symbol extensions of existing in-game tooltips.</summary>
    public static class TooltipPresentation
    {
        private static readonly TooltipRegistry registry = new TooltipRegistry();
        private static readonly Dictionary<TooltipTarget, TooltipContext> contexts = new Dictionary<TooltipTarget, TooltipContext>();
        private static readonly Dictionary<TooltipTarget, TooltipRegistry.Block[]> content = new Dictionary<TooltipTarget, TooltipRegistry.Block[]>();
        private static readonly Action<string, Exception> report = Report;
        private static readonly HashSet<TooltipTarget> missingAnchors = new HashSet<TooltipTarget>();
        private static readonly TooltipTarget[] targets = { TooltipTarget.BuildingCompact, TooltipTarget.BuildingDetailed,
            TooltipTarget.Recruitment, TooltipTarget.SiegeConstruction, TooltipTarget.Repair };
        private static BuildingTooltipPresentation.HudSelection buildingSelection;
        private static ManualLogSource log;
        private static bool initialized, lifecycleSubscribed, collecting, confirmed;
        private static readonly bool[] installed = new bool[6];
        private static MainViewModel hoverOwner;
        private static int? siegeUnit;
        private static bool recruitmentHover;
        private static int retirement;
        private static int appliedRetirement;
        private static TooltipTarget selectedBuildingTarget = TooltipTarget.BuildingCompact;

        /// <summary>Checks the documented semantic placements for a target mask. Description positions require BuildingDetailed alone.</summary>
        public static bool Supports(TooltipTarget targets, TooltipPlacement placement) => TooltipRegistry.Supports(targets, placement);

        /// <summary>
        /// Registers on Unity's main thread after APIShared startup initialization. Owner/ID must be unique
        /// across this service and the legacy building-text adapter. Providers run after completed Vanilla
        /// presentation operations, synchronously outside locks. Return empty to deactivate; no unregister.
        /// Ordering is order, ordinal owner GUID, ordinal ID. Providers must not mutate HUDs or re-enter
        /// presentation. Failed providers lose only their current result; bounded diagnostics allow recovery.
        /// Costs, authorization, localization and resource/quantity calculations remain the provider's responsibility.
        /// </summary>
        public static bool TryRegisterContent(string ownerGuid, string contributionId, TooltipTarget targets,
            TooltipPlacement placement, Func<TooltipContext, IReadOnlyList<TooltipLine>> provider,
            out string reason, int order = 0)
        {
            if (!UnityMainThreadDispatch.IsMainThread || !initialized)
            { reason = "Tooltip registration requires initialized APIShared on Unity's main thread."; return false; }
            if (!Supports(targets, placement)) { reason = "Unsupported tooltip target/placement combination."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(contributionId) || provider == null)
            { reason = "Nonempty owner GUID, contribution ID and provider are required."; return false; }
            if (!Subscribe(targets, out reason)) return false;
            return registry.TryRegister(ownerGuid, contributionId, targets, placement, provider, order, out reason);
        }

        /// <summary>Convenience registration for one literal string. Unchanged strings reuse their immutable line. Other contracts match TryRegisterContent.</summary>
        public static bool TryRegisterText(string ownerGuid, string contributionId, TooltipTarget targets,
            TooltipPlacement placement, Func<TooltipContext, string> provider, out string reason, int order = 0)
        {
            if (provider == null) { reason = "A provider is required."; return false; }
            var adapter = new TooltipTextProvider(provider);
            return TryRegisterContent(ownerGuid, contributionId, targets, placement, adapter.Get, out reason, order);
        }

        internal static void Initialize(ManualLogSource logger, BuildingTooltipPresentation.HudSelection selection)
        {
            log = logger; buildingSelection = selection; initialized = true;
            registry.TryRegister(APISharedPlugin.PluginGuid, "RepairTooltip", TooltipTarget.Repair,
                TooltipPlacement.InlineAfterCosts, _ => ApiSharedRuntime.ProcessInstance.RepairTooltip.Lines,
                0, out _);
        }

        private static bool Subscribe(TooltipTarget requested, out string reason)
        {
            // Install each existing managed publisher once; retries after partial failure never duplicate it.
            for (int i = 0; i < installed.Length; i++)
            {
                bool required = i == 0 || (i == 1 && (requested & (TooltipTarget.BuildingCompact | TooltipTarget.BuildingDetailed | TooltipTarget.Repair)) != 0) ||
                    ((i == 2 || i == 3) && (requested & TooltipTarget.Recruitment) != 0) ||
                    ((i == 4 || i == 5) && (requested & TooltipTarget.SiegeConstruction) != 0);
                if (!required || installed[i]) continue;
                if (!PresentationEvents.TryRegister((PresentationOperation)i, APISharedPlugin.PluginGuid,
                    "TooltipPresentation", null, Presented, out reason, int.MaxValue)) return false;
                installed[i] = true;
            }
            if (lifecycleSubscribed) { reason = string.Empty; return true; }
            if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var missions, out var diagnostic) ||
                !missions.TryRegisterObserver("TooltipPresentation", Retire, Retire, Retire, out diagnostic))
            { reason = "Tooltip mission lifecycle unavailable: " + diagnostic?.Reason; return false; }
            lifecycleSubscribed = true; reason = string.Empty; return true;
        }

        // Lifecycle publishers may use another thread. Advance an epoch immediately; all HUD mutation
        // stays on Unity's thread, including clearing a paused or temporarily absent GUI.
        private static void Retire(MissionLifecycleNotification _)
        {
            Interlocked.Increment(ref retirement);
            UnityMainThreadDispatch.TryRunInlineOrEnqueue(ApplyRetirement);
        }
        private static void ApplyRetirement()
        {
            if (!UnityMainThreadDispatch.IsMainThread) return;
            int current = Volatile.Read(ref retirement);
            if (appliedRetirement == current) return;
            appliedRetirement = current; hoverOwner = null; recruitmentHover = false; siegeUnit = null;
            content.Clear(); contexts.Clear(); TooltipAnchor.ClearAll();
        }

        private static void Presented(PresentationPostEventArgs args)
        {
            if (!registry.HasDemand || !UnityMainThreadDispatch.IsMainThread || collecting) return;
            ApplyRetirement();
            if (!args.OriginalCompleted) { content.Clear(); TooltipAnchor.ClearAll(); return; }
            switch (args.Operation)
            {
                case PresentationOperation.RecruitmentEnter:
                    hoverOwner = args.ViewModel; recruitmentHover = true; siegeUnit = null; break;
                case PresentationOperation.RecruitmentLeave: recruitmentHover = false; break;
                case PresentationOperation.TroopPanelEnter:
                    hoverOwner = args.ViewModel; recruitmentHover = false; siegeUnit = ResolveSiege(args.Parameter as string); break;
                case PresentationOperation.TroopPanelLeave: siegeUnit = null; break;
            }
            Refresh(args.BuildingHud);
        }

        private static void Refresh(HUD_Main caller)
        {
            collecting = true;
            try
            {
                if (!MainViewModel.viewModelLoaded) { content.Clear(); TooltipAnchor.ClearAll(); return; }
                var main = MainViewModel.Instance;
                var hud = caller ?? main.HUDmain;
                if (hud == null || GameData.Instance.lastGameState == null) { content.Clear(); TooltipAnchor.ClearAll(); return; }
                if (hoverOwner != null && !ReferenceEquals(hoverOwner, main)) { recruitmentHover = false; siegeUnit = null; hoverOwner = null; }
                int player = Math.Max(0, GamePlayerManagerAPI.Instance.GetLocalPlayerId());
                bool detailed = main.RolloverBuilding_TooltipVis;
                selectedBuildingTarget = detailed ? TooltipTarget.BuildingDetailed : TooltipTarget.BuildingCompact;
                int building = buildingSelection.Resolve(hud, detailed, out bool hovering);
                bool repair = !detailed && building == 0 && buildingSelection.IsRepair(hud);
                int? unit = recruitmentHover && !string.IsNullOrEmpty(main.lastTroopBuildOver) &&
                    (int)main.lastTroopBuildChimp != (int)eChimps.CHIMP_NUM_TYPES ? (int?)main.lastTroopBuildChimp : null;
                foreach (var target in targets)
                {
                    bool active; int structure = 0; int? chimp = null; int quantity = 1;
                    switch (target)
                    {
                        case TooltipTarget.BuildingCompact: structure = building; active = building != 0 && !detailed && !repair; break;
                        case TooltipTarget.BuildingDetailed: structure = building; active = building != 0 && detailed && !repair; break;
                        case TooltipTarget.Recruitment: chimp = unit; quantity = Math.Max(1, main.lastTroopsAmountToMake); active = unit.HasValue; break;
                        case TooltipTarget.SiegeConstruction: chimp = siegeUnit; active = siegeUnit.HasValue; break;
                        default: active = repair; break;
                    }
                    contexts.TryGetValue(target, out var context);
                    if (context == null || context.IsActive != active || context.BuildingType != structure || context.UnitType != chimp ||
                        context.LocalPlayerId != player || context.Quantity != quantity || context.IsSymbolHover != hovering)
                        contexts[target] = context = new TooltipContext(target, active, structure, chimp, player, quantity, hovering);
                    content[target] = registry.Collect(context, report);
                }
                TooltipAnchor.RefreshAll();
                foreach (var target in targets)
                    if (content[target].Length != 0 && !TooltipAnchor.HasTarget(target) && missingAnchors.Add(target))
                        Report("missing anchor/" + target, new MissingMemberException("No compatible loaded tooltip anchor for " + target));
                if (!confirmed) { confirmed = true; log?.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] TOOLTIP_RUNTIME_CONFIRMED Unity presentation after startup cleanup."); }
            }
            catch (Exception error) { content.Clear(); TooltipAnchor.ClearAll(); Report("presentation", error); }
            finally { collecting = false; }
        }

        // Legacy providers historically run once for the selected variant, including its zero-building retirement.
        internal static bool IsSelectedBuildingTarget(TooltipTarget target) => target == selectedBuildingTarget;
        internal static TooltipRegistry.Block[] GetContent(TooltipTarget target) =>
            content.TryGetValue(target, out var blocks) ? blocks : Array.Empty<TooltipRegistry.Block>();
        internal static void EnsureRepairPresentation()
        {
            if (!UnityMainThreadDispatch.IsMainThread) return;
            if ((!installed[0] || !installed[1] || !lifecycleSubscribed) && !Subscribe(TooltipTarget.Repair, out string reason)) Report("repair registration", new InvalidOperationException(reason));
        }
        internal static void RetireHud()
        {
            hoverOwner = null; recruitmentHover = false; siegeUnit = null; content.Clear(); contexts.Clear();
            TooltipAnchor.ClearAll();
        }
        private static int? ResolveSiege(string parameter)
        {
            switch (parameter)
            {
                case "UnitBuildCat": return (int)eChimps.CHIMP_TYPE_CATAPULT;
                case "UnitBuildTreb": return (int)eChimps.CHIMP_TYPE_TREBUCHET;
                case "UnitBuildRam": return (int)eChimps.CHIMP_TYPE_BATTERING_RAM;
                case "UnitBuildTower": return (int)eChimps.CHIMP_TYPE_SIEGE_TOWER;
                case "UnitbuildMantlet": return (int)eChimps.CHIMP_TYPE_PORTABLE_SHIELD;
                case "UnitbuildArabBallista": return (int)eChimps.CHIMP_TYPE_ARAB_BALLISTA;
                default: return null;
            }
        }
        private static readonly HashSet<string> reported = new HashSet<string>(StringComparer.Ordinal);
        internal static void Report(string owner, Exception error)
        {
            if (reported.Add(owner)) log?.LogError($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Tooltip contribution {owner}: {error}");
        }
    }
}

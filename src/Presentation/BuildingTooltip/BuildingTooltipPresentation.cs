using APIShared.Internal;
using BepInEx.Logging;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.NoesisUtil;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;

namespace APIShared.Presentation
{
    /// <summary>Process-lifetime broker for plain-text additions above unchanged Vanilla building costs.</summary>
    public static class BuildingTooltipPresentation
    {
        private static readonly BuildingTooltipRegistry registry = new BuildingTooltipRegistry();
        private static readonly TooltipView compact = new TooltipView(), detailed = new TooltipView();
        private static readonly Action<string, Exception> report = Report;
        private static ManualLogSource log;
        private static FieldInfo hoverStruct, selectedStruct, hoverString, selectedString, otherVisible, otherPriority;
        private static bool initialized, subscribed, confirmed;
        private static string failure;
        private static BuildingTooltipContext lastContext;
        /// <summary>
        /// Register on Unity's main thread after APIShared initialization. Owner GUID and contribution ID
        /// must be nonempty and unique together. No replay. Providers run synchronously outside registry
        /// locks after BuildingRollover/GuiChecks originals, on the startup-confirmed Unity thread.
        /// Return null/empty lines for logical deactivation. Exceptions hide only the failed contribution
        /// for that update; diagnostics are bounded. Callbacks/registrations live until process exit.
        /// Rows sort top-to-bottom by ascending order, ordinal owner GUID, then ordinal ID (default 0).
        /// Do not retain HUD objects, mutate Vanilla UI or re-enter presentation from providers.
        /// False returns a diagnostic and does not register the contribution. No unregister or hook teardown.
        /// </summary>
        public static bool TryRegister(string ownerGuid, string contributionId,
            Func<BuildingTooltipContext, IReadOnlyList<BuildingTooltipLine>> provider, out string reason, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(contributionId) || provider == null) { reason = "Nonempty owner GUID, contribution ID and provider are required."; return false; }
            if (!UnityMainThreadDispatch.IsMainThread) { reason = "Building tooltip registration requires Unity's main thread."; return false; }
            if (!initialized) { reason = failure ?? "Building tooltip presentation is not initialized."; return false; }
            if (!subscribed)
            {
                foreach (var operation in new[] { PresentationOperation.BuildingRollover, PresentationOperation.GuiChecks })
                    if (!PresentationEvents.TryRegister(operation, APISharedPlugin.PluginGuid, "BuildingTooltipPresentation", null, Presented, out reason, int.MaxValue)) return false;
                subscribed = true;
            }
            return registry.TryRegister(ownerGuid, contributionId, order, provider, out reason);
        }
        internal static void Initialize(ManualLogSource logger)
        {
            log = logger;
            try
            {
                if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Tooltip initialization requires Unity's main thread.");
                hoverStruct = Field("HoverStruct", typeof(int), false); selectedStruct = Field("SelectedStruct", typeof(int), false);
                hoverString = Field("HoverString", typeof(string), true); selectedString = Field("SelectedString", typeof(string), false);
                otherVisible = Field("OtherVisible", typeof(bool), false); otherPriority = Field("OtherHighestPri", typeof(bool), false);
                initialized = true;
            }
            catch (Exception error) { failure = error.Message; Report("initialization", error); }
        }
        internal static void Bind()
        {
            GameXAMLManagerAPI.Instance.RegisterBinding("APISharedBuildingTooltipCompact", compact);
            GameXAMLManagerAPI.Instance.RegisterBinding("APISharedBuildingTooltipDetailed", detailed);
        }
        private static FieldInfo Field(string name, Type type, bool isPublic)
        {
            var field = typeof(HUD_Main).GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != type || field.IsPublic != isPublic) throw new MissingFieldException("Real HUD_Main contract differs: " + name);
            return field;
        }
        private static void Presented(PresentationPostEventArgs args)
        {
            if (!registry.HasDemand || !UnityMainThreadDispatch.IsMainThread) return;
            if (!args.OriginalCompleted) { Clear(); return; }
            var main = MainViewModel.Instance;
            var hud = args.BuildingHud ?? main?.HUDmain;
            if (hud == null || main == null || GameData.Instance.lastGameState == null) { Clear(); return; }
            bool full = main.RolloverBuilding_TooltipVis;
            int building = BuildingTooltipSelection.Resolve((int)hoverStruct.GetValue(hud), (string)hoverString.GetValue(hud),
                (int)selectedStruct.GetValue(hud), (string)selectedString.GetValue(hud), (bool)otherVisible.GetValue(hud),
                (bool)otherPriority.GetValue(hud), full, out bool hovering);
            var variant = full ? BuildingTooltipVariant.Detailed : BuildingTooltipVariant.Compact;
            var context = lastContext;
            if (context == null || context.BuildingType != building || context.Variant != variant || context.IsSymbolHover != hovering)
                lastContext = context = new BuildingTooltipContext(building, variant, hovering);
            var lines = registry.Collect(context, report);
            if (building == 0) { Clear(); return; } // Providers can retire selection state, but no lines render outside building tooltips.
            (full ? detailed : compact).Set(lines);
            (full ? compact : detailed).Set(Array.Empty<BuildingTooltipLine>());
            if (!confirmed) { confirmed = true; log?.LogDebug($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] BUILDING_TOOLTIP_RUNTIME_CONFIRMED Unity post publisher after startup cleanup."); }
        }
        private static void Clear() { compact.Set(Array.Empty<BuildingTooltipLine>()); detailed.Set(Array.Empty<BuildingTooltipLine>()); }
        private static void Report(string owner, Exception error) => log?.LogError("Building tooltip contribution " + owner + ": " + error);
        /// <summary>Shared Noesis binding model; consumers register text through the broker.</summary>
        public sealed class TooltipView : INotifyPropertyChanged, INoesisElementBindingAware
        {
            private FrameworkElement element, root;
            private volatile bool layoutDirty = true;
            internal TooltipView() { }
            /// <summary>Notifies Noesis when rendered text, visibility or available width changes on Unity's thread.</summary>
            public event PropertyChangedEventHandler PropertyChanged;
            /// <summary>Read-only current lines, owned by the service.</summary>
            public IReadOnlyList<BuildingTooltipLine> Lines { get; private set; } = Array.Empty<BuildingTooltipLine>();
            /// <summary>Collapsed when no contributor supplies text, reserving no space.</summary>
            public Visibility Visibility => Lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            /// <summary>Width in the existing Noesis coordinate space, bounded by the screen and Vanilla text width.</summary>
            public double MaxWidth { get; private set; } = 440;
            internal void Set(BuildingTooltipLine[] lines)
            {
                if (lines.Length == 0 && Lines.Count == 0) return;
                if (layoutDirty) UpdateWidth();
                if (BuildingTooltipRegistry.Same(Lines, lines)) return;
                Lines = Array.AsReadOnly(lines);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lines)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Visibility)));
            }
            void INoesisElementBindingAware.OnNoesisElementBound(FrameworkElement host)
            {
                if (!UnityMainThreadDispatch.IsMainThread) return;
                Detach();
                element = host; root = host;
                while (root.Parent is FrameworkElement parent) root = parent;
                root.Unloaded += Unloaded;
                root.SizeChanged += SizeChanged;
                element.SizeChanged += SizeChanged;
                UpdateWidth();
            }
            // Defer VM/geometry work to the next post publisher; size events only invalidate the cache.
            private void SizeChanged(object sender, SizeChangedEventArgs args) => layoutDirty = true;
            private void Detach()
            {
                if (root != null) { root.Unloaded -= Unloaded; root.SizeChanged -= SizeChanged; }
                if (element != null) element.SizeChanged -= SizeChanged;
                root = null; element = null; layoutDirty = true;
            }
            private void Unloaded(object sender, RoutedEventArgs args)
            {
                Detach();
                Set(Array.Empty<BuildingTooltipLine>());
            }
            private void UpdateWidth()
            {
                if (element == null || root == null || root.ActualWidth <= 0) return;
                // Relative rendered position includes Vanilla's compact -250 translation.
                // Keep that anchor; only constrain our own rows to remaining screen space.
                var point = element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, 1, 1));
                double width = Math.Max(1, Math.Min(440, root.ActualWidth - Math.Max(0, point.X) - 10));
                layoutDirty = false;
                if (width == MaxWidth) return;
                MaxWidth = width;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MaxWidth)));
            }
        }
    }
}

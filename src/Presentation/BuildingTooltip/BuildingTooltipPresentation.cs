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
using System.Reflection.Emit;

namespace APIShared.Presentation
{
    /// <summary>Process-lifetime broker for plain-text additions above unchanged Vanilla building costs.</summary>
    public static class BuildingTooltipPresentation
    {
        private static readonly TooltipView compact = new TooltipView(), detailed = new TooltipView();
        private static ManualLogSource log;
        private static HudSelection selection;
        private static string failure;
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
            if (provider == null) { reason = "A provider is required."; return false; }
            var adapter = new LegacyBuildingTooltipProvider(provider);
            return TooltipPresentation.TryRegisterContent(ownerGuid, contributionId,
                TooltipTarget.BuildingCompact | TooltipTarget.BuildingDetailed, TooltipPlacement.BeforeMainLine,
                context => adapter.Get(context, TooltipPresentation.IsSelectedBuildingTarget(context.Target)), out reason, order);
        }

        internal static void Initialize(ManualLogSource logger)
        {
            log = logger;
            try
            {
                if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Tooltip initialization requires Unity's main thread.");
                selection = new HudSelection();
                TooltipPresentation.Initialize(logger, selection);
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
        // GuiChecks and BuildingRollover Post read the six selection fields on every update once a
        // provider is registered. Validate them at startup as before, then compile one typed read
        // per field so the update path performs no reflection or boxing.
        internal sealed class HudSelection
        {
            private readonly Func<HUD_Main, int> hoverStruct, selectedStruct, otherData1;
            private readonly Func<HUD_Main, string> hoverString, selectedString;
            private readonly Func<HUD_Main, bool> otherVisible, otherPriority;
            internal HudSelection()
            {
                otherData1 = Read<int>(Field("OtherData1", typeof(int), false));
                hoverStruct = Read<int>(Field("HoverStruct", typeof(int), false)); selectedStruct = Read<int>(Field("SelectedStruct", typeof(int), false));
                hoverString = Read<string>(Field("HoverString", typeof(string), true)); selectedString = Read<string>(Field("SelectedString", typeof(string), false));
                otherVisible = Read<bool>(Field("OtherVisible", typeof(bool), false)); otherPriority = Read<bool>(Field("OtherHighestPri", typeof(bool), false));
            }
            // Managed HUD_Main.UpdateRollover interprets OtherData1 == 1 as the repair cost rollover.
            internal bool IsRepair(HUD_Main hud) => otherVisible(hud) && otherData1(hud) == 1;
            internal int Resolve(HUD_Main hud, bool detailed, out bool hovering) =>
                BuildingTooltipSelection.Resolve(hoverStruct(hud), hoverString(hud), selectedStruct(hud), selectedString(hud),
                    otherVisible(hud), otherPriority(hud), detailed, out hovering);
            private static Func<HUD_Main, T> Read<T>(FieldInfo field)
            {
                // ldarg.0; ldfld; ret. Owner-associated and visibility-skipping, as the fields are private.
                var method = new DynamicMethod("APIShared.BuildingTooltip.Read" + field.Name, typeof(T), new[] { typeof(HUD_Main) }, typeof(HUD_Main), true);
                ILGenerator il = method.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret);
                return (Func<HUD_Main, T>)method.CreateDelegate(typeof(Func<HUD_Main, T>));
            }
        }


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

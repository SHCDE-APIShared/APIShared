using System;
using System.Collections.Generic;
using CrusaderDE;
using Noesis;

namespace APIShared.Presentation
{
    // A presentation mask only: Vanilla continues writing the original controls and bindings.
    // The owner retains this object until the HUD is replaced or its presentation is reset.
    internal sealed class TroopActionPagingSurface
    {
        private static readonly string[] vanillaNames = {
            "UnitDisband", "UnitBuildCat", "UnitStop", "UnitBuildTreb", "UnitPatrol",
            "UnitPatrolActive", "UnitBuildTower", "UnitAttackHere", "UnitTunnelHere",
            "UnitPourOil", "UnitBuildRam", "UnitBuild", "UnitReload", "UnitbuildMantlet",
            "UnitFireCow", "UnitBack", "UnitbuildArabBallista"
        };
        private static readonly HashSet<DependencyObject> masks = new HashSet<DependencyObject>();
        private readonly Grid controls;
        private readonly Grid vanillaLayer;
        private readonly List<OriginalChild> originalChildren;
        private bool restored;

        private TroopActionPagingSurface(Grid controls,
            Grid vanillaLayer, List<OriginalChild> originalChildren)
        {
            this.controls = controls;
            this.vanillaLayer = vanillaLayer;
            this.originalChildren = originalChildren;
        }

        internal static TroopActionPagingSurface Create(HUD_Troops panel)
        {
            if (!(panel?.FindName("UnitControls") is Grid controls))
                throw new InvalidOperationException("The Vanilla troop action grid is unavailable.");
            var originals = new List<OriginalChild>(vanillaNames.Length);
            foreach (string name in vanillaNames)
            {
                if (!(panel.FindName(name) is FrameworkElement element) ||
                    !ReferenceEquals(element.Parent, controls))
                    throw new InvalidOperationException("The Vanilla troop action is not a direct grid child: " + name);
                originals.Add(new OriginalChild(name, element, controls.Children.IndexOf(element)));
            }
            originals.Sort((left, right) => left.Index.CompareTo(right.Index));
            var layer = new Grid { Name = "APISharedVanillaTroopActionLayer", Visibility = Visibility.Visible };
            var surface = new TroopActionPagingSurface(controls, layer, originals);
            try
            {
                controls.Children.Insert(originals[0].Index, layer);
                masks.Add(layer);
                foreach (OriginalChild child in originals)
                {
                    controls.Children.Remove(child.Element);
                    layer.Children.Add(child.Element);
                }
                // Reparenting must preserve the XAML namescope used by Vanilla and other mods.
                // Do not publish a mask if this Noesis version loses any of those identities.
                foreach (OriginalChild child in originals)
                    if (!ReferenceEquals(panel.FindName(child.Name), child.Element))
                        throw new InvalidOperationException("Troop-action namescope changed while grouping: " + child.Name);
                return surface;
            }
            catch
            {
                surface.Restore();
                throw;
            }
        }

        internal static bool IsVanillaMask(DependencyObject element) => masks.Contains(element);

        internal void SetVanillaHidden(bool hidden)
        {
            if (!restored)
                vanillaLayer.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
        }

        internal TroopActionPageSlots CaptureSlots()
        {
            // The new stretch Grid needs one normal Noesis arrange pass. Never infer free slots
            // from temporarily unarranged children immediately after inserting it.
            if (restored || vanillaLayer.ActualWidth <= 0 || vanillaLayer.ActualHeight <= 0)
                return new TroopActionPageSlots();
            return TroopActionHudLayout.CapturePageSlots(controls);
        }

        internal void Restore()
        {
            if (restored) return;
            vanillaLayer.Visibility = Visibility.Visible;
            // Remove the wrapper before reinsertion: it must not shift the saved sibling
            // indices when foreign controls were interleaved with Vanilla controls.
            if (ReferenceEquals(vanillaLayer.Parent, controls)) controls.Children.Remove(vanillaLayer);
            // Move only our original nodes; foreign siblings are neither hidden nor removed.
            foreach (OriginalChild child in originalChildren)
            {
                if (!ReferenceEquals(child.Element.Parent, vanillaLayer)) continue;
                vanillaLayer.Children.Remove(child.Element);
                controls.Children.Insert(Math.Min(child.Index, controls.Children.Count), child.Element);
            }
            masks.Remove(vanillaLayer);
            restored = true;
        }

        private sealed class OriginalChild
        {
            internal OriginalChild(string name, FrameworkElement element, int index)
            { Name = name; Element = element; Index = index; }
            internal string Name { get; }
            internal FrameworkElement Element { get; }
            internal int Index { get; }
        }
    }

    internal sealed class TroopActionPageSlots
    {
        internal List<Rect> FirstSlots { get; } = new List<Rect>(2);
        internal List<Rect> LaterSlots { get; } = new List<Rect>(6);
        internal Rect FallbackNavigation { get; set; }
        internal bool IsReady { get; set; }
    }
}

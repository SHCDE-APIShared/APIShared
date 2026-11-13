using System;
using System.Collections.Generic;
using System.Globalization;
using CrusaderDE;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    public static partial class AllyGoodsPanelExtensions
    {
        private static readonly AllyGoodsAdornmentRegistry adornmentRegistry = new AllyGoodsAdornmentRegistry();
        private static readonly AllyGoodsAdornmentCache<FrameworkElement> adornmentCache = new AllyGoodsAdornmentCache<FrameworkElement>();
        private static readonly List<GoodsAnchor> goodsAnchors = new List<GoodsAnchor>();
        private static readonly Dictionary<Panel, Canvas> goodsOverlays = new Dictionary<Panel, Canvas>();
        private static bool adornmentsResolved, adornmentsDirty = true;

        /// <summary>Registers compact, noninteractive markers on the existing goods buttons. Requires the initialized
        /// Unity thread after Extender readiness. Contributions sort by ascending order, ordinal owner GUID and ID;
        /// duplicate owner/ID pairs within adornments return false. Section IDs are independent.</summary>
        /// <remarks>The factory runs once per displayed ware and bound host, outside locks. Return a fresh unattached
        /// FrameworkElement, preferably a small icon. Its DataContext and Visibility are consumer-owned; update them
        /// on Unity and call RequestRefresh. Successful controls survive selection changes and refresh requests.
        /// Factories receive a factory-time context whose Host supplies inherited resources; do not mutate that host.
        /// Null, attached or throwing results suppress only that owner's ware until refresh or host recreation.
        /// Markers wrap inside the actual button bounds, are clipped to those bounds, cannot receive input, and follow
        /// the existing goods raster's visibility in send/request modes. No Vanilla content, command or binding is replaced.
        /// Registration lasts until process exit. This API supplies no gameplay state, permissions or commands.</remarks>
        public static bool TryRegisterGoodsAdornment(string ownerGuid, string registrationId,
            Func<AllyGoodsSectionContext, eGoods, FrameworkElement> factory,
            out AllyGoodsAdornmentRegistration registration, out string reason, int order = 0)
        {
            registration = null;
            if (!EnsureInitialized(out reason)) return false;
            if (!adornmentRegistry.TryRegister(ownerGuid, registrationId, factory, order, out var entry, out reason)) return false;
            registration = new AllyGoodsAdornmentRegistration(entry, InvalidateAdornments);
            adornmentsDirty = true; return true;
        }

        private static void InvalidateAdornments() => adornmentsDirty = true;

        // The existing permanent GUI publisher owns this path; no component Update or new hook is needed.
        // Scan only once per bound host. Geometry follows real anchors, including foreign layout changes.
        private static void UpdateAdornments(AllyGoodsPanelContext context)
        {
            if (adornmentRegistry.Entries.Length == 0 || !context.IsOpen) return;
            if (!adornmentsResolved)
            {
                var main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
                var root = main?.HUDAlliesPanel?.FindName("LayoutRoot") as Grid;
                if (root == null || !ReferenceEquals(host.Parent, root)) return;
                ResolveGoodsAnchors(root); adornmentsResolved = true; adornmentsDirty = true;
            }
            if (adornmentsDirty) RebuildAdornments(context);
            foreach (var anchor in goodsAnchors)
            {
                try { PositionAdornment(anchor); }
                catch (Exception error)
                {
                    anchor.Slots.Visibility = Visibility.Collapsed;
                    Report("adornment-layout:" + anchor.Good, error);
                }
            }
        }

        // Separate method: the factory lambdas capture the context, and inside UpdateAdornments the
        // compiler would allocate that closure at method entry, ahead of its per-GUI-check early returns.
        private static void RebuildAdornments(AllyGoodsPanelContext context)
        {
            // Factories may register other providers or request refresh: process this immutable snapshot,
            // retaining the dirty flag for the next publication in that case.
            adornmentsDirty = false;
            var entries = adornmentRegistry.Entries;
            foreach (var anchor in goodsAnchors)
            {
                int slot = 0;
                foreach (var entry in entries)
                {
                    var element = adornmentCache.GetOrCreate(entry, anchor.Good,
                        () => CreateAdornment(entry, anchor, context),
                        error => Report("adornment-factory:" + entry.Owner + "/" + entry.Id + "/" + anchor.Good, error));
                    if (element == null) continue;
                    if (element.Parent != null && !ReferenceEquals(element.Parent, anchor.Slots))
                    {
                        Report("adornment-parent:" + entry.Owner + "/" + entry.Id + "/" + anchor.Good,
                            new InvalidOperationException("An adornment control was attached by another UI owner."));
                        continue;
                    }
                    // Refresh does not detach/reload an unchanged successful control.
                    if (slot >= anchor.Slots.Children.Count || !ReferenceEquals(anchor.Slots.Children[slot], element))
                    {
                        if (ReferenceEquals(element.Parent, anchor.Slots)) anchor.Slots.Children.Remove(element);
                        anchor.Slots.Children.Insert(slot, element);
                    }
                    slot++;
                }
                while (anchor.Slots.Children.Count > slot) anchor.Slots.Children.RemoveAt(anchor.Slots.Children.Count - 1);
            }
        }

        private static FrameworkElement CreateAdornment(AllyGoodsAdornmentEntry entry, GoodsAnchor anchor, AllyGoodsPanelContext context)
        {
            var element = entry.Factory(new AllyGoodsSectionContext(anchor.Slots, context), anchor.Good);
            if (element == null || element.Parent != null)
                throw new InvalidOperationException("Adornment factories must return a fresh unattached FrameworkElement.");
            // The entire overlay excludes pointer and keyboard navigation, including templated descendants.
            element.IsHitTestVisible = false; element.Focusable = false;
            return element;
        }

        private static void ResolveGoodsAnchors(DependencyObject root)
        {
            var buttons = new Dictionary<eGoods, Button>();
            var ambiguous = new HashSet<eGoods>();
            FindGoodsButtons(root, buttons, ambiguous);
            foreach (var item in buttons)
            {
                if (ambiguous.Contains(item.Key)) continue;
                // Only Grid parents have nonintrusive overlay semantics (a StackPanel would gain a row).
                if (!(item.Value.Parent is Grid parent)) continue;
                if (!goodsOverlays.TryGetValue(parent, out var overlay))
                {
                    overlay = new Canvas { IsHitTestVisible = false, Focusable = false };
                    KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.None);
                    KeyboardNavigation.SetControlTabNavigation(overlay, KeyboardNavigationMode.None);
                    KeyboardNavigation.SetDirectionalNavigation(overlay, KeyboardNavigationMode.None);
                    Grid.SetRowSpan(overlay, Math.Max(1, parent.RowDefinitions.Count));
                    Grid.SetColumnSpan(overlay, Math.Max(1, parent.ColumnDefinitions.Count));
                    Panel.SetZIndex(overlay, 10000); parent.Children.Add(overlay); goodsOverlays.Add(parent, overlay);
                }
                var slots = new WrapPanel { Orientation = Orientation.Horizontal, IsHitTestVisible = false,
                    Focusable = false, ClipToBounds = true };
                overlay.Children.Add(slots);
                goodsAnchors.Add(new GoodsAnchor(item.Key, item.Value, parent, slots));
            }
            if (goodsAnchors.Count == 0)
                Report("adornment-anchors", new InvalidOperationException("No unambiguous Gxx goods buttons with Grid parents were found; Vanilla is preserved."));
        }

        private static void FindGoodsButtons(DependencyObject node, Dictionary<eGoods, Button> buttons, HashSet<eGoods> ambiguous)
        {
            if (node is Button button && TryParseGoodsCommand(button.CommandParameter as string, out var good))
            {
                if (buttons.ContainsKey(good)) ambiguous.Add(good);
                else buttons.Add(good, button);
            }
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) FindGoodsButtons(VisualTreeHelper.GetChild(node, i), buttons, ambiguous);
        }

        internal static bool TryParseGoodsCommand(string command, out eGoods good)
        {
            good = default;
            if (command == null || command.Length != 3 || command[0] != 'G' ||
                command[1] < '0' || command[1] > '9' || command[2] < '0' || command[2] > '9') return false;
            int value = int.Parse(command.Substring(1), CultureInfo.InvariantCulture);
            var parsed = AllyGoodsContextPolicy.Good(value);
            if (!parsed.HasValue) return false;
            good = parsed.Value; return true;
        }

        private static void PositionAdornment(GoodsAnchor anchor)
        {
            if (!ReferenceEquals(anchor.Button.Parent, anchor.Parent) || anchor.Button.ActualWidth <= 0 || anchor.Button.ActualHeight <= 0)
            { anchor.Slots.Visibility = Visibility.Collapsed; return; }
            var bounds = anchor.Button.TransformToAncestor(anchor.Parent).TransformBounds(
                new Rect(0, 0, anchor.Button.ActualWidth, anchor.Button.ActualHeight));
            Canvas.SetLeft(anchor.Slots, bounds.X); Canvas.SetTop(anchor.Slots, bounds.Y);
            anchor.Slots.Width = bounds.Width; anchor.Slots.Height = bounds.Height;
            anchor.Slots.Visibility = anchor.Button.Visibility;
        }

        private static void DetachAdornments()
        {
            foreach (var pair in goodsOverlays) pair.Key.Children.Remove(pair.Value);
            goodsOverlays.Clear(); goodsAnchors.Clear(); adornmentCache.Clear();
            adornmentsResolved = false; adornmentsDirty = true;
        }

        private sealed class GoodsAnchor
        {
            internal readonly eGoods Good;
            internal readonly Button Button;
            internal readonly Grid Parent;
            internal readonly WrapPanel Slots;
            internal GoodsAnchor(eGoods good, Button button, Grid parent, WrapPanel slots)
            { Good = good; Button = button; Parent = parent; Slots = slots; }
        }
    }
}

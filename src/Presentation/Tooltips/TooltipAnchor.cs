using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Noesis;

namespace APIShared.Presentation
{
    /// <summary>APIShared XAML anchors on existing Vanilla TextBlocks. Consumers register content through TooltipPresentation.</summary>
    public static class TooltipAnchor
    {
        private static readonly ConditionalWeakTable<TextBlock, Host> hosts = new ConditionalWeakTable<TextBlock, Host>();
        private static readonly List<Host> live = new List<Host>();
        /// <summary>Internal layout identity; use only APIShared's supplied patches, not consumer XAML.</summary>
        public static readonly DependencyProperty AnchorProperty = DependencyProperty.RegisterAttached("Anchor", typeof(string), typeof(TooltipAnchor),
            new PropertyMetadata(string.Empty, Changed));
        /// <summary>Reads the APIShared anchor identity.</summary>
        public static string GetAnchor(DependencyObject value) => (string)value.GetValue(AnchorProperty);
        /// <summary>Sets the APIShared anchor identity on Unity's thread.</summary>
        public static void SetAnchor(DependencyObject value, string anchor) => value.SetValue(AnchorProperty, anchor);
        private static void Changed(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            if (!(value is TextBlock text)) return;
            if (!hosts.TryGetValue(text, out Host host)) { host = new Host(text); hosts.Add(text, host); }
            host.Name = args.NewValue as string ?? string.Empty;
            host.Attach();
        }
        internal static void RefreshAll()
        {
            // Size/Loaded events never run collection recursively. Each host isolates its own renderer failure.
            for (int i = 0; i < live.Count; i++)
                try { live[i].Refresh(); }
                catch (Exception error) { try { live[i].Clear(); } catch { } TooltipPresentation.Report("anchor/" + live[i].Name, error); }
        }
        internal static void ClearAll()
        {
            for (int i = 0; i < live.Count; i++)
                try { live[i].Clear(); } catch (Exception error) { TooltipPresentation.Report("anchor cleanup", error); }
        }
        internal static bool HasTarget(TooltipTarget target)
        {
            foreach (var host in live)
            {
                string name = host.Name;
                if ((target == TooltipTarget.BuildingCompact || target == TooltipTarget.Repair) && name == "BuildingCompact") return true;
                if (target == TooltipTarget.BuildingDetailed && name == "BuildingDetailed") return true;
                if (target == TooltipTarget.Recruitment && name == "Recruitment") return true;
                if (target == TooltipTarget.SiegeConstruction && name == "SiegeConstruction") return true;
            }
            return false;
        }

        private sealed class Host
        {
            internal readonly TextBlock Anchor;
            internal string Name;
            private readonly List<Inline> owned = new List<Inline>();
            private readonly Dictionary<TextBlock, float> rowWidthLimits = new Dictionary<TextBlock, float>();
            private readonly List<TooltipLine> beforeLines = new List<TooltipLine>(), afterLines = new List<TooltipLine>(), inlineLines = new List<TooltipLine>();
            private TooltipRegistry.Block[] last, lastRepair;
            private StackPanel before, after, stackParent;
            private Canvas overlay;
            private Panel root;
            private bool attached, widthOwned;
            private float originalMaxWidth, appliedMaxWidth, lastFontSize;
            private FlowDirection lastFlowDirection;
            private TextWrapping originalWrapping;
            internal Host(TextBlock anchor)
            {
                Anchor = anchor;
                anchor.Loaded += Loaded;
                anchor.Unloaded += Unloaded;
            }
            private void Loaded(object sender, RoutedEventArgs args) => Attach();
            internal void Attach()
            {
                if (attached || !Anchor.IsLoaded) return;
                attached = true; live.Add(this);
                root = TooltipLayout.FindRoot<FrameworkElement>(Anchor, current => current.Parent,
                    current => current is Panel, current => current is UserControl) as Panel;
                // Rendering waits for the completed GUI publisher; Loaded must not mutate a loading tree.
            }
            private void Unloaded(object sender, RoutedEventArgs args)
            {
                try { Clear(); } catch (Exception error) { TooltipPresentation.Report("anchor unload/" + Name, error); }
                finally
                {
                    attached = false; live.Remove(this); root = null;
                    TooltipPresentation.RetireHud();
                }
            }
            internal void Refresh()
            {
                string name = Name;
                TooltipTarget target;
                switch (name)
                {
                    case "BuildingCompact": target = TooltipTarget.BuildingCompact; break;
                    case "BuildingDetailed":
                    case "BuildingDescription": target = TooltipTarget.BuildingDetailed; break;
                    case "Recruitment": target = TooltipTarget.Recruitment; break;
                    case "SiegeConstruction": target = TooltipTarget.SiegeConstruction; break;
                    default: Clear(); return;
                }
                var blocks = Anchor.IsVisible ? TooltipPresentation.GetContent(target) : Array.Empty<TooltipRegistry.Block>();
                var repair = name == "BuildingCompact" && Anchor.IsVisible ? TooltipPresentation.GetContent(TooltipTarget.Repair) : Array.Empty<TooltipRegistry.Block>();
                // Vanilla may rebuild a TextBlock's inlines. Reattach only our lost suffix, never reset its content.
                bool lost = lastFontSize != Anchor.FontSize || lastFlowDirection != Anchor.FlowDirection;
                foreach (var item in owned) if (!Anchor.Inlines.Contains(item)) { lost = true; break; }
                if (!ReferenceEquals(last, blocks) || !ReferenceEquals(lastRepair, repair) || lost)
                {
                    Clear(); last = blocks; lastRepair = repair;
                    lastFontSize = Anchor.FontSize; lastFlowDirection = Anchor.FlowDirection;
                    Select(blocks, name == "BuildingDescription"); Select(repair, false);
                    if (beforeLines.Count != 0 || afterLines.Count != 0) BuildGroups();
                    for (int i = 0; i < inlineLines.Count; i++)
                    {
                        // No permanent empty InlineUIContainer: complete removal restores Vanilla's empty geometry.
                        AddOwned(i == 0 ? (Inline)new Run("  ") : new LineBreak());
                        Append(Anchor, inlineLines[i], owned);
                    }
                    if (inlineLines.Count > 0 && !widthOwned)
                    { originalMaxWidth = appliedMaxWidth = Anchor.MaxWidth; originalWrapping = Anchor.TextWrapping; widthOwned = true; Anchor.TextWrapping = TextWrapping.Wrap; }
                }
                UpdateGeometry();
            }
            private void Select(TooltipRegistry.Block[] blocks, bool description)
            {
                foreach (var block in blocks)
                {
                    if (description)
                    {
                        if (block.Placement == TooltipPlacement.BeforeDescription) beforeLines.Add(block.Line);
                        if (block.Placement == TooltipPlacement.AfterDescription) afterLines.Add(block.Line);
                    }
                    else switch (block.Placement)
                    {
                        case TooltipPlacement.BeforeMainLine: beforeLines.Add(block.Line); break;
                        case TooltipPlacement.AfterMainLine: afterLines.Add(block.Line); break;
                        case TooltipPlacement.InlineAfterCosts: inlineLines.Add(block.Line); break;
                    }
                }
            }
            private void AddOwned(Inline inline) { Anchor.Inlines.Add(inline); owned.Add(inline); }
            internal void Clear()
            {
                foreach (var item in owned) if (Anchor.Inlines.Contains(item)) Anchor.Inlines.Remove(item);
                owned.Clear();
                if (widthOwned)
                {
                    if (Anchor.MaxWidth == appliedMaxWidth) Anchor.MaxWidth = originalMaxWidth;
                    if (Anchor.TextWrapping == TextWrapping.Wrap) Anchor.TextWrapping = originalWrapping;
                    widthOwned = false;
                }
                beforeLines.Clear(); afterLines.Clear(); inlineLines.Clear(); last = lastRepair = null;
                RemoveGroups();
            }
            private void RemoveGroups()
            {
                if (stackParent != null)
                { if (before != null) stackParent.Children.Remove(before); if (after != null) stackParent.Children.Remove(after); }
                if (overlay != null && root != null) root.Children.Remove(overlay);
                before = after = null; stackParent = null; overlay = null; rowWidthLimits.Clear();
            }
            private void BuildGroups()
            {
                before = Group(beforeLines); after = Group(afterLines);
                if (Anchor.Parent is StackPanel parent && parent.Orientation == Orientation.Vertical)
                {
                    // Building descriptions/backgrounds expand in their existing StackPanel. Keep the compact -250 transform.
                    stackParent = parent; int index = parent.Children.IndexOf(Anchor);
                    before.RenderTransform = Anchor.RenderTransform; after.RenderTransform = Anchor.RenderTransform;
                    parent.Children.Insert(index, before); parent.Children.Insert(index + 2, after);
                }
                else if (root != null)
                {
                    // Recruitment texts are positioned directly in a Grid. An attached overlay grows above/below
                    // their actual bounds instead of multiplying hard-coded -44/-68 offsets for every mod.
                    overlay = new Canvas { IsHitTestVisible = false };
                    Panel.SetZIndex(overlay, 10000);
                    overlay.Children.Add(before); overlay.Children.Add(after); root.Children.Add(overlay);
                }
            }
            private StackPanel Group(List<TooltipLine> lines)
            {
                var group = new StackPanel { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left,
                    Visibility = lines.Count == 0 ? Visibility.Collapsed : Visibility.Visible, FlowDirection = Anchor.FlowDirection };
                foreach (var line in lines)
                {
                    var text = new TextBlock { FontFamily = Anchor.FontFamily, FontSize = TooltipLayout.RowFontSize(line, Anchor.FontSize), Foreground = Anchor.Foreground,
                        Background = Anchor.Background, Padding = Anchor.Padding, FlowDirection = Anchor.FlowDirection,
                        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false };
                    rowWidthLimits[text] = TooltipLayout.RowWidthLimit(line);
                    text.Effect = Anchor.Effect; Append(text, line, null); group.Children.Add(text);
                }
                return group;
            }
            private void UpdateGeometry()
            {
                if (root == null || root.ActualWidth <= 0 || (!widthOwned && before == null)) return;
                var bounds = Anchor.TransformToAncestor(root).TransformBounds(new Rect(0, 0, Anchor.ActualWidth, Anchor.ActualHeight));
                float width = Math.Max(1, root.ActualWidth - Math.Max(0, bounds.X) - 10);
                if (widthOwned)
                {
                    // Preserve later foreign width changes instead of restoring a stale pre-hover value.
                    if (Anchor.MaxWidth != appliedMaxWidth) originalMaxWidth = Anchor.MaxWidth;
                    appliedMaxWidth = Math.Min(originalMaxWidth, width);
                    if (Anchor.MaxWidth != appliedMaxWidth) Anchor.MaxWidth = appliedMaxWidth;
                }
                if (before != null) SetWidth(before, width);
                if (after != null) SetWidth(after, width);
                if (overlay != null)
                {
                    Canvas.SetLeft(before, bounds.X); Canvas.SetTop(before, bounds.Y - before.ActualHeight);
                    Canvas.SetLeft(after, bounds.X); Canvas.SetTop(after, bounds.Y + Anchor.ActualHeight);
                }
            }
            private void SetWidth(StackPanel group, float width)
            {
                group.MaxWidth = width;
                foreach (var child in group.Children) if (child is TextBlock text) text.MaxWidth = Math.Min(width, rowWidthLimits[text]);
            }
            private static void Append(TextBlock target, TooltipLine line, List<Inline> tracking)
            {
                foreach (var segment in line.Segments)
                {
                    Inline inline;
                    if (segment is TooltipText text)
                    {
                        var run = new Run(text.Text);
                        if (text.ForegroundArgb.HasValue)
                        { uint c = text.ForegroundArgb.Value; run.Foreground = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c)); }
                        inline = run;
                    }
                    else if (segment is TooltipSymbol symbol)
                    {
                        inline = symbol.Source == null ? (Inline)new Run(symbol.FallbackText) : new InlineUIContainer(new Image {
                            Source = symbol.Source, Height = Math.Max(1, target.FontSize * 0.9f), Stretch = Stretch.Uniform,
                            Margin = new Thickness(2, 0, 2, -3), IsHitTestVisible = false });
                    }
                    else throw new ArgumentException("Unknown tooltip segment.");
                    target.Inlines.Add(inline); tracking?.Add(inline);
                }
            }
        }
    }
}

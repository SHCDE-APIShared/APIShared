using System;
using Noesis;

namespace APIShared.Presentation
{
    public static partial class AllyGoodsPanelExtensions
    {
        private static readonly AllyGoodsFrameRegistry frameRegistry = new AllyGoodsFrameRegistry();
        private static AllyGoodsFrameEntry selectedFrame;
        private static int selectedFrameRevision;
        private static FrameworkElement frameSource;
        private static AllyGoodsFrameOpacityLease frameLease;
        private static bool frameBlocked;
        private static AllyGoodsFrameOptions surfaceFrameOptions;
        private static Brush standardAnchoredBackground, standardAnchoredBorder;
        private static Thickness standardAnchoredPadding, standardAnchoredBorderThickness;

        /// <summary>Registers a process-lifetime, initially disabled presentation provider on initialized
        /// Unity after APIShared/Extender readiness. Nonempty owner/ID must be unique in the independent
        /// frame namespace. Enabled providers sort by order, ordinal owner and ID; first wins the shared
        /// GoodsDialogBottom group. Factories run outside locks on Unity, return a fresh unattached
        /// decoration (or null to decline), and own all design/resources. The broker assigns its bounds,
        /// disables hit testing/focus and reserves supplied overflow; it never measures detached controls.
        /// A successful decoration is cached per HUD/revision. Failure/decline retains the original frame
        /// and standard section layout until explicit refresh or host replacement, without provider failover.
        /// Original geometry and bindings are preserved; only an unbound opacity is temporarily leased.
        /// Provider switching, closure and failures restore owned changes. No gameplay authority or hooks.</summary>
        public static bool TryRegisterFramePresentation(string ownerGuid, string registrationId,
            Func<AllyGoodsFrameContext, FrameworkElement> factory, AllyGoodsFrameOptions options,
            out AllyGoodsFrameRegistration registration, out string reason, int order = 0)
        {
            registration = null;
            if (!EnsureInitialized(out reason)) return false;
            if (!frameRegistry.TryRegister(ownerGuid, registrationId, factory, options, order, out var entry, out reason)) return false;
            registration = new AllyGoodsFrameRegistration(entry); InvalidateFrame(); return true;
        }
        internal static void InvalidateFrame() { dirty = true; }

        private static AllyGoodsFrameEntry PrepareFramePresentation(bool eligible)
        {
            var candidate = eligible ? frameRegistry.FirstEnabled() : null;
            if (!ReferenceEquals(selectedFrame, candidate) ||
                candidate != null && (selectedFrameRevision != candidate.Revision || !ReferenceEquals(frameSource, goodsFrame)))
            {
                if (!RestoreFramePresentation()) return null;
                selectedFrame = candidate;
                selectedFrameRevision = candidate?.Revision ?? 0; frameSource = goodsFrame; frameBlocked = false;
            }
            if (candidate == null || frameBlocked) return null;
            if (frameLease != null && !frameLease.IsOwned)
            {
                // A foreign writer wins, including a new opacity binding. Do not
                // reacquire on every GUI pass after observing loss of ownership.
                RestoreFramePresentation(); frameBlocked = true; return null;
            }
            if (!ReferenceEquals(goodsFrame.Parent, layout) ||
                BindingOperations.GetBindingBase(goodsFrame, UIElement.OpacityProperty) != null) return null;
            if (candidate.AttemptRevision != candidate.Revision)
            {
                // A refreshed factory must inspect the original opacity, not our zero.
                if (!RestoreFramePresentation()) return null;
                if (candidate.Visual?.Parent is Panel oldParent) oldParent.Children.Remove(candidate.Visual);
                candidate.Visual = null; candidate.AttemptRevision = candidate.Revision;
                try
                {
                    var visual = candidate.Factory(new AllyGoodsFrameContext(layout, goodsFrame, Capture()));
                    if (visual != null)
                    {
                        if (visual.Parent != null || ReferenceEquals(visual, goodsFrame))
                            throw new InvalidOperationException("Frame factories must return a fresh unattached decoration.");
                        visual.IsHitTestVisible = false; visual.Focusable = false;
                        KeyboardNavigation.SetTabNavigation(visual, KeyboardNavigationMode.None);
                        KeyboardNavigation.SetControlTabNavigation(visual, KeyboardNavigationMode.None);
                        KeyboardNavigation.SetDirectionalNavigation(visual, KeyboardNavigationMode.None);
                        visual.HorizontalAlignment = HorizontalAlignment.Left; visual.VerticalAlignment = VerticalAlignment.Top;
                        visual.Visibility = Visibility.Collapsed; candidate.Visual = visual;
                    }
                }
                catch (Exception error) { Report("frame-factory:" + candidate.Owner + "/" + candidate.Id, error); }
            }
            return candidate.Visual == null ? null : candidate;
        }
        private static void SetFrameSurface(AllyGoodsFrameOptions options)
        {
            if (ReferenceEquals(options, surfaceFrameOptions) || anchoredSurface == null) return;
            surfaceFrameOptions = options;
            anchoredSurface.Background = options == null ? standardAnchoredBackground : null;
            anchoredSurface.BorderBrush = options == null ? standardAnchoredBorder : null;
            anchoredSurface.BorderThickness = options == null ? standardAnchoredBorderThickness : new Thickness(0);
            anchoredSurface.Padding = options == null ? standardAnchoredPadding : options.ContentPadding;
            float spacing = options?.SectionSpacing ?? 8;
            for (int index = 0; index < anchoredSections.Children.Count; index++)
                if (anchoredSections.Children[index] is FrameworkElement child)
                    SetLayoutMargin(child, new Thickness(0, index == 0 ? 0 : spacing, 0, 0));
        }
        private static bool ApplyFramePresentation(AllyGoodsFrameEntry entry, AllyGoodsPanelLayoutPlan plan)
        {
            if (entry == null || plan.GoodsHeight <= 0) { RestoreFramePresentation(); return true; }
            var visual = entry.Visual;
            // Direct root siblings give decoration and anchor the same coordinate system
            // and paint order. Unsupported replacement hierarchies retain standard layout.
            if (!ReferenceEquals(goodsFrame.Parent, layout)) return false;
            if (visual.Parent != null && !ReferenceEquals(visual.Parent, layout)) return false;
            var bounds = goodsFrame.TransformToAncestor(layout).TransformBounds(new Rect(0, 0, goodsFrame.ActualWidth, goodsFrame.ActualHeight));
            if (visual.Parent == null)
            {
                int index = 0;
                while (index < layout.Children.Count && !ReferenceEquals(layout.Children[index], goodsFrame)) index++;
                layout.Children.Insert(index, visual);
            }
            SetLayoutMargin(visual, new Thickness(bounds.X, bounds.Y, 0, 0));
            SetLayoutWidth(visual, bounds.Width); SetLayoutHeight(visual, plan.GoodsTop + plan.GoodsHeight - bounds.Y);
            if (frameLease == null) frameLease = new AllyGoodsFrameOpacityLease(new FrameOpacityTarget(goodsFrame));
            if (!frameLease.TryAcquire()) return false;
            SetLayoutVisibility(visual, Visibility.Visible); return true;
        }
        private static bool RestoreFramePresentation()
        {
            try
            {
                if (selectedFrame?.Visual != null) SetLayoutVisibility(selectedFrame.Visual, Visibility.Collapsed);
                frameLease?.Release(); frameLease = null; return true;
            }
            catch (Exception error)
            {
                // Retain a failed restoration lease for the next GUI/closure pass.
                // Never start another provider while an owned original is hidden.
                frameBlocked = true; Report("frame-restore", error); return false;
            }
        }
        private static void DetachFramePresentation()
        {
            RestoreFramePresentation();
            foreach (var entry in frameRegistry.Entries)
            {
                if (entry.Visual?.Parent is Panel parent) parent.Children.Remove(entry.Visual);
                entry.Visual = null; entry.AttemptRevision = -1;
            }
            selectedFrame = null; frameSource = null; frameBlocked = false; surfaceFrameOptions = null;
            frameLease = null;
            standardAnchoredBackground = standardAnchoredBorder = null;
        }
        private sealed class FrameOpacityTarget : IAllyGoodsFrameOpacity
        {
            private readonly FrameworkElement element;
            internal FrameOpacityTarget(FrameworkElement element) { this.element = element; }
            public bool HasBinding => BindingOperations.GetBindingBase(element, UIElement.OpacityProperty) != null;
            public object LocalValue => element.ReadLocalValue(UIElement.OpacityProperty);
            public void SetHidden() => element.Opacity = 0;
            public void Restore(object previous)
            {
                if (ReferenceEquals(previous, DependencyProperty.UnsetValue)) element.ClearValue(UIElement.OpacityProperty);
                else element.SetValue(UIElement.OpacityProperty, previous);
            }
        }
    }
}

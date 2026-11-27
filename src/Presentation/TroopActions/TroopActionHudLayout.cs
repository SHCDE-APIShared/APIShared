// Layout for independently installed troop-action buttons (shared cross-mod host convention).
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace APIShared.Presentation
{
    internal static class TroopActionHudLayout
    {
        public const int ButtonWidth = 36;
        public const int ButtonHeight = 37;
        private const string LayoutVersionProperty = "TroopActionLayoutVersion";
        private const string ActionIdProperty = "TroopActionId";
        private const string PriorityProperty = "TroopActionPriority";
        private const string WantsVisibilityProperty = "WantsVisibility";
        private const string LayoutAvailableProperty = "LayoutAvailable";
        private static readonly Thickness BottomRightMargin = new Thickness(80, 40, 0, 3);
        private static readonly Thickness BottomMiddleMargin = new Thickness(1, 40, 2, 3);
        private static readonly HashSet<string> invalidLogged = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> duplicateLogged = new HashSet<string>(StringComparer.Ordinal);

        public static void Reflow(HUD_Troops troopPanel, ManualLogSource log)
        {
            if (troopPanel == null)
                return;

            FrameworkElement controls = troopPanel.FindName("UnitControls") as FrameworkElement;
            if (controls == null)
                return;

            var hosts = new List<ActionHost>();
            var invalidHosts = new List<string>();
            CollectActionHosts(controls, hosts, invalidHosts);
            if (hosts.Count == 0 && invalidHosts.Count == 0)
                return;

            // Hidden hosts can retain stale screen transforms in Noesis. The working Knight
            // layout uses only its XAML margin, so every shared host returns to that baseline.
            for (int index = 0; index < hosts.Count; index++)
                hosts[index].Element.RenderTransform = new TranslateTransform(0f, 0f);

            var occupied = new List<ScreenRectangle>();
            CollectOccupiedRectangles(controls, occupied);
            bool rightGeometryAvailable = TryGetSlotRectangle(controls, BottomRightMargin, out ScreenRectangle rightRectangle);
            bool middleGeometryAvailable = TryGetSlotRectangle(controls, BottomMiddleMargin, out ScreenRectangle middleRectangle);
            bool rightOccupied = !rightGeometryAvailable || IntersectsAny(rightRectangle, occupied);
            bool middleOccupied = !middleGeometryAvailable || IntersectsAny(middleRectangle, occupied);

            var requests = new List<TroopActionRequest>(hosts.Count);
            for (int index = 0; index < hosts.Count; index++)
            {
                ActionHost host = hosts[index];
                requests.Add(new TroopActionRequest(host.ActionId, host.Priority, host.WantsVisibility));
                if (host.WantsVisibility)
                    SetLayoutAvailable(host, false);
                else
                    SetLayoutAvailable(host, true);
            }

            TroopActionLayoutDecision decision = TroopActionHudLayoutPolicy.CreateDecision(
                requests,
                rightOccupied,
                middleOccupied);
            var assignmentsById = new Dictionary<string, TroopActionSlot>(StringComparer.Ordinal);
            for (int index = 0; index < decision.Assignments.Count; index++)
            {
                TroopActionSlotAssignment assignment = decision.Assignments[index];
                assignmentsById[assignment.ActionId] = assignment.Slot;
            }

            for (int index = 0; index < hosts.Count; index++)
            {
                ActionHost host = hosts[index];
                if (!host.WantsVisibility || !assignmentsById.TryGetValue(host.ActionId, out TroopActionSlot slot))
                    continue;

                host.Element.Margin = slot == TroopActionSlot.BottomRight
                    ? BottomRightMargin
                    : BottomMiddleMargin;
                host.Element.RenderTransform = new TranslateTransform(0f, 0f);
                SetLayoutAvailable(host, true);
            }

            LogInvalidHosts(log, invalidHosts);
            LogDuplicateActions(log, decision.DuplicateActionIds);
        }

        private static void CollectActionHosts(
            DependencyObject parent,
            List<ActionHost> result,
            List<string> invalidHosts)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (child is FrameworkElement element &&
                    TroopActionHudLayoutPolicy.IsStandardHostName(element.Name))
                {
                    try
                    {
                        if (TryResolveActionHost(element, out ActionHost host))
                            result.Add(host);
                        else
                            invalidHosts.Add(element.Name ?? "<unnamed>");
                    }
                    catch
                    {
                        invalidHosts.Add(element.Name ?? "<unnamed>");
                    }
                    continue;
                }
                CollectActionHosts(child, result, invalidHosts);
            }
        }

        private static bool TryResolveActionHost(FrameworkElement element, out ActionHost host)
        {
            host = null;
            object viewModel = element.DataContext;
            if (viewModel == null)
                return false;

            Type type = viewModel.GetType();
            PropertyInfo wantsProperty = type.GetProperty(WantsVisibilityProperty, BindingFlags.Instance | BindingFlags.Public);
            PropertyInfo layoutProperty = type.GetProperty(LayoutAvailableProperty, BindingFlags.Instance | BindingFlags.Public);
            if (wantsProperty?.PropertyType != typeof(bool) || !wantsProperty.CanRead ||
                layoutProperty?.PropertyType != typeof(bool) || !layoutProperty.CanWrite)
            {
                return false;
            }

            if (!TryReadOptionalInt(viewModel, type, LayoutVersionProperty, out int? layoutVersion) ||
                !TryReadOptionalString(viewModel, type, ActionIdProperty, out string actionId) ||
                !TryReadOptionalInt(viewModel, type, PriorityProperty, out int? priority) ||
                !TroopActionHudLayoutPolicy.TryResolveIdentity(
                    element.Name,
                    layoutVersion,
                    actionId,
                    priority,
                    out int resolvedPriority,
                    out string resolvedActionId))
            {
                layoutProperty.SetValue(viewModel, false, null);
                return false;
            }

            host = new ActionHost(
                element,
                resolvedPriority,
                resolvedActionId,
                layoutProperty,
                (bool)wantsProperty.GetValue(viewModel, null));
            return true;
        }

        private static bool TryReadOptionalInt(
            object viewModel,
            Type type,
            string propertyName,
            out int? value)
        {
            value = null;
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                return true;
            if (property.PropertyType != typeof(int) || !property.CanRead)
                return false;
            value = (int)property.GetValue(viewModel, null);
            return true;
        }

        private static bool TryReadOptionalString(
            object viewModel,
            Type type,
            string propertyName,
            out string value)
        {
            value = null;
            PropertyInfo property = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                return true;
            if (property.PropertyType != typeof(string) || !property.CanRead)
                return false;
            value = (string)property.GetValue(viewModel, null);
            return true;
        }

        internal static TroopActionPageSlots CapturePageSlots(FrameworkElement controls)
        {
            var result = new TroopActionPageSlots();
            var occupied = new List<ScreenRectangle>();
            CollectOccupiedRectangles(controls, occupied, true);
            foreach (Thickness margin in new[] { BottomRightMargin, BottomMiddleMargin })
                if (TryGetSlotRectangle(controls, margin, out ScreenRectangle rectangle) && !IntersectsAny(rectangle, occupied))
                    result.FirstSlots.Add(ToRect(rectangle));

            occupied.Clear();
            CollectOccupiedRectangles(controls, occupied, true, true);
            // Row-major positions on Mod pages. Foreign and legacy hosts reserve their areas
            // even while the Vanilla group is hidden; the caller removes the navigation slot.
            foreach (Thickness margin in new[] {
                new Thickness(2, 0, 83, 43), new Thickness(1, 0, 0, 43), new Thickness(80, 0, 0, 43),
                new Thickness(2, 40, 83, 3), BottomMiddleMargin, BottomRightMargin })
                if (TryGetSlotRectangle(controls, margin, out ScreenRectangle rectangle) && !IntersectsAny(rectangle, occupied))
                    result.LaterSlots.Add(ToRect(rectangle));

            // UnitBack's normal horizontal anchor is x=128 in the 130-unit grid. It is
            // unavailable only in the Engineer submenu, where the pager is excluded entirely.
            float bottomTop = 40 + (controls.ActualHeight - 40 - 3 - ButtonHeight) * 0.5f;
            Point first = controls.PointToScreen(new Point(controls.ActualWidth - 2, bottomTop));
            Point last = controls.PointToScreen(new Point(controls.ActualWidth + 13, bottomTop + ButtonHeight));
            var fallback = new ScreenRectangle(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
                Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
            if (fallback.IsValid && !IntersectsAny(fallback, occupied)) result.FallbackNavigation = ToRect(fallback);
            result.IsReady = true;
            return result;
        }

        private static Rect ToRect(ScreenRectangle rectangle) =>
            new Rect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);

        private static void CollectOccupiedRectangles(DependencyObject parent, List<ScreenRectangle> result,
            bool includeSharedHosts = false, bool excludeVanilla = false)
        {
            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(parent, index);
                if (excludeVanilla && TroopActionPagingSurface.IsVanillaMask(child))
                    continue;
                if (child is FrameworkElement element)
                {
                    if (!includeSharedHosts && TroopActionHudLayoutPolicy.IsStandardHostName(element.Name))
                        continue;

                    // Only effectively interactive Vanilla or foreign buttons own a slot.
                    if (element is Button &&
                        TroopActionHudLayoutPolicy.IsEffectivelyOccupied(
                            IsVisibleIgnoringVanillaMask(element),
                            element.IsHitTestVisible) &&
                        TryGetScreenRectangle(element, out ScreenRectangle rectangle))
                    {
                        result.Add(rectangle);
                    }
                }
                CollectOccupiedRectangles(child, result, includeSharedHosts, excludeVanilla);
            }
        }

        private static bool IsVisibleIgnoringVanillaMask(DependencyObject element)
        {
            for (DependencyObject node = element; node != null; node = VisualTreeHelper.GetParent(node))
                if (node is UIElement ui && !TroopActionPagingSurface.IsVanillaMask(node) &&
                    ui.Visibility != Visibility.Visible)
                    return false;
            return true;
        }

        private static bool TryGetSlotRectangle(
            FrameworkElement controls,
            Thickness margin,
            out ScreenRectangle rectangle)
        {
            rectangle = default;
            float availableWidth = controls.ActualWidth - margin.Left - margin.Right;
            float availableHeight = controls.ActualHeight - margin.Top - margin.Bottom;
            if (availableWidth < ButtonWidth || availableHeight < ButtonHeight)
                return false;

            float left = margin.Left + (availableWidth - ButtonWidth) * 0.5f;
            float top = margin.Top + (availableHeight - ButtonHeight) * 0.5f;
            try
            {
                Point topLeft = ((Visual)controls).PointToScreen(new Point(left, top));
                Point bottomRight = ((Visual)controls).PointToScreen(
                    new Point(left + ButtonWidth, top + ButtonHeight));
                rectangle = new ScreenRectangle(
                    Math.Min(topLeft.X, bottomRight.X),
                    Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Max(topLeft.X, bottomRight.X),
                    Math.Max(topLeft.Y, bottomRight.Y));
                return rectangle.IsValid;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetScreenRectangle(FrameworkElement element, out ScreenRectangle rectangle)
        {
            rectangle = default;
            float width = element.ActualWidth;
            float height = element.ActualHeight;
            if (!(width > 0f) || !(height > 0f) || float.IsNaN(width) || float.IsNaN(height))
                return false;

            try
            {
                Point topLeft = ((Visual)element).PointToScreen(new Point(0f, 0f));
                Point bottomRight = ((Visual)element).PointToScreen(new Point(width, height));
                rectangle = new ScreenRectangle(
                    Math.Min(topLeft.X, bottomRight.X),
                    Math.Min(topLeft.Y, bottomRight.Y),
                    Math.Max(topLeft.X, bottomRight.X),
                    Math.Max(topLeft.Y, bottomRight.Y));
                return rectangle.IsValid;
            }
            catch
            {
                return false;
            }
        }

        private static bool IntersectsAny(ScreenRectangle candidate, List<ScreenRectangle> rectangles)
        {
            for (int index = 0; index < rectangles.Count; index++)
            {
                if (candidate.Intersects(rectangles[index]))
                    return true;
            }
            return false;
        }

        private static void SetLayoutAvailable(ActionHost host, bool available)
        {
            object viewModel = host.Element?.DataContext;
            if (viewModel != null)
                host.LayoutProperty.SetValue(viewModel, available, null);
        }

        private static void LogInvalidHosts(ManualLogSource log, IReadOnlyList<string> invalidHosts)
        {
            if (log == null)
                return;
            for (int index = 0; index < invalidHosts.Count; index++)
            {
                string hostName = invalidHosts[index];
                if (invalidLogged.Add(hostName))
                    NativeApiLog.Warning(log, $"Troop action host '{hostName}' has invalid or incomplete shared layout metadata and remains hidden.");
            }
        }

        private static void LogDuplicateActions(ManualLogSource log, IReadOnlyList<string> duplicateActionIds)
        {
            if (log == null)
                return;
            for (int index = 0; index < duplicateActionIds.Count; index++)
            {
                string actionId = duplicateActionIds[index];
                if (duplicateLogged.Add(actionId))
                    NativeApiLog.Warning(log, $"Troop action id '{actionId}' is registered more than once; every duplicate remains hidden.");
            }
        }

        private sealed class ActionHost
        {
            public ActionHost(
                FrameworkElement element,
                int priority,
                string actionId,
                PropertyInfo layoutProperty,
                bool wantsVisibility)
            {
                Element = element;
                Priority = priority;
                ActionId = actionId;
                LayoutProperty = layoutProperty;
                WantsVisibility = wantsVisibility;
            }

            public FrameworkElement Element { get; }
            public int Priority { get; }
            public string ActionId { get; }
            public PropertyInfo LayoutProperty { get; }
            public bool WantsVisibility { get; }
        }

        private struct ScreenRectangle
        {
            private const float CollisionTolerance = 0.5f;

            public ScreenRectangle(float left, float top, float right, float bottom)
            {
                Left = left;
                Top = top;
                Right = right;
                Bottom = bottom;
            }

            public float Left;
            public float Top;
            public float Right;
            public float Bottom;
            public bool IsValid => Right > Left && Bottom > Top;

            public bool Intersects(ScreenRectangle other)
            {
                return Left < other.Right - CollisionTolerance && Right > other.Left + CollisionTolerance &&
                    Top < other.Bottom - CollisionTolerance && Bottom > other.Top + CollisionTolerance;
            }
        }
    }
}

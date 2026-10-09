using APIShared.Internal;
using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace APIShared.ModSettings
{
#if !API_SHARED_PRESET_TESTS
    internal static class ModSettingsHorizontalFocusScrollGuard
    {
        private static readonly Dictionary<Noesis.ScrollViewer, DiagnosticState> AttachedScrollViewers =
            new Dictionary<Noesis.ScrollViewer, DiagnosticState>();

        public static bool Attach(
            object view,
            ManualLogSource log,
            string modName,
            bool enableDiagnostics = false)
        {
            Noesis.ScrollViewer scrollViewer = FindFirstScrollViewer(
                view as Noesis.FrameworkElement);
            if (scrollViewer == null || AttachedScrollViewers.ContainsKey(scrollViewer))
                return false;

            var state = new DiagnosticState(
                scrollViewer,
                log,
                modName,
                enableDiagnostics);
            AttachedScrollViewers.Add(scrollViewer, state);
            state.Attach();
            return true;
        }

        private static Noesis.ScrollViewer FindFirstScrollViewer(
            Noesis.DependencyObject parent)
        {
            if (parent == null)
                return null;
            if (parent is Noesis.ScrollViewer scrollViewer)
                return scrollViewer;

            int childCount = Noesis.VisualTreeHelper.GetChildrenCount(parent);
            for (int index = 0; index < childCount; index++)
            {
                Noesis.ScrollViewer child = FindFirstScrollViewer(
                    Noesis.VisualTreeHelper.GetChild(parent, index));
                if (child != null)
                    return child;
            }

            return null;
        }

        private sealed class DiagnosticState
        {
            private readonly Noesis.ScrollViewer scrollViewer;
            private readonly ManualLogSource log;
            private readonly string modName;
            private readonly bool diagnosticsEnabled;
            private float acceptedHorizontalOffset;
            private bool manualHorizontalScrollAuthorized;
            private bool restoringHorizontalOffset;

            public DiagnosticState(
                Noesis.ScrollViewer scrollViewer,
                ManualLogSource log,
                string modName,
                bool diagnosticsEnabled)
            {
                this.scrollViewer = scrollViewer;
                this.log = log;
                this.modName = modName;
                this.diagnosticsEnabled = diagnosticsEnabled;
                acceptedHorizontalOffset = scrollViewer.HorizontalOffset;
            }

            public void Attach()
            {
                scrollViewer.PreviewMouseDown += OnPreviewMouseDown;
                scrollViewer.PreviewKeyDown += OnPreviewKeyDown;
                scrollViewer.ScrollChanged += OnScrollChanged;
                Log(
                    () => $"attached; horizontal={scrollViewer.HorizontalOffset:0.###}, " +
                    $"vertical={scrollViewer.VerticalOffset:0.###}, " +
                    $"extentWidth={scrollViewer.ExtentWidth:0.###}, " +
                    $"viewportWidth={scrollViewer.ViewportWidth:0.###}.");
            }

            private void OnPreviewMouseDown(
                object sender,
                Noesis.MouseButtonEventArgs args)
            {
                manualHorizontalScrollAuthorized =
                    IsHorizontalScrollBarInput(args.Source);
                Log(
                    () => $"PreviewMouseDown; source={Describe(args.Source)}, " +
                    $"horizontalScrollbar={manualHorizontalScrollAuthorized}, " +
                    $"acceptedHorizontal={acceptedHorizontalOffset:0.###}, " +
                    $"currentHorizontal={scrollViewer.HorizontalOffset:0.###}.");
            }

            private void OnPreviewKeyDown(
                object sender,
                Noesis.KeyEventArgs args)
            {
                manualHorizontalScrollAuthorized =
                    IsHorizontalScrollBarInput(args.Source);
                Log(
                    () => $"PreviewKeyDown; source={Describe(args.Source)}, " +
                    $"horizontalScrollbar={manualHorizontalScrollAuthorized}.");
            }

            private bool IsHorizontalScrollBarInput(object source)
            {
                var current = source as Noesis.DependencyObject;
                while (current != null && !ReferenceEquals(current, scrollViewer))
                {
                    if (current is Noesis.ScrollBar scrollBar)
                        return scrollBar.Orientation == Noesis.Orientation.Horizontal;

                    current = Noesis.VisualTreeHelper.GetParent(current);
                }
                return false;
            }

            private void OnScrollChanged(
                object sender,
                Noesis.ScrollChangedEventArgs args)
            {
                if (Math.Abs(args.HorizontalChange) < 0.001f &&
                    Math.Abs(args.VerticalChange) < 0.001f)
                {
                    return;
                }

                Log(
                    () => $"ScrollChanged; horizontal={args.HorizontalOffset:0.###}, " +
                    $"horizontalChange={args.HorizontalChange:0.###}, " +
                    $"vertical={args.VerticalOffset:0.###}, " +
                    $"verticalChange={args.VerticalChange:0.###}.");

                if (Math.Abs(args.HorizontalChange) < 0.001f)
                    return;

                if (manualHorizontalScrollAuthorized)
                {
                    acceptedHorizontalOffset = args.HorizontalOffset;
                    Log(
                        () => $"accepted explicit horizontal scrollbar input; horizontal=" +
                        $"{acceptedHorizontalOffset:0.###}.");
                    return;
                }

                if (restoringHorizontalOffset ||
                    Math.Abs(args.HorizontalOffset - acceptedHorizontalOffset) < 0.001f)
                {
                    return;
                }

                // Horizontal movement is permitted only after explicit input inside the
                // horizontal ScrollBar template. Focus, layout and programmatic reveal
                // operations therefore cannot move the settings page sideways.
                restoringHorizontalOffset = true;
                try
                {
                    scrollViewer.ScrollToHorizontalOffset(acceptedHorizontalOffset);
                }
                finally
                {
                    restoringHorizontalOffset = false;
                }
                Log(
                    () => $"rejected non-scrollbar horizontal scroll; horizontal=" +
                    $"{scrollViewer.HorizontalOffset:0.###}, preserved=" +
                    $"{acceptedHorizontalOffset:0.###}.");
            }

            private void Log(Func<string> message)
            {
                if (diagnosticsEnabled)
                {
                    DebugLogHelper.LogDebug(
                        log,
                        () => $"[{modName} ModSettingsScrollDiagnostic] {message()}");
                }
            }

            private static string Describe(object value) =>
                value == null ? "null" : value.GetType().FullName;
        }
    }
#else
    internal static class ModSettingsHorizontalFocusScrollGuard
    {
        private static readonly HashSet<object> AttachedViews = new HashSet<object>();

        internal static int AttachedViewCount => AttachedViews.Count;

        public static bool Attach(
            object view,
            ManualLogSource log,
            string modName,
            bool enableDiagnostics = false) =>
            view != null && AttachedViews.Add(view);

        internal static void ResetForTests() => AttachedViews.Clear();
    }
#endif

}

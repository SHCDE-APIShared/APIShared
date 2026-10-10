#if !API_SHARED_PRESET_TESTS
using Noesis;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace APIShared.ModSettings
{
    /// <summary>Opt-in Noesis inactive-mode background. Bind Availability on the page root and set Key on each logical Grid row; settings remain editable. UI-thread only.</summary>
    public static class ModSettingsMode
    {
        private static readonly ConditionalWeakTable<Grid, Marker> Markers = new ConditionalWeakTable<Grid, Marker>();
        /// <summary>Inherited owner-local availability collection. Bind to System_ModeAvailability or an independent collection.</summary>
        public static readonly DependencyProperty AvailabilityProperty = DependencyProperty.RegisterAttached("Availability", typeof(ModSettingsModeAvailability), typeof(ModSettingsMode),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnChanged));
        /// <summary>Stable setting/group key on a Grid row. Can bind to the row's ModSettingsSearch.Key; search registration is optional.</summary>
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached("Key", typeof(string), typeof(ModSettingsMode), new PropertyMetadata(string.Empty, OnChanged));
        /// <summary>Reads the inherited collection.</summary>
        public static ModSettingsModeAvailability GetAvailability(DependencyObject value) => (ModSettingsModeAvailability)value.GetValue(AvailabilityProperty);
        /// <summary>Sets the collection on the UI thread.</summary>
        public static void SetAvailability(DependencyObject value, ModSettingsModeAvailability availability) => value.SetValue(AvailabilityProperty, availability);
        /// <summary>Reads the row's stable key.</summary>
        public static string GetKey(DependencyObject value) => (string)value.GetValue(KeyProperty);
        /// <summary>Sets a stable row key; empty removes mode presentation.</summary>
        public static void SetKey(DependencyObject value, string key) => value.SetValue(KeyProperty, key);

        private static void OnChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
        {
            if (!(value is Grid grid)) return;
            if (Markers.TryGetValue(grid, out Marker marker)) { marker.Refresh(); return; }
            if (string.IsNullOrWhiteSpace(GetKey(grid))) return;
            Markers.GetValue(grid, g => new Marker(g)).Refresh();
        }
        private sealed class Marker
        {
            private readonly Grid grid;
            private readonly Border background;
            private ModSettingsModeAvailability collection;
            private PropertyChangedEventHandler handler;
            internal Marker(Grid grid)
            {
                this.grid = grid;
                background = new Border { Background = new SolidColorBrush(Color.FromArgb(0x20, 0xff, 0xcc, 0x66)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                // A separate back layer preserves the search grid's Background/style and takes no input.
                Panel.SetZIndex(background, -1);
                grid.Children.Insert(0, background);
                grid.Loaded += (_, __) => Refresh();
            }
            internal void Refresh()
            {
                Grid.SetRowSpan(background, Math.Max(1, grid.RowDefinitions.Count));
                Grid.SetColumnSpan(background, Math.Max(1, grid.ColumnDefinitions.Count));
                var next = GetAvailability(grid);
                if (!ReferenceEquals(collection, next))
                {
                    if (collection != null) collection.PropertyChanged -= handler;
                    collection = next;
                    if (collection != null)
                    {
                        // The long-lived settings collection must not retain destroyed page controls.
                        var weak = new WeakReference<Marker>(this);
                        PropertyChangedEventHandler callback = null;
                        callback = (sender, args) =>
                        {
                            if (weak.TryGetTarget(out Marker marker)) marker.Refresh();
                            else ((ModSettingsModeAvailability)sender).PropertyChanged -= callback;
                        };
                        handler = callback;
                        collection.PropertyChanged += handler;
                    }
                }
                string key = GetKey(grid);
                background.Visibility = collection != null && !string.IsNullOrWhiteSpace(key) && collection.GetState(key).IsInactive ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }
}
#endif

#nullable enable
// External Noesis/game boundaries only. The tests compile and execute the production renderer.
using System.Collections.Generic;
using System.Windows.Input;
using Noesis;

namespace Noesis
{
    public class RoutedEventArgs : EventArgs { }
    public class SizeChangedEventArgs : EventArgs { }
    public enum ScrollBarVisibility { Disabled, Auto }
    public enum TextWrapping { NoWrap, Wrap }
    public enum TextTrimming { None, CharacterEllipsis }
    public struct Point { public float X, Y; public Point(float x, float y) { X = x; Y = y; } }
    public struct Rect
    {
        public float Height;
        public Rect(float x, float y, float width, float height) { Height = height; }
    }
    public sealed class GeneralTransform
    {
        private readonly float scale;
        public GeneralTransform(float scale) { this.scale = scale; }
        public Rect TransformBounds(Rect rect) => new Rect(0, 0, 1, rect.Height * scale);
    }
    public partial class FrameworkElement
    {
        public float ActualHeight;
        public float ActualWidth = 1280, ScreenScaleX = 1, ScreenScaleY = 1;
        public double MinHeight, MaxWidth = double.PositiveInfinity, MaxHeight = double.PositiveInfinity;
        public float ScaleToRoot = 1;
        public event EventHandler<RoutedEventArgs>? Loaded;
        public event EventHandler<SizeChangedEventArgs>? SizeChanged;
        public void Load() { IsLoaded = true; Loaded?.Invoke(this, new RoutedEventArgs()); }
        public void Resize() => SizeChanged?.Invoke(this, new SizeChangedEventArgs());
        public GeneralTransform TransformToAncestor(FrameworkElement ancestor) => new GeneralTransform(ScaleToRoot);
        public Point PointToScreen(Point point) => new Point(point.X * ScreenScaleX, point.Y * ScreenScaleY);
    }
    public struct GridLength { public float Value; public GridLength(float value) { Value = value; } }
    public class ColumnDefinition { public GridLength Width; }
    public class Grid : Panel
    {
        public List<ColumnDefinition> ColumnDefinitions = new();
        public static void SetColumn(Button element, int column) { element.Column = column; }
        public static int GetColumn(Button element) => element.Column;
        public static void SetColumnSpan(Button element, int span) { element.ColumnSpan = span; }
        public static int GetColumnSpan(Button element) => element.ColumnSpan;
    }
    public class StackPanel : Panel { }
    public class ScrollViewer : FrameworkElement
    {
        private FrameworkElement? content;
        public FrameworkElement? Content
        {
            get => content;
            set { if (content != null) content.Parent = null; content = value; if (value != null) value.Parent = this; }
        }
        public ScrollBarVisibility HorizontalScrollBarVisibility, VerticalScrollBarVisibility;
    }
    public class ControlTemplate
    {
        public readonly Dictionary<string, FrameworkElement> Parts = new();
        public object? FindName(string name, Button button) => Parts.GetValueOrDefault(name);
    }
    public partial class Button
    {
        public int Column, ColumnSpan = 1;
        public float FontSize = 16;
        public ControlTemplate? Template;
        public event EventHandler<RoutedEventArgs>? MouseEnter;
        public void Enter() => MouseEnter?.Invoke(this, new RoutedEventArgs());
    }
    public partial class TextBlock
    {
        public TextWrapping TextWrapping;
        public TextTrimming TextTrimming;
    }
    public partial class ToolTip
    {
        private bool open;
        private float fontSize = 16;
        public int FontWrites;
        public float FontSize { get => fontSize; set { fontSize = value; FontWrites++; } }
        public Thickness Padding, BorderThickness;
        public bool ScalerEnabled;
        public bool IsOpen
        {
            get => open;
            set { if (open == value) return; open = value; if (value) Opened?.Invoke(this, new RoutedEventArgs()); else Closed?.Invoke(this,new RoutedEventArgs()); }
        }
        public event EventHandler<RoutedEventArgs>? Opened;
        public event EventHandler<RoutedEventArgs>? Closed;
    }
    public static partial class ToolTipService
    {
        public static object? GetToolTip(Button button) => button.ToolTip;
    }
}
namespace SHCDESE.UI
{
    public static class ToolTipResolutionScale
    {
        public static bool GetEnabled(ToolTip tip) => tip.ScalerEnabled;
        public static void SetEnabled(ToolTip tip, bool enabled) { tip.ScalerEnabled = enabled; }
    }
}
namespace CrusaderDE
{
    public class HUD_IngameMenu : Grid
    {
        public readonly Dictionary<string, FrameworkElement> Names = new();
        public object? FindName(string name) => Names.GetValueOrDefault(name);
    }
    public partial class MainViewModel
    {
        public HUD_IngameMenu? HUDIngameMenu;
        public bool Show_HUD_IngameMenu = true;
        public void CommonRedButtonEnter(object? sender, RoutedEventArgs args) { }
    }
    public static class PropEx
    {
        private static readonly Dictionary<Button, object?> Text = new();
        public static object? GetTextCentre(Button button) => Text.GetValueOrDefault(button);
        public static void SetTextCentre(Button button, object? text) { Text[button] = text; }
        public static void SetTextLeft(Button button, object text) { }
        public static void SetTextRight(Button button, object text) { }
    }
}
namespace SHCDESE.NoesisUtil
{
    public class RelayCommand : ICommand
    {
        private readonly Action execute;
        public RelayCommand(Action execute) { this.execute = execute; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
namespace APIShared.Presentation
{
    // Test seam for the renderer's existing coordinator, not another implementation of its rules.
    public static partial class IngameMenuActions
    {
        private static IngameMenuRegistry registry = new();
        private static CrusaderDE.MainViewModel? observedMain;
        private static bool MenuVisible() => observedMain?.Show_HUD_IngameMenu == true;
        private static bool Refresh(bool all)
        { CaptureVanilla(); registry.Evaluate(all, null); Render(registry.Resolve()); return true; }
        private static void Execute(IngameMenuRegistry.Entry entry, CrusaderDE.HUD_IngameMenu view, bool additional) { }
        internal static void TestAttach(CrusaderDE.HUD_IngameMenu menu, IngameMenuRegistry registrations)
        {
            registry = registrations; observedMain = CrusaderDE.MainViewModel.Instance;
            ResolveView(menu); ShowAdditional(false); Refresh(true);
        }
        internal static void TestRefresh() => Refresh(true);
        internal static void TestInvalidate(string owner) { registry.Invalidate(owner); Refresh(false); }
        internal static void TestPage(bool additional) => ShowAdditional(additional);
    }
}

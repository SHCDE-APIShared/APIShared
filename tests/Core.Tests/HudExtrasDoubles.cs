#nullable enable
// Managed UI doubles: exercise production registry, pagination, rebinding and command gates without Noesis native rendering.
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;

namespace Noesis
{
    public enum Visibility { Visible, Hidden, Collapsed }
    public enum HorizontalAlignment { Left, Center }
    public enum VerticalAlignment { Bottom, Center }
    public struct Thickness
    {
        public double Bottom;
        public Thickness(double all) { Bottom = all; }
        public Thickness(double left, double top, double right, double bottom) { Bottom = bottom; }
    }
    public class Style { }
    public class FrameworkElement
    {
        public FrameworkElement? Parent { get; internal set; }
        public double Width, Height; public Thickness Margin;
        public Visibility Visibility = Visibility.Visible;
        private bool enabled = true;
        public bool IsEnabled { get => enabled && (Parent?.IsEnabled ?? true); set => enabled = value; }
        public bool IsVisible => Visibility == Visibility.Visible && (Parent?.IsVisible ?? true);
        public bool IsHitTestVisible = true;
        public HorizontalAlignment HorizontalAlignment; public VerticalAlignment VerticalAlignment;
        public Style? Style;
        public readonly Dictionary<string, object> Resources = new();
        public object? TryFindResource(object key) => Resources.TryGetValue((string)key, out var value) ? value : Parent?.TryFindResource(key);
    }
    public class Grid : FrameworkElement
    {
        public ElementCollection Children;
        public Grid() { Children = new ElementCollection(this); }
    }
    public sealed class ElementCollection : Collection<FrameworkElement>
    {
        private readonly FrameworkElement owner;
        public ElementCollection(FrameworkElement owner) { this.owner = owner; }
        protected override void InsertItem(int index, FrameworkElement item) { base.InsertItem(index, item); item.Parent = owner; }
        protected override void RemoveItem(int index) { this[index].Parent = null; base.RemoveItem(index); }
        protected override void ClearItems() { foreach (var item in this) item.Parent = null; base.ClearItems(); }
    }
    public class Button : FrameworkElement
    {
        public object? Content, CommandParameter, ToolTip; public ICommand? Command;
        public int Duration; public bool ToolTipEnabled = true;
        public event EventHandler? Click;
        public void Invoke() { Command?.Execute(CommandParameter); Click?.Invoke(this, EventArgs.Empty); }
    }
    public class TextBlock : FrameworkElement { public string? Text; public float FontSize; }
    public class ToolTip : FrameworkElement { public object? Content, Template; }
    public static class ToolTipService
    {
        public static void SetIsEnabled(Button button, bool value) { button.ToolTipEnabled = value; }
        public static void SetToolTip(Button button, object? tip) { button.ToolTip = tip; }
        public static void SetShowDuration(Button button, int duration) { button.Duration = duration; }
    }
}
namespace CrusaderDE
{
    public class MainViewModel
    {
        public static bool viewModelLoaded;
        public static MainViewModel? Instance;
        public bool Show_HUD_Extras = true, Show_HUD_Extras_Button_Objectves, Show_HUD_Extras_Button_Freebuild;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Raise(string name) { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)); }
    }
}
namespace SHCDESE.API
{
    public class GameXAMLManagerAPI
    {
        public static readonly GameXAMLManagerAPI Instance = new();
        public Noesis.FrameworkElement? Element;
        public Noesis.FrameworkElement? FindGlobalElement(string name) => Element;
    }
}
namespace UnityEngine
{
    public static class Application
    {
        public static event Action? onBeforeRender;
        public static void Render() { onBeforeRender?.Invoke(); }
    }
}
namespace APIShared
{
    public enum NativeCapabilityState { Pending, Available, ValidationFailed }
    public static class NativeCapabilityIds { public const string HudExtrasButtons = "hud-extras-buttons"; }
    public sealed class NativeCapabilityDiagnostic
    {
        public readonly NativeCapabilityState State;
        public NativeCapabilityDiagnostic(string id, NativeCapabilityState state, string hash, string reason) { State = state; }
    }
    internal static class NativeApiLog
    {
        internal static void Debug(BepInEx.Logging.ManualLogSource? log, string text) { log?.LogDebug(text); }
        internal static void Error(BepInEx.Logging.ManualLogSource? log, string text) { log?.LogDebug(text); }
    }
}

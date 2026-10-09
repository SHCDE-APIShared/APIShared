#nullable enable
using APIShared;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;
using SHCDESE.API;
using System.Reflection;
using System.Windows.Input;

namespace Core.Tests
{
    [TestClass]
    [DoNotParallelize]
    public class HudExtrasPresentationTests
    {
        private sealed class Command : ICommand
        {
            internal int Calls; internal object? Last; internal bool Allowed = true;
            public event EventHandler? CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object? parameter) => Allowed;
            public void Execute(object? parameter) { Calls++; Last = parameter; }
        }
        private static Grid Host()
        {
            var host = new Grid();
            foreach (string key in new[] { "BTN_Building", "APISharedHudExtrasTooltipStyle", "APISharedHudExtrasPageButtonStyle" }) host.Resources[key] = new Style();
            GameXAMLManagerAPI.Instance.Element = host; return host;
        }
        private static void Tick(HudExtrasButtonsService service) => typeof(HudExtrasButtonsService)
            .GetMethod("OnBeforeRender", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, null);
        private static Button[] Visible(Grid host) => host.Children.OfType<Grid>().Where(x => x.IsVisible).Select(x => x.Children.OfType<Button>().Single()).ToArray();
        [TestInitialize]
        public void Initialize() { MainViewModel.viewModelLoaded = true; MainViewModel.Instance = new MainViewModel(); }

        [TestMethod]
        public void ProductionPagesUseParentGatesAndPreserveVanillaSlots()
        {
            var service = new HudExtrasButtonsService(null); var api = service.Bind("mod"); var host = Host();
            var handles = new List<IHudExtrasButtonRegistration>();
            for (int i = 0; i < 11; i++)
            {
                string id = i.ToString("D2");
                api.TryRegisterButton(new HudExtrasButtonDefinition(id, new Command(), id, _ => new Button { Content = id }), out var handle, out _); handles.Add(handle);
            }
            Tick(service); CollectionAssert.AreEqual(new[] { "00", "01", "02", "03", "04" }, Visible(host).Select(x => (string)x.Content!).ToArray());
            Assert.AreEqual(34d, host.Margin.Bottom);
            CollectionAssert.AreEqual(new[] { 0d, 34d, 68d, 102d, 136d }, Visible(host).Select(x => x.Parent!.Margin.Bottom).ToArray());
            var next = host.Children.OfType<Button>().Single(); Assert.AreEqual(170d, next.Margin.Bottom);
            next.Invoke(); Tick(service); Assert.AreEqual("05", Visible(host).First().Content);
            next.Invoke(); Tick(service); Assert.AreEqual(1, Visible(host).Length); Assert.AreEqual("10", Visible(host).Single().Content);
            next.Invoke(); Tick(service); Assert.AreEqual("00", Visible(host).First().Content);
            // A Vanilla animation may set Button.Visibility but cannot override the surrounding page gate.
            var offPage = host.Children.OfType<Grid>().SelectMany(x => x.Children).OfType<Button>().Single(x => Equals(x.Content, "10"));
            offPage.Visibility = Visibility.Visible; Assert.IsFalse(offPage.IsVisible);
            MainViewModel.Instance!.Show_HUD_Extras_Button_Objectves = true;
            MainViewModel.Instance.Raise(nameof(MainViewModel.Show_HUD_Extras_Button_Objectves)); Tick(service); Assert.AreEqual(68d, host.Margin.Bottom);
            next.Invoke(); Tick(service); next.Invoke(); Tick(service);
            foreach (var handle in handles.Skip(5)) handle.SetVisible(false);
            Tick(service); Assert.AreEqual(5, Visible(host).Length); Assert.IsFalse(next.IsVisible); Assert.AreEqual("00", Visible(host).First().Content);
        }
        [TestMethod]
        public void CommandsAndRecreatedHudsRespectLiveAvailability()
        {
            var service = new HudExtrasButtonsService(null); var api = service.Bind("mod"); var host = Host(); var command = new Command();
            api.TryRegisterButton(new HudExtrasButtonDefinition("one", command, "Blueprints", _ => new Button()), out var handle, out _);
            Tick(service); var button = Visible(host).Single(); button.Invoke(); Assert.AreSame(button, command.Last); Assert.AreEqual(1, command.Calls);
            handle.SetEnabled(false); button.Invoke(); Assert.AreEqual(1, command.Calls);
            handle.SetEnabled(true); command.Allowed = false; button.Invoke(); Assert.AreEqual(1, command.Calls); command.Allowed = true;
            api.SetOwnerActive(false); button.Invoke(); Tick(service); Assert.IsFalse(host.IsVisible);
            api.SetOwnerActive(true); Tick(service);
            MainViewModel.Instance!.Show_HUD_Extras = false; button.Invoke(); Tick(service); Assert.IsFalse(host.IsVisible); Assert.AreEqual(1, command.Calls);
            MainViewModel.Instance.Show_HUD_Extras = true; MainViewModel.Instance.Raise(nameof(MainViewModel.Show_HUD_Extras)); Tick(service);
            var replacement = Host(); button.Invoke(); Assert.AreEqual(1, command.Calls); Tick(service);
            Assert.AreEqual(0, host.Children.Count); var fresh = Visible(replacement).Single(); Assert.AreNotSame(button, fresh); fresh.Invoke(); Assert.AreEqual(2, command.Calls);
            MainViewModel.viewModelLoaded = false; Tick(service); Assert.IsFalse(replacement.IsVisible);
            MainViewModel.viewModelLoaded = true; MainViewModel.Instance = new MainViewModel(); Tick(service); Assert.AreEqual(1, Visible(replacement).Length);
            replacement.IsEnabled = false; Visible(replacement).Single().Invoke(); Assert.AreEqual(2, command.Calls);
        }
        [TestMethod]
        public void DefaultAndCustomTooltipsRefreshAndFailureDoesNotBlockOthers()
        {
            var service = new HudExtrasButtonsService(null); var api = service.Bind("mod"); var host = Host(); var customStyle = new Style(); int calls = 0;
            api.TryRegisterButton(new HudExtrasButtonDefinition("a", new Command(), "Blueprints", _ => new Button { Content = "a" }), out var standard, out _);
            api.TryRegisterButton(new HudExtrasButtonDefinition("b", new Command(), "Custom", _ => new Button { Content = "b" },
                tooltipFactory: _ => new ToolTip { Style = customStyle }), out _, out _);
            api.TryRegisterButton(new HudExtrasButtonDefinition("c", new Command(), "Broken", _ => { calls++; if (calls == 1) throw new Exception("bad"); return new Button { Content = "c" }; }), out var retry, out _);
            api.TryRegisterButton(new HudExtrasButtonDefinition("d", new Command(), "Broken tooltip", _ => new Button(), tooltipFactory: _ => throw new Exception("bad tooltip")), out _, out _);
            Tick(service); Assert.AreEqual(2, Visible(host).Length);
            var a = Visible(host).Single(x => Equals(x.Content, "a")); var b = Visible(host).Single(x => Equals(x.Content, "b"));
            Assert.AreSame(host.Resources["APISharedHudExtrasTooltipStyle"], ((ToolTip)a.ToolTip!).Style); Assert.AreEqual("Blueprints", ((ToolTip)a.ToolTip!).Content);
            Assert.AreSame(customStyle, ((ToolTip)b.ToolTip!).Style); Assert.AreEqual(60000, a.Duration);
            standard.SetTooltip("Updated"); Tick(service); Assert.AreEqual("Updated", ((ToolTip)a.ToolTip!).Content);
            standard.SetTooltip(""); Tick(service); Assert.IsNull(a.ToolTip);
            retry.RequestContentRefresh(); Tick(service); Assert.AreEqual(3, Visible(host).Length); Assert.AreEqual(2, calls);
        }
    }
}

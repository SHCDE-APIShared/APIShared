#nullable enable
using System.Xml.Linq;
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;
using SHCDESE.NoesisUtil;

namespace Core.Tests
{
    [TestClass, DoNotParallelize]
    public sealed class IngameMenuViewTests
    {
        private static readonly string[] Names = { "ButtonSave", "ButtonLoad", "ButtonWorkshop", "ButtonOptions",
            "ButtonHelp", "ButtonRestartMission", "ButtonQuitMission", "ButtonExitStronghold", "ButtonResume" };
        private static HUD_IngameMenu Menu(float viewportHeight = 1440, float scale = 1)
        {
            var screen = new Grid { ActualHeight = viewportHeight };
            var menu = new HUD_IngameMenu(); screen.Children.Add(menu);
            var root = new Grid { Width = 500, Height = 400, ScaleToRoot = scale };
            var parent = new Grid(); var panel = new StackPanel { Width = 500, Margin = new Thickness(0, 72, 0, 0) };
            menu.Children.Add(root); root.Children.Add(parent); parent.Children.Add(panel);
            menu.Names["LayoutRoot"] = root; menu.Names["LeftPanel"] = panel;
            var style = new Style(); menu.Resources["BTN_SH_GlowSDisable"] = style;
            menu.Resources["APISharedMenuTooltipStyle"] = new Style();
            foreach (string name in Names)
            {
                var button = new Button { Width = 300, Height = double.NaN, Style = style,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 10, 0, 0), Command = new RelayCommand(() => { }), CommandParameter = name,
                    Template = new ControlTemplate() };
                foreach (string part in new[] { "NormalMode", "GlowMode" })
                    button.Template.Parts[part] = new Grid { Height = 31, Margin = new Thickness(-1.5) };
                foreach (string part in new[] { "CenterText", "TextDisabled" })
                    button.Template.Parts[part] = new TextBlock { Height = 24, FontSize = 16, Margin = new Thickness(0, 4, 0, 0), Text = name };
                PropEx.SetTextCentre(button, name); panel.Children.Add(button); menu.Names[name] = button; button.Load();
            }
            menu.Names["ButtonWorkshop"].Visibility = Visibility.Collapsed;
            MainViewModel.viewModelLoaded = true; MainViewModel.Instance = new MainViewModel { HUDIngameMenu = menu };
            return menu;
        }
        private static IngameMenuRegistry.Entry Add(IngameMenuRegistry registry, string id, Func<IngameMenuActionState?> provider,
            IngameMenuActionOptions? options = null)
        {
            options ??= new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Left);
            Assert.IsTrue(registry.CanRegister("mod", id, provider, () => { }, options, false, out string reason), reason);
            return registry.Register("mod", id, provider, () => { }, options, false, 0);
        }
        private static Button Original(HUD_IngameMenu menu, string name) => (Button)menu.Names[name];
        private static IEnumerable<FrameworkElement> Walk(FrameworkElement element)
        {
            yield return element;
            if (element is Panel panel)
                foreach (var child in panel.Children) foreach (var nested in Walk(child)) yield return nested;
            if (element is ScrollViewer scroll && scroll.Content != null)
                foreach (var nested in Walk(scroll.Content)) yield return nested;
        }
        [TestMethod]
        public void ProductionRendererKeepsOriginalStyleTexturesBindingsAndHeightWhileSplittingOnlyTheClaimedRow()
        {
            var menu = Menu(); var registry = new IngameMenuRegistry();
            Add(registry, "stats", () => new IngameMenuActionState("Statistics", true, false));
            var quit = Original(menu, "ButtonQuitMission"); var command = quit.Command; var template = quit.Template;
            IngameMenuActions.TestAttach(menu, registry);
            foreach (string name in Names.Where(n => n != "ButtonWorkshop"))
            {
                Button button = Original(menu, name);
                Assert.AreSame(menu.Resources["BTN_SH_GlowSDisable"], button.Style);
                Assert.IsTrue(double.IsNaN(button.Height)); Assert.AreEqual(0d, button.MinHeight);
                Assert.AreEqual(VerticalAlignment.Top, button.VerticalAlignment);
                foreach (string part in new[] { "NormalMode", "GlowMode" })
                {
                    var background = button.Template!.Parts[part];
                    Assert.AreEqual(31d, background.Height); Assert.AreEqual(-1.5d, background.Margin.Top);
                    Assert.AreEqual(0d, background.MinHeight);
                }
                foreach (string part in new[] { "CenterText", "TextDisabled" })
                {
                    var text = (TextBlock)button.Template!.Parts[part];
                    Assert.AreEqual(24d, text.Height); Assert.AreEqual(16f, text.FontSize);
                    Assert.AreEqual(4d, text.Margin.Top); Assert.AreEqual(name, text.Text);
                }
            }
            var row = (Grid)quit.Parent!;
            Assert.AreEqual(367.5d, row.Width); Assert.AreEqual(10d, row.Margin.Top);
            CollectionAssert.AreEqual(new[] { 181.25f, 5f, 181.25f }, row.ColumnDefinitions.Select(c => c.Width.Value).ToArray());
            Assert.AreEqual(181.25d, quit.Width); Assert.AreEqual(2, Grid.GetColumn(quit));
            var mod = row.Children.OfType<Button>().Single(b => !ReferenceEquals(b, quit));
            Assert.AreEqual(181.25d, mod.Width); Assert.IsFalse(mod.IsEnabled);
            Assert.AreSame(quit.Style, mod.Style); Assert.IsNull(mod.Template); // No custom local template assigned.
            Assert.AreEqual(300d, Original(menu, "ButtonSave").Width);
            Assert.AreEqual(400d, menu.Names["LayoutRoot"].Height);
            Assert.AreSame(command, quit.Command); Assert.AreSame(template, quit.Template);
            Assert.AreEqual("ButtonQuitMission", quit.CommandParameter);
        }
        [TestMethod]
        public void HidingAndRestoringAnActionRestoresTheSameCenteredFullWidthVanillaButton()
        {
            bool visible = true; var registry = new IngameMenuRegistry(); var menu = Menu();
            Add(registry, "stats", () => visible ? new IngameMenuActionState("Statistics", true, true) : null);
            IngameMenuActions.TestAttach(menu, registry); var quit = Original(menu, "ButtonQuitMission");
            for (int i = 0; i < 4; i++)
            {
                visible = !visible; IngameMenuActions.TestRefresh();
                Assert.AreEqual(visible ? 181.25d : 300d, quit.Width);
                Assert.AreEqual(visible ? 1 : 3, Grid.GetColumnSpan(quit));
                Assert.AreEqual(visible ? 2 : 0, Grid.GetColumn(quit));
                Assert.AreEqual(HorizontalAlignment.Center, quit.HorizontalAlignment);
                Assert.AreEqual(400d, menu.Names["LayoutRoot"].Height);
            }
        }
        [TestMethod]
        public void EllipsisConstrainsBothVanillaTextPartsWithoutChangingTextOrFontAndUpdatesOnResize()
        {
            var menu = Menu(); var registry = new IngameMenuRegistry();
            Add(registry, "long", () => new IngameMenuActionState(new string('W', 300), true, true));
            var quit = Original(menu, "ButtonQuitMission");
            PropEx.SetTextCentre(quit, "A very long translated mission exit action");
            IngameMenuActions.TestAttach(menu, registry);
            foreach (string part in new[] { "CenterText", "TextDisabled" })
            {
                var text = (TextBlock)quit.Template!.Parts[part];
                Assert.AreEqual(157.25d, text.MaxWidth); Assert.AreEqual(TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.AreEqual(TextWrapping.NoWrap, text.TextWrapping); Assert.AreEqual(24d, text.Height);
            }
            Assert.IsNull(quit.ToolTip); // Even a truncated Vanilla label never gains a generated popup.
            quit.Width = 300; quit.Resize();
            Assert.AreEqual(276d, quit.Template!.Parts["TextDisabled"].MaxWidth);
        }
        [TestMethod]
        public void ExtraPageAndNavigationUseVanillaStyleAndScrollInLocalUnitsWithoutChangingMainMenuGeometry()
        {
            var menu = Menu(viewportHeight: 1440, scale: 3); var registry = new IngameMenuRegistry();
            for (int i = 0; i < 61; i++)
                Add(registry, i.ToString(), () => new IngameMenuActionState("Extra", true, true), new IngameMenuActionOptions());
            IngameMenuActions.TestAttach(menu, registry);
            Assert.AreEqual(438d, menu.Names["LayoutRoot"].Height); // Only the extra navigation row adds height.
            IngameMenuActions.TestPage(true);
            Assert.AreEqual(460d, menu.Names["LayoutRoot"].Height); // 1440 / scale 3, minus screen margin 20.
            var scrolls = Walk(menu).OfType<ScrollViewer>().ToArray();
            Assert.IsTrue(scrolls.All(s => s.MaxHeight <= 364));
            Assert.IsTrue(scrolls.Any(s => s.MaxHeight == 326)); // Back is outside the scroll area.
            foreach (var button in Walk(menu).OfType<Button>().Where(b => !Names.Any(n => ReferenceEquals(menu.Names[n], b))))
            {
                Assert.AreSame(menu.Resources["BTN_SH_GlowSDisable"], button.Style);
                Assert.AreEqual(VerticalAlignment.Top, button.VerticalAlignment);
                Assert.IsNull(button.ToolTip); // Three-argument actions and both navigation buttons are opt-out.
            }
            var single = Walk(menu).OfType<Grid>().Single(g => g.Children.Count == 1 && g.Children[0] is Button b && Equals(PropEx.GetTextCentre(b), "Extra"));
            Assert.AreEqual(300d, ((Button)single.Children[0]).Width);
            IngameMenuActions.TestPage(false); Assert.AreEqual(438d, menu.Names["LayoutRoot"].Height);
        }
        [TestMethod]
        public void ShippedActionStyleDoesNotReplaceAnyPartOfTheVanillaTemplate()
        {
            // Resource identity is a compatibility contract: this published key remains an empty
            // alias of the actual Vanilla style, so textures and state triggers cannot diverge.
            string path = Path.Combine(AppContext.BaseDirectory, "MenuPatch.xaml");
            var document = XDocument.Load(path); XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var style = document.Descendants("Style").Single(s => (string?)s.Attribute(x + "Key") == "APISharedMenuActionButtonStyle");
            Assert.AreEqual("{StaticResource BTN_SH_GlowSDisable}", (string?)style.Attribute("BasedOn"));
            Assert.IsFalse(style.Elements().Any());
        }
        [TestMethod]
        public void DisplacedOriginalKeepsItsCommandAndReturnsToItsOriginalRow()
        {
            var menu = Menu(); var registry = new IngameMenuRegistry(); bool replace = true;
            var original = Original(menu, "ButtonQuitMission"); var command = original.Command; var template = original.Template;
            Add(registry, "replacement", () => replace ? new IngameMenuActionState("Replace", true, true) : null,
                new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Right, replaceVanilla: true));
            IngameMenuActions.TestAttach(menu, registry); IngameMenuActions.TestPage(true);
            Assert.AreSame(command, original.Command); Assert.AreSame(template, original.Template);
            Assert.AreEqual(300d, original.Width); Assert.AreEqual(0, Grid.GetColumn(original));
            replace = false; IngameMenuActions.TestRefresh();
            Assert.AreSame(original, ((Grid)original.Parent!).Children.Single());
            Assert.AreSame(command, original.Command); Assert.AreEqual(400d, menu.Names["LayoutRoot"].Height);
        }
        [TestMethod]
        public void GeometryEventsDoNotPollProvidersAndRecreatedHudDetachesTheOldView()
        {
            int calls = 0; var menu = Menu(); var registry = new IngameMenuRegistry();
            Add(registry, "action", () => { calls++; return new IngameMenuActionState("Action", true, true); });
            IngameMenuActions.TestAttach(menu, registry); var old = Original(menu, "ButtonQuitMission");
            for (int i = 0; i < 100; i++) { old.Resize(); old.Load(); menu.Parent!.Resize(); }
            Assert.AreEqual(1, calls);
            var fresh = Menu(); IngameMenuActions.TestAttach(fresh, registry);
            old.Width = 500; old.Resize(); old.Load();
            Assert.AreEqual(157.25d, old.Template!.Parts["CenterText"].MaxWidth);
            Assert.AreEqual(181.25d, Original(fresh, "ButtonQuitMission").Width);
            Assert.AreEqual(2, calls);
        }
        [TestMethod]
        public void TooltipEventsKeepButtonGeometryAndProviderCountsAndPageChangesClosePopups()
        {
            int calls = 0; var menu = Menu(); var registry = new IngameMenuRegistry();
            Add(registry, "direct", () => { calls++; return new IngameMenuActionState("Action", true, true, "Explicit help"); });
            Add(registry, "extra", () => new IngameMenuActionState("Extra", true, true), new IngameMenuActionOptions());
            IngameMenuActions.TestAttach(menu, registry);
            var quit = Original(menu, "ButtonQuitMission");
            var button = ((Grid)quit.Parent!).Children.OfType<Button>().Single(b => !ReferenceEquals(b, quit));
            button.ScreenScaleX = button.ScreenScaleY = 3; button.Load();
            var tip = (ToolTip)button.ToolTip!; tip.Load(); tip.IsOpen = true;
            Assert.AreEqual(48f, tip.FontSize); Assert.AreEqual(60000, button.Duration);
            for (int i = 0; i < 100; i++) { tip.Resize(); tip.Load(); }
            Assert.AreEqual(1, calls); Assert.AreEqual(181.25d, button.Width);
            Assert.AreEqual(31d, quit.Template!.Parts["NormalMode"].Height);
            Assert.AreEqual(24d, quit.Template.Parts["CenterText"].Height);
            IngameMenuActions.TestPage(true); Assert.IsFalse(tip.IsOpen);
            IngameMenuActions.TestPage(false); tip.IsOpen = true;
            MainViewModel.Instance!.Show_HUD_IngameMenu = false; IngameMenuActions.TestPage(false);
            Assert.IsFalse(tip.IsOpen); Assert.AreEqual(1, calls);
            MainViewModel.Instance.Show_HUD_IngameMenu = true;
            tip.IsOpen = true; var fresh = Menu(); IngameMenuActions.TestAttach(fresh, registry);
            Assert.IsFalse(tip.IsOpen); tip.FontSize = 11; tip.Load(); tip.Resize();
            Assert.AreEqual(11f, tip.FontSize);
        }
        [TestMethod]
        public void VanillaAndForeignTooltipObjectsRemainUntouchedEvenWhenDisplacedToTheExtraPage()
        {
            var menu = Menu(); var registry = new IngameMenuRegistry();
            var quit = Original(menu, "ButtonQuitMission");
            var existing = new ToolTip { Content = new object(), FontSize = 7, ScalerEnabled = true };
            var content = existing.Content; quit.ToolTip = existing; quit.Duration = 812; quit.ToolTipEnabled = false;
            Add(registry, "replace", () => new IngameMenuActionState("Replace", true, true),
                new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Right, replaceVanilla: true));
            IngameMenuActions.TestAttach(menu, registry); IngameMenuActions.TestPage(true);
            existing.Load(); existing.IsOpen = true; existing.Resize(); menu.Parent!.Resize();
            Assert.AreSame(existing, quit.ToolTip); Assert.AreSame(content, existing.Content);
            Assert.AreEqual(7f, existing.FontSize); Assert.IsTrue(existing.ScalerEnabled);
            Assert.AreEqual(812, quit.Duration); Assert.IsFalse(quit.ToolTipEnabled);
            Assert.AreEqual(1, Walk(menu).OfType<Button>().Count(b => b.ToolTip != null));
            foreach (string name in Names.Where(n => n != "ButtonQuitMission")) Assert.IsNull(Original(menu, name).ToolTip);
            IngameMenuActions.TestPage(false); IngameMenuActions.TestRefresh();
            Assert.AreSame(existing, quit.ToolTip); Assert.AreEqual(7f, existing.FontSize);
        }
        [TestMethod]
        public void OnlyExplicitTooltipTextIsShownAndInvalidationUpdatesRemovesAndRestoresTheOwnedPopup()
        {
            string? tooltip = "Precise help\nsecond line"; bool visible = true;
            var menu = Menu(); var registry = new IngameMenuRegistry();
            Add(registry, "action", () => new IngameMenuActionState("Different button label", visible, true, tooltip));
            IngameMenuActions.TestAttach(menu, registry);
            var row = (Grid)Original(menu, "ButtonQuitMission").Parent!;
            var button = row.Children.OfType<Button>().Single(b => !ReferenceEquals(b, Original(menu, "ButtonQuitMission")));
            button.Load(); var first = (ToolTip)button.ToolTip!; first.Load(); first.IsOpen = true;
            Assert.AreEqual(tooltip, first.Content);
            tooltip = "Updated help"; IngameMenuActions.TestInvalidate("mod");
            Assert.AreSame(first, button.ToolTip); Assert.AreEqual(tooltip, first.Content);
            foreach (string? empty in new[] { null, "", " \t\r\n" })
            {
                tooltip = empty; IngameMenuActions.TestInvalidate("mod");
                Assert.IsNull(button.ToolTip); Assert.IsFalse(first.IsOpen);
            }
            first.FontSize = 9; first.IsOpen = true; first.Load(); first.Resize();
            Assert.AreEqual(9f, first.FontSize); first.IsOpen = false;
            tooltip = "Restored help"; IngameMenuActions.TestInvalidate("mod");
            var second = (ToolTip)button.ToolTip!; Assert.AreNotSame(first, second); Assert.AreEqual(tooltip, second.Content);
            second.Load(); second.IsOpen = true;
            visible = false; IngameMenuActions.TestInvalidate("mod");
            Assert.IsNull(button.ToolTip); Assert.IsFalse(second.IsOpen);
        }
    }
}

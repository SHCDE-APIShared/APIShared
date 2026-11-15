using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class UnitHudCategoryLayoutTests
{
    // Execute the complete production renderer. Only game/Noesis boundaries and the
    // selected-entry provider are doubled; standalone CLR cannot create a game HUD.
    // This checks slot reuse, not native selection capture, XAML layout or hit testing.
    private static readonly Lazy<Action<string>> Run = new(CompileRenderer);

    [TestMethod]
    [DataRow("lord-first")]
    [DataRow("troop-lord-troop")]
    [DataRow("mixed")]
    [DataRow("pages")]
    public void CategoryHeightDoesNotDependOnPreviousSlotOccupant(string scenario) => Run.Value(scenario);

    private static Action<string> CompileRenderer()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "APIShared.csproj")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("APIShared root");
        var source = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,
            "src", "Presentation", "UnitHud", "UnitHudPresentationService.Categories.cs")));
        var renderer = source.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == "RenderTroopCategories");
        var tree = CSharpSyntaxTree.ParseText(Fixture.Replace("/* RENDERER */", renderer.ToFullString()));
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("HudLayoutFixture", new[] { tree }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var output = new MemoryStream();
        var result = compilation.Emit(output);
        Assert.IsTrue(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return Assembly.Load(output.ToArray()).GetType("HudFixture")!.GetMethod("Run")!
            .CreateDelegate<Action<string>>();
    }

    private const string Fixture = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Reflection;
        public sealed class TranslateTransform { public float X, Y; }
        public enum Visibility { Visible, Collapsed }
        public enum UnitHudSurface { TroopSelection }
        public enum UnitHudTextKind { DisplayName }
        public sealed class ImageSource { }
        public sealed class Grid { public TranslateTransform RenderTransform; public Visibility Visibility; }
        public sealed class Button { public string Tag; public object ToolTip; public bool IsMouseOver; }
        public sealed class Definition { public object Tint; }
        public sealed class Category { public string Key = "lord"; public Definition Definition = new Definition(); }
        public sealed class UnitHudUnitSnapshot { }
        public sealed class UnitHudCategorySnapshot { }
        public sealed class UnitHudSlotSnapshot {
            public int Slot; public UnitHudCategorySnapshot Category;
            public UnitHudSlotSnapshot(int slot, int type, UnitHudCategorySnapshot category) { Slot = slot; Category = category; }
        }
        public sealed class DisplayEntry {
            public int VanillaType, Count = 1;
            public Category Category;
            public List<UnitHudUnitSnapshot> Units = new List<UnitHudUnitSnapshot>();
        }
        public sealed class HUD_Troops {
            public int[] Counts = new int[89];
            public TranslateTransform[] Positions = Enumerable.Range(0, 8).Select(i => new TranslateTransform { X = i * 52 - 181 }).ToArray();
            public int Page, Pages, TypeCount;
            public int[] Numbers = new int[8];
            public void HideAllSelectedTroops() { }
            public void HideAllSelectedTroopsNumbers() { Array.Clear(Numbers, 0, Numbers.Length); }
            // Managed Vanilla boundary: ordinary troop portraits request -13.
            public int SetSelectedTroopVisible(int type) => -13;
            public void SetSelectedTroopPosition(int type, int slot) { }
            public void ShowSelectedTroopsNumber(int slot, int count) { Numbers[slot] = count; }
        }
        public sealed class HudWorkBuffers {
            public List<UnitHudUnitSnapshot> Selected = new List<UnitHudUnitSnapshot>();
            public HashSet<int> Seen = new HashSet<int>();
            public List<UnitHudSlotSnapshot> Slots = new List<UnitHudSlotSnapshot>();
            public void Clear() { Selected.Clear(); Seen.Clear(); Slots.Clear(); }
        }
        public sealed class HudFixture {
            const int TroopSlotCount = 8;
            static FieldInfo Field(string name) => typeof(HUD_Troops).GetField(name);
            static readonly FieldInfo SelectedCountsField = Field("Counts"), TroopPositionsField = Field("Positions"),
                PagesField = Field("Pages"), CurrentPageField = Field("Page"), SelectedTypeCountField = Field("TypeCount");
            readonly object sync = new object();
            readonly Stack<HudWorkBuffers> hudBufferPool = new Stack<HudWorkBuffers>();
            readonly List<UnitHudSlotSnapshot> visibleSlots = new List<UnitHudSlotSnapshot>();
            readonly Grid[] categoryHosts = Enumerable.Range(0, 8).Select(_ => new Grid()).ToArray();
            readonly Button[] categoryButtons = Enumerable.Range(0, 8).Select(_ => new Button()).ToArray();
            readonly object[] categoryTints = new object[8];
            List<DisplayEntry> entries;
            HudWorkBuffers RentHudBuffers() => hudBufferPool.Count == 0 ? new HudWorkBuffers() : hudBufferPool.Pop();
            void EnsureCategoryButtons(HUD_Troops panel) { }
            bool TryCaptureSelectedUnits(List<UnitHudUnitSnapshot> selected, HashSet<int> seen) => true;
            List<DisplayEntry> BuildEntries(List<UnitHudUnitSnapshot> selected, int[] counts, bool complete, UnitHudSurface surface, HudWorkBuffers buffers) => entries;
            void HideCategoryButtons() { foreach (var host in categoryHosts) host.Visibility = Visibility.Collapsed; }
            void SetPageButtons(HUD_Troops panel, int page, int pages) { }
            string ResolveText(Category category, UnitHudTextKind kind) => "Lord";
            void ShowTroopHover(Button button, string text) { }
            ImageSource ResolveCategoryImage(Category category, UnitHudSurface surface, HUD_Troops panel) => null;
            void ApplyTroopButtonImages(Button button, Category category, HUD_Troops panel, ImageSource source) { }
            void ApplyTint(object tint, object definition, ImageSource source) { }
            UnitHudCategorySnapshot Snapshot(Category category, List<UnitHudUnitSnapshot> units) => new UnitHudCategorySnapshot();
            void RememberRenderedTroopSelection() { }
            /* RENDERER */

            void Draw(HUD_Troops panel, params bool[] categories) {
                entries = categories.Select(category => new DisplayEntry { Category = category ? new Category() : null }).ToList();
                float[] horizontal = panel.Positions.Select(p => p.X).ToArray();
                RenderTroopCategories(panel);
                for (int slot = 0; slot < visibleSlots.Count; slot++) {
                    bool category = categories[panel.Page * 8 + slot];
                    Check(panel.Positions[slot].X == horizontal[slot], "Horizontal slot position changed");
                    Check(panel.Positions[slot].Y == (category ? 0 : -13), "Wrong vertical position in slot " + slot);
                    Check(panel.Numbers[slot] == 1, "Selection count changed");
                    if (category) {
                        Check(ReferenceEquals(categoryHosts[slot].RenderTransform, panel.Positions[slot]), "Host lost its slot transform");
                        Check(categoryHosts[slot].Visibility == Visibility.Visible, "Category hidden");
                    } else Check(categoryHosts[slot].Visibility == Visibility.Collapsed, "Stale category visible");
                }
            }
            static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
            public static void Run(string scenario) {
                var fixture = new HudFixture(); var panel = new HUD_Troops();
                switch (scenario) {
                    case "lord-first": fixture.Draw(panel, true); break;
                    case "troop-lord-troop":
                        fixture.Draw(panel, false); fixture.Draw(panel, true); fixture.Draw(panel, false); fixture.Draw(panel, true); break;
                    case "mixed":
                        fixture.Draw(panel, false, false, false, false, false, false, false, false);
                        fixture.Draw(panel, false, true, false, true, false, true, false, true);
                        fixture.Draw(panel, true, false, true, false, true, false, true, false); break;
                    case "pages":
                        var categories = new[] { false, false, false, false, false, false, false, false, true, true };
                        fixture.Draw(panel, categories); panel.Page = 1; fixture.Draw(panel, categories);
                        Check(panel.Pages == 2, "Second page lost");
                        panel.Page = 0; fixture.Draw(panel, categories); break;
                    default: throw new Exception("Unknown scenario");
                }
            }
        }
        """;
}

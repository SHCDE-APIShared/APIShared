using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // FW-23: the building panel extension registry runs here with a fake element and view, because the Noesis
    // runtime is not available to the test host. The production service adds the selection read and the Noesis view.
    [TestClass]
    [DoNotParallelize]
    public sealed class BuildingPanelExtensionTests
    {
        public TestContext TestContext { get; set; }
        private static readonly BuildingPanelSelection Barracks = new BuildingPanelSelection(12, 3400, 1, 1, default(eStructs), 0);
        private static readonly Func<BuildingPanelSelection, bool> Always = _ => true;
        private static readonly Action<BuildingPanelSelection, Element> NoUpdate = (_, __) => { };

        internal sealed class Element { internal readonly string Name; internal Element(string name) { Name = name; } }

        internal sealed class View : IBuildingPanelView<Element>
        {
            internal readonly List<Element> Children = new List<Element>();
            internal readonly Dictionary<Element, bool> Shown = new Dictionary<Element, bool>();
            internal bool HostShown;
            internal int Calls;
            public void Insert(Element element, int index) { Calls++; Children.Insert(index, element); }
            public void Remove(Element element) { Calls++; Children.Remove(element); }
            public void SetShown(Element element, bool shown) { Calls++; Shown[element] = shown; }
            public void SetHostShown(bool shown) { Calls++; HostShown = shown; }
            internal string[] Visible => Children.Where(e => Shown.TryGetValue(e, out bool s) && s).Select(e => e.Name).ToArray();
            // Recruitment area and Vanilla's seven recruitment panels; panel 0 (barracks) and 6 (click mask) start shown.
            internal readonly List<Element> Area = new List<Element>();
            internal readonly BuildingPanelVisibility[] Vanilla = {
                BuildingPanelVisibility.Visible, BuildingPanelVisibility.Hidden, BuildingPanelVisibility.Hidden,
                BuildingPanelVisibility.Hidden, BuildingPanelVisibility.Hidden, BuildingPanelVisibility.Hidden,
                BuildingPanelVisibility.Visible };
            public void InsertArea(Element element) { Calls++; Area.Add(element); }
            public void RemoveArea(Element element) { Calls++; Area.Remove(element); }
            public int VanillaPanelCount => Vanilla.Length;
            public BuildingPanelVisibility GetVanilla(int index) => Vanilla[index];
            public void SetVanilla(int index, BuildingPanelVisibility visibility) { Calls++; Vanilla[index] = visibility; }
            internal string[] AreaShown => Area.Where(e => Shown.TryGetValue(e, out bool s) && s).Select(e => e.Name).ToArray();
            internal bool AllVanillaHidden => Vanilla.All(v => v == BuildingPanelVisibility.Collapsed);
        }

        private sealed class Fixture
        {
            internal readonly List<string> Failures = new List<string>();
            internal readonly BuildingPanelExtensionRegistry<Element> Registry;
            internal readonly View View = new View();
            internal Fixture() { Registry = new BuildingPanelExtensionRegistry<Element>((key, phase, error) => Failures.Add(key + ":" + phase)); }
            internal BuildingPanelExtensionRegistry<Element>.Claim Claim(string owner, string id, eStructs[] types,
                Func<object, Element> factory = null, Func<BuildingPanelSelection, bool> appliesTo = null,
                Action<BuildingPanelSelection, Element> update = null)
            {
                Assert.IsTrue(Registry.TryClaim(owner, id, types, factory ?? (_ => new Element(owner + "/" + id)), appliesTo, update,
                    out var claim, out string reason, out _), reason);
                return claim;
            }
            internal void Show(BuildingPanelSelection selection) => Registry.Apply(View, null, selection, true);
            internal BuildingPanelExtensionRegistry<Element>.Entry Add(string owner, string id, int order = 0,
                Func<object, Element> factory = null, Func<BuildingPanelSelection, bool> appliesTo = null,
                Action<BuildingPanelSelection, Element> update = null)
            {
                Assert.IsTrue(Registry.TryAdd(owner, id, order, factory ?? (_ => new Element(owner + "/" + id)), appliesTo, update,
                    out var entry, out string reason), reason);
                return entry;
            }
            internal void Refresh(bool selected = true) => Registry.Apply(View, null, Barracks, selected);
        }

        [TestMethod]
        public void ExtensionsStackByOrderThenOwnerThenId()
        {
            var f = new Fixture();
            f.Add("owner.b", "x", order: 1);
            f.Add("owner.z", "a");
            f.Add("owner.a", "b");
            f.Add("owner.a", "a");
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner.a/a", "owner.a/b", "owner.z/a", "owner.b/x" }, f.View.Visible);
            f.Add("owner.m", "m");
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner.a/a", "owner.a/b", "owner.m/m", "owner.z/a", "owner.b/x" }, f.View.Visible,
                "A later registration must be inserted at its ordered position.");
        }

        [TestMethod]
        public void RefreshTouchesTheViewOnlyWhenVisibilityChanges()
        {
            var f = new Fixture();
            bool applies = true;
            int updates = 0;
            f.Add("owner", "row", appliesTo: _ => applies, update: (s, e) => updates++);
            f.Refresh();
            Assert.IsTrue(f.View.HostShown);
            int calls = f.View.Calls;
            for (int i = 0; i < 5; i++) f.Refresh();
            Assert.AreEqual(calls, f.View.Calls, "An unchanged refresh must not touch the view.");
            Assert.AreEqual(6, updates, "Update runs on every refresh while shown.");
            applies = false;
            f.Refresh();
            Assert.AreEqual(0, f.View.Visible.Length);
            Assert.IsFalse(f.View.HostShown);
            Assert.AreEqual(6, updates, "Update does not run while the filter excludes the building.");
            applies = true;
            f.Refresh();
            f.Refresh(selected: false);
            Assert.AreEqual(0, f.View.Visible.Length, "No selected building hides every extension.");
            Assert.IsFalse(f.View.HostShown);
        }

        [TestMethod]
        public void AFailingCallbackDisablesOnlyItsExtensionUntilRefreshRequested()
        {
            var f = new Fixture();
            bool filterThrows = true;
            int filterCalls = 0;
            var filter = f.Add("owner", "filter", appliesTo: _ => { filterCalls++; if (filterThrows) throw new InvalidOperationException("filter"); return true; });
            f.Add("owner", "update", update: (s, e) => throw new InvalidOperationException("update"));
            f.Add("owner", "factory", factory: _ => throw new InvalidOperationException("factory"));
            f.Add("owner", "null", factory: _ => null);
            f.Add("owner", "healthy");
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner/healthy" }, f.View.Visible);
            CollectionAssert.AreEquivalent(new[] { "owner/filter:filter", "owner/update:update", "owner/factory:factory", "owner/null:factory" }, f.Failures);
            f.Refresh();
            Assert.AreEqual(1, filterCalls, "A disabled extension's callbacks must not run again.");
            Assert.AreEqual(4, f.Failures.Count, "Each failure is reported once.");
            filterThrows = false;
            filter.RequestContentRefresh();
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner/filter", "owner/healthy" }, f.View.Visible);
        }

        [TestMethod]
        public void HiddenExtensionsAndInactiveOwnersRunNoCallbacks()
        {
            var f = new Fixture();
            int calls = 0;
            var row = f.Add("owner.a", "row", appliesTo: _ => { calls++; return true; });
            f.Add("owner.b", "other");
            f.Refresh();
            row.SetVisible(false);
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner.b/other" }, f.View.Visible);
            Assert.AreEqual(1, calls);
            row.SetVisible(true);
            f.Registry.SetOwnerActive("owner.a", false);
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner.b/other" }, f.View.Visible);
            Assert.AreEqual(1, calls);
            f.Registry.SetOwnerActive("owner.a", true);
            f.Refresh();
            CollectionAssert.AreEqual(new[] { "owner.a/row", "owner.b/other" }, f.View.Visible);
        }

        [TestMethod]
        public void DuplicateIdsAreRejectedPerOwner()
        {
            var f = new Fixture();
            f.Add("owner.a", "row");
            Assert.IsFalse(f.Registry.TryAdd("owner.a", "row", 0, _ => new Element("x"), null, null, out var duplicate, out string reason));
            Assert.IsNull(duplicate);
            StringAssert.Contains(reason, "already registered");
            f.Add("owner.b", "row");
            Assert.IsFalse(f.Registry.TryAdd("owner.a", " ", 0, _ => new Element("x"), null, null, out _, out _));
            Assert.IsFalse(f.Registry.TryAdd("owner.a", "nofactory", 0, null, null, null, out _, out _));
        }

        [TestMethod]
        public void ANewHostRecreatesElementsAndKeepsFailures()
        {
            var f = new Fixture();
            int created = 0;
            f.Add("owner", "row", factory: _ => { created++; return new Element("owner/row"); });
            f.Add("owner", "broken", factory: _ => throw new InvalidOperationException());
            f.Refresh();
            var next = new View();
            f.Registry.Reset();
            f.Registry.Apply(next, null, Barracks, true);
            Assert.AreEqual(2, created, "The new host gets fresh elements.");
            CollectionAssert.AreEqual(new[] { "owner/row" }, next.Visible);
            Assert.AreEqual(1, f.Failures.Count, "A failed extension stays disabled on the new host.");
        }

        private const BuildingPanelVisibility V = BuildingPanelVisibility.Visible, H = BuildingPanelVisibility.Hidden;
        private static readonly eStructs Stone = (eStructs)41, Wood = (eStructs)40, Market = (eStructs)3;
        private static BuildingPanelSelection Select(int id, eStructs type) => new BuildingPanelSelection(id, 1000 + id, 1, 1, type, 0);
        private static BuildingPanelVisibility[] Initial => new[] { V, H, H, H, H, H, V };

        [TestMethod]
        public void AClaimHidesVanillaRecruitmentAndRestoresItExactlyOnRelease()
        {
            var f = new Fixture();
            var claim = f.Claim("byz", "garrison", new[] { Stone });
            f.Show(Select(12, Stone));
            Assert.IsTrue(f.View.AllVanillaHidden, "Claiming hides every Vanilla recruitment panel.");
            CollectionAssert.AreEqual(new[] { "byz/garrison" }, f.View.AreaShown);
            claim.Release();
            claim.Release();
            Assert.IsTrue(claim.IsReleased);
            f.Show(Select(12, Stone));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla, "Release must restore every panel exactly.");
            Assert.AreEqual(0, f.View.Area.Count, "A released claim's replacement is removed.");
            Assert.IsFalse(f.Registry.HasEntries, "Nothing is left to refresh.");
        }

        [TestMethod]
        public void DeselectRestoresVanillaAndKeepsVanillasNewerWrites()
        {
            var f = new Fixture();
            f.Claim("byz", "garrison", new[] { Stone });
            f.Show(Select(12, Stone));
            // While claimed, Vanilla shows the mercenary panel: it is re-recorded and hidden again.
            f.View.Vanilla[1] = V;
            f.Show(Select(12, Stone));
            Assert.IsTrue(f.View.AllVanillaHidden);
            // Vanilla sets up the next building before the Post runs; its own write to the barracks panel wins.
            f.View.Vanilla[0] = H;
            f.Show(Select(13, Market));
            CollectionAssert.AreEqual(new[] { H, V, H, H, H, H, V }, f.View.Vanilla);
            Assert.AreEqual(0, f.View.AreaShown.Length);
        }

        [TestMethod]
        public void ASecondClaimantIsRefusedWithTheCurrentOwner()
        {
            var f = new Fixture();
            var first = f.Claim("byz", "garrison", new[] { Stone, Wood });
            Assert.IsFalse(f.Registry.TryClaim("other.mod", "panel", new[] { Wood }, _ => new Element("x"), null, null,
                out var refused, out string reason, out string owner));
            Assert.IsNull(refused);
            Assert.AreEqual("byz", owner);
            StringAssert.Contains(reason, "byz/garrison");
            Assert.IsFalse(f.Registry.TryClaim("byz", "garrison", new[] { Market }, _ => new Element("x"), null, null, out _, out _, out owner));
            Assert.AreEqual("byz", owner, "The same owner and ID cannot claim twice.");
            Assert.IsFalse(f.Registry.TryClaim("other.mod", "empty", new eStructs[0], _ => new Element("x"), null, null, out _, out _, out owner));
            Assert.IsNull(owner, "An invalid request is not a conflict.");
            f.Claim("other.mod", "market", new[] { Market });
            f.Show(Select(12, Wood));
            CollectionAssert.AreEqual(new[] { "byz/garrison" }, f.View.AreaShown, "The refused claimant never overrides.");
            first.Release();
            f.Claim("other.mod", "panel", new[] { Wood });
            f.Show(Select(12, Wood));
            CollectionAssert.AreEqual(new[] { "other.mod/panel" }, f.View.AreaShown);
            Assert.IsTrue(f.View.AllVanillaHidden);
        }

        [TestMethod]
        public void OnlyFilteredBuildingsOfAClaimedTypeLoseTheVanillaArea()
        {
            var f = new Fixture();
            f.Claim("byz", "garrison", new[] { Stone }, appliesTo: s => s.BuildingId == 12);
            f.Show(Select(13, Stone));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla, "Other buildings of the type keep Vanilla.");
            f.Show(Select(12, Stone));
            Assert.IsTrue(f.View.AllVanillaHidden);
            f.Registry.SetOwnerActive("byz", false);
            f.Show(Select(12, Stone));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla, "An inactive owner hands the area back.");
        }

        [TestMethod]
        public void APanelRecreationHandsTheOldPanelBackAndStartsFresh()
        {
            var f = new Fixture();
            int created = 0;
            f.Claim("byz", "garrison", new[] { Stone }, factory: _ => { created++; return new Element("byz/garrison"); });
            f.Show(Select(12, Stone));
            View old = f.View;
            // The service hands the old panel back (no selection) before it forgets it.
            f.Registry.Apply(old, null, default, false);
            CollectionAssert.AreEqual(Initial, old.Vanilla);
            f.Registry.Reset();
            var next = new View();
            f.Registry.Apply(next, null, Select(12, Stone), true);
            Assert.IsTrue(next.AllVanillaHidden);
            Assert.AreEqual(2, created, "The new panel gets a fresh replacement.");
            CollectionAssert.AreEqual(Initial, old.Vanilla, "The old panel is not touched after the reset.");
        }

        [TestMethod]
        public void MissionEndHandsTheAreaBackUntilTheNextSelection()
        {
            var f = new Fixture();
            f.Claim("byz", "garrison", new[] { Stone });
            f.Show(Select(12, Stone));
            f.Registry.ReleaseActive(f.View);
            CollectionAssert.AreEqual(Initial, f.View.Vanilla);
            Assert.AreEqual(0, f.View.AreaShown.Length);
            f.Show(Select(12, Stone));
            Assert.IsTrue(f.View.AllVanillaHidden, "The claim persists into the next mission.");
        }

        [TestMethod]
        public void AFailingClaimHandsTheAreaBackAndReportsOnce()
        {
            var f = new Fixture();
            bool throws = false;
            var claim = f.Claim("byz", "garrison", new[] { Stone }, update: (s, e) => { if (throws) throw new InvalidOperationException(); });
            f.Show(Select(12, Stone));
            throws = true;
            f.Show(Select(12, Stone));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla, "A failed update returns the area to Vanilla.");
            f.Show(Select(12, Stone));
            CollectionAssert.AreEqual(new[] { "byz/garrison:update" }, f.Failures);
            throws = false;
            claim.RequestContentRefresh();
            f.Show(Select(12, Stone));
            Assert.IsTrue(f.View.AllVanillaHidden, "RequestContentRefresh re-enables the claim.");
            f.Claim("other", "broken", new[] { Wood }, factory: _ => throw new InvalidOperationException());
            f.Claim("other", "filter", new[] { Market }, appliesTo: _ => throw new InvalidOperationException());
            f.Show(Select(14, Wood));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla, "A failed factory leaves Vanilla in place.");
            f.Show(Select(15, Market));
            CollectionAssert.AreEqual(Initial, f.View.Vanilla);
            CollectionAssert.AreEqual(new[] { "byz/garrison:update", "other/broken:factory", "other/filter:filter" }, f.Failures);
        }

        [TestMethod]
        public void RefreshAllocatesNothingAndIsComparableToAHandWrittenPost()
        {
            // ns per refresh after warm-up, best of five runs, selection read excluded on both sides (identical work).
            var rows = new List<string>();
            foreach (int count in new[] { 0, 1, 8 })
            {
                var f = new Fixture();
                for (int i = 0; i < count; i++) f.Add("owner", "row" + i, appliesTo: Always, update: NoUpdate);
                f.Refresh();
                Measure(() => f.Registry.Apply(f.View, null, Barracks, true), out double ns, out double bytes);
                rows.Add(string.Format("broker, {0} extensions: {1:F1} ns, {2:F1} B", count, ns, bytes));
                Assert.AreEqual(0.0, bytes, "Refresh with " + count + " extensions allocates.");
            }
            foreach (int count in new[] { 0, 8 })
            {
                var f = new Fixture();
                f.Claim("owner", "area", new[] { Barracks.BuildingType }, appliesTo: Always, update: NoUpdate);
                for (int i = 0; i < count; i++) f.Add("owner", "row" + i, appliesTo: Always, update: NoUpdate);
                f.Refresh();
                Assert.IsTrue(f.View.AllVanillaHidden);
                Measure(() => f.Registry.Apply(f.View, null, Barracks, true), out double ns, out double bytes);
                rows.Add(string.Format("broker, claim held + {0} extensions: {1:F1} ns, {2:F1} B", count, ns, bytes));
                Assert.AreEqual(0.0, bytes, "Refresh with a held claim and " + count + " extensions allocates.");
            }
            var hand = new HandWrittenPost();
            var view = new View();
            hand.Post(view, Barracks, true);
            Measure(() => hand.Post(view, Barracks, true), out double handNs, out double handBytes);
            rows.Add(string.Format("hand-written Post, 1 element: {0:F1} ns, {1:F1} B", handNs, handBytes));
            foreach (string row in rows) TestContext.WriteLine("BUILDING_PANEL_BENCH " + row);
        }

        // What a mod writes without the broker: its own GuiChecks Post with the same filter, update and visibility diff.
        private sealed class HandWrittenPost
        {
            private readonly Func<BuildingPanelSelection, bool> appliesTo = Always;
            private readonly Action<BuildingPanelSelection, Element> update = NoUpdate;
            private readonly Element element = new Element("hand");
            private bool shown;
            internal void Post(View view, BuildingPanelSelection selection, bool selected)
            {
                bool show = selected && appliesTo(selection);
                if (show) update(selection, element);
                if (shown != show) { view.SetShown(element, show); shown = show; }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Measure(Action call, out double nanoseconds, out double bytes)
        {
            const int Iterations = 200000;
            for (int i = 0; i < 20000; i++) call();
            double bestNs = double.MaxValue; long bestBytes = long.MaxValue;
            var watch = new Stopwatch();
            for (int sample = 0; sample < 5; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                for (int i = 0; i < Iterations; i++) call();
                watch.Stop();
                bestBytes = Math.Min(bestBytes, GC.GetAllocatedBytesForCurrentThread() - before);
                bestNs = Math.Min(bestNs, watch.Elapsed.TotalMilliseconds * 1e6 / Iterations);
            }
            nanoseconds = bestNs; bytes = bestBytes / (double)Iterations;
        }

        [TestMethod]
        public void TheRefreshPathNeverSearchesReflectsOrAllocates()
        {
            string[] forbidden = { "FindName", "FindGlobalElement", "GetField", "GetMethod", "GetProperty", "GetValue", "GetType" };
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                var module = assembly.MainModule;
                var service = module.GetType("APIShared.BuildingPanelExtensionsService");
                var registry = module.GetType("APIShared.BuildingPanelExtensionRegistry`1");
                var methods = service.Methods.Where(m => new[] { "OnGuiChecks", "OnSetUp", "Refresh", "TryReadSelection" }.Contains(m.Name))
                    .Concat(registry.Methods.Where(m => new[] { "Apply", "Applies", "Run", "ApplyClaims", "KeepVanillaHidden", "ClaimApplies" }.Contains(m.Name))).ToList();
                Assert.AreEqual(10, methods.Count);
                foreach (var method in methods)
                    foreach (var instruction in method.Body.Instructions)
                    {
                        string name = method.DeclaringType.Name + "." + method.Name;
                        if (instruction.Operand is Mono.Cecil.MethodReference called)
                            Assert.IsFalse(forbidden.Contains(called.Name), name + " calls " + called.Name + " on the refresh path.");
                        // C# boxes a reference-constrained type parameter for null checks; the JIT removes that box.
                        bool referenceBox = instruction.OpCode.Code == Mono.Cecil.Cil.Code.Box &&
                            instruction.Operand is Mono.Cecil.GenericParameter parameter && parameter.HasReferenceTypeConstraint;
                        Assert.IsFalse((instruction.OpCode.Code == Mono.Cecil.Cil.Code.Box && !referenceBox) || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Newarr ||
                            instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldftn || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldvirtftn,
                            name + " allocates on the refresh path (" + instruction.OpCode + ").");
                        if (instruction.OpCode.Code == Mono.Cecil.Cil.Code.Newobj)
                            Assert.IsTrue(((Mono.Cecil.MethodReference)instruction.Operand).DeclaringType.IsValueType,
                                name + " allocates " + ((Mono.Cecil.MethodReference)instruction.Operand).DeclaringType.Name + " on the refresh path.");
                    }
            }
        }

        [TestMethod]
        public void TheGuiChecksPostRunsAfterTheOriginalWithoutAllocation()
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(ApiSharedRuntime).Assembly.Location))
            {
                var site = assembly.MainModule.GetType("APIShared.Presentation.PresentationEvents/Site");
                var code = site.Methods.Single(m => m.Name == "DispatchGui").Body.Instructions.ToList();
                int original = code.FindLastIndex(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "DispatchObservedGui");
                int post = code.FindIndex(i => i.Operand is Mono.Cecil.MethodReference m && m.Name == "Invoke" &&
                    m.DeclaringType.FullName == "System.Action`1<FatControler>");
                Assert.IsTrue(original >= 0 && post > original, "The internal GuiChecks Post must run after the original and public observers.");
            }
        }
    }
}

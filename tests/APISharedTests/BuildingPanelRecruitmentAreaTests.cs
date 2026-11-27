using System;
using System.Collections.Generic;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Decision, hiding/restore and status rules of recruitment-area replacements, through the production
    // BuildingPanelRecruitmentAreas with a fake view; Noesis rendering and the live barracks panel are in-game checks.
    [TestClass]
    public sealed class BuildingPanelRecruitmentAreaTests
    {
        private const int Collapsed = 0, Hidden = 1, Visible = 2;
        private static readonly eStructs[] Stone = { eStructs.STRUCT_BARRACKS_STONE };

        private sealed class View : IBuildingPanelRecruitmentAreaView
        {
            internal readonly int[] Vanilla = { Visible, Hidden, Hidden, Hidden, Hidden, Hidden, Visible };
            internal readonly List<object> Children = new List<object>();
            internal readonly HashSet<object> Shown = new HashSet<object>();
            internal readonly HashSet<string> FailFactory = new HashSet<string>();
            internal int Creates;
            public object Create(BuildingPanelRecruitmentAreaEntry entry, long generation)
            {
                Creates++;
                if (FailFactory.Contains(entry.Id)) throw new InvalidOperationException("factory " + entry.Id);
                var element = entry.Owner + "/" + entry.Id + "#" + generation;
                Children.Add(element);
                return element;
            }
            public void Remove(object element) => Children.Remove(element);
            public void SetShown(object element, bool shown) { if (shown) Shown.Add(element); else Shown.Remove(element); }
            public int VanillaCount => Vanilla.Length;
            public int GetVanilla(int index) => Vanilla[index];
            public void SetVanilla(int index, int visibility) => Vanilla[index] = visibility;
        }

        private sealed class Harness
        {
            internal readonly List<string> Reports = new List<string>();
            internal readonly List<string> Overlaps = new List<string>();
            internal readonly List<string> Events = new List<string>();
            internal readonly BuildingPanelRecruitmentAreas Areas;
            internal readonly View View = new View();
            internal Harness()
            {
                Areas = new BuildingPanelRecruitmentAreas((key, error) => Reports.Add(key),
                    (shown, other, context) => Overlaps.Add(shown.Id + ">" + other.Id + "@" + context.BuildingId));
            }
            internal BuildingPanelRecruitmentAreaEntry Add(string owner, string id, Func<BuildingPanelContext, bool> selects = null,
                eStructs[] types = null)
            {
                var options = new BuildingPanelRecruitmentAreaOptions(owner, id, types ?? Stone, _ => null)
                {
                    Selects = selects,
                    Opened = args => Events.Add("open " + id + "@" + args.Context.BuildingId),
                    Updated = args => Events.Add("update " + id + "@" + args.Context.BuildingId),
                    Closed = args => Events.Add("close " + id + "@" + args.Context.BuildingId)
                };
                Assert.IsTrue(Areas.TryRegister(options, out var entry, out string reason), reason);
                return entry;
            }
            internal bool Step(BuildingPanelContext context, long generation = 1, bool editor = false) =>
                Areas.Step(View, context, generation, editor);
        }

        private static BuildingPanelContext Barracks(int id, eStructs type = eStructs.STRUCT_BARRACKS_STONE) =>
            new BuildingPanelContext(id, (uint)(100 + id), type, 1, 1, 1, 1);

        [TestMethod]
        [TestCategory("Presentation")]
        public void RegistrationRejectsInvalidOrDuplicateVariantsButNeverOverlappingTypes()
        {
            var areas = new BuildingPanelRecruitmentAreas((k, e) => { }, null);
            Assert.IsFalse(areas.TryRegister(null, out _, out _));
            Assert.IsFalse(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("", "v", Stone, _ => null), out _, out _));
            Assert.IsFalse(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("o", "v", Stone, null), out _, out _));
            Assert.IsFalse(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("o", "v", new eStructs[0], _ => null), out _, out _));
            Assert.IsTrue(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("o", "v", Stone, _ => null), out _, out _));
            Assert.IsTrue(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("p", "v", Stone, _ => null), out _, out _),
                "another owner may register a variant on the same native type");
            Assert.IsFalse(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("o", "v", Stone, _ => null), out _, out string reason));
            StringAssert.Contains(reason, "Duplicate");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void PrecedenceIsIndependentOfRegistrationOrder()
        {
            foreach (bool reversed in new[] { false, true })
            {
                var h = new Harness();
                var specs = new (string Owner, string Id, bool Selector)[] { ("b.mod", "whole", false), ("z.mod", "garrison", true), ("a.mod", "fort", true) };
                foreach (var spec in reversed ? specs.Reverse() : specs)
                    h.Add(spec.Owner, spec.Id, spec.Selector ? (Func<BuildingPanelContext, bool>)(c => true) : null);
                CollectionAssert.AreEqual(new[] { "a.mod/fort", "z.mod/garrison", "b.mod/whole" },
                    h.Areas.Entries.Select(e => e.Owner + "/" + e.Id).ToArray(), "selector first, then owner GUID, then variant ID");
                Assert.IsTrue(h.Step(Barracks(7)));
                Assert.AreEqual("fort", h.Areas.Active.Id);
            }
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void VariantsOnOneNativeTypeCoexistAndOnlyOverlapsCompete()
        {
            var h = new Harness();
            var garrison = h.Add("byz", "garrison", c => c.BuildingId == 7);
            var fort = h.Add("other", "fort", c => c.BuildingId == 8);
            Assert.IsTrue(h.Step(Barracks(7)));
            Assert.AreSame(garrison, h.Areas.Active);
            Assert.IsTrue(h.Step(Barracks(8)));
            Assert.AreSame(fort, h.Areas.Active);
            Assert.IsFalse(h.Step(Barracks(9)), "an unselected building keeps Vanilla's area");
            Assert.IsNull(h.Areas.Active);
            Assert.AreEqual(0, h.Overlaps.Count, "disjoint selectors never compete");
            CollectionAssert.AreEqual(new[] { Visible, Hidden, Hidden, Hidden, Hidden, Hidden, Visible }, h.View.Vanilla);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ShownReplacementHidesVanillaAndRestoresOnlyItsOwnCollapse()
        {
            var h = new Harness();
            h.Add("byz", "garrison");
            var seven = Barracks(7); // production reuses one context instance while nothing changes
            Assert.IsTrue(h.Step(seven));
            Assert.IsTrue(h.View.Vanilla.All(v => v == Collapsed));
            Assert.AreEqual(1, h.View.Shown.Count);
            // Vanilla's setUpInbuilding rewrites a panel while the replacement stays: it is re-recorded and hidden again.
            h.View.Vanilla[0] = Hidden;
            Assert.IsTrue(h.Step(seven));
            Assert.AreEqual(Collapsed, h.View.Vanilla[0]);
            // Another building: Vanilla writes Hidden/Visible before the check; only panels still Collapsed are restored.
            h.View.Vanilla[1] = Visible;
            h.Areas.Entries[0].Enabled = false;
            Assert.IsFalse(h.Step(Barracks(9)));
            CollectionAssert.AreEqual(new[] { Hidden, Visible, Hidden, Hidden, Hidden, Hidden, Visible }, h.View.Vanilla,
                "the re-recorded value is restored and a newer Vanilla write wins");
            Assert.AreEqual(0, h.View.Shown.Count);
            CollectionAssert.AreEqual(new[] { "open garrison@7", "update garrison@7", "close garrison@7" }, h.Events);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void SameVariantOnAnotherBuildingEndsTheShowingAndStartsANewOne()
        {
            var h = new Harness();
            h.Add("byz", "garrison");
            h.Step(Barracks(7));
            h.Step(Barracks(8));
            CollectionAssert.AreEqual(new[] { "open garrison@7", "close garrison@7", "open garrison@8" }, h.Events);
            Assert.IsTrue(h.View.Vanilla.All(v => v == Collapsed));
            Assert.AreEqual(1, h.View.Creates, "one control per HUD instance");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void CompetingPairIsObservedOnceAndReportedToBothStatuses()
        {
            var h = new Harness();
            h.Add("b.mod", "whole");
            h.Add("a.mod", "garrison", c => true);
            var seven = Barracks(7);
            for (int i = 0; i < 3; i++) h.Step(seven);
            CollectionAssert.AreEqual(new[] { "garrison>whole@7" }, h.Overlaps, "logged once per pair, not per check");
            Assert.IsTrue(h.Areas.TryGetStatus("a.mod", "garrison", out var winner, out _));
            Assert.IsTrue(winner.IsShownNow);
            Assert.IsTrue(winner.HasSelector);
            var rival = winner.Rivals.Single();
            Assert.AreEqual("b.mod", rival.OwnerGuid);
            Assert.IsFalse(rival.Precedes);
            Assert.IsTrue(rival.ObservedOverlap);
            CollectionAssert.AreEqual(Stone, rival.SharedBuildingTypes.ToArray());
            Assert.IsTrue(h.Areas.TryGetStatus("b.mod", "whole", out var loser, out _));
            Assert.IsFalse(loser.IsShownNow);
            Assert.IsTrue(loser.Rivals.Single().Precedes, "the loser's tooltip can name the effective owner");
            Assert.IsTrue(loser.Rivals.Single().ObservedOverlap);
            Assert.IsFalse(h.Areas.TryGetStatus("a.mod", "missing", out _, out string reason));
            StringAssert.Contains(reason, "missing");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void StatusReadingEvaluatesNoSelectorAndChangesNothing()
        {
            var h = new Harness();
            int calls = 0;
            h.Add("a.mod", "garrison", c => { calls++; return true; });
            h.Add("c.mod", "wood", types: new[] { eStructs.STRUCT_BARRACKS_WOOD });
            h.Step(Barracks(7));
            int before = calls;
            int[] vanilla = (int[])h.View.Vanilla.Clone();
            Assert.IsTrue(h.Areas.TryGetStatus("a.mod", "garrison", out var status, out _));
            Assert.AreEqual(before, calls);
            CollectionAssert.AreEqual(vanilla, h.View.Vanilla);
            Assert.AreEqual(0, status.Rivals.Count, "a variant on another native type is no rival");
            Assert.IsFalse(status.Rivals.Any(r => r.ObservedOverlap));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FailuresFallThroughAndRecoverWithANewContext()
        {
            var h = new Harness();
            var narrow = h.Add("a.mod", "narrow", c => { if (c.BuildingId == 7) throw new InvalidOperationException("selector"); return true; });
            h.Add("b.mod", "whole");
            var seven = Barracks(7);
            Assert.IsTrue(h.Step(seven));
            Assert.AreEqual("whole", h.Areas.Active.Id, "a throwing selector lets the next variant show");
            Assert.IsTrue(h.Step(seven));
            Assert.AreEqual(1, h.Reports.Count(r => r == "selector/a.mod/narrow"));
            Assert.IsTrue(h.Step(Barracks(8)));
            Assert.AreSame(narrow, h.Areas.Active, "the faulted variant returns with another context");

            var f = new Harness();
            f.View.FailFactory.Add("narrow");
            f.Add("a.mod", "narrow", c => true);
            f.Add("b.mod", "whole");
            Assert.IsTrue(f.Step(Barracks(7)));
            Assert.AreEqual("whole", f.Areas.Active.Id, "a failed factory hands the area to the next variant for this HUD instance");
            f.Step(Barracks(8));
            Assert.AreEqual(2, f.View.Creates, "the failed factory is not retried within its HUD instance");
            f.Areas.Release(f.View);
            Assert.IsTrue(f.Step(Barracks(8), generation: 2));
            Assert.AreEqual(4, f.View.Creates, "a new HUD instance retries every factory once");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FailingOpenedCallbackRestoresVanilla()
        {
            var h = new Harness();
            Assert.IsTrue(h.Areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("a.mod", "bad", Stone, _ => null)
            { Opened = _ => throw new InvalidOperationException("opened") }, out _, out _));
            var seven = Barracks(7);
            Assert.IsFalse(h.Step(seven));
            CollectionAssert.AreEqual(new[] { Visible, Hidden, Hidden, Hidden, Hidden, Hidden, Visible }, h.View.Vanilla);
            Assert.AreEqual(0, h.View.Shown.Count);
            Assert.IsFalse(h.Step(seven), "stays out of the failing context");
            Assert.AreEqual(1, h.View.Creates);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void MapEditorClosedPanelAndHudReleaseHandTheAreaBack()
        {
            var h = new Harness();
            h.Add("byz", "garrison");
            Assert.IsFalse(h.Step(Barracks(7), editor: true), "never shown in the map editor");
            Assert.IsTrue(h.View.Vanilla.All(v => v != Collapsed));
            Assert.IsTrue(h.Step(Barracks(7)));
            Assert.IsFalse(h.Step(null));
            Assert.IsFalse(h.View.Vanilla.Any(v => v == Collapsed), "a closed panel restores Vanilla");
            Assert.IsTrue(h.Step(Barracks(7)));
            h.Areas.Release(h.View);
            Assert.IsNull(h.Areas.Active);
            Assert.AreEqual(0, h.View.Children.Count, "controls of the replaced HUD are removed");
            Assert.IsFalse(h.View.Vanilla.Any(v => v == Collapsed));
            h.Areas.Release(null);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void SteadyPanelAllocatesNothingAfterWarmup()
        {
            var h = new Harness();
            var areas = new BuildingPanelRecruitmentAreas((k, e) => { }, null);
            Assert.IsTrue(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("a.mod", "garrison", Stone, _ => null) { Selects = c => true }, out _, out _));
            Assert.IsTrue(areas.TryRegister(new BuildingPanelRecruitmentAreaOptions("b.mod", "whole", Stone, _ => null), out _, out _));
            var context = Barracks(7);
            for (int i = 0; i < 64; i++) areas.Step(h.View, context, 1, false);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) areas.Step(h.View, context, 1, false);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, "a steady replaced panel and a known overlap allocate nothing");
        }
    }
}

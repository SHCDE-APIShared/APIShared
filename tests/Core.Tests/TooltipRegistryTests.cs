using System;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class TooltipRegistryTests
    {
        private static TooltipContext Context(TooltipTarget target = TooltipTarget.BuildingCompact, bool active = true) =>
            new TooltipContext(target, active, 18, null, 2, 1, true);
        private static TooltipLine[] Text(string value) => new[] { new TooltipLine(new TooltipText(value)) };
        private static string Value(TooltipRegistry.Block block) => ((TooltipText)block.Line.Segments[0]).Text;

        [TestMethod]
        public void TargetsPlacementsAndOwnerOrderingComposeWithoutOverwriting()
        {
            var registry = new TooltipRegistry();
            Assert.IsTrue(registry.TryRegister("foreign.z", "same", TooltipTarget.BuildingCompact | TooltipTarget.BuildingDetailed,
                TooltipPlacement.InlineAfterCosts, _ => Text("second"), 1, out _));
            Assert.IsTrue(registry.TryRegister("foreign.a", "same", TooltipTarget.BuildingCompact,
                TooltipPlacement.BeforeMainLine, _ => Text("first"), 1, out _));
            Assert.IsTrue(registry.TryRegister("foreign.z", "early", TooltipTarget.BuildingCompact,
                TooltipPlacement.AfterMainLine, _ => Text("early"), -1, out _));
            CollectionAssert.AreEqual(new[] { "early", "first", "second" }, registry.Collect(Context(), null).Select(Value).ToArray());
            Assert.AreEqual("second", Value(registry.Collect(Context(TooltipTarget.BuildingDetailed), null).Single()));
            Assert.AreEqual(0, registry.Collect(Context(TooltipTarget.Recruitment), null).Length);
            Assert.IsFalse(registry.TryRegister("foreign.a", "same", TooltipTarget.Recruitment,
                TooltipPlacement.AfterMainLine, _ => Text("duplicate"), 0, out _));
            Assert.IsFalse(registry.TryRegister("", "id", TooltipTarget.Repair, TooltipPlacement.AfterMainLine, _ => Text("x"), 0, out _));
            Assert.IsFalse(registry.TryRegister("owner", "id", TooltipTarget.Recruitment, TooltipPlacement.BeforeDescription, _ => Text("x"), 0, out _));
            Assert.IsTrue(TooltipRegistry.Supports(TooltipTarget.BuildingDetailed, TooltipPlacement.AfterDescription));
            Assert.IsFalse(TooltipRegistry.Supports((TooltipTarget)128, TooltipPlacement.AfterMainLine));
            Assert.IsFalse(TooltipRegistry.Supports(TooltipTarget.Repair, (TooltipPlacement)99));
        }

        [TestMethod]
        public void EmptyContentReservesNothingAndInactiveProvidersCanRetireState()
        {
            var registry = new TooltipRegistry(); int inactive = 0;
            registry.TryRegister("independent", "rows", TooltipTarget.Repair, TooltipPlacement.InlineAfterCosts, context => {
                if (!context.IsActive) inactive++;
                return new[] { new TooltipLine(new TooltipText(" \r\n\t")), new TooltipLine(new TooltipSymbol(null)),
                    new TooltipLine(new TooltipSymbol(null, "horse")), new TooltipLine(new TooltipText("literal <Grid/>")) };
            }, 0, out _);
            var rows = registry.Collect(Context(TooltipTarget.Repair), null);
            Assert.AreEqual(2, rows.Length);
            Assert.AreEqual("literal <Grid/>", Value(rows[1]));
            Assert.AreEqual(0, registry.Collect(Context(TooltipTarget.Repair, false), null).Length);
            Assert.AreEqual(1, inactive);
        }

        [TestMethod]
        public void InvalidPartialResultAndThrowingProviderDoNotHideOtherOwners()
        {
            var registry = new TooltipRegistry(); bool fail = true; int reports = 0;
            registry.TryRegister("a", "broken", TooltipTarget.Recruitment, TooltipPlacement.AfterMainLine,
                _ => fail ? new[] { Text("partial")[0], null } : Text("recovered"), 0, out _);
            registry.TryRegister("b", "valid", TooltipTarget.Recruitment, TooltipPlacement.AfterMainLine, _ => Text("alive"), 0, out _);
            for (int i = 0; i < 3; i++) Assert.AreEqual("alive", Value(registry.Collect(Context(TooltipTarget.Recruitment), (_, _) => reports++).Single()));
            Assert.AreEqual(1, reports);
            fail = false;
            Assert.AreEqual(2, registry.Collect(Context(TooltipTarget.Recruitment), null).Length);
        }

        [TestMethod]
        public void PublishedSnapshotsStayImmutableAndUnchangedOutputIsReused()
        {
            var registry = new TooltipRegistry(); string value = "one";
            registry.TryRegister("owner", "rows", TooltipTarget.SiegeConstruction, TooltipPlacement.BeforeMainLine, _ => Text(value), 0, out _);
            var first = registry.Collect(Context(TooltipTarget.SiegeConstruction), null);
            Assert.AreSame(first, registry.Collect(Context(TooltipTarget.SiegeConstruction), null));
            value = "two";
            var second = registry.Collect(Context(TooltipTarget.SiegeConstruction), null);
            Assert.AreNotSame(first, second); Assert.AreEqual("one", Value(first.Single())); Assert.AreEqual("two", Value(second.Single()));
            var segments = new TooltipSegment[] { new TooltipText("safe") };
            var line = new TooltipLine(segments); segments[0] = new TooltipText("changed");
            Assert.AreEqual("safe", ((TooltipText)line.Segments[0]).Text);
            Assert.ThrowsExactly<ArgumentException>(() => new TooltipLine(new TooltipSegment[] { null }));
        }

        [TestMethod]
        public void RegistrationFromProviderJoinsNextCollectionAndRecursionIsIsolated()
        {
            var registry = new TooltipRegistry(); bool added = false;
            registry.TryRegister("a", "first", TooltipTarget.Repair, TooltipPlacement.AfterMainLine, context => {
                if (!added) { added = true; registry.TryRegister("b", "later", TooltipTarget.Repair, TooltipPlacement.AfterMainLine, _ => Text("later"), 0, out _); }
                return Text("first");
            }, 0, out _);
            registry.TryRegister("c", "recursive", TooltipTarget.Repair, TooltipPlacement.AfterMainLine,
                _ => { registry.Collect(Context(TooltipTarget.Repair), null); return Text("unreachable"); }, 0, out _);
            Assert.AreEqual(1, registry.Collect(Context(TooltipTarget.Repair), null).Length);
            Assert.AreEqual(2, registry.Collect(Context(TooltipTarget.Repair), null).Length);
        }

        [TestMethod]
        public void StableAndEmptyCollectionsDoNotAllocateAfterWarmup()
        {
            var registry = new TooltipRegistry();
            var line = Text("cached");
            var active = Context(); var inactive = Context(active: false);
            registry.TryRegister("foreign", "cached", TooltipTarget.BuildingCompact, TooltipPlacement.InlineAfterCosts, _ => line, 0, out _);
            for (int i = 0; i < 2000; i++) { registry.Collect(active, null); registry.Collect(inactive, null); }
            // Measure each stable state separately: transitions intentionally publish new immutable snapshots.
            foreach (var context in new[] { active, inactive })
            {
                registry.Collect(context, null);
                long best = long.MaxValue;
                for (int sample = 0; sample < 5; sample++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 20000; i++) registry.Collect(context, null);
                    best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
                }
                Assert.AreEqual(0L, best, "Unchanged provider output must reuse snapshots without per-pass allocations.");
            }
        }
    }
}

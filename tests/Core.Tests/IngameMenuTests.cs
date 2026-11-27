using System;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    // Exercises the production resolver/evaluator, not a second implementation of the rules.
    [TestClass]
    public sealed class IngameMenuTests
    {
        private static IngameMenuActionState Shown(string text = "action", bool enabled = true) => new IngameMenuActionState(text, true, enabled);
        private static IngameMenuActionOptions Left(bool replace = false) =>
            new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Left, replace);
        private static IngameMenuRegistry.Entry Add(IngameMenuRegistry registry, string owner, string id,
            Func<IngameMenuActionState> state, IngameMenuActionOptions options, int order = 0)
        {
            Assert.IsTrue(registry.CanRegister(owner, id, state, () => { }, options, out string reason), reason);
            return registry.Register(owner, id, state, () => { }, options, order);
        }
        [TestMethod]
        public void ConflictsAreExplicitIncludingHiddenClaimsAndVanilla()
        {
            var registry = new IngameMenuRegistry();
            Add(registry, "a", "hidden", () => null, Left());
            Assert.IsFalse(registry.CanRegister("b", "x", () => Shown(), () => { }, Left(), out string reason));
            StringAssert.Contains(reason, "a/hidden");
            Assert.AreEqual(1, registry.Entries.Length);
            Assert.IsFalse(registry.CanRegister("b", "right", () => Shown(), () => { },
                new IngameMenuActionOptions(IngameMenuRow.Save, IngameMenuSlot.Right), out _));
            Add(registry, "b", "right", () => Shown(),
                new IngameMenuActionOptions(IngameMenuRow.Save, IngameMenuSlot.Right, replaceVanilla: true));
            Assert.IsFalse(registry.CanRegister("b", "right", () => Shown(), () => { }, new IngameMenuActionOptions(), out _));
            // Every public registration states its placement; there is no implicit QuitMission/Left fallback.
            Assert.IsFalse(registry.CanRegister("b", "implicit", () => Shown(), () => { }, null, out reason));
            StringAssert.Contains(reason, "Placement options are required");
        }
        [TestMethod]
        public void TakeoversRestorePrecedingVisibleClaimWithoutChangingPrecedenceOnRefresh()
        {
            var registry = new IngameMenuRegistry();
            bool secondVisible = true, thirdVisible = true;
            var first = Add(registry, "first", "x", () => Shown("first"), Left());
            var second = Add(registry, "second", "x", () => secondVisible ? Shown("second", false) : null, Left(true));
            var third = Add(registry, "third", "x", () => Shown("third"), Left(true));
            third.Provider = () => thirdVisible ? Shown("third") : new IngameMenuActionState("third", false, true);
            registry.Evaluate(true, null);
            Assert.AreSame(third, registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 0]);
            thirdVisible = false; registry.Invalidate("third"); registry.Evaluate(false, null);
            Assert.AreSame(second, registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 0]);
            Assert.IsFalse(second.State.Enabled); // Disabled is still a real occupant.
            registry.Invalidate("first"); registry.Evaluate(false, null);
            Assert.AreSame(second, registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 0]);
            secondVisible = false; registry.Invalidate("second"); registry.Evaluate(false, null);
            Assert.AreSame(first, registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 0]);
        }
        [TestMethod]
        public void BothPositionsComposeAndDisplacedVanillaRemainsInOverflow()
        {
            var registry = new IngameMenuRegistry();
            registry.SetVanilla(IngameMenuRow.QuitMission, Shown("Quit"));
            var left = Add(registry, "a", "left", () => Shown(), Left());
            bool rightVisible = true;
            var right = Add(registry, "b", "right", () => rightVisible ? Shown() : null,
                new IngameMenuActionOptions(IngameMenuRow.QuitMission, IngameMenuSlot.Right, replaceVanilla: true));
            registry.Evaluate(true, null);
            var layout = registry.Resolve();
            Assert.AreSame(left, layout.Slots[(int)IngameMenuRow.QuitMission, 0]);
            Assert.AreSame(right, layout.Slots[(int)IngameMenuRow.QuitMission, 1]);
            Assert.AreSame(registry.Vanilla(IngameMenuRow.QuitMission), layout.Overflow.Single());
            rightVisible = false; registry.Invalidate("b"); registry.Evaluate(false, null);
            Assert.AreSame(registry.Vanilla(IngameMenuRow.QuitMission), registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 1]);
            Assert.AreEqual(0, registry.Resolve().Overflow.Count);
        }
        [TestMethod]
        public void SuppressionExcludesVanillaFromBothPagesWithoutClaimingItsSlot()
        {
            var registry = new IngameMenuRegistry();
            const IngameMenuRow row = IngameMenuRow.QuitMission;
            var original = registry.Vanilla(row);
            registry.SetVanilla(row, Shown("Quit"));
            bool suppress = true, replace = true;
            Assert.IsTrue(registry.CanRegisterSuppression("hide", "quit", row, () => suppress, out string reason), reason);
            registry.RegisterSuppression("hide", "quit", row, () => suppress);
            registry.Evaluate(true, null);
            var hidden = registry.GetRow(row).Right;
            Assert.IsNull(hidden.Current);
            Assert.IsTrue(hidden.Claims.Single().IsSuppressed);
            Assert.IsTrue(hidden.Claims.Single().State.Visible);
            Assert.AreEqual(0, registry.Resolve().Overflow.Count);
            Assert.IsFalse(registry.CanExecute(original, false));
            Assert.IsFalse(registry.CanExecute(original, true));

            var replacement = Add(registry, "replace", "quit", () => replace ? Shown("Replacement") : null,
                new IngameMenuActionOptions(row, IngameMenuSlot.Right, replaceVanilla: true));
            var extra = Add(registry, "other", "extra", () => Shown(), new IngameMenuActionOptions());
            registry.Evaluate(false, null);
            Assert.AreSame(replacement, registry.Resolve().Slots[(int)row, 1]);
            Assert.IsTrue(registry.CanExecute(replacement, false));
            Assert.AreSame(extra, registry.Resolve().Overflow.Single());

            suppress = false;
            registry.Invalidate("hide");
            registry.Evaluate(false, null);
            CollectionAssert.AreEquivalent(new[] { original, extra }, registry.Resolve().Overflow.ToArray());
            Assert.IsFalse(registry.GetRow(row).Right.Claims.Single(claim => claim.IsVanilla).IsSuppressed);
            Assert.IsTrue(hidden.Claims.Single().IsSuppressed); // Earlier snapshots retain their original state.

            replace = false;
            registry.Evaluate(true, null);
            Assert.AreSame(original, registry.Resolve().Slots[(int)row, 1]);
            registry.SetVanilla(row, new IngameMenuActionState("Quit", false, true));
            Assert.IsNull(registry.GetRow(row).Right.Current);
            Assert.AreSame(extra, registry.Resolve().Overflow.Single());
        }
        [TestMethod]
        public void SuppressionsComposeAndAProviderFailureReleasesOnlyItsOwnRule()
        {
            var registry = new IngameMenuRegistry();
            const IngameMenuRow row = IngameMenuRow.Help;
            registry.SetVanilla(row, Shown("Help"));
            bool broken = false, secondActive = true;
            int firstCalls = 0, secondCalls = 0;
            registry.RegisterSuppression("first", "help", row, () =>
            {
                firstCalls++;
                return broken ? throw new InvalidOperationException("suppression") : true;
            });
            registry.RegisterSuppression("second", "help", row, () => { secondCalls++; return secondActive; });
            registry.Evaluate(true, null);
            Assert.IsNull(registry.GetRow(row).Right.Current);
            broken = true;
            registry.Invalidate("first");
            registry.Evaluate(false, (registration, error) =>
            {
                Assert.AreEqual("first", registration.Owner);
                Assert.AreEqual("suppression", error.Message);
            });
            Assert.IsNull(registry.GetRow(row).Right.Current);
            Assert.AreEqual(2, firstCalls);
            Assert.AreEqual(1, secondCalls);
            secondActive = false;
            registry.Invalidate("second");
            registry.Evaluate(false, null);
            Assert.IsTrue(registry.GetRow(row).Right.Current.IsVanilla);
            Assert.AreEqual(2, secondCalls);
            broken = false;
            registry.Invalidate("first");
            registry.Evaluate(false, null);
            Assert.IsNull(registry.GetRow(row).Right.Current);
            Assert.AreEqual(3, firstCalls);
        }
        [TestMethod]
        public void SuppressionValidatesRowProviderAndSharedOwnerLocalIdentity()
        {
            var registry = new IngameMenuRegistry();
            const IngameMenuRow row = IngameMenuRow.Help;
            Add(registry, "mod", "action", () => Shown(), new IngameMenuActionOptions());
            Assert.IsFalse(registry.CanRegisterSuppression("mod", "action", row, () => true, out _));
            Assert.IsFalse(registry.CanRegisterSuppression("mod", "invalid", (IngameMenuRow)int.MaxValue, () => true, out _));
            Assert.IsFalse(registry.CanRegisterSuppression("mod", "invalid", row, null, out _));
            Assert.IsFalse(registry.CanRegisterSuppression(IngameMenuRegistry.VanillaOwner, "invalid", row, () => true, out _));
            registry.RegisterSuppression("mod", "hide", row, () => true);
            Assert.IsFalse(registry.CanRegisterSuppression("mod", "hide", IngameMenuRow.Save, () => true, out _));
            Assert.IsFalse(registry.CanRegister("mod", "hide", () => Shown(), () => { }, new IngameMenuActionOptions(), out _));
            Assert.IsTrue(registry.CanRegisterSuppression("other", "hide", row, () => true, out string reason), reason);
        }
        [TestMethod]
        public void OverflowIncludesEveryDisplacedAndAdditionalEntryExactlyOnceInStableOrder()
        {
            var registry = new IngameMenuRegistry();
            var displaced = Add(registry, "z", "old", () => Shown(), Left(), -1);
            Add(registry, "winner", "new", () => Shown(), Left(true));
            var a = Add(registry, "a", "same", () => Shown(), new IngameMenuActionOptions());
            var b = Add(registry, "b", "same", () => Shown(), new IngameMenuActionOptions());
            Add(registry, "hidden", "x", () => null, new IngameMenuActionOptions());
            registry.Evaluate(true, null);
            CollectionAssert.AreEqual(new[] { displaced, a, b }, registry.Resolve().Overflow.ToArray());
            Assert.AreEqual(registry.Resolve().Overflow.Count, registry.Resolve().Overflow.Distinct().Count());
        }
        [TestMethod]
        public void QueriesAndIdleResolutionNeverPollProvidersAndSnapshotsAreDetached()
        {
            var registry = new IngameMenuRegistry();
            int calls = 0;
            Add(registry, "a", "x", () => { calls++; return Shown(calls.ToString()); }, Left());
            var unknown = registry.GetRow(IngameMenuRow.QuitMission);
            Assert.IsFalse(unknown.Left.Claims.Single().HasState);
            Assert.AreEqual(0, calls);
            registry.Evaluate(true, null); // Opening.
            var before = registry.GetRow(IngameMenuRow.QuitMission);
            for (int i = 0; i < 1000; i++) { registry.Resolve(); registry.GetRow(IngameMenuRow.QuitMission); registry.Evaluate(false, null); }
            Assert.AreEqual(1, calls);
            registry.Invalidate("a"); registry.Invalidate("a");
            Assert.AreEqual(1, calls); // Closed-menu invalidation only records demand.
            registry.Evaluate(false, null);
            Assert.AreEqual(2, calls);
            Assert.AreEqual("1", before.Left.Current.State.Text);
            Assert.IsFalse(unknown.Left.Claims.Single().HasState);
        }
        [TestMethod]
        public void ProviderFailuresDoNotStarveOtherActionsAndCanRecoverOnNotification()
        {
            var registry = new IngameMenuRegistry();
            bool broken = true;
            Add(registry, "a", "broken", () => broken ? throw new InvalidOperationException("provider") : Shown(), Left());
            var good = Add(registry, "b", "good", () => Shown(), new IngameMenuActionOptions());
            int errors = 0;
            registry.Evaluate(true, (_, error) => { Assert.AreEqual("provider", error.Message); errors++; });
            Assert.AreEqual(1, errors);
            Assert.IsNull(registry.Resolve().Slots[(int)IngameMenuRow.QuitMission, 0]);
            Assert.AreSame(good, registry.Resolve().Overflow.Single());
            broken = false; registry.Invalidate("a"); registry.Evaluate(false, null);
            Assert.AreEqual("a", registry.GetRow(IngameMenuRow.QuitMission).Left.Current.OwnerGuid);
        }
        [TestMethod]
        public void ReentrantProviderCannotMutateRegistrationOrRecursivelyEvaluate()
        {
            var registry = new IngameMenuRegistry();
            int calls = 0;
            Add(registry, "a", "x", () => {
                calls++;
                Assert.IsFalse(registry.CanRegister("b", "nested", () => Shown(), () => { }, new IngameMenuActionOptions(), out _));
                registry.Evaluate(true, null);
                return Shown();
            }, Left());
            registry.Evaluate(true, null);
            Assert.AreEqual(1, calls);
        }
        [TestMethod]
        public void CommandValidationRejectsStalePlacementHiddenAndDisabledActions()
        {
            var registry = new IngameMenuRegistry();
            bool enabled = true, visible = true;
            var first = Add(registry, "first", "x", () => new IngameMenuActionState("first", visible, enabled), Left());
            registry.Evaluate(true, null);
            Assert.IsTrue(registry.CanExecute(first, false));
            Assert.IsFalse(registry.CanExecute(first, true));
            var second = Add(registry, "second", "x", () => Shown(), Left(true));
            registry.Evaluate(false, null);
            Assert.IsFalse(registry.CanExecute(first, false));
            Assert.IsTrue(registry.CanExecute(first, true));
            Assert.IsTrue(registry.CanExecute(second, false));
            enabled = false; registry.Invalidate("first"); registry.Evaluate(false, null);
            Assert.IsFalse(registry.CanExecute(first, true));
            visible = false; registry.Invalidate("first"); registry.Evaluate(false, null);
            Assert.IsFalse(registry.CanExecute(first, true));
        }
        [TestMethod]
        public void RowIdentityDoesNotShiftWhenModeHidesVanillaRows()
        {
            var registry = new IngameMenuRegistry();
            Add(registry, "a", "editor", () => Shown(), new IngameMenuActionOptions(IngameMenuRow.Workshop, IngameMenuSlot.Left));
            registry.SetVanilla(IngameMenuRow.Workshop, new IngameMenuActionState("Workshop", false, false));
            registry.Evaluate(true, null);
            Assert.IsNotNull(registry.GetRow(IngameMenuRow.Workshop).Left.Current);
            Assert.IsNull(registry.GetRow(IngameMenuRow.Workshop).Right.Current);
            Assert.IsNull(registry.GetRow(IngameMenuRow.Load).Left.Current);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => registry.GetRow((IngameMenuRow)999));
        }
        [TestMethod]
        public void TooltipsAreExplicitAndSnapshotsKeepTheirPreviouslyEvaluatedText()
        {
            Assert.IsNull(new IngameMenuActionState("Label", true, true).Tooltip);
            foreach (var empty in new[] { null, "", " \t\r\n" })
                Assert.IsNull(new IngameMenuActionState("Label", true, true, empty).Tooltip);
            string tooltip = " Exact help\nwith spacing ";
            var registry = new IngameMenuRegistry();
            Add(registry, "owner", "action", () => new IngameMenuActionState("Label", true, true, tooltip), Left());
            registry.Evaluate(true, null);
            var snapshot = registry.GetRow(IngameMenuRow.QuitMission);
            Assert.AreEqual(tooltip, snapshot.Left.Current.State.Tooltip);
            tooltip = "Updated"; registry.Invalidate("owner"); registry.Evaluate(false, null);
            Assert.AreEqual("Updated", registry.GetRow(IngameMenuRow.QuitMission).Left.Current.State.Tooltip);
            Assert.AreEqual(" Exact help\nwith spacing ", snapshot.Left.Current.State.Tooltip);
        }
    }
}

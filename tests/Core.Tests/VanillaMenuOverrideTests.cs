using System;
using System.Linq;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;

namespace Core.Tests
{
    // Production overlay/command policy with Noesis boundary doubles. These tests
    // do not prove rendering, BepInEx hook installation or physical input delivery.
    [TestClass]
    public sealed class VanillaMenuOverrideTests
    {
        private static void Add(VanillaMenuOverrides registry, IngameMenuRow row, Func<bool?> enabled,
            Action execute, string owner = "mod", string id = "action")
        {
            Assert.IsTrue(registry.CanRegister(owner, id, row, enabled, execute, out string reason), reason);
            registry.Register(owner, id, row, enabled, execute);
        }
        private static Button[] Buttons() => Enumerable.Range(0, 9).Select(_ => new Button { IsEnabled = false }).ToArray();
        [TestMethod]
        public void EveryVanillaCommandKeepsAnIndependentNullableOverride()
        {
            int[] functions = { 1, 2, 99, 3, 4, 5, 6, 7, 8 };
            var overrides = new VanillaMenuOverrides();
            bool?[] enabled = new bool?[9]; int[] executions = new int[9];
            for (int i = 0; i < functions.Length; i++)
            {
                int slot = i;
                Add(overrides, (IngameMenuRow)i, () => enabled[slot], () => executions[slot]++, id: "row" + i);
            }
            var buttons = Buttons();
            foreach (bool? decision in new bool?[] { null, true, false, null })
                for (int i = 0; i < functions.Length; i++)
                {
                    enabled[i] = decision; overrides.Apply(buttons, null);
                    Assert.AreEqual(decision ?? false, buttons[i].IsEnabled);
                    int before = executions[i];
                    Assert.AreEqual(decision.HasValue, overrides.TryExecute(overrides.Get(functions[i]), null));
                    Assert.AreEqual(before + (decision == true ? 1 : 0), executions[i]);
                    Assert.AreEqual((IngameMenuRow)i, overrides.Get(functions[i]).Row);
                }
            foreach (int unknown in new[] { -1, 0, 9, 98, 100, int.MaxValue }) Assert.IsNull(overrides.Get(unknown));
        }
        [TestMethod]
        public void RefreshRestoresOnlyItsOwnEnabledValueAndInitStartsANewBaseline()
        {
            bool? enabled = true;
            var overrides = new VanillaMenuOverrides(); var buttons = Buttons();
            Add(overrides, IngameMenuRow.RestartMission, () => enabled, () => { });
            overrides.Apply(buttons, null); Assert.IsTrue(buttons[5].IsEnabled);
            enabled = null; overrides.Apply(buttons, null); Assert.IsFalse(buttons[5].IsEnabled);
            enabled = false; buttons[5].IsEnabled = true; overrides.Apply(buttons, null);
            enabled = null; overrides.Apply(buttons, null); Assert.IsTrue(buttons[5].IsEnabled);
            enabled = false; overrides.Apply(buttons, null);
            buttons[5].IsEnabled = true; // A foreign write after our false value is preserved.
            enabled = null; overrides.Apply(buttons, null); Assert.IsTrue(buttons[5].IsEnabled);
            enabled = true; buttons[5].IsEnabled = false; overrides.Apply(buttons, null);
            overrides.RestoreApplied(null); buttons[5].IsEnabled = false; // Vanilla Init rebuilt this mode's state after restore.
            enabled = null; overrides.Apply(buttons, null); Assert.IsFalse(buttons[5].IsEnabled);
        }
        [TestMethod]
        public void ReopeningEditorRestoresLoadEvenWhenVanillaLeavesItsEnabledValueUnchanged()
        {
            bool? enabled = false;
            var overrides = new VanillaMenuOverrides(); var buttons = Buttons(); buttons[1].IsEnabled = true;
            Add(overrides, IngameMenuRow.Load, () => enabled, () => { });
            overrides.Apply(buttons, null); Assert.IsFalse(buttons[1].IsEnabled);
            // HUD.SetAsMapEditor never writes RefButtonLoad.IsEnabled. Restore must
            // happen before that Init rather than discarding its previous true value.
            overrides.RestoreApplied(null);
            enabled = null; overrides.Apply(buttons, null); Assert.IsTrue(buttons[1].IsEnabled);
            enabled = false; overrides.Apply(buttons, null);
            overrides.RestoreApplied(null); overrides.Apply(buttons, null); Assert.IsFalse(buttons[1].IsEnabled);
            overrides.RestoreApplied(null); enabled = null; overrides.Apply(buttons, null);
            Assert.IsTrue(buttons[1].IsEnabled, "Repeated editor opening replaced the baseline with the override.");
        }
        [TestMethod]
        public void RestoreFailureIsIsolatedPerOriginalButtonAndReleasesRetainedViewState()
        {
            var overrides = new VanillaMenuOverrides(); var buttons = Buttons(); int failures = 0;
            Add(overrides, IngameMenuRow.Load, () => true, () => { });
            Add(overrides, IngameMenuRow.RestartMission, () => true, () => { }, id: "restart");
            overrides.Apply(buttons, null);
            buttons[1].ReadLocalFailure = new InvalidOperationException("disposed Noesis boundary");
            overrides.RestoreApplied((_, __) => { failures++; throw new InvalidOperationException("diagnostic sink"); });
            Assert.AreEqual(1, failures); Assert.IsFalse(buttons[5].IsEnabled, "One failed control prevented restoring another row.");
            foreach (var entry in new[] { overrides.Get(2), overrides.Get(5) })
            { Assert.IsNull(entry.AppliedButton); Assert.IsNull(entry.PreviousEnabled); Assert.IsNull(entry.PreviousBinding); }
            overrides.RestoreApplied((_, __) => failures++); Assert.AreEqual(1, failures);
        }
        [TestMethod]
        public void NullableOverlayNeverChangesButtonIdentityCommandLabelVisibilityOrPlacementRegistry()
        {
            var overrides = new VanillaMenuOverrides(); var buttons = Buttons(); var before = buttons.ToArray();
            var command = new SHCDESE.NoesisUtil.RelayCommand(() => { });
            buttons[5].Command = command; buttons[5].Visibility = Visibility.Collapsed;
            CrusaderDE.PropEx.SetTextCentre(buttons[5], "Vanilla localized text");
            var placement = new IngameMenuRegistry();
            Add(overrides, IngameMenuRow.RestartMission, () => true, () => { });
            overrides.Apply(buttons, null);
            for (int i = 0; i < buttons.Length; i++) Assert.AreSame(before[i], buttons[i]);
            Assert.AreSame(command, buttons[5].Command); Assert.AreEqual(Visibility.Collapsed, buttons[5].Visibility);
            Assert.AreEqual("Vanilla localized text", CrusaderDE.PropEx.GetTextCentre(buttons[5]));
            Assert.AreEqual(0, placement.Entries.Length); Assert.AreEqual(0, placement.Resolve().Overflow.Count);
        }
        [TestMethod]
        public void DisabledParentCannotHideOurLocalValueFromRestoreAndBindingsAreRetained()
        {
            bool? enabled = true;
            var overrides = new VanillaMenuOverrides(); var buttons = Buttons();
            var parent = new Grid { IsEnabled = false }; parent.Children.Add(buttons[5]);
            Add(overrides, IngameMenuRow.RestartMission, () => enabled, () => { });
            overrides.Apply(buttons, null); Assert.IsFalse(buttons[5].IsEnabled);
            Assert.AreEqual(true, buttons[5].ReadLocalValue(UIElement.IsEnabledProperty));
            enabled = null; overrides.Apply(buttons, null); parent.IsEnabled = true;
            Assert.IsFalse(buttons[5].IsEnabled, "Returning to Vanilla while parent-disabled left a local true override behind.");
            var binding = new BindingBase { Value = false };
            BindingOperations.SetBinding(buttons[5], UIElement.IsEnabledProperty, binding);
            enabled = true; overrides.Apply(buttons, null); Assert.IsTrue(buttons[5].IsEnabled);
            enabled = null; overrides.Apply(buttons, null);
            Assert.AreSame(binding, BindingOperations.GetBindingBase(buttons[5], UIElement.IsEnabledProperty));
            Assert.IsFalse(buttons[5].IsEnabled);
            buttons[5].ClearValue(UIElement.IsEnabledProperty);
            enabled = false; overrides.Apply(buttons, null); enabled = null; overrides.Apply(buttons, null);
            Assert.AreSame(DependencyProperty.UnsetValue, buttons[5].ReadLocalValue(UIElement.IsEnabledProperty));
            var foreign = new BindingBase { Value = false };
            enabled = true; overrides.Apply(buttons, null);
            BindingOperations.SetBinding(buttons[5], UIElement.IsEnabledProperty, foreign);
            enabled = null; overrides.Apply(buttons, null);
            Assert.AreSame(foreign, BindingOperations.GetBindingBase(buttons[5], UIElement.IsEnabledProperty));
        }
        [TestMethod]
        public void ClickReevaluatesProviderAndNeverFallsThroughOnDisableFailureOrStaleView()
        {
            var overrides = new VanillaMenuOverrides(); bool? enabled = true; int calls = 0, failures = 0;
            bool fail = false;
            Add(overrides, IngameMenuRow.Save, () => fail ? throw new InvalidOperationException("provider") : enabled, () => calls++);
            var entry = overrides.Get(1); overrides.Apply(Buttons(), (_, __) => failures++);
            enabled = false; Assert.IsTrue(overrides.TryExecute(entry, (_, __) => failures++)); Assert.AreEqual(0, calls);
            enabled = true; Assert.IsTrue(overrides.TryExecute(entry, null, canExecute: () => false)); Assert.AreEqual(0, calls);
            Assert.IsTrue(overrides.TryExecute(entry, null)); Assert.AreEqual(1, calls);
            enabled = null; Assert.IsFalse(overrides.TryExecute(entry, null, canExecute: () => false));
            fail = true;
            for (int i = 0; i < 3; i++) Assert.IsTrue(overrides.TryExecute(entry, (_, __) => failures++));
            Assert.AreEqual(1, failures); Assert.AreEqual(1, calls);
            fail = false; enabled = true; Assert.IsTrue(overrides.TryExecute(entry, null)); Assert.AreEqual(2, calls);
        }
        [TestMethod]
        public void ScopeIsRecheckedAfterProviderRatherThanBeforeItChangesTheView()
        {
            var overrides = new VanillaMenuOverrides(); bool currentView = true; int commands = 0;
            Add(overrides, IngameMenuRow.Load, () => { currentView = false; return true; }, () => commands++);
            Assert.IsTrue(overrides.TryExecute(overrides.Get(2), null, () => currentView));
            Assert.AreEqual(0, commands);
        }
        [TestMethod]
        public void DisabledAncestorConsumesAnActiveCommandEvenWithALocalTrueOverride()
        {
            var overrides = new VanillaMenuOverrides(); int commands = 0; var buttons = Buttons();
            var parent = new Grid { IsEnabled = false }; parent.Children.Add(buttons[5]);
            Add(overrides, IngameMenuRow.RestartMission, () => true, () => commands++);
            overrides.Apply(buttons, null);
            Assert.AreEqual(true, buttons[5].ReadLocalValue(UIElement.IsEnabledProperty));
            Assert.IsTrue(overrides.TryExecute(overrides.Get(5), null, () => buttons[5].IsVisible && buttons[5].IsEnabled));
            Assert.AreEqual(0, commands);
            parent.IsEnabled = true;
            Assert.IsTrue(overrides.TryExecute(overrides.Get(5), null, () => buttons[5].IsVisible && buttons[5].IsEnabled));
            Assert.AreEqual(1, commands);
        }
        [TestMethod]
        public void ProviderAndCommandRecursionAreConsumedAndOtherOverridesStillWorkAfterFailure()
        {
            var overrides = new VanillaMenuOverrides(); int calls = 0, failures = 0;
            Add(overrides, IngameMenuRow.Help, () => true, () => { calls++; Assert.IsTrue(overrides.TryExecute(overrides.Get(4), null)); throw new Exception("command"); });
            Assert.IsTrue(overrides.TryExecute(overrides.Get(4), (_, __) => failures++));
            Assert.AreEqual(1, calls); Assert.AreEqual(1, failures);
            Add(overrides, IngameMenuRow.Options, () => {
                Assert.IsTrue(overrides.TryExecute(overrides.Get(3), null));
                Assert.IsFalse(overrides.CanRegister("other", "nested", IngameMenuRow.Save, () => true, () => { }, out _));
                return true;
            }, () => calls++, id: "options");
            Assert.IsTrue(overrides.TryExecute(overrides.Get(3), null)); Assert.AreEqual(2, calls);
        }
        [TestMethod]
        public void InvalidAndConflictingRegistrationsRejectWithoutTakingTheOriginalOwnersCommand()
        {
            var overrides = new VanillaMenuOverrides(); int first = 0;
            Add(overrides, IngameMenuRow.RestartMission, () => true, () => first++);
            Assert.IsFalse(overrides.CanRegister("other", "restart", IngameMenuRow.RestartMission, () => true, () => { }, out string conflict));
            StringAssert.Contains(conflict, "mod/action");
            Assert.IsFalse(overrides.CanRegister("mod", "action", IngameMenuRow.Save, () => true, () => { }, out _));
            Assert.IsFalse(overrides.CanRegister("", "id", IngameMenuRow.Save, () => true, () => { }, out _));
            Assert.IsFalse(overrides.CanRegister(IngameMenuRegistry.VanillaOwner, "id", IngameMenuRow.Save, () => true, () => { }, out _));
            Assert.IsFalse(overrides.CanRegister("other", "id", (IngameMenuRow)99, () => true, () => { }, out _));
            Assert.IsFalse(overrides.CanRegister("other", "id", IngameMenuRow.Save, null, () => { }, out _));
            Assert.IsFalse(overrides.CanRegister("other", "id", IngameMenuRow.Save, () => true, null, out _));
            Assert.IsTrue(overrides.TryExecute(overrides.Get(5), null)); Assert.AreEqual(1, first);
        }
    }
}

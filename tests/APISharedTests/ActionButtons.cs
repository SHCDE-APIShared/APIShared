using MsAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Windows.Input;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        [TestCategory("Presentation")]
        public void ActionButtonSnapshotsReuseUnchangedStateAndFreezeReentrantMutations()
        {
            Type type = typeof(UnitHudPresentationService);
            var service = (UnitHudPresentationService)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke(new object[] { "test", null, null, false });
            var binding = service.Bind("observer");
            var buttons = (IUnitHudActionButtonsCapability)binding;
            MethodInfo capture = type.GetMethod("CaptureActionButtonSnapshot", BindingFlags.Instance | BindingFlags.NonPublic);
            object Snapshot() => capture.Invoke(service, null);
            object Field(object instance, string name) => instance.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
            Array States(object snapshot) => (Array)Field(snapshot, "States");
            Array Visible(object snapshot) => (Array)Field(snapshot, "Visible");

            object empty = Snapshot();
            MsAssert.AreEqual(0, States(empty).Length);
            MsAssert.AreSame(empty, Snapshot());
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("button", new TestActionCommand(), "Original", _ => null),
                out var handle, out _);
            object initial = Snapshot();
            MsAssert.AreNotSame(empty, initial);
            MsAssert.AreSame(States(initial), Visible(initial));
            for (int index = 0; index < 10000; index++) MsAssert.AreSame(initial, Snapshot());
            handle.SetVisible(true); handle.SetEnabled(true); handle.SetTooltip("Original");
            MsAssert.AreSame(initial, Snapshot(), "Equal setters still request presentation but need no new registry view.");

            handle.SetVisible(false); handle.SetEnabled(false); handle.SetTooltip("Changed"); handle.RequestContentRefresh();
            object changed = Snapshot();
            MsAssert.AreNotSame(initial, changed);
            MsAssert.AreEqual(0, Visible(changed).Length);
            object oldState = States(initial).GetValue(0), newState = States(changed).GetValue(0);
            MsAssert.IsTrue((bool)Field(oldState, "Visible"));
            MsAssert.IsTrue((bool)Field(oldState, "Enabled"));
            MsAssert.AreEqual("Original", Field(oldState, "Tooltip"));
            MsAssert.AreEqual(0, Field(oldState, "Version"));
            MsAssert.IsFalse((bool)Field(newState, "Visible"));
            MsAssert.IsFalse((bool)Field(newState, "Enabled"));
            MsAssert.AreEqual("Changed", Field(newState, "Tooltip"));
            MsAssert.AreEqual(1, Field(newState, "Version"));

            handle.SetVisible(true);
            object shown = Snapshot();
            ((IUnitHudActivationCapability)binding).SetOwnerActive(false);
            object inactive = Snapshot();
            MsAssert.AreEqual(0, Visible(inactive).Length);
            MsAssert.AreEqual(1, Visible(shown).Length, "Deactivation cannot mutate an earlier invocation view.");
            ((IUnitHudActivationCapability)binding).SetOwnerActive(false);
            MsAssert.AreSame(inactive, Snapshot());
            ((IUnitHudActivationCapability)binding).SetOwnerActive(true);
            MsAssert.AreEqual(1, Visible(Snapshot()).Length);

            object beforeRegistration = Snapshot();
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("late", new TestActionCommand(), "Late", _ => null), out _, out _);
            MsAssert.AreEqual(1, States(beforeRegistration).Length);
            MsAssert.AreEqual(2, States(Snapshot()).Length);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ActionButtonCallbackChangesOnlyTheNextCapturedRegistryView()
        {
            Type type = typeof(UnitHudPresentationService);
            var service = (UnitHudPresentationService)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke(new object[] { "test", null, null, false });
            var buttons = (IUnitHudActionButtonsCapability)service.Bind("test");
            IUnitHudActionButtonRegistration later = null;
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("first", new TestActionCommand(), "First", _ => null,
                contextChanged: available => { if (available) { later.SetVisible(false); later.SetTooltip("Next frame"); } }), out _, out _);
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("later", new TestActionCommand(), "Original", _ => null), out later, out _);
            MethodInfo capture = type.GetMethod("CaptureActionButtons", BindingFlags.Instance | BindingFlags.NonPublic);
            MethodInfo notify = type.GetMethod("NotifyActionContext", BindingFlags.Instance | BindingFlags.NonPublic);
            object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
            var original = (Array)capture.Invoke(service, null);
            notify.Invoke(service, new[] { Field(original.GetValue(0), "Registration"), (object)true });
            var next = (Array)capture.Invoke(service, null);
            MsAssert.IsTrue((bool)Field(original.GetValue(1), "Visible"));
            MsAssert.AreEqual("Original", Field(original.GetValue(1), "Tooltip"));
            MsAssert.IsFalse((bool)Field(next.GetValue(1), "Visible"));
            MsAssert.AreEqual("Next frame", Field(next.GetValue(1), "Tooltip"));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ActionButtonRegistrationsAreOwnerBoundAndDeterministic()
        {
            Type type = typeof(UnitHudPresentationService);
            var service = (UnitHudPresentationService)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke(new object[] { "test", null, null, false });
            var z = service.Bind("z"); var a = service.Bind("a");
            var zb = (IUnitHudActionButtonsCapability)z; var ab = (IUnitHudActionButtonsCapability)a;
            UnitHudActionButtonDefinition Definition(string id, int order = 0) =>
                new UnitHudActionButtonDefinition(id, new TestActionCommand(), "Button", _ => null, order);
            MsAssert.IsTrue(zb.TryRegisterActionButton(Definition("second"), out var zh, out _));
            MsAssert.IsTrue(ab.TryRegisterActionButton(Definition("first"), out var ah, out _));
            MsAssert.IsTrue(ab.TryRegisterActionButton(Definition("early", -1), out _, out _));
            MsAssert.IsFalse(ab.TryRegisterActionButton(Definition("first"), out var duplicate, out _));
            MsAssert.IsNull(duplicate);
            MsAssert.IsFalse(ab.TryRegisterActionButton(null, out _, out _));
            MsAssert.IsFalse(ab.TryRegisterActionButton(new UnitHudActionButtonDefinition("invalid", new TestActionCommand(), "", _ => null), out _, out _));
            object[] States() => ((IEnumerable)type.GetMethod("CaptureActionButtons", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(service, null)).Cast<object>().ToArray();
            object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
            string Key(object state) => (string)Field(Field(state, "Registration"), "Key");
            CollectionAssert.AreEqual(new[] { "a:early", "a:first", "z:second" }, States().Select(Key).ToArray());
            ah.SetVisible(false); zh.SetEnabled(false); ah.SetTooltip("Aufstellung"); ah.RequestContentRefresh();
            var first = States().Single(s => Key(s) == "a:first");
            MsAssert.IsFalse((bool)Field(first, "Visible"));
            MsAssert.AreEqual("Aufstellung", Field(first, "Tooltip"));
            MsAssert.AreEqual(1, Field(first, "Version"));
            MsAssert.IsFalse((bool)Field(States().Single(s => Key(s) == "z:second"), "Enabled"));
            ((IUnitHudActivationCapability)a).SetOwnerActive(false);
            MsAssert.IsTrue(States().Where(s => Key(s).StartsWith("a:")).All(s => !(bool)Field(s, "Visible")));
            MsAssert.IsTrue((bool)Field(States().Single(s => Key(s) == "z:second"), "Visible"));
            ((IUnitHudActivationCapability)a).SetOwnerActive(true); ah.SetVisible(true);
            MsAssert.IsTrue((bool)Field(States().Single(s => Key(s) == "a:first"), "Visible"));
            MsAssert.IsTrue((bool)Field(service, "hasActionButtons"), "Buttons alone keep the existing presentation publisher active.");
            MsAssert.ThrowsExactly<ArgumentException>(() => ah.SetTooltip(" "));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ActionButtonContextCallbacksCloseAndRecoverIndependently()
        {
            Type type = typeof(UnitHudPresentationService);
            var service = (UnitHudPresentationService)type.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single().Invoke(new object[] { "test", null, null, false });
            var buttons = (IUnitHudActionButtonsCapability)service.Bind("test");
            var transitions = new System.Collections.Generic.List<bool>();
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("healthy", new TestActionCommand(), "Healthy", _ => null,
                contextChanged: transitions.Add), out var healthy, out _);
            buttons.TryRegisterActionButton(new UnitHudActionButtonDefinition("broken", new TestActionCommand(), "Broken", _ => null,
                contextChanged: _ => throw new InvalidOperationException("consumer failure")), out var broken, out _);
            MethodInfo notify = type.GetMethod("NotifyActionContext", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (bool available in new[] { true, false, false, true })
            {
                notify.Invoke(service, new object[] { broken, available });
                notify.Invoke(service, new object[] { healthy, available });
            }
            CollectionAssert.AreEqual(new[] { true, false, true }, transitions);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void ActionButtonFactoryFailureIsIsolatedAndRetryable()
        {
            var service = (UnitHudPresentationService)typeof(UnitHudPresentationService)
                .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
                .Invoke(new object[] { "test", null, null, false });
            int attempts = 0;
            MsAssert.IsFalse(service.TryCreateActionContent(_ => { attempts++; throw new InvalidOperationException("bad factory"); },
                null, "bad", out var content));
            MsAssert.IsNull(content);
            MsAssert.IsFalse(service.TryCreateActionContent(_ => { attempts++; return null; }, null, "other", out content));
            MsAssert.IsNull(content);
            MsAssert.IsFalse(service.TryCreateActionContent(_ => { attempts++; return null; }, null, "bad", out _));
            MsAssert.AreEqual(3, attempts, "A failed consumer never prevents invoking another factory or an explicit retry.");
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void VanillaHudClickShieldsLeaveRoomForFormationButButtonsReserveSpace()
        {
            // Relevant direct children from the audited Vanilla HUD_Troops LayoutRoot (FBCB9319).
            var layout = System.Xml.Linq.XElement.Parse(@"<Grid>
                <Button Name='ToggleControlGroups' Width='35' Height='35' Margin='519,11,0,0'/>
                <StackPanel Name='StanceTabs' Width='150' Height='64' Margin='0,5,0,0'/>
                <Rectangle Width='560' Height='25' Fill='#00F4F4F5'/>
                <Rectangle Width='144' Height='15' Margin='560,0,0,0' Fill='#00F4F4F5'/>
            </Grid>");
            Noesis.Rect Bounds(System.Xml.Linq.XElement element)
            {
                string[] margin = ((string)element.Attribute("Margin") ?? "0,0,0,0").Split(',');
                float Number(string value) => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                return new Noesis.Rect(Number(margin[0]), Number(margin[1]),
                    Number((string)element.Attribute("Width")), Number((string)element.Attribute("Height")));
            }
            var anchor = Bounds(layout.Elements().First());
            float left = 0;
            foreach (var element in layout.Elements().Skip(1))
            {
                Type type = element.Name.LocalName == "Rectangle" ? typeof(Noesis.Rectangle) : typeof(Noesis.StackPanel);
                left = UnitHudPresentationService.ReserveActionObstacle(type, anchor, Bounds(element), left);
            }
            MsAssert.AreEqual(154f, left, "Only the stance controls reserve space in Vanilla's upper row.");
            MsAssert.AreEqual(9, UnitHudActionButtonLayout.Capacity(anchor.X, left));
            MsAssert.AreEqual(480f, anchor.X - UnitHudActionButtonLayout.Pitch, "The first button sits immediately left of Control Groups.");
            left = UnitHudPresentationService.ReserveActionObstacle(typeof(Noesis.Button), anchor,
                new Noesis.Rect(470, 11, 35, 35), left);
            MsAssert.AreEqual(509f, left, "An actual third-party button must still reserve its occupied space.");
            MsAssert.AreEqual(0, UnitHudActionButtonLayout.Capacity(anchor.X, left));
        }
        [TestMethod]
        public void ActionColourPreservesRedAndAppliesLoadedMultiplayerRemapping()
        {
            int[] original = SpriteMapping.mpLoadRemapping;
            try
            {
                SpriteMapping.mpLoadRemapping = null;
                int[] colours = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
                MsAssert.AreEqual(1, UnitHudPresentationService.ResolveActionColour(SpriteMapping.RemapMPLoadedColour(1), colours));
                SpriteMapping.mpLoadRemapping = new[] { 0, 4, 2, 3, 1, 5, 6, 7, 8 };
                MsAssert.AreEqual(4, UnitHudPresentationService.ResolveActionColour(SpriteMapping.RemapMPLoadedColour(1), colours));
                colours[4] = 1;
                MsAssert.AreEqual(1, UnitHudPresentationService.ResolveActionColour(SpriteMapping.RemapMPLoadedColour(1), colours));
                MsAssert.AreEqual(-1, UnitHudPresentationService.ResolveActionColour(9, colours));
                MsAssert.AreEqual(-1, UnitHudPresentationService.ResolveActionColour(0, colours));
                MsAssert.AreEqual(-1, UnitHudPresentationService.ResolveActionColour(1, null));
            }
            finally { SpriteMapping.mpLoadRemapping = original; }
        }
        [TestMethod]
        public void RuntimeHudButtonsExplicitlyDisableNoesisPopups()
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(UnitHudPresentationService).Assembly.Location))
            {
                var type = assembly.MainModule.Types.Single(t => t.Name == nameof(UnitHudPresentationService));
                foreach (string name in new[] { "GetActionVisual", "EnsureActionHost", "EnsureCategoryButtons", "EnsureRecruitmentControls" })
                {
                    var method = type.Methods.Single(m => m.Name == name);
                    MsAssert.IsTrue(method.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference reference &&
                        reference.DeclaringType.FullName == "Noesis.ToolTipService" && reference.Name == "SetIsEnabled"), name);
                    MsAssert.IsFalse(method.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference reference &&
                        reference.DeclaringType.FullName == "Noesis.ToolTipService" && reference.Name == "SetToolTip"), name);
                }
            }
        }
        private sealed class TestActionCommand : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) { }
        }
    }
}

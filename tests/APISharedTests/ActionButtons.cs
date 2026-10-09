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

        private sealed class TestActionCommand : ICommand
        {
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => true;
            public void Execute(object parameter) { }
        }
    }
}

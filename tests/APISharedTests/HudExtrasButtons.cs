using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using MsAssert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;

namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod]
        [TestCategory("Presentation")]
        public void SideHudRegistrySortsAndIsolatesOwnersWithoutNativeHooks()
        {
            var service = new HudExtrasButtonsService(null);
            var a = service.Bind("a"); var z = service.Bind("z");
            HudExtrasButtonDefinition Definition(string id, int order = 0) =>
                new HudExtrasButtonDefinition(id, new TestActionCommand(), "", _ => null, order);
            MsAssert.IsTrue(z.TryRegisterButton(Definition("second"), out var zh, out _));
            MsAssert.IsTrue(a.TryRegisterButton(Definition("first"), out var ah, out _));
            MsAssert.IsTrue(a.TryRegisterButton(Definition("early", -1), out _, out _));
            MsAssert.IsFalse(a.TryRegisterButton(Definition("first"), out var duplicate, out _));
            MsAssert.IsNull(duplicate);
            MsAssert.IsFalse(a.TryRegisterButton(null, out _, out _));
            MsAssert.IsFalse(a.TryRegisterButton(new HudExtrasButtonDefinition("bad", null, "", _ => null), out _, out _));
            CollectionAssert.AreEqual(new[] { "a:early", "a:first", "z:second" }, service.CaptureStates().Select(x => x.Item.Key).ToArray());
            ah.SetVisible(false); zh.SetEnabled(false); ah.SetTooltip("Blueprints"); ah.RequestContentRefresh();
            var first = service.CaptureStates().Single(x => x.Item.Key == "a:first");
            MsAssert.IsFalse(first.Visible); MsAssert.AreEqual("Blueprints", first.Tooltip); MsAssert.AreEqual(1, first.Version);
            a.SetOwnerActive(false);
            MsAssert.IsTrue(service.CaptureStates().Where(x => x.Item.Owner == "a").All(x => !x.Visible));
            MsAssert.IsTrue(service.CaptureStates().Single(x => x.Item.Owner == "z").Visible);
            MsAssert.IsFalse(service.CaptureStates().Single(x => x.Item.Owner == "z").Enabled);
            a.SetOwnerActive(true); ah.SetVisible(true); ah.SetTooltip(null);
            MsAssert.IsTrue(service.CaptureStates().Single(x => x.Item.Key == "a:first").Visible);
            MsAssert.AreEqual("", service.CaptureStates().Single(x => x.Item.Key == "a:first").Tooltip);
        }
        [TestMethod]
        [TestCategory("Presentation")]
        public void SideHudFactoryFailuresAreIsolatedAndRetryable()
        {
            var service = new HudExtrasButtonsService(null);
            int attempts = 0;
            MsAssert.IsFalse(service.TryCreateButton(_ => { attempts++; throw new InvalidOperationException("failure"); }, null, "one", out var button));
            MsAssert.IsNull(button);
            MsAssert.IsFalse(service.TryCreateButton(_ => { attempts++; return null; }, null, "two", out button));
            MsAssert.IsNull(button); MsAssert.AreEqual(2, attempts);
            service.Bind("owner").TryRegisterButton(new HudExtrasButtonDefinition("one", new TestActionCommand(), "", _ => null), out var handle, out _);
            handle.RequestContentRefresh();
            MsAssert.AreEqual(1, service.CaptureStates().Single().Version);
        }
        [TestMethod]
        [TestCategory("Presentation")]
        public void SideHudAcquisitionDoesNotDependOnNativeAvailability()
        {
            var runtime = new ApiSharedRuntime();
            MsAssert.IsFalse(runtime.TryGetHudExtrasButtons(" ", out _, out var invalid));
            MsAssert.AreEqual(NativeCapabilityState.ValidationFailed, invalid.State);
            MsAssert.IsFalse(runtime.TryGetHudExtrasButtons("owner", out _, out var pending));
            MsAssert.AreEqual(NativeCapabilityState.Pending, pending.State);
            typeof(ApiSharedRuntime).GetField("hudExtrasButtons", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .SetValue(runtime, new HudExtrasButtonsService(null));
            MsAssert.IsTrue(runtime.TryGetHudExtrasButtons("owner", out _, out var ready));
            MsAssert.AreEqual(NativeCapabilityState.Available, ready.State);
        }
    }
}

using APIShared.ScriptExtenderFixes;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using R3;
using SHCDESE.EventAPI.Units;
using SHCDESE.ManagedHooks;
using System;
using System.Collections.Generic;
using System.Reflection;

// Regression class: reusing a Script Extender event object must never reach a subscriber that can keep
// it. The analysis and the observer gate decide that; these tests run them against the installed
// Extender/R3 assemblies and against handlers with known retention behavior.
[TestClass]
public sealed class InterpolateEventReuseTests
{
    public class FakeArgs
    {
        public string Value { get; set; }
        public int Number => 1;
    }

    private static readonly Func<MethodBase, bool> FakeGetters = method =>
        method.DeclaringType == typeof(FakeArgs) && (method.Name == "get_Value" || method.Name == "get_Number");

    private static object sink;
    private static FakeArgs stored;
    private static readonly List<Func<string>> deferred = new List<Func<string>>();

    private static void ReadsGetter(FakeArgs args) => sink = args.Value;
    private static void NullConditional(FakeArgs args) { if (args?.Value != null) sink = args.Value + args.Number; }
    private static void IgnoresArgs(FakeArgs args) => sink = "unused";
    private static void StoresArgs(FakeArgs args) => stored = args;
    private static void PassesArgs(FakeArgs args) => Consume(args);
    private static void SetsValue(FakeArgs args) => args.Value = "changed";
    private static void BoxesArgs(FakeArgs args) { object copy = args; sink = copy; }
    private static void CapturesArgs(FakeArgs args) => deferred.Add(() => args.Value);
    private static void TakesAddress(FakeArgs args) => Touch(ref args);
    private static void CallsObjectMethod(FakeArgs args) => sink = args.ToString();
    private static void Consume(FakeArgs args) => sink = args;
    private static void Touch(ref FakeArgs args) => sink = args;

    private static bool Transient(string name) => TransientHandlerAnalysis.IsTransient(
        typeof(InterpolateEventReuseTests).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static), typeof(FakeArgs), FakeGetters);

    [TestMethod]
    public void SynchronousReadersQualify()
    {
        Assert.IsTrue(Transient(nameof(ReadsGetter)));
        Assert.IsTrue(Transient(nameof(NullConditional)));
        Assert.IsTrue(Transient(nameof(IgnoresArgs)));
        string captured = "outer";
        Action<FakeArgs> closure = args => sink = captured + args.Value;
        Assert.IsFalse(closure.Method.IsStatic, "Lambda handlers are instance methods; the args are argument 1");
        Assert.IsTrue(TransientHandlerAnalysis.IsTransient(closure.Method, typeof(FakeArgs), FakeGetters));
    }

    [TestMethod]
    public void HandlersThatCanRetainOrForwardTheArgsAreRefused()
    {
        foreach (string name in new[] { nameof(StoresArgs), nameof(PassesArgs), nameof(SetsValue), nameof(BoxesArgs),
            nameof(CapturesArgs), nameof(TakesAddress), nameof(CallsObjectMethod) })
            Assert.IsFalse(Transient(name), name);
        Assert.IsFalse(TransientHandlerAnalysis.IsTransient(typeof(InterpolateEventReuseTests).GetMethod(nameof(ReadsGetter),
            BindingFlags.NonPublic | BindingFlags.Static), typeof(FakeArgs), _ => false), "Only listed getters are allowed");
    }

    [TestMethod]
    public void InstalledExtenderHookAndAfterImageSubscriberMatchTheAudit()
    {
        MethodInfo hook = typeof(ManagedHookManager).GetMethod("GameMap_InterpChimpHook",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            new[] { typeof(GameMap), typeof(Chimp), typeof(bool) }, null);
        Assert.IsNotNull(hook);
        InterpolateEventReuseRuntime.ValidateHookBody(PatchProcessor.GetOriginalInstructions(hook));
        InterpolateEventReuseRuntime.ValidateGetters();
        Assert.IsInstanceOfType(InterpolateEventReuseRuntime.Subject(), typeof(Subject<UnitUnityVisualInterpolateEventArgs>));
        MethodInfo afterImages = typeof(ManagedHookManager).Assembly.GetType("SHCDESE.API.GameAfterImageManager", true)
            .GetMethod("OnUnitUnityVisualInterpolate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsTrue(TransientHandlerAnalysis.IsTransient(afterImages, typeof(UnitUnityVisualInterpolateEventArgs),
            InterpolateEventReuseRuntime.IsAllowedGetter), "The Extender's own after-image subscriber must keep reuse possible");
    }

    private static void TransientSubscriber(UnitUnityVisualInterpolateEventArgs args) => sink = args?.Chimp;
    private static UnitUnityVisualInterpolateEventArgs retained;
    private static void RetainingSubscriber(UnitUnityVisualInterpolateEventArgs args) => retained = args;

    [TestMethod]
    public void GateFollowsEverySubscriptionChange()
    {
        var subject = new Subject<UnitUnityVisualInterpolateEventArgs>();
        var gate = new TransientObserverGate(subject, typeof(UnitUnityVisualInterpolateEventArgs), InterpolateEventReuseRuntime.IsAllowedGetter);
        Assert.IsTrue(gate.Refresh(), "No subscriber");

        IDisposable first = subject.Subscribe(TransientSubscriber);
        Assert.IsTrue(gate.Changed(), "First subscription changes the root");
        Assert.IsTrue(gate.Refresh());
        Assert.IsFalse(gate.Changed());

        IDisposable second = subject.Subscribe(TransientSubscriber);
        Assert.IsTrue(gate.Changed(), "Appended subscription changes the tail");
        Assert.IsTrue(gate.Refresh());

        IDisposable filtered = subject.Where(args => args != null).Subscribe(TransientSubscriber);
        Assert.IsTrue(gate.Changed());
        Assert.IsFalse(gate.Refresh(), "R3 operators are unknown observers");
        StringAssert.Contains(gate.LastRefusal, "observer");
        filtered.Dispose();
        Assert.IsTrue(gate.Refresh(), "Removing the operator restores reuse");

        IDisposable retaining = subject.Subscribe(RetainingSubscriber);
        Assert.IsFalse(gate.Refresh(), "A retaining handler refuses");
        StringAssert.Contains(gate.LastRefusal, nameof(RetainingSubscriber));

        // Removing a middle observer may leave root and tail unchanged; the remaining set stays validated.
        second.Dispose();
        retaining.Dispose();
        Assert.IsTrue(gate.Refresh());
        first.Dispose();
        Assert.IsTrue(gate.Refresh(), "Empty again");
    }
}

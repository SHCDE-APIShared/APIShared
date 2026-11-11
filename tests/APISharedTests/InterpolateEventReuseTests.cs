using APIShared.ScriptExtenderFixes;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using R3;
using SHCDESE.EventAPI.Units;
using SHCDESE.ManagedHooks;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using SHCDESE.EventAPI;

// Retainable receivers must get fresh args even when they appear only between Rent and dispatch.
// These tests use the actual R3 delivery path and the production delivery guard.
[TestClass]
[DoNotParallelize]
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
        InterpolateEventReuseRuntime.ValidateArgumentConstruction();
        InterpolateEventReuseRuntime.ValidateRaiseBody();
        InterpolateEventReuseRuntime.ValidateDeliveryBody(PatchProcessor.GetOriginalInstructions(
            typeof(Subject<UnitUnityVisualInterpolateEventArgs>).GetMethod("OnNext")));
        Assert.IsInstanceOfType(InterpolateEventReuseRuntime.Subject(), typeof(Subject<UnitUnityVisualInterpolateEventArgs>));
        MethodInfo afterImages = typeof(ManagedHookManager).Assembly.GetType("SHCDESE.API.GameAfterImageManager", true)
            .GetMethod("OnUnitUnityVisualInterpolate", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsTrue(TransientHandlerAnalysis.IsTransient(afterImages, typeof(UnitUnityVisualInterpolateEventArgs),
            InterpolateEventReuseRuntime.IsAllowedGetter), "The Extender's own after-image subscriber must keep reuse possible");
    }

    private static void TransientSubscriber(UnitUnityVisualInterpolateEventArgs args) => sink = args?.Chimp;
    private static UnitUnityVisualInterpolateEventArgs retained;
    private static void RetainingSubscriber(UnitUnityVisualInterpolateEventArgs args) => retained = args;


    private static void ReadsInterpolate(UnitUnityVisualInterpolateEventArgs args) => sink = args.Chimp;

    // Uses the production dispatcher, real installed R3 and the production finally wrapper.
    // No game hook is installed. The private stand-in is process-lifetime; restore fixture state.
    private sealed class DeliveryFixture : IDisposable
    {
        private static readonly FieldInfo DeliveryField = typeof(InterpolateEventReuseRuntime).GetField("delivery", BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly FieldInfo RaiseField = typeof(InterpolateEventReuseRuntime).GetField("raise", BindingFlags.Static | BindingFlags.NonPublic);
        private readonly object oldDelivery, oldRaise;
        internal readonly R3EventHook<UnitUnityVisualInterpolateEventArgs> Hook = new R3EventHook<UnitUnityVisualInterpolateEventArgs>();
        internal readonly Subject<UnitUnityVisualInterpolateEventArgs> Subject;
        internal readonly InterpolateEventDelivery Delivery;

        internal DeliveryFixture()
        {
            InterpolateEventReuseRuntime.EnsureDeliveryGuard();
            Subject = (Subject<UnitUnityVisualInterpolateEventArgs>)typeof(R3EventHook<UnitUnityVisualInterpolateEventArgs>)
                .GetField("_subject", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(Hook);
            Delivery = new InterpolateEventDelivery(Subject, Thread.CurrentThread.ManagedThreadId);
            oldDelivery = DeliveryField.GetValue(null);
            oldRaise = RaiseField.GetValue(null);
            DeliveryField.SetValue(null, Delivery);
            SetRaise((hook, args) => InterpolateEventReuseRuntime.DispatchCopy(Subject, args));
        }

        internal void SetRaise(Action<R3EventHook<UnitUnityVisualInterpolateEventArgs>, UnitUnityVisualInterpolateEventArgs> action) => RaiseField.SetValue(null, action);
        internal UnitUnityVisualInterpolateEventArgs Rent() => Delivery.Rent(EventHookPhase.Pre, null);
        internal void Raise(UnitUnityVisualInterpolateEventArgs args) => InterpolateEventReuseRuntime.RaiseAndRelease(Hook, args);
        internal UnitUnityVisualInterpolateEventArgs Publish()
        {
            var args = Rent();
            Raise(args);
            return args;
        }

        public void Dispose()
        {
            DeliveryField.SetValue(null, oldDelivery);
            RaiseField.SetValue(null, oldRaise);
            Subject.Dispose();
        }
    }

    [TestMethod]
    public void TransientReaderKeepsTheAllocationOptimization()
    {
        using (var fixture = new DeliveryFixture())
        using (fixture.Subject.Subscribe(ReadsInterpolate))
        {
            var first = fixture.Publish();
            var second = fixture.Publish();
            Assert.AreSame(first, second, "A proven reader must not force per-event allocation");
        }
    }

    [TestMethod]
    public void TemporaryRetainerCannotObserveAReusedPayloadEvenWhenItDisposesDuringDelivery()
    {
        using (var fixture = new DeliveryFixture())
        using (fixture.Subject.Subscribe(ReadsInterpolate))
        {
            fixture.Publish();
            var retained = new List<UnitUnityVisualInterpolateEventArgs>();
            for (int iteration = 0; iteration < 3; iteration++)
            {
                // This is the old check/use race: the subscriber exists only after Rent,
                // receives the event and disappears before the finally/Release check.
                var rented = fixture.Rent();
                IDisposable temporary = null;
                temporary = fixture.Subject.Subscribe(args =>
                {
                    retained.Add(args);
                    args.SkipOriginalFunction = true;
                    temporary.Dispose();
                });
                fixture.Raise(rented);
                Assert.AreNotSame(rented, retained[iteration]);
                fixture.Publish();
            }
            Assert.AreNotSame(retained[0], retained[1]);
            Assert.AreNotSame(retained[1], retained[2]);
            foreach (var args in retained) Assert.IsTrue(args.SkipOriginalFunction, "Later events must not overwrite retained data");
        }
    }

    [TestMethod]
    public void PromotionKeepsOnePublicObjectAndPropertyChangesForTheWholeNotification()
    {
        using (var fixture = new DeliveryFixture())
        {
            UnitUnityVisualInterpolateEventArgs first = null, second = null;
            using (fixture.Subject.Subscribe(ReadsInterpolate))
            using (fixture.Subject.Subscribe(args => { first = args; args.SkipOriginalFunction = true; }))
            using (fixture.Subject.Subscribe(args => second = args))
            {
                var rented = fixture.Publish();
                Assert.AreNotSame(rented, first);
                Assert.AreSame(first, second);
                Assert.IsTrue(second.SkipOriginalFunction);
            }
            fixture.Publish();
            Assert.IsTrue(first.SkipOriginalFunction);
        }
    }

    [TestMethod]
    public void OperatorsAndCustomObserversReceiveRetainableArguments()
    {
        using (var fixture = new DeliveryFixture())
        {
            var retained = new List<UnitUnityVisualInterpolateEventArgs>();
            using (fixture.Subject.Where(args => args != null).Subscribe(args => retained.Add(args)))
            {
                var rented = fixture.Publish();
                fixture.Publish();
                Assert.AreNotSame(rented, retained[0]);
                Assert.AreNotSame(retained[0], retained[1]);
            }
            var custom = new KeepingObserver();
            using (fixture.Subject.Subscribe(custom))
            {
                var rented = fixture.Publish();
                Assert.AreNotSame(rented, custom.Last);
                var first = custom.Last;
                fixture.Publish();
                Assert.AreNotSame(first, custom.Last);
            }
        }
    }

    private sealed class KeepingObserver : Observer<UnitUnityVisualInterpolateEventArgs>
    {
        internal UnitUnityVisualInterpolateEventArgs Last;
        protected override void OnNextCore(UnitUnityVisualInterpolateEventArgs value) => Last = value;
        protected override void OnErrorResumeCore(Exception error) { }
        protected override void OnCompletedCore(Result result) { }
    }

    [TestMethod]
    public void NestedNotificationsRetainSeparateObjectsAndOuterChanges()
    {
        using (var fixture = new DeliveryFixture())
        {
            var retained = new List<UnitUnityVisualInterpolateEventArgs>();
            using (fixture.Subject.Subscribe(args =>
            {
                retained.Add(args);
                if (retained.Count == 1)
                {
                    args.SkipOriginalFunction = true;
                    fixture.Publish();
                }
            }))
            {
                fixture.Publish();
                fixture.Publish();
                Assert.AreEqual(3, retained.Count);
                Assert.AreNotSame(retained[0], retained[1]);
                Assert.AreNotSame(retained[1], retained[2]);
                Assert.IsTrue(retained[0].SkipOriginalFunction);
                Assert.IsFalse(retained[1].SkipOriginalFunction);
            }
        }
    }

    [TestMethod]
    public void ParallelNotificationsCannotBorrowTheMainThreadObject()
    {
        using (var fixture = new DeliveryFixture())
        {
            var retained = new ConcurrentBag<UnitUnityVisualInterpolateEventArgs>();
            int started = 0;
            bool completed = false;
            using (fixture.Subject.Subscribe(args =>
            {
                retained.Add(args);
                if (Interlocked.CompareExchange(ref started, 1, 0) == 0)
                    completed = Task.Run(() => { for (int i = 0; i < 20; i++) fixture.Publish(); }).Wait(TimeSpan.FromSeconds(5));
            }))
            {
                fixture.Publish();
                Assert.IsTrue(completed);
                var all = retained.ToArray();
                Assert.AreEqual(21, all.Length);
                for (int i = 0; i < all.Length; i++)
                    for (int j = i + 1; j < all.Length; j++) Assert.AreNotSame(all[i], all[j]);
            }
        }
    }

    [TestMethod]
    public void CallbackCanWaitForAnotherThreadToChangeSubscriptions()
    {
        using (var fixture = new DeliveryFixture())
        {
            bool completed = false;
            using (fixture.Subject.Subscribe(args =>
                completed = Task.Run(() => { using (fixture.Subject.Subscribe(ReadsInterpolate)) { } }).Wait(TimeSpan.FromSeconds(5))))
                fixture.Publish();
            Assert.IsTrue(completed, "No callback may run under a subscription lock");
        }
    }

    [TestMethod]
    public void ExceptionUnwindsTheLeaseWithoutAFrameRecoveryCallback()
    {
        using (var fixture = new DeliveryFixture())
        {
            var first = fixture.Rent();
            fixture.SetRaise((hook, args) => { throw new InvalidOperationException("fixture"); });
            bool threw = false;
            try { InterpolateEventReuseRuntime.RaiseAndRelease(new R3EventHook<UnitUnityVisualInterpolateEventArgs>(), first); } catch (InvalidOperationException) { threw = true; }
            Assert.IsTrue(threw);
            fixture.SetRaise((hook, args) => InterpolateEventReuseRuntime.DispatchCopy(fixture.Subject, args));
            Assert.AreSame(first, fixture.Publish(), "The finally wrapper must balance depth after an exception");
        }
    }

    [TestMethod]
    public void OtherSubjectsAndPayloadTypesKeepTheirOriginalDispatch()
    {
        MethodInfo other = typeof(Subject<string>).GetMethod("OnNext");
        object before = Harmony.GetPatchInfo(other);
        using (var fixture = new DeliveryFixture())
        using (var subject = new Subject<string>())
        {
            var calls = new List<string>();
            using (subject.Subscribe(calls.Add)) subject.OnNext("unchanged");
            CollectionAssert.AreEqual(new[] { "unchanged" }, calls);
            Assert.AreSame(before, Harmony.GetPatchInfo(other), "The optimization must not patch a shared generic method");
        }
    }
}

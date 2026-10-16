using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;

namespace APISharedTests
{
    // Issue #11: a weighted builder exception ends at AssassinPathAPI's native boundary. The tests call the
    // real Build callback; its original is a fixture Vanilla delegate attached through a RedBird handle
    // (InstallOnCommit = false, so no memory is patched). Process-wide fields are restored afterwards.
    [TestClass]
    [DoNotParallelize]
    public sealed class AssassinBuilderBoundaryTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private const int VanillaResult = 41;
        private static readonly string[] Fields = { "builderHook", "weightedBuilder", "log", "callbackLogged", "builderFailureLogged" };
        private readonly Dictionary<string, object> saved = new Dictionary<string, object>();
        private readonly List<string> errors = new List<string>();
        private int vanillaCalls;
        private readonly int[] vanillaArguments = new int[6];
        private IntPtr vanillaContext;
        private Func<int> vanillaReturn = () => VanillaResult;
        private AssassinPathBuilder build;
        // Native-like contexts: the generation int lives at +4, as in Vanilla 0xD9C40.
        private IntPtr contextA, contextB;

        [TestInitialize]
        public void Arrange()
        {
            contextA = Context(); contextB = Context();
            foreach (string name in Fields) saved[name] = Field(name).GetValue(null);
            var logger = new ManualLogSource("AssassinBuilderBoundaryFixture");
            logger.LogEvent += (_, entry) => { if (entry.Level == LogLevel.Error) errors.Add(entry.Data?.ToString()); };
            Field("log").SetValue(null, logger);
            Field("callbackLogged").SetValue(null, 1);
            Field("builderFailureLogged").SetValue(null, 0);
            Field("builderHook").SetValue(null, PublishedVanilla());
            build = (AssassinPathBuilder)Delegate.CreateDelegate(typeof(AssassinPathBuilder),
                typeof(AssassinPathAPI).GetMethod("Build", PrivateStatic));
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (string name in Fields) Field(name).SetValue(null, saved[name]);
            Marshal.FreeHGlobal(contextA); Marshal.FreeHGlobal(contextB);
        }

        private static IntPtr Context()
        {
            IntPtr context = Marshal.AllocHGlobal(0x100);
            for (int offset = 0; offset < 0x100; offset += 4) Marshal.WriteInt32(context, offset, 0);
            return context;
        }

        private static int Generation(IntPtr context) => Marshal.ReadInt32(context, 4);

        [TestMethod]
        public void AThrowingHandlerGetsTheVanillaResultAndOneLogLine()
        {
            Handler((context, sx, sy, tx, ty, nodes, continuation) => throw new InvalidOperationException("consumer bug"));
            Assert.AreEqual(VanillaResult, build(contextA, 1, 2, 3, 4, 100, 0), "The native caller did not get Vanilla's result.");
            Assert.AreEqual(1, vanillaCalls, "Vanilla did not answer a handler that threw before running it.");
            Assert.AreEqual(contextA, vanillaContext);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 100, 0 }, vanillaArguments, "Vanilla did not get the native arguments.");

            // A throw after the handler completed Vanilla and left its field alone: the first result answers.
            Handler((context, sx, sy, tx, ty, nodes, continuation) =>
            {
                AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation);
                throw new InvalidOperationException("late consumer bug");
            });
            Assert.AreEqual(VanillaResult, build(contextB, 7, 8, 9, 10, 200, 0));
            Assert.AreEqual(2, vanillaCalls, "Vanilla ran a second time although its field was unchanged.");
            Assert.AreEqual(1, Generation(contextB), "The field was rebuilt.");

            Assert.AreEqual(1, errors.Count, "Each failure must not be logged; only the first one is.");
            StringAssert.Contains(errors[0], "consumer bug");
        }

        [TestMethod]
        public void TheFirstVanillaResultIsReusedWithoutASecondRun()
        {
            int next = 40;
            vanillaReturn = () => ++next;
            Handler((context, sx, sy, tx, ty, nodes, continuation) =>
            {
                AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation);
                throw new InvalidOperationException("after Vanilla");
            });
            Assert.AreEqual(41, build(contextA, 1, 2, 3, 4, 100, 1), "A resumed search must not be advanced again.");
            Assert.AreEqual(1, vanillaCalls);
        }

        [TestMethod]
        public void AChangedFieldIsRebuiltOnceByAFreshSearch()
        {
            int next = 40;
            vanillaReturn = () => ++next;
            foreach (int continuation in new[] { 0, 1 })
            {
                vanillaCalls = 0;
                Handler((context, sx, sy, tx, ty, nodes, resume) =>
                {
                    AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, resume);
                    // A consumer starts its own field the way Vanilla does: generation first, then stamps.
                    Marshal.WriteInt32(context, 4, Generation(context) + 1);
                    throw new InvalidOperationException("partial replacement");
                });
                int result = build(contextA, 1, 2, 3, 4, 100, continuation);
                Assert.AreEqual(2, vanillaCalls, "The partially replaced field must be rebuilt exactly once.");
                Assert.AreEqual(0, vanillaArguments[5], "The rebuild must be a fresh search (continuation 0).");
                Assert.AreEqual(next, result, "The rebuilt search's result answers the native caller.");
            }
        }

        [TestMethod]
        public void NestedBuildsKeepTheirOwnVanillaRecord()
        {
            int handlerCalls = 0;
            Handler((context, sx, sy, tx, ty, nodes, continuation) =>
            {
                if (++handlerCalls == 2) return AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation);
                build(contextB, 5, 6, 7, 8, 100, 0);
                throw new InvalidOperationException("outer throws before its own Vanilla");
            });
            Assert.AreEqual(VanillaResult, build(contextA, 1, 2, 3, 4, 100, 1));
            Assert.AreEqual(2, vanillaCalls, "The outer call must not reuse the inner call's Vanilla run.");
            Assert.AreEqual(contextA, vanillaContext);
            Assert.AreEqual(1, vanillaArguments[5], "Vanilla had not run for the outer call, so its own arguments apply.");
        }

        [TestMethod]
        public void ARouteStagedBeforeTheThrowIsNotPublished()
        {
            int publications = 0;
            bool staged = false;
            var frame = new AssassinRouteHandoff(contextA, 1, 2, 3, 4, 1,
                () => true, (bytes, count) => { publications++; return 77; }, 1, 0);
            try
            {
                Handler((context, sx, sy, tx, ty, nodes, continuation) =>
                {
                    AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation);
                    staged = AssassinRouteHandoff.Stage(context, sx, sy, tx, ty, 1, new byte[] { 0x10 }, 2, () => true);
                    throw new InvalidOperationException("after staging");
                });
                Assert.AreEqual(VanillaResult, build(contextA, 1, 2, 3, 4, 100, 0));
                Assert.IsTrue(staged, "The fixture route was not staged.");
                Assert.AreEqual(VanillaResult, frame.Complete(VanillaResult), "A failed call's staged route was published.");
                Assert.AreEqual(0, publications);
            }
            finally { frame.Leave(); }
        }

        [TestMethod]
        public void HandlerResultsAndTheVanillaPathAreUnchanged()
        {
            Handler((context, sx, sy, tx, ty, nodes, continuation) => 99);
            Assert.AreEqual(99, build(contextA, 1, 2, 3, 4, 100, 0));
            Assert.AreEqual(0, vanillaCalls);
            Field("weightedBuilder").SetValue(null, null);
            Assert.AreEqual(VanillaResult, build(contextA, 1, 2, 3, 4, 100, 0));
            Assert.AreEqual(1, vanillaCalls);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void ABuildAllocatesNothingAfterTheFirstOnItsThread()
        {
            Handler((context, sx, sy, tx, ty, nodes, continuation) => AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation));
            vanillaReturn = () => VanillaResult;
            for (int i = 0; i < 1000; i++) build(contextA, 1, 2, 3, 4, 100, 1);
            // Stopwatch.GetTimestamp, not a Stopwatch instance: the measured window allocates nothing itself.
            // The fastest of seven rounds is reported, because one round is noisy on a loaded machine.
            long fastest = long.MaxValue, before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 7; round++)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < 100000; i++) build(contextA, 1, 2, 3, 4, 100, 1);
                fastest = Math.Min(fastest, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            TestContext?.WriteLine("ASSASSIN_BUILD_BENCH ns per Build with a Vanilla-running handler: " +
                (fastest * 1e9 / System.Diagnostics.Stopwatch.Frequency / 100000).ToString("F1"));
            Assert.AreEqual(0L, bytes, "The per-thread frame must not allocate per call.");
        }

        public TestContext TestContext { get; set; }

        [TestMethod]
        public void FailureWorkStaysOutOfTheCallbackSuccessPath()
        {
            // Build allocates no closure, the whole body sits in the boundary, each catch only hands the failure
            // to BuilderFailed and the finally only restores the frame.
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(AssassinPathAPI).Assembly.Location))
            {
                var method = assembly.MainModule.GetType("APIShared.AssassinPathAPI").Methods.Single(m => m.Name == "Build");
                Assert.IsFalse(method.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newobj &&
                    !((Mono.Cecil.MethodReference)i.Operand).DeclaringType.IsValueType), "Build allocates.");
                var handlers = method.Body.ExceptionHandlers;
                var outermost = handlers.OrderBy(h => h.TryStart.Offset).ThenByDescending(h => h.TryEnd?.Offset ?? int.MaxValue).First();
                Assert.IsTrue(method.Body.Instructions.TakeWhile(i => i != outermost.TryStart).All(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Nop),
                    "Part of Build runs outside the boundary.");
                Assert.AreEqual(2, handlers.Count(h => h.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Catch && h.CatchType.FullName == "System.Exception"));
                foreach (var handler in handlers)
                {
                    var calls = new List<string>();
                    for (var i = handler.HandlerStart; i != handler.HandlerEnd; i = i.Next)
                        if (i.Operand is Mono.Cecil.MethodReference called) calls.Add(called.Name);
                    string[] expected = handler.HandlerType == Mono.Cecil.Cil.ExceptionHandlerType.Finally ? new[] { "Leave" } : new[] { "BuilderFailed" };
                    CollectionAssert.AreEqual(expected, calls, "A " + handler.HandlerType + " block does more than its job.");
                }
            }
        }

        private void Handler(AssassinPathBuilder handler) => Field("weightedBuilder").SetValue(null, handler);

        private static FieldInfo Field(string name) => typeof(AssassinPathAPI).GetField(name, PrivateStatic);

        private DetourHandle<AssassinPathBuilder> PublishedVanilla()
        {
            AssassinPathBuilder vanilla = (context, sx, sy, tx, ty, nodes, continuation) =>
            {
                vanillaCalls++;
                vanillaContext = context;
                vanillaArguments[0] = sx; vanillaArguments[1] = sy; vanillaArguments[2] = tx;
                vanillaArguments[3] = ty; vanillaArguments[4] = nodes; vanillaArguments[5] = continuation;
                // Like 0xD9C40: a fresh search advances the generation; a resumed one keeps it.
                if (continuation == 0) Marshal.WriteInt32(context, 4, Generation(context) + 1);
                return vanillaReturn();
            };
            var handle = new DetourHandle<AssassinPathBuilder>();
            var transaction = new HookTransaction(ScanRegion.FromMemory(new byte[64], 0x10000, "Assassin builder fixture"), null,
                new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, InstallOnCommit = false,
                    Backend = new VanillaBackend(vanilla) });
            transaction.AddDetour(handle, HookTarget.FromAddress(0x10000), (AssassinPathBuilder)((c, sx, sy, tx, ty, n, k) => 0));
            Assert.IsTrue(transaction.Commit().IsCompleteSuccess && handle.TryGetOriginal(out _), "Fixture original was not attached.");
            return handle;
        }

        private sealed class VanillaBackend : IHookBackend
        {
            private readonly Delegate original;
            internal VanillaBackend(Delegate original) { this.original = original; }
            public string Name => "Assassin builder fixture";
            public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate =>
                new FixtureDetour<TFunction>(request.Name, request.TargetAddress, (TFunction)original);
        }

        private sealed class FixtureDetour<TFunction> : IDetour<TFunction> where TFunction : Delegate
        {
            internal FixtureDetour(string name, ulong target, TFunction original)
            { Name = name; TargetAddress = target; Original = original; }
            public string Name { get; }
            public ulong TargetAddress { get; }
            public HookState State { get; private set; } = HookState.Prepared;
            public bool IsInstalled => State == HookState.Installed;
            public IntPtr TrampolineAddress => IntPtr.Zero;
            public TFunction Original { get; }
            public void Enable() => State = HookState.Installed;
            public void Disable() { if (State == HookState.Installed) State = HookState.Disabled; }
            public void Dispose() => State = HookState.Disposed;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        private int[] vanillaArguments;
        private AssassinPathBuilder build;

        [TestInitialize]
        public void Arrange()
        {
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
        }

        [TestMethod]
        public void AThrowingHandlerGetsTheVanillaResultAndOneLogLine()
        {
            Handler((context, sx, sy, tx, ty, nodes, continuation) => throw new InvalidOperationException("consumer bug"));
            Assert.AreEqual(VanillaResult, build(new IntPtr(5), 1, 2, 3, 4, 100, 0), "The native caller did not get Vanilla's result.");
            Assert.AreEqual(1, vanillaCalls, "Vanilla did not answer a handler that threw before running it.");
            CollectionAssert.AreEqual(new[] { 5, 1, 2, 3, 4, 100, 0 }, vanillaArguments, "Vanilla did not get the native arguments.");

            // A throw after the handler ran Vanilla: Vanilla answers again with the same arguments.
            Handler((context, sx, sy, tx, ty, nodes, continuation) =>
            {
                AssassinPathAPI.RunVanillaBuilder(context, sx, sy, tx, ty, nodes, continuation);
                throw new InvalidOperationException("late consumer bug");
            });
            Assert.AreEqual(VanillaResult, build(new IntPtr(6), 7, 8, 9, 10, 200, 0));
            Assert.AreEqual(3, vanillaCalls);
            CollectionAssert.AreEqual(new[] { 6, 7, 8, 9, 10, 200, 0 }, vanillaArguments);

            Assert.AreEqual(1, errors.Count, "Each failure must not be logged; only the first one is.");
            StringAssert.Contains(errors[0], "consumer bug");
        }

        [TestMethod]
        public void HandlerResultsAndTheVanillaPathAreUnchanged()
        {
            Handler((context, sx, sy, tx, ty, nodes, continuation) => 99);
            Assert.AreEqual(99, build(new IntPtr(5), 1, 2, 3, 4, 100, 0));
            Assert.AreEqual(0, vanillaCalls);
            Field("weightedBuilder").SetValue(null, null);
            Assert.AreEqual(VanillaResult, build(new IntPtr(5), 1, 2, 3, 4, 100, 0));
            Assert.AreEqual(1, vanillaCalls);
            Assert.AreEqual(0, errors.Count);
        }

        [TestMethod]
        public void FailureWorkStaysOutOfTheCallbackSuccessPath()
        {
            // Build allocates no closure, and its only catch hands the failure to BuilderFailed.
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(AssassinPathAPI).Assembly.Location))
            {
                var method = assembly.MainModule.GetType("APIShared.AssassinPathAPI").Methods.Single(m => m.Name == "Build");
                Assert.IsFalse(method.Body.Instructions.Any(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Newobj), "Build allocates.");
                var handler = method.Body.ExceptionHandlers.Single();
                Assert.AreEqual(Mono.Cecil.Cil.ExceptionHandlerType.Catch, handler.HandlerType);
                Assert.AreEqual("System.Exception", handler.CatchType.FullName);
                Assert.IsTrue(method.Body.Instructions.TakeWhile(i => i != handler.TryStart).All(i => i.OpCode.Code == Mono.Cecil.Cil.Code.Nop),
                    "Part of Build runs outside the boundary.");
                var calls = new List<string>();
                for (var i = handler.HandlerStart; i != handler.HandlerEnd; i = i.Next)
                    if (i.Operand is Mono.Cecil.MethodReference called) calls.Add(called.Name);
                CollectionAssert.AreEqual(new[] { "BuilderFailed" }, calls, "The catch does more than delegate to BuilderFailed.");
            }
        }

        private void Handler(AssassinPathBuilder handler) => Field("weightedBuilder").SetValue(null, handler);

        private static FieldInfo Field(string name) => typeof(AssassinPathAPI).GetField(name, PrivateStatic);

        private DetourHandle<AssassinPathBuilder> PublishedVanilla()
        {
            AssassinPathBuilder vanilla = (context, sx, sy, tx, ty, nodes, continuation) =>
            {
                vanillaCalls++;
                vanillaArguments = new[] { context.ToInt32(), sx, sy, tx, ty, nodes, continuation };
                return VanillaResult;
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

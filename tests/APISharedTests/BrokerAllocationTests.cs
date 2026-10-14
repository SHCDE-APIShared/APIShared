using System;
using System.Linq;
using System.Runtime.CompilerServices;
using APIShared;
using APIShared.Commands;
using APIShared.Economy;
using APIShared.Events;
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace APISharedTests
{
    // Observed broker paths after warm-up on the .NET Framework test host. "core" is the
    // shared registry alone with a static Post factory; "publicArgs" allocates exactly the
    // public args the path must create (Pre, one Post per Post registration), the Pre rollback
    // checkpoint and paired owner-State storage. Unity/Mono and real callbacks are outside this fixture.
    [TestClass, DoNotParallelize]
    public sealed class BrokerAllocationTests
    {
        public TestContext TestContext { get; set; }
        private const int Iterations = 20000;
        private static readonly object Marker = new object();
        private static readonly GameActionEvents.Call GameOriginal = (command, structure, state, value2) => structure;
        private static readonly MarketPriceNativeRuntime.PriceCall MarketOriginal = (manager, player, good, amount) => amount;
        private static readonly Action<FatControler, object> GuiOriginal = (self, parameter) => { };
        private static readonly Func<GameActionPreEventArgs, object, GameActionPostEventArgs> GamePost = (pre, state) => new GameActionPostEventArgs(pre, 1, state);
        private static readonly Func<MarketPricePreEventArgs, object, MarketPricePostEventArgs> MarketPost = (pre, state) => new MarketPricePostEventArgs(pre, 4, state);
        private static readonly Func<PresentationPreEventArgs, object, PresentationPostEventArgs> GuiPost = (pre, state) => new PresentationPostEventArgs(pre, state);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static double Measure(Action call)
        {
            for (int i = 0; i < 2000; i++) call();
            long best = long.MaxValue;
            for (int sample = 0; sample < 5; sample++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Iterations; i++) call();
                best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
            }
            return best / (double)Iterations;
        }
        private void Check(string path, string shape, double bytes, double core, double payload)
        {
            TestContext.WriteLine("BROKER_ALLOC {0} {1}: bytesPerCall={2:F1}, core={3:F1}, publicArgs={4:F1}",
                path, shape, bytes, core, payload);
            Assert.AreEqual(core, bytes, path + " " + shape + " allocates beyond the shared registry (closure or delegate).");
        }
        private static Action<TPre> Pre<TPre>(bool active) where TPre : InterceptionPreEventArgs
            => active ? a => a.State = Marker : (Action<TPre>)null;
        private static Action<TPost> Post<TPost>(bool active) => active ? a => { } : (Action<TPost>)null;

        [TestMethod]
        public void ObservedDispatchAddsNoAllocationAfterWarmup()
        {
            foreach (string shape in new[] { "Post", "Pre", "PrePost" })
            {
                bool pre = shape != "Post", post = shape != "Pre";
                var game = new InterceptionEvent<GameActionPreEventArgs, GameActionPostEventArgs>();
                Assert.IsTrue(game.TryRegister("bench", "id", Pre<GameActionPreEventArgs>(pre), Post<GameActionPostEventArgs>(post), out _));
                Check("GameAction", shape,
                    Measure(() => GameActionEvents.DispatchObserved(game, Enums.GameActionCommand.MakeTroop, 1, 2, 3, GameOriginal)),
                    Measure(() => {
                        var invocation = game.Begin(new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1, 2, 3));
                        if (invocation.HasPostObservers) invocation.Complete(invocation.Pre, GamePost);
                    }),
                    Measure(() => {
                        var args = new GameActionPreEventArgs(Enums.GameActionCommand.MakeTroop, 1, 2, 3);
                        if (pre) args.Capture();
                        if (pre && post) GC.KeepAlive(new object[1]);
                        if (post) GC.KeepAlive(new GameActionPostEventArgs(args, 1, null));
                    }));

                var market = new InterceptionEvent<MarketPricePreEventArgs, MarketPricePostEventArgs>();
                Assert.IsTrue(market.TryRegister("bench", "id", Pre<MarketPricePreEventArgs>(pre), Post<MarketPricePostEventArgs>(post), out _));
                Check("MarketPrice", shape,
                    Measure(() => MarketPriceEvents.DispatchObserved(market, MarketPriceDirection.Sell, new IntPtr(1), 2, 3, 4, MarketOriginal)),
                    Measure(() => {
                        var invocation = market.Begin(new MarketPricePreEventArgs(MarketPriceDirection.Sell, new IntPtr(1), 2, 3, 4));
                        if (invocation.HasPostObservers) invocation.Complete(invocation.Pre, MarketPost);
                    }),
                    Measure(() => {
                        var args = new MarketPricePreEventArgs(MarketPriceDirection.Sell, new IntPtr(1), 2, 3, 4);
                        if (pre) args.Capture();
                        if (pre && post) GC.KeepAlive(new object[1]);
                        if (post) GC.KeepAlive(new MarketPricePostEventArgs(args, 4, null));
                    }));

                // GuiChecks as the production site calls it: receiver plus a delegate cached per site.
                var gui = new InterceptionEvent<PresentationPreEventArgs, PresentationPostEventArgs>();
                Assert.IsTrue(gui.TryRegister("bench", "id", Pre<PresentationPreEventArgs>(pre), Post<PresentationPostEventArgs>(post), out _));
                Check("Presentation", shape,
                    Measure(() => PresentationEvents.Execute(gui, new PresentationPreEventArgs(PresentationOperation.GuiChecks, null, null),
                        (FatControler)null, GuiOriginal)),
                    Measure(() => {
                        var invocation = gui.Begin(new PresentationPreEventArgs(PresentationOperation.GuiChecks, null, null));
                        if (invocation.HasPostObservers) invocation.Complete(invocation.Pre, GuiPost);
                    }),
                    Measure(() => {
                        var args = new PresentationPreEventArgs(PresentationOperation.GuiChecks, null, null);
                        if (pre) args.Capture();
                        if (pre && post) GC.KeepAlive(new object[1]);
                        if (post) GC.KeepAlive(new PresentationPostEventArgs(args, null));
                    }));
            }
        }

        [TestMethod]
        public void CompiledObservedPathsCreateNoClosuresOrDelegates()
        {
            // Verify compiler output: a captured lambda allocates a display class at method
            // entry, before any HasPostObservers guard. Value-type contexts are not heap objects.
            using (var module = ModuleDefinition.ReadModule(typeof(ApiShared).Assembly.Location))
            {
                var targets = new[] {
                    ("APIShared.Commands.GameActionEvents", "DispatchObserved", "APIShared.Commands.GameActionPreEventArgs"),
                    ("APIShared.Economy.MarketPriceEvents", "DispatchObserved", "APIShared.Economy.MarketPricePreEventArgs"),
                    ("APIShared.Presentation.PresentationEvents", "ExecuteObserved", null),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchObservedView", "APIShared.Presentation.PresentationPreEventArgs"),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchObservedHud", "APIShared.Presentation.PresentationPreEventArgs"),
                    ("APIShared.Presentation.PresentationEvents/Site", "DispatchObservedGui", "APIShared.Presentation.PresentationPreEventArgs")
                };
                foreach (var target in targets)
                {
                    var method = module.GetTypes().Single(t => t.FullName == target.Item1).Methods.Single(m => m.Name == target.Item2);
                    foreach (var instruction in method.Body.Instructions)
                    {
                        Assert.IsFalse(instruction.OpCode == OpCodes.Ldftn || instruction.OpCode == OpCodes.Ldvirtftn ||
                            instruction.OpCode == OpCodes.Newarr || instruction.OpCode == OpCodes.Box,
                            target.Item1 + "." + target.Item2 + ": " + instruction);
                        if (instruction.OpCode != OpCodes.Newobj) continue;
                        var type = ((MethodReference)instruction.Operand).DeclaringType;
                        Assert.IsTrue(type.Resolve().IsValueType || type.FullName == target.Item3,
                            target.Item1 + "." + target.Item2 + " allocates " + type.FullName);
                    }
                }
            }
        }
    }
}

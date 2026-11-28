using System;
using System.Collections.Generic;
using APIShared.WorldMutations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests
{
    [TestClass]
    public sealed class WorldMutationTests
    {
        private sealed class Observer : IWorldMutationObserver, IWorldMutationInvocation
        {
            internal readonly List<WorldMutationContext> Starts = new();
            internal readonly List<WorldMutationCompletion> Ends = new();
            internal readonly List<WorldMutationBatchCompletion> Batches = new();
            internal Action<WorldMutationContext> Before;
            internal Action<WorldMutationCompletion> After;
            public IWorldMutationInvocation TryBegin(WorldMutationContext context)
            { Starts.Add(context); Before?.Invoke(context); return this; }
            public void Complete(WorldMutationCompletion completion)
            { Ends.Add(completion); After?.Invoke(completion); }
            public void OnBatchCompleted(WorldMutationBatchCompletion completion) => Batches.Add(completion);
        }
        private static WorldMutationRegistry Registry(Observer observer, Func<WorldMutationWorld> world = null,
            Action<Exception> reporter = null)
        {
            object token = new();
            var registry = new WorldMutationRegistry(world ?? (() => new WorldMutationWorld(7, token)), reporter);
            registry.SetAvailable(WorldMutationSource.All);
            Assert.IsTrue(registry.Register("tests", "main", observer, WorldMutationSource.All, null, 0, out _));
            return registry;
        }

        [TestMethod]
        public void SiblingAndNestedOriginalsHavePrivateReceiptsAndOneOuterCompletion()
        {
            var observer = new Observer(); var registry = Registry(observer);
            long batch = registry.EnterBatch(WorldMutationSource.AivBuildStep);
            var first = registry.Begin(WorldMutationSource.BuildStructure, new long[] { 123, 4, 10, 20, 8, 1, 2 });
            var nested = registry.Begin(WorldMutationSource.WallRemoval, new long[] { 123, 4, 80 });
            Assert.AreEqual(first.Context.InvocationId, nested.Context.ParentInvocationId);
            registry.Complete(nested, true, true, 1, null);
            registry.Complete(first, true, true, 22, null);
            Assert.AreEqual(0, observer.Batches.Count);
            long siblingBatch = registry.EnterBatch(WorldMutationSource.BuildStructure);
            var sibling = registry.Begin(WorldMutationSource.BuildStructure, new long[] { 123, 4, 11, 21, 8, 1, 2 });
            registry.Complete(sibling, true, true, 23, null);
            registry.LeaveBatch(siblingBatch, true);
            Assert.AreEqual(0, observer.Batches.Count);
            registry.LeaveBatch(batch, true);
            Assert.AreEqual(1, observer.Batches.Count);
            Assert.IsTrue(observer.Batches[0].ReturnedNormally);
            Assert.IsTrue(observer.Batches[0].IsCurrentWorld);
            Assert.AreEqual(0L, sibling.Context.ParentInvocationId);
            Assert.AreEqual(10, first.Context.X); Assert.AreEqual(20, first.Context.Y);
            Assert.AreNotSame(observer.Ends[0], observer.Ends[1]);
            Assert.AreEqual(22L, observer.Ends[1].ReturnValue);
        }

        [TestMethod]
        public void CompletionFactsAndCallbackFailuresCannotHideOriginalFailure()
        {
            var failure = new InvalidOperationException("native"); int reports = 0;
            var observer = new Observer { After = _ => throw new Exception("consumer") };
            var registry = Registry(observer, reporter: _ => { reports++; throw new Exception("logger"); });
            long batch = registry.EnterBatch(WorldMutationSource.MoatDig);
            var invocation = registry.Begin(WorldMutationSource.MoatDig, new long[] { 123, 9 });
            registry.Complete(invocation, true, false, 0, failure);
            registry.LeaveBatch(batch, false);
            Assert.AreSame(failure, observer.Ends[0].Failure);
            Assert.IsTrue(observer.Ends[0].OriginalInvoked); Assert.IsFalse(observer.Ends[0].OriginalReturned);
            Assert.IsTrue(observer.Batches[0].ContainsOriginalFailure);
            Assert.IsFalse(observer.Batches[0].ReturnedNormally); Assert.AreEqual(1, reports);
        }

        [TestMethod]
        public void WorldIdentityChangesAndExplicitInvalidationRejectOldBatch()
        {
            object world = new(); long session = 2;
            var observer = new Observer(); var registry = Registry(observer, () => new WorldMutationWorld(session, world));
            long batch = registry.EnterBatch(WorldMutationSource.AivBuildStep);
            registry.TryGetWorld(out _, out long generation);
            world = new(); registry.TryGetWorld(out _, out long next);
            Assert.IsTrue(next > generation);
            registry.LeaveBatch(batch, true); Assert.IsFalse(observer.Batches[0].IsCurrentWorld);
            registry.Invalidate(); registry.TryGetWorld(out _, out long invalidated);
            Assert.IsTrue(invalidated > next);
            session++; registry.TryGetWorld(out long newSession, out long newGeneration);
            Assert.AreEqual(3L, newSession); Assert.IsTrue(newGeneration > invalidated);
        }

        [TestMethod]
        public void DisabledOrUnavailableDemandDoesNotReadWorldOrAllocatePerCall()
        {
            var registry = new WorldMutationRegistry(() => throw new Exception("must not read"), null);
            registry.SetAvailable(WorldMutationSource.All);
            var observer = new Observer(); bool enabled = false;
            Assert.IsTrue(registry.Register("test", "disabled", observer, WorldMutationSource.All, () => enabled, 0, out _));
            // Warm the actual delegate/volatile fast path beyond tiered-JIT call counting.
            // An intermittent one-time 24-byte runtime allocation with the former 100-call
            // warmup is not a steady-state per-mutation allocation. Keep the measured bound exact.
            long token = 0;
            for (int i = 0; i < 100000; i++) token = registry.EnterBatch(WorldMutationSource.BuildStructure);
            Assert.AreEqual(0L, token);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++) registry.EnterBatch(WorldMutationSource.BuildStructure);
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            enabled = true; registry.SetAvailable(WorldMutationSource.None);
            Assert.IsFalse(registry.HasDemand(WorldMutationSource.All));
            Assert.AreEqual(0L, registry.EnterBatch(WorldMutationSource.BuildStructure));
        }

        [TestMethod]
        public void RegistrationSnapshotAndReverseReceiptOrderSurviveChangesDuringBegin()
        {
            var events = new List<string>(); var first = new Observer(); var second = new Observer();
            var registry = Registry(first);
            first.Before = context => { events.Add("first.begin"); registry.Register("later", "new", new Observer(), WorldMutationSource.All, null, 0, out _); };
            first.After = _ => events.Add("first.end");
            second.Before = _ => events.Add("second.begin"); second.After = _ => events.Add("second.end");
            Assert.IsTrue(registry.Register("tests", "second", second, WorldMutationSource.All, null, 1, out _));
            Assert.IsFalse(registry.Register("tests", "main", second, WorldMutationSource.All, null, 1, out _));
            long batch = registry.EnterBatch(WorldMutationSource.BuildStructure);
            var call = registry.Begin(WorldMutationSource.BuildStructure, Array.Empty<long>());
            registry.Complete(call, true, true, 0, null); registry.LeaveBatch(batch, true);
            CollectionAssert.AreEqual(new[] { "first.begin", "second.begin", "second.end", "first.end" }, events);
        }
    }
}

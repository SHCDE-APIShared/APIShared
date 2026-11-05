using System;
using System.Collections.Generic;
using APIShared.Economy;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    public sealed class AllyTransferTests
    {
        private sealed class World : IAllyTransfersBackend
        {
            internal readonly int[] Stock = new int[9];
            internal int Free = 1000, Reads, CapacityCalls, Calls, LastAmount;
            internal bool Ready = true;
            internal bool AlliedLiving = true;
            internal readonly InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs> Events =
                new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            public bool IsReady => Ready;
            public bool AreAlliedLiving(int sender, int recipient) => AlliedLiving;
            public bool TryReadStock(int player, eGoods good, out int amount) { Reads++; amount = Stock[player]; return true; }
            public int GetFreeCapacity(int recipient, eGoods good) { CapacityCalls++; return Free; }
            public AllyTransferResult Execute(AllyTransferRequest request, int amount) =>
                AllyTransferEvents.DispatchObserved(Events, request.SenderPlayerId, request.RecipientPlayerId, (int)request.Good, amount, Transfer, TryReadStock, true);
            internal void Transfer(int recipient, int good, int amount, int sender)
            { Calls++; LastAmount = amount; Stock[sender] -= amount; Stock[recipient] += amount; Free -= amount; }
        }

        [TestMethod]
        public void OffContextAndUnavailableServicesPerformNoGameReads()
        {
            var world = new World();
            var engine = new AllyTransfersEngine(world, () => false);
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 10), out var result, out _));
            Assert.AreEqual(AllyTransferStatus.Rejected, result.Status);
            Assert.IsFalse(engine.TryGetCapacity(2, eGoods.STORED_GOLD, out _, out _));
            Assert.AreEqual(0, world.Reads); Assert.AreEqual(0, world.CapacityCalls); Assert.AreEqual(0, world.Calls);
            world.Ready = false;
            engine = new AllyTransfersEngine(world, () => true);
            Assert.IsFalse(engine.TryGetCapacity(2, eGoods.STORED_GOLD, out _, out _));
            Assert.AreEqual(0, world.Reads);
        }

        [TestMethod]
        public void CurrentReserveCeilingCapacityAndPacketBoundsComposeWithoutOverflow()
        {
            var world = new World { Free = 100 };
            world.Stock[1] = 250; world.Stock[2] = 20;
            var engine = new AllyTransfersEngine(world, () => true);
            Assert.IsTrue(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 500, 200, 60), out var result, out _));
            Assert.AreEqual(40, result.TransferredAmount); Assert.AreEqual(210, world.Stock[1]); Assert.AreEqual(60, world.Stock[2]);
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 500, 200, 60), out _, out _));
            Assert.AreEqual(1, world.Calls);
            world.Stock[1] = int.MaxValue; world.Stock[2] = int.MaxValue - 2;
            Assert.IsTrue(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, int.MaxValue), out result, out _));
            Assert.AreEqual(2, result.TransferredAmount); Assert.AreEqual(int.MaxValue, world.Stock[2]);
            world.Stock[1] = int.MaxValue; world.Stock[2] = 0;
            Assert.IsTrue(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, int.MaxValue), out result, out _));
            Assert.AreEqual(short.MaxValue, result.TransferredAmount);
        }

        [TestMethod]
        public void FinalAdmissionReadsCurrentReserveRecipientCeilingStorageAndAllianceAgain()
        {
            var world=new World { Free=10 }; world.Stock[1]=100;
            var engine=new AllyTransfersEngine(world,()=>true);
            var request=new AllyTransferRequest(1,2,eGoods.STORED_WOOD_PLANKS,10,90,10);
            Assert.IsTrue(engine.IsStillAdmissible(request,10));
            world.Stock[1]=99; Assert.IsFalse(engine.IsStillAdmissible(request,10));
            world.Stock[1]=100; world.Stock[2]=1; Assert.IsFalse(engine.IsStillAdmissible(request,10));
            world.Stock[2]=0; world.Free=9; Assert.IsFalse(engine.IsStillAdmissible(request,10));
            world.Free=10; world.AlliedLiving=false; Assert.IsFalse(engine.IsStillAdmissible(request,10));
            Assert.AreEqual(0,world.Calls);
        }

        [TestMethod]
        public void NoStorageFullStorageAndUnsupportedGoodsRemainDistinct()
        {
            var world = new World { Free = -1 };
            world.Stock[1] = 100;
            var engine = new AllyTransfersEngine(world, () => true);
            Assert.IsTrue(engine.TryGetCapacity(2, eGoods.STORED_SWORDS, out var capacity, out _));
            Assert.IsFalse(capacity.HasStorage); Assert.AreEqual(0, capacity.FreeAmount);
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_SWORDS, 10), out _, out _));
            world.Free = 0;
            Assert.IsTrue(engine.TryGetCapacity(2, eGoods.STORED_SWORDS, out capacity, out _));
            Assert.IsTrue(capacity.HasStorage); Assert.AreEqual(0, capacity.FreeAmount);
            int reads = world.Reads;
            foreach (eGoods unsupported in new[] { eGoods.STORED_NULL, eGoods.STORED_WOOD_LOGS, eGoods.STORED_COW_HIDES, eGoods.STORED_PITCH_REFINED, eGoods.Count })
                Assert.IsFalse(engine.TryGetCapacity(2, unsupported, out _, out _));
            Assert.AreEqual(reads, world.Reads); Assert.AreEqual(0, world.Calls);
            Assert.AreEqual(21, AllyTransfers.SupportedGoods.Count, "This exact list is the audited Vanilla ally-send UI contract.");
            foreach (eGoods good in AllyTransfers.SupportedGoods) Assert.IsTrue(AllyTransfers.IsSupportedGood(good));
        }

        [TestMethod]
        public void GoldCapacityDoesNotDependOnStorageBuildings()
        {
            var world = new World { Free = -1 };
            world.Stock[1] = 100; world.Stock[2] = 5;
            var engine = new AllyTransfersEngine(world, () => true);
            Assert.IsTrue(engine.TryGetCapacity(2, eGoods.STORED_GOLD, out var capacity, out _));
            Assert.IsTrue(capacity.HasStorage); Assert.AreEqual(int.MaxValue - 5, capacity.FreeAmount);
            Assert.AreEqual(0, world.CapacityCalls);
        }

        [TestMethod]
        public void NonAlliedOrEliminatedPlayersNeverEnterTheNativeTransfer()
        {
            var world = new World { AlliedLiving = false }; world.Stock[1] = 100;
            var engine = new AllyTransfersEngine(world, () => true);
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10), out var result, out _));
            Assert.AreEqual(AllyTransferStatus.Rejected, result.Status); Assert.AreEqual(0, world.Reads); Assert.AreEqual(0, world.Calls);
        }

        [TestMethod]
        public void NegativeBalancesBoundsAndSelfTransfersFailBeforeNativeMutation()
        {
            var world = new World(); world.Stock[1] = 100;
            var engine = new AllyTransfersEngine(world, () => true);
            foreach (var request in new[] {
                new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, -1),
                new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10, -1),
                new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10, 0, -1),
                new AllyTransferRequest(1, 1, eGoods.STORED_GOLD, 10),
                new AllyTransferRequest(0, 2, eGoods.STORED_GOLD, 10),
                new AllyTransferRequest(1, 9, eGoods.STORED_GOLD, 10) })
                Assert.IsFalse(engine.TryExecute(request, out _, out _));
            world.Stock[1] = -1;
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10), out _, out _));
            world.Stock[1] = 100; world.Stock[2] = -1;
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_GOLD, 10), out _, out _));
            Assert.AreEqual(0, world.Calls);
        }

        [TestMethod]
        public void VetoIsStickyFailingVetoRollsBackAndStateIsOwnerPrivate()
        {
            var world = new World();
            world.Stock[1] = 100;
            AllyTransferPostEventArgs observed = null;
            world.Events.TryRegister("a", "broken", pre => { pre.State = "bad"; pre.SkipOriginalFunction = true; throw new Exception("bad observer"); },
                post => Assert.IsNull(post.State), out _);
            world.Events.TryRegister("b", "veto", pre => { Assert.IsFalse(pre.SkipOriginalFunction); pre.State = "mine"; pre.SkipOriginalFunction = true; },
                post => { observed = post; Assert.AreEqual("mine", post.State); }, out _);
            world.Events.TryRegister("c", "cannot-clear", pre => pre.SkipOriginalFunction = false, null, out _);
            var engine = new AllyTransfersEngine(world, () => true);
            Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 10), out var result, out _));
            Assert.IsTrue(result.WasVetoed); Assert.IsFalse(result.OriginalWasCalled);
            Assert.AreEqual(0, world.Calls); Assert.AreEqual(100, world.Stock[1]); Assert.AreEqual(0, world.Stock[2]);
            Assert.AreSame(result, observed.Result); Assert.AreEqual(1L, world.Events.CallbackFailures);
        }

        [TestMethod]
        public void PostReportsBothNetDeltasInsteadOfAssumingTheRequestedAmount()
        {
            int sender = 100, recipient = 10, calls = 0;
            var events = new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            var posts = new List<AllyTransferPostEventArgs>();
            events.TryRegister("a", "observe", null, posts.Add, out _);
            bool Read(int player, eGoods good, out int amount) { amount = player == 1 ? sender : recipient; return true; }
            var result = AllyTransferEvents.DispatchObserved(events, 1, 2, (int)eGoods.STORED_GOLD, 10,
                (r, g, a, s) => { calls++; sender -= 10; recipient += 3; }, Read, true);
            Assert.AreEqual(AllyTransferStatus.Uncertain, result.Status); Assert.IsNull(result.TransferredAmount);
            Assert.AreEqual(10L, result.SenderDebit); Assert.AreEqual(3L, result.RecipientCredit);
            Assert.AreEqual(1, calls); Assert.AreSame(result, posts[0].Result);
            bool Missing(int player, eGoods good, out int amount) { amount = 0; return false; }
            result = AllyTransferEvents.DispatchObserved(events, 1, 2, (int)eGoods.STORED_GOLD, 10,
                (r, g, a, s) => calls++, Missing, true);
            Assert.IsNull(result.SenderDebit); Assert.IsNull(result.RecipientCredit); Assert.IsNull(result.TransferredAmount);
            Assert.AreEqual(2, calls);
        }

        [TestMethod]
        public void NativeOriginalExceptionReportsPartialMutationWithoutRetry()
        {
            int source = 100, target = 0, calls = 0;
            bool Read(int player, eGoods good, out int amount) { amount = player == 1 ? source : target; return true; }
            var events = new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            AllyTransferPostEventArgs post = null;
            events.TryRegister("test", "post", null, value => post = value, out _);
            var result = AllyTransferEvents.DispatchObserved(events, 1, 2, (int)eGoods.STORED_WOOD_PLANKS, 10,
                (r, g, a, s) => { calls++; target += 10; throw new Exception("call chain failed"); }, Read, true);
            Assert.AreEqual(1, calls); Assert.AreEqual(AllyTransferStatus.Faulted, result.Status);
            Assert.IsTrue(result.OriginalWasCalled); Assert.AreEqual(0L, result.SenderDebit); Assert.AreEqual(10L, result.RecipientCredit);
            Assert.IsNull(result.TransferredAmount); Assert.AreSame(result, post.Result); Assert.IsFalse(AllyTransferEvents.IsDispatching);
        }

        [TestMethod]
        public void UnobservedVanillaPathDoesNotReadBalancesAndCallsOriginalOnce()
        {
            int calls = 0;
            var events = new InterceptionEvent<AllyTransferPreEventArgs, AllyTransferPostEventArgs>();
            bool Forbidden(int player, eGoods good, out int amount) { amount = 0; Assert.Fail("Dormant observer read the game."); return false; }
            Assert.IsNull(AllyTransferEvents.DispatchObserved(events, 1, 2, (int)eGoods.STORED_GOLD, 10,
                (r, g, a, s) => calls++, Forbidden, false));
            Assert.AreEqual(1, calls);
        }

        [TestMethod]
        public void NestedNativeTransferRunsOnceWithoutRecursiveEventsAndMakesAttributionUnknown()
        {
            var world = new World(); world.Stock[1] = 100;
            int pres = 0, posts = 0;
            world.Events.TryRegister("a", "observe", pre => pres++, post => posts++, out _);
            var result = AllyTransferEvents.DispatchObserved(world.Events, 1, 2, (int)eGoods.STORED_WOOD_PLANKS, 10,
                (r, g, a, s) => {
                    AllyTransferEvents.DispatchObserved(world.Events, s, r, g, 1, world.Transfer, world.TryReadStock, false);
                    world.Transfer(r, g, a, s);
                }, world.TryReadStock, true);
            Assert.AreEqual(2, world.Calls); Assert.AreEqual(1, pres); Assert.AreEqual(1, posts);
            Assert.IsTrue(result.HasNestedTransfer); Assert.AreEqual(AllyTransferStatus.Uncertain, result.Status);
            Assert.IsNull(result.TransferredAmount); Assert.IsFalse(AllyTransferEvents.IsDispatching);
        }

        [TestMethod]
        public void TransferCallbacksCannotUseTheServiceToStartNestedMutations()
        {
            var world = new World(); world.Stock[1] = 100;
            var engine = new AllyTransfersEngine(world, () => true);
            world.Events.TryRegister("a", "nested", pre => {
                int reads = world.Reads;
                Assert.IsFalse(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 1), out _, out _));
                Assert.AreEqual(reads, world.Reads);
            }, null, out _);
            Assert.IsTrue(engine.TryExecute(new AllyTransferRequest(1, 2, eGoods.STORED_WOOD_PLANKS, 10), out _, out _));
            Assert.AreEqual(1, world.Calls);
        }
    }
}

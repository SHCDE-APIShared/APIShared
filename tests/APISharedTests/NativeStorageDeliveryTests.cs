using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    // Actual packed SDK actor buffers and actual prepared physical/ledger publication. The
    // qualified native producer continuation, live pathfinding and file I/O remain separate tests.
    [TestClass]
    public unsafe class NativeStorageDeliveryTests
    {
        private sealed class Backend : IGoodsWalletBackend
        {
            internal object CurrentWorld = new object();
            internal bool Ready = true, Allowed = true;
            internal Action OnWorldRead;
            public bool IsReady => Ready;
            public object EngineMonitor { get; } = new object();
            public object World { get { OnWorldRead?.Invoke(); return CurrentWorld; } }
            public bool HasAuthorityContext => Allowed;
            public bool TryGetGoldCell(int player, out int* cell)
            { cell = null; throw new InvalidOperationException("Delivery must not access native gold."); }
        }
        private sealed class ActorBuffer : IDisposable
        {
            private readonly IntPtr unit = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameUnit)));
            private readonly IntPtr home = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameBuilding)));
            internal GameUnit* Worker => (GameUnit*)unit;
            internal GameBuilding* Home => (GameBuilding*)home;
            internal NativeStorageProbe Destination => new NativeStorageProbe(true, 20, 300, 1, StoragePool.Armory, 0, 50);
            internal NativeStorageDeliveryActor Actor => new NativeStorageDeliveryActor(Worker, 1, 200, Home, 10, 100);
            internal short Cargo { get => *(short*)((byte*)Worker + 0x384); set => *(short*)((byte*)Worker + 0x384) = value; }
            internal ushort Adjacent => *(ushort*)((byte*)Worker + 0x386);
            internal ActorBuffer()
            {
                new Span<byte>((void*)unit, Marshal.SizeOf(typeof(GameUnit))).Clear();
                new Span<byte>((void*)home, Marshal.SizeOf(typeof(GameBuilding))).Clear();
                Worker->r_GlobalId = 200; Worker->r_AliveState = AliveState.IsAlive;
                Worker->r_ControllableForPlayerId = 1; Worker->r_UnitChimp = eChimps.CHIMP_TYPE_FLETCHER;
                Worker->r_LinkedProductionBuildingId = 10; Worker->r_LinkedProductionBuildingGlobalId = 100;
                Home->r_GlobalId = 100; Home->r_AliveState = AliveState.IsAlive;
                Home->r_PlayerIdOwner = 1; Home->r_BuildingType = eStructs.STRUCT_FLETCHERS_WORKSHOP;
                *(ushort*)((byte*)Worker + 0x332) = 20;
                *(uint*)((byte*)Worker + 0x9C) = 300;
                *(uint*)((byte*)Worker + 0x384) = 0xA55A0002;
            }
            public void Dispose() { Marshal.FreeHGlobal(unit); Marshal.FreeHGlobal(home); }
        }
        private static CustomGoodId Register(NativeStorageState state, string owner = "owner")
        {
            var good = new CustomGoodId(owner, "custom-armor");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Armory, out string reason), reason);
            return good;
        }
        private static void Equal(byte[] goods, byte[] stores, NativeStorageState state)
        {
            state.Capture(out byte[] currentGoods, out byte[] currentStores);
            CollectionAssert.AreEqual(goods, currentGoods); CollectionAssert.AreEqual(stores, currentStores);
        }

        [TestMethod]
        public void EachInvocationConsumesOneSignedCargoItemAndPreservesTheAdjacentWord()
        {
            Assert.IsTrue(NativeStorageDeliveryEngine.ValidateLayout());
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            lock (backend.EngineMonitor)
            {
                Assert.IsTrue(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.AreEqual((short)1, actor.Cargo);
                Assert.AreEqual((ushort)0xA55A, actor.Adjacent);
                Assert.IsTrue(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out status, out reason), reason);
                Assert.AreEqual((short)0, actor.Cargo); Assert.AreEqual((ushort)0xA55A, actor.Adjacent);
                Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out _));
                Assert.AreEqual(2, state.GetQuantity(good, actor.Destination));
                Assert.AreEqual(2L, state.GetLastSequence(good.OwnerGuid));
                Assert.IsTrue(state.TryGetBalance(good, 1, out var balance)); Assert.AreEqual(2, balance.Quantity);
            }
        }

        [TestMethod]
        public void CapacityOrSequenceFailureLeavesCargoLedgerAndPhysicalRowsUnchanged()
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            lock (backend.EngineMonitor)
            {
                Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, actor.Destination, 50, 0, out var seed, out _));
                using (seed) seed.Publish();
                state.Capture(out byte[] goods, out byte[] stores);
                Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out var status, out _));
                Assert.AreEqual(GoodsTransactionStatus.CapacityExceeded, status); Assert.AreEqual((short)2, actor.Cargo);
                Equal(goods, stores, state);
                Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, long.MaxValue, actor.Destination, -1, 0, out var terminal, out _));
                using (terminal) terminal.Publish();
                state.Capture(out goods, out stores);
                Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out status, out _));
                Assert.AreEqual(GoodsTransactionStatus.LimitReached, status); Assert.AreEqual((short)2, actor.Cargo);
                Equal(goods, stores, state);
            }
        }

        [TestMethod]
        public void MissingEngineContextCannotConsumeCargoOrCreateClaims()
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            state.Capture(out byte[] goods, out byte[] stores);
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            {
                Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out _));
                lock (backend.EngineMonitor)
                {
                    backend.Allowed = false;
                    Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out _));
                    backend.Allowed = true; backend.Ready = false;
                    Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out _));
                }
                Assert.AreEqual((short)2, actor.Cargo); Equal(goods, stores, state);
            }
        }

        [TestMethod]
        [DataRow(0)] [DataRow(1)] [DataRow(2)] [DataRow(3)] [DataRow(4)] [DataRow(5)]
        public void InvalidActorHomeDestinationOrSignedCargoRefusesBeforePublication(int fault)
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            state.Capture(out byte[] goods, out byte[] stores);
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            lock (backend.EngineMonitor)
            {
                switch (fault)
                {
                    case 0: actor.Worker->r_GlobalId++; break;
                    case 1: actor.Home->r_PlayerIdOwner = 2; break;
                    case 2: actor.Worker->r_LinkedProductionBuildingGlobalId++; break;
                    case 3: *(uint*)((byte*)actor.Worker + 0x9C) = 301; break;
                    case 4: actor.Worker->r_IsKilledByProjectile = 1; break;
                    default: actor.Cargo = short.MinValue; break;
                }
                short original = actor.Cargo;
                Assert.IsFalse(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out _));
                Assert.AreEqual(original, actor.Cargo); Assert.AreEqual((ushort)0xA55A, actor.Adjacent);
                Equal(goods, stores, state);
            }
        }

        [TestMethod]
        public void ReentrantFinalContextFailureAbortsThePreparedPhysicalChange()
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            state.Capture(out byte[] goods, out byte[] stores);
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            lock (backend.EngineMonitor)
            {
                int reads = 0;
                backend.OnWorldRead = () => { if (++reads == 2) backend.CurrentWorld = new object(); };
                object expected = backend.CurrentWorld;
                Assert.IsFalse(engine.TryDeliver(state, good, expected, actor.Destination, actor.Actor, out _, out _));
                Assert.AreEqual((short)2, actor.Cargo); Equal(goods, stores, state);
                backend.OnWorldRead = null;
                Assert.IsTrue(engine.TryDeliver(state, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out string reason), reason);
                Assert.AreEqual(1L, state.GetLastSequence(good.OwnerGuid));
            }
        }

        [TestMethod]
        public void DeliveryAllocatesItsSequenceAfterStorageLossMaintenance()
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state);
            var oldStore = new NativeStorageProbe(true, 2, 22, 1, StoragePool.Armory, 0, 50);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, oldStore, 3, 0, out var seed, out _));
            using (seed) seed.Publish();
            Assert.IsTrue(state.Reconcile(_ => default, out var reconciled, out string reason), reason);
            Assert.AreEqual(2L, reconciled.GetLastSequence(good.OwnerGuid));
            var backend = new Backend(); var engine = new NativeStorageDeliveryEngine(backend);
            using (var actor = new ActorBuffer())
            lock (backend.EngineMonitor)
            {
                Assert.IsTrue(engine.TryDeliver(reconciled, good, backend.CurrentWorld, actor.Destination, actor.Actor, out _, out reason), reason);
                Assert.AreEqual(3L, reconciled.GetLastSequence(good.OwnerGuid)); Assert.AreEqual((short)1, actor.Cargo);
            }
        }

        [TestMethod]
        public void CriticalSaveRequiresTheExactFrozenPairAndSuccessfulCurrentCapture()
        {
            var state = new NativeStorageState(); CustomGoodId good = Register(state); object world = new object();
            var frozen = new NativeStorageProductionSave(state, world);
            Assert.IsFalse(frozen.IsWritten(VersionedSaveWriteStatus.Written, false));
            Assert.IsTrue(VersionedSaveKnownEntries.TryCreate(new[] { "ledger", "stores" }, out var known, out _));
            var writer = new VersionedSaveWriter(known, true, false, "synthetic");
            frozen.Write(writer); writer.Close();
            byte[] record = VersionedSaveRecordCodec.Encode(1, 1, writer.Written);
            Assert.IsTrue(frozen.MatchesRecord(record)); Assert.IsTrue(frozen.IsWritten(VersionedSaveWriteStatus.Written, false));
            Assert.IsFalse(frozen.IsWritten(VersionedSaveWriteStatus.NotWritten, false));
            Assert.IsFalse(frozen.IsWritten(VersionedSaveWriteStatus.KeptLoadedRecord, true));
            Assert.IsFalse(frozen.MatchesRecord(record.Take(record.Length - 1).ToArray()));
            Assert.IsFalse(frozen.Matches(state, new object()));
            Assert.IsTrue(frozen.Matches(state, world));
            var building = new NativeStorageProbe(true, 20, 300, 1, StoragePool.Armory, 0, 50);
            Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareExchange(good, 1, building, 1, 0, out var plan, out _));
            using (plan) plan.Publish();
            Assert.IsFalse(frozen.Matches(state, world), "A later publication must invalidate the checkpoint.");
            state.Capture(out byte[] goods, out byte[] stores);
            var different = new[] { new System.Collections.Generic.KeyValuePair<string, byte[]>("ledger", goods),
                new System.Collections.Generic.KeyValuePair<string, byte[]>("stores", stores) };
            Assert.IsFalse(frozen.MatchesRecord(VersionedSaveRecordCodec.Encode(1, 1, different)));
        }

        [TestMethod]
        public void CompiledDeliveryApertureStoresOneWordAndCallsOnlyKnownPublication()
        {
            using (var module = ModuleDefinition.ReadModule(typeof(NativeStoredGood).Assembly.Location))
            {
                var code = module.GetType("APIShared.Economy.NativeStorageDeliveryEngine").Methods.Single(m => m.Name == "PublishCargoAndStorage").Body.Instructions;
                Assert.AreEqual(1, code.Count(i => i.OpCode.Code == Code.Stind_I2));
                Assert.AreEqual(0, code.Count(i => i.OpCode.Code == Code.Stind_I4));
                var calls = code.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).FullName).ToArray();
                Assert.AreEqual(1, calls.Length); StringAssert.Contains(calls[0], "PreparedExchange::Publish()");
                Assert.IsFalse(code.Any(i => i.OpCode.Code == Code.Newobj || i.OpCode.Code == Code.Newarr));
            }
        }
    }
}

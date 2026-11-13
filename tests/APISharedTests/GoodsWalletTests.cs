using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    public unsafe sealed class GoodsWalletTests
    {
        private sealed class Backend : IGoodsWalletBackend, IDisposable
        {
            internal readonly IntPtr Buffer = Marshal.AllocHGlobal(22588 * 8);
            internal object CurrentWorld = new object();
            internal bool Ready = true, Allowed = true, Missing;
            internal Action BeforeResolve;
            internal int Resolves;
            public bool IsReady => Ready;
            public object EngineMonitor { get; } = new object();
            public object World => CurrentWorld;
            public bool HasAuthorityContext => Allowed;
            internal int Gold { get => *GoodsWalletNative.GoldCell((GamePlayerResources*)Buffer); set => *GoodsWalletNative.GoldCell((GamePlayerResources*)Buffer) = value; }
            internal Backend() { new Span<byte>((void*)Buffer, 22588 * 8).Clear(); }
            public bool TryGetGoldCell(int player, out int* cell)
            {
                Resolves++; BeforeResolve?.Invoke();
                cell = Missing ? null : GoodsWalletNative.GoldCell((GamePlayerResources*)((byte*)Buffer + (player - 1) * 22588));
                return cell != null;
            }
            public void Dispose() => Marshal.FreeHGlobal(Buffer);
        }
        private static CustomGoodsClient Setup(out CustomGoodsInventory inventory, out CustomGoodId good)
        { inventory = new CustomGoodsInventory(); var c = inventory.ForOwner("owner"); Assert.IsTrue(c.TryRegister("armor", 10, out good, out _)); return c; }
        [TestMethod]
        public void CriticalPathWarmupTouchesOnlyIsolatedScratchState()
        {
            Setup(out var inventory, out _); byte[] before = inventory.Capture();
            using (var native = new Backend())
            {
                native.Gold = 17;
                GoodsWalletEngine.WarmCommitPath(); GoodsWalletEngine.WarmCommitPath();
                Assert.AreEqual(17, native.Gold); Assert.AreEqual(0, native.Resolves);
                CollectionAssert.AreEqual(before, inventory.Capture());
            }
        }
        [TestMethod]
        public void RealPackedGoldFieldSupportsZeroAndSignedReadsSeparatelyFromPointerValidity()
        {
            Assert.IsTrue(GoodsWalletNative.ValidateLayout());
            using (var native = new Backend())
            {
                var engine = new GoodsWalletEngine(native);
                lock (native.EngineMonitor)
                {
                    Assert.IsTrue(engine.TryRead(1, out int zero, out _)); Assert.AreEqual(0, zero);
                    native.Gold = -7; Assert.IsTrue(engine.TryRead(1, out int debt, out _)); Assert.AreEqual(-7, debt);
                    native.Missing = true; Assert.IsFalse(engine.TryRead(1, out _, out _));
                    Assert.IsFalse(engine.TryRead(0, out _, out _));
                }
            }
        }
        [TestMethod]
        public void GoldAndVirtualStockApplyOnceAndChangedPriceCannotReplay()
        {
            var c = Setup(out var inventory, out var good);
            using (var native = new Backend())
            {
                native.Gold = 20; var engine = new GoodsWalletEngine(native); var lines = new[] { new GoodsChange(good, 1, 2) };
                lock (native.EngineMonitor)
                {
                    var result = engine.Exchange("owner", c, 1, lines, 1, -5);
                    Assert.AreEqual(GoodsWalletStatus.Applied, result.Status); Assert.AreEqual(20, result.GoldBefore); Assert.AreEqual(15, result.GoldAfter);
                    Assert.AreEqual(15, native.Gold); Assert.IsTrue(c.TryGetBalance("armor", 1, out var balance)); Assert.AreEqual(2, balance.Quantity);
                    native.Gold = 0; int resolves = native.Resolves;
                    Assert.AreEqual(GoodsWalletStatus.AlreadyApplied, engine.Exchange("owner", c, 1, lines, 1, -5).Status);
                    Assert.AreEqual(resolves, native.Resolves, "Replay cannot recheck affordability or touch gold.");
                    Assert.AreEqual(GoodsTransactionStatus.Conflict, engine.Exchange("owner", c, 1, lines, 1, -6).LedgerStatus);
                    Assert.AreEqual(0, native.Gold); Assert.AreEqual(1L, c.LastAcceptedSequence);
                }
            }
        }
        [TestMethod]
        public void NativeLookupFailureAndPreparationReentrancyCannotPublishGoodsOrChargeGold()
        {
            var c = Setup(out var inventory, out var good); byte[] before = inventory.Capture();
            using (var native = new Backend())
            {
                native.Gold = 20; var engine = new GoodsWalletEngine(native); var lines = new[] { new GoodsChange(good, 1, 1) };
                lock (native.EngineMonitor)
                {
                    native.BeforeResolve = () => { Assert.AreEqual(GoodsTransactionStatus.Conflict, c.Apply(2, lines)); throw new InvalidOperationException("lookup"); };
                    Assert.AreEqual(GoodsWalletStatus.Unavailable, engine.Exchange("owner", c, 1, lines, 1, -5).Status);
                    Assert.AreEqual(20, native.Gold); CollectionAssert.AreEqual(before, inventory.Capture());
                    native.BeforeResolve = () => native.Gold = 2;
                    Assert.AreEqual(GoodsWalletStatus.InsufficientGold, engine.Exchange("owner", c, 1, lines, 1, -5).Status);
                    Assert.AreEqual(2, native.Gold); CollectionAssert.AreEqual(before, inventory.Capture());
                    native.BeforeResolve = null; native.Gold = int.MaxValue;
                    Assert.AreEqual(GoodsWalletStatus.GoldOverflow, engine.Exchange("owner", c, 1, lines, 1, 1).Status);
                    CollectionAssert.AreEqual(before, inventory.Capture());
                }
            }
        }
        [TestMethod]
        public void WorldAndInventoryEpochMustMatchAndMidPreparationReplacementRejects()
        {
            var c = Setup(out var inventory, out var good); var lines = new[] { new GoodsChange(good, 1, 1) };
            using (var native = new Backend())
            {
                native.Gold = 20; var engine = new GoodsWalletEngine(native);
                lock (native.EngineMonitor)
                {
                    Assert.AreEqual(GoodsWalletStatus.Applied, engine.Exchange("owner", c, 1, lines, 1, -1).Status);
                    native.CurrentWorld = new object();
                    Assert.AreEqual(GoodsWalletStatus.WorldChanged, engine.Exchange("owner", c, 2, lines, 1, -1).Status);
                    inventory.Reset();
                    native.BeforeResolve = () => native.CurrentWorld = new object();
                    Assert.AreEqual(GoodsWalletStatus.WorldChanged, engine.Exchange("owner", c, 1, lines, 1, -1).Status);
                    Assert.AreEqual(19, native.Gold); Assert.AreEqual(0L, c.LastAcceptedSequence);
                    inventory.Reset(); native.BeforeResolve = null;
                    Assert.AreEqual(GoodsWalletStatus.Applied, engine.Exchange("owner", c, 1, lines, 1, -1).Status);
                }
            }
        }
        [TestMethod]
        public void MissingMonitorUnknownModeAndUnverifiedMultiplayerScopeRefuseBeforeWalletRead()
        {
            var c = Setup(out _, out var good); var lines = new[] { new GoodsChange(good, 1, 1) };
            using (var native = new Backend())
            {
                var engine = new GoodsWalletEngine(native);
                Assert.AreEqual(GoodsWalletStatus.InvalidContext, engine.Exchange("owner", c, 1, lines, 1, 0).Status);
                lock (native.EngineMonitor)
                {
                    native.Allowed = false; Assert.AreEqual(GoodsWalletStatus.InvalidContext, engine.Exchange("owner", c, 1, lines, 1, 0).Status);
                }
                Assert.AreEqual(0, native.Resolves);
            }
            Assert.IsFalse(GoodsWalletNative.AllowsMode(false, false, true, true));
            Assert.IsTrue(GoodsWalletNative.AllowsMode(true, false, false, false));
            Assert.IsFalse(GoodsWalletNative.AllowsMode(true, true, false, false));
            Assert.IsTrue(GoodsWalletNative.AllowsMode(true, true, true, false));
            Assert.IsTrue(GoodsWalletNative.AllowsMode(true, true, false, true));
        }
        [TestMethod]
        public void CompiledCommitSectionContainsOnlyDirectGoldStoreAndKnownPreallocatedPublication()
        {
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(GoodsWalletClient).Assembly.Location))
            {
                var code = assembly.MainModule.GetType("APIShared.Economy.GoodsWalletEngine").Methods.Single(m => m.Name == "PublishGoldAndGoods").Body.Instructions;
                Assert.AreEqual(1, code.Count(i => i.OpCode.Code == Code.Stind_I4));
                var calls = code.Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).FullName).ToArray();
                Assert.AreEqual(1, calls.Length); StringAssert.Contains(calls[0], "PreparedGoodsCommit::Publish()");
                Assert.IsFalse(code.Any(i => i.OpCode.Code == Code.Newobj || i.OpCode.Code == Code.Newarr));
            }
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(CustomGoodsInventory).Assembly.Location))
            {
                var publish = assembly.MainModule.GetType("APIShared.Goods.CustomGoodsInventory/PreparedGoodsCommit").Methods.Single(m => m.Name == "Publish");
                Assert.IsFalse(publish.Body.Instructions.Any(i => i.Operand is MethodReference || i.OpCode.Code == Code.Newobj || i.OpCode.Code == Code.Newarr));
            }
        }
    }
}

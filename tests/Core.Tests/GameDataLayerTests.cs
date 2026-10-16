using APIShared.Events;
using APIShared.GameTables;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APIShared.Core.Tests;

// Production GameDataTableComposer (src/GameTables) over an array-backed trade price table: 25 rows
// (eGoods), buy and sell columns, and the production versioned save registry for the record.
[TestClass]
[DoNotParallelize]
public class GameDataLayerTests
{
    internal sealed class FakePrices : IGameDataTableStore
    {
        internal readonly int[] Defaults = new int[50], Live = new int[50];
        internal int Writes, LiveReads;
        public int Rows => 25;
        public int Columns => 2;
        internal FakePrices()
        {
            for (int good = 1; good < 25; good++) { Defaults[good * 2] = 10 * good + 3; Defaults[good * 2 + 1] = 5 * good + 1; }
            ResetMap();
        }
        internal void ResetMap() => Array.Copy(Defaults, Live, Live.Length);
        public bool TryReadBase(int row, int[] values) { values[0] = Defaults[row * 2]; values[1] = Defaults[row * 2 + 1]; return true; }
        public bool TryReadLive(int row, int[] values) { LiveReads++; values[0] = Live[row * 2]; values[1] = Live[row * 2 + 1]; return true; }
        public void WriteRow(int row, int[] values) { Writes++; Live[row * 2] = values[0]; Live[row * 2 + 1] = values[1]; }
        internal (int Buy, int Sell) this[int good] => (Live[good * 2], Live[good * 2 + 1]);
    }

    private sealed class Peer
    {
        internal readonly FakePrices Prices = new();
        internal readonly GameDataTableComposer Composer;
        internal readonly List<string> Reports = new();
        internal readonly VersionedSaveDataRegistry Saves;
        internal int Activations;
        private Func<VersionedSaveContext, byte[]> save;
        private Action<byte[]> load;
        private readonly GameDataTableComposer[] tables;
        private static readonly string[] Names = { "trade-prices" };

        internal Peer()
        {
            Saves = new VersionedSaveDataRegistry((id, capture, deliver) => { save = capture; load = deliver; return true; }, _ => { }, _ => { });
            Composer = new GameDataTableComposer(GameDataTable.TradeBasePrices, Prices, () =>
            {
                Activations++;
                var record = new VersionedSaveDataRegistration("APIShared_Serp", "APIShared.GameData", 1, 1, Names,
                    writer => GameDataSaveRecord.Capture(writer, tables, Names), loaded => GameDataSaveRecord.Loaded(loaded, tables, Names));
                return Saves.TryRegister(record, out _, out string reason) ? null : reason;
            }, Reports.Add);
            tables = new[] { Composer };
        }

        internal GameDataRegistration Add(string owner, string id, GameDataLayer layer, int order, GameDataLayerHandler handler)
        {
            Assert.IsTrue(Composer.TryAdd(owner, id, layer, order, handler, out GameDataRegistration handle, out string reason), reason);
            return handle;
        }

        internal int StartMission() { Composer.BeginMission(); return Composer.Apply(); }
        internal byte[] SaveGame() => save(new VersionedSaveContext(true, false, "test.sav"));
        internal void LoadGame(byte[] record) { Saves.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav"); load(record); Saves.EndLoad(); }
        internal void LoadMap() { Saves.BeginLoad(VersionedSaveLoadSource.MapToPlay, "map.map"); Saves.EndLoad(); }
    }

    private static GameDataLayerHandler Percent(int percent) => (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Percent(percent); return true; };
    private static GameDataLayerHandler Scale(int numerator, int denominator) => (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Scale(numerator, denominator); return true; };

    [TestMethod]
    public void AFactorOfOneWritesNothing()
    {
        var peer = new Peer();
        peer.Add("ExtraFeatures_Serp", "buy", GameDataLayer.Multiplier, 0, Percent(100));
        peer.Add("ExtraFeatures_Serp", "sell", GameDataLayer.Multiplier, 1, Scale(1000, 1000));
        peer.Prices.Live[7 * 2] = 999; // a value another mod already wrote stays untouched as well
        Assert.AreEqual(0, peer.StartMission());
        Assert.AreEqual(0, peer.Prices.Writes);
        Assert.AreEqual(999, peer.Prices.Live[7 * 2]);
        Assert.AreEqual(1, peer.Reports.Count, "the direct write is reported, not overwritten");
    }

    [TestMethod]
    public void LayerOrderIsDeterministicWhateverTheRegistrationOrder()
    {
        var layers = new (string Owner, string Id, GameDataLayer Layer, int Order, GameDataLayerHandler Handler)[]
        {
            ("tweaker", "salt", GameDataLayer.Override, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Set(500); return cell.Row == 3 && cell.Column == 0; }),
            ("extra", "buy x1.5", GameDataLayer.Multiplier, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Percent(150); return cell.Column == 0; }),
            ("another", "sell x1/3", GameDataLayer.Multiplier, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Scale(1, 3); return true; }),
            ("extra", "plus 2", GameDataLayer.Additive, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Add(2); return true; }),
            ("cap", "range", GameDataLayer.Clamp, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Range(1, 400); return true; }),
        };
        var forward = new Peer();
        var backward = new Peer();
        foreach (var layer in layers) forward.Add(layer.Owner, layer.Id, layer.Layer, layer.Order, layer.Handler);
        foreach (var layer in layers.Reverse()) backward.Add(layer.Owner, layer.Id, layer.Layer, layer.Order, layer.Handler);
        forward.StartMission(); backward.StartMission();
        CollectionAssert.AreEqual(forward.Prices.Live, backward.Prices.Live);
        // Good 5: buy base 53: +2 = 55, then owner "another" (x1/3 = 18.33 -> 18) before owner "extra" (x1.5 = 27).
        Assert.AreEqual(27, forward.Prices[5].Buy);
        // Sell base 26: +2 = 28, x1/3 = 9.33 -> 9.
        Assert.AreEqual(9, forward.Prices[5].Sell);
        Assert.AreEqual(400, forward.Prices[3].Buy, "Override 500, then the clamp");
    }

    [TestMethod]
    public void RestoreThenApplyOnReloadNeverCompounds()
    {
        var host = new Peer();
        host.Add("extra", "x1.5", GameDataLayer.Multiplier, 0, Percent(150));
        host.StartMission();
        Assert.AreEqual((80, 39), host.Prices[5]);   // 53 * 1.5 = 79.5 -> 80, 26 * 1.5 = 39
        byte[] saved = host.SaveGame();

        // The save carries the composed table (or the game resets it from defaults): both start from Base.
        foreach (bool tableSaved in new[] { true, false })
        {
            var reloaded = new Peer();
            reloaded.Add("extra", "x1.5", GameDataLayer.Multiplier, 0, Percent(150));
            if (tableSaved) Array.Copy(host.Prices.Live, reloaded.Prices.Live, 50);
            reloaded.LoadGame(saved);
            reloaded.StartMission();
            Assert.AreEqual((80, 39), reloaded.Prices[5], "table saved: " + tableSaved);
            Assert.AreEqual(tableSaved ? 0 : 24, reloaded.Prices.Writes, "table saved: " + tableSaved);
            Assert.AreEqual(0, reloaded.Reports.Count);
        }

        // A changed layer recomposes from Base: x2 gives 106, not 80 x 2.
        var changed = new Peer();
        changed.Add("extra", "x2", GameDataLayer.Multiplier, 0, Percent(200));
        Array.Copy(host.Prices.Live, changed.Prices.Live, 50);
        changed.LoadGame(saved);
        changed.StartMission();
        Assert.AreEqual((106, 52), changed.Prices[5]);

        // Without the record the composed values look foreign: reported and kept, still never compounded.
        var unmarked = new Peer();
        unmarked.Add("extra", "x1.5", GameDataLayer.Multiplier, 0, Percent(150));
        Array.Copy(host.Prices.Live, unmarked.Prices.Live, 50);
        unmarked.LoadMap();
        unmarked.StartMission();
        Assert.AreEqual((80, 39), unmarked.Prices[5]);
        Assert.IsTrue(unmarked.Reports.Count > 0);
    }

    [TestMethod]
    public void DirectWritersAreReportedOncePerMissionAndNotFought()
    {
        var peer = new Peer();
        peer.Add("tweaker", "x2", GameDataLayer.Multiplier, 0, Percent(200));
        peer.StartMission();
        Assert.AreEqual((106, 52), peer.Prices[5]);
        // Another mod writes good 5's buy price directly.
        peer.Prices.Live[5 * 2] = 77;
        Assert.AreEqual(0, peer.Composer.Apply(), "nothing else changed");
        Assert.AreEqual(77, peer.Prices[5].Buy, "the direct write is kept");
        Assert.AreEqual(1, peer.Reports.Count);
        StringAssert.Contains(peer.Reports[0], "row 5 column 0 holds 77");
        // A direct write of the game's own value (ExtraFeatures at factor 1.0) is the table as the game
        // fills it, so the next composition simply applies the layers again.
        peer.Prices.Live[6 * 2] = 63;
        peer.Composer.Apply();
        Assert.AreEqual(126, peer.Prices[6].Buy);
        peer.Composer.Apply();
        Assert.AreEqual(1, peer.Reports.Count, "reported once per mission");
        peer.StartMission();
        Assert.AreEqual(2, peer.Reports.Count, "and again in the next mission");
        Assert.AreEqual(2, peer.Composer.ForeignCells);
    }

    [TestMethod]
    public void RemovingTheLastLayerRestoresTheGameValues()
    {
        var peer = new Peer();
        GameDataRegistration layer = peer.Add("extra", "x2", GameDataLayer.Multiplier, 0, Percent(200));
        peer.StartMission();
        Assert.AreEqual((106, 52), peer.Prices[5]);
        layer.Dispose();
        Assert.AreEqual(24, peer.Composer.Apply());
        CollectionAssert.AreEqual(peer.Prices.Defaults, peer.Prices.Live);
        Assert.AreEqual(0, peer.Reports.Count);
    }

    [TestMethod]
    public void BaseIsTheFirstReadGameValueEvenIfDefaultsChangeLater()
    {
        var peer = new Peer();
        peer.Add("extra", "x2", GameDataLayer.Multiplier, 0, Percent(200));
        peer.StartMission();
        // Another mod changes the default table afterwards; the next map copies it.
        peer.Prices.Defaults[5 * 2] = 1000;
        peer.Prices.ResetMap();
        peer.StartMission();
        Assert.AreEqual(1000, peer.Prices[5].Buy, "a changed default is a direct write: kept and reported");
        Assert.AreEqual(52, peer.Prices[5].Sell);
        Assert.AreEqual(1, peer.Reports.Count);
    }

    [TestMethod]
    public void FailingLayersAreIsolatedAndActivationHappensOnce()
    {
        var reports = new List<string>();
        Action<string, Exception> previous = InterceptionDiagnostics.Error;
        InterceptionDiagnostics.Error = (registration, error) => reports.Add(registration);
        try
        {
            var peer = new Peer();
            Assert.IsFalse(peer.Composer.TryAdd("a", " ", GameDataLayer.Additive, 0, Percent(1), out _, out _));
            Assert.AreEqual(0, peer.Activations);
            peer.Add("a", "throws", GameDataLayer.Additive, 0, (GameDataCell cell, out GameDataContribution c) => throw new InvalidOperationException());
            peer.Add("a", "wrong", GameDataLayer.Additive, 1, Percent(300));
            peer.Add("a", "bad-ratio", GameDataLayer.Multiplier, 0, Scale(1, 0));
            peer.Add("b", "x2", GameDataLayer.Multiplier, 1, Percent(200));
            Assert.AreEqual(1, peer.Activations);
            peer.StartMission();
            Assert.AreEqual((106, 52), peer.Prices[5]);
            Assert.AreEqual(3, reports.Distinct().Count(), "every failing layer is reported");
            Assert.AreEqual(150, peer.Composer.CallbackFailures, "three failing layers on 50 cells");
        }
        finally { InterceptionDiagnostics.Error = previous; }
    }

    [TestMethod]
    public void CellStatusNamesTheWinningOverrideTheLimitAndDirectWrites()
    {
        var peer = new Peer();
        GameDataRegistration multiplier = peer.Add("extra", "buy x1.5", GameDataLayer.Multiplier, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Percent(150); return cell.Column == 0; });
        GameDataRegistration early = peer.Add("first", "salt", GameDataLayer.Override, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Set(500); return cell.Row == 3; });
        GameDataRegistration late = peer.Add("tweaker", "salt", GameDataLayer.Override, 1, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Set(450); return cell.Row == 3 && cell.Column == 0; });
        GameDataRegistration cap = peer.Add("cap", "range", GameDataLayer.Clamp, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Range(1, 400); return true; });
        GameDataCellStatus Status(GameDataRegistration registration, int row, int column)
        {
            Assert.IsTrue(peer.Composer.TryDescribe(registration, row, column, out GameDataCellStatus status, out string reason), reason);
            return status;
        }

        // Available before the first mission, from the game's values. Good 3 buy: x1.5, then two overrides
        // (the later one by order wins), then the clamp.
        GameDataCellStatus salt = Status(multiplier, 3, 0);
        Assert.IsTrue(salt.Contributes && salt.IsOverridden);
        Assert.AreEqual(("tweaker", "salt"), (salt.OverrideOwnerGuid, salt.OverrideRegistrationId));
        Assert.AreEqual(("cap", "range"), (salt.LimitOwnerGuid, salt.LimitRegistrationId));
        Assert.AreEqual(400, salt.ComposedValue);
        Assert.IsTrue(Status(early, 3, 0).IsOverridden, "an override replaced by a later override");
        Assert.IsFalse(Status(late, 3, 0).IsOverridden);
        Assert.IsFalse(Status(cap, 3, 0).IsOverridden, "a clamp limits, it is never replaced");
        GameDataCellStatus sell = Status(early, 3, 1);
        Assert.IsTrue(sell.Contributes && !sell.IsOverridden);
        Assert.AreEqual(("first", "salt"), (sell.OverrideOwnerGuid, sell.OverrideRegistrationId));
        Assert.IsFalse(Status(late, 3, 1).Contributes);
        GameDataCellStatus plain = Status(multiplier, 5, 0);
        Assert.IsTrue(plain.Contributes && !plain.IsOverridden);
        Assert.IsNull(plain.OverrideOwnerGuid);
        Assert.IsNull(plain.LimitOwnerGuid);
        Assert.AreEqual(80, plain.ComposedValue);
        Assert.AreEqual(0, peer.Prices.Writes, "a query writes nothing");

        // The status matches the composition cell by cell, including an Additive layer and a failing one.
        peer.Add("plus", "two", GameDataLayer.Additive, 0, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Add(2); return cell.Row % 2 == 0; });
        peer.Add("broken", "ratio", GameDataLayer.Multiplier, 1, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Scale(1, 0); return cell.Row == 7; });
        peer.StartMission();
        for (int row = 0; row < 25; row++)
            for (int column = 0; column < 2; column++)
                Assert.AreEqual(peer.Prices.Live[row * 2 + column], Status(cap, row, column).EffectiveValue, row + "/" + column);

        // A direct write: overridden for every contributing layer, effective value is the written one, before and
        // after the next composition (which leaves it).
        peer.Prices.Live[5 * 2] = 77;
        Assert.IsTrue(Status(multiplier, 5, 0).HasDirectWrite);
        peer.Composer.Apply();
        GameDataCellStatus written = Status(multiplier, 5, 0);
        Assert.IsTrue(written.HasDirectWrite && written.IsOverridden);
        Assert.AreEqual((80, 77), (written.ComposedValue, written.EffectiveValue));
        Assert.IsTrue(Status(cap, 5, 0).IsOverridden);
        Assert.IsFalse(Status(multiplier, 5, 1).IsOverridden, "a layer that does not contribute is not overridden");
        peer.Prices.ResetMap();
        peer.StartMission();
        Assert.IsFalse(Status(multiplier, 5, 0).HasDirectWrite, "the next mission starts from the game's table");

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 200; round++) peer.Composer.TryDescribe(multiplier, 3, round & 1, out _, out _);
        Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, "a query allocates nothing");

        Assert.IsFalse(peer.Composer.TryDescribe(multiplier, 25, 0, out _, out _));
        Assert.IsFalse(peer.Composer.TryDescribe(multiplier, 0, 2, out _, out _));
        multiplier.Dispose();
        Assert.IsFalse(peer.Composer.TryDescribe(multiplier, 3, 0, out _, out _));
    }

    [TestMethod]
    public void ACompositionAllocatesNothing()
    {
        foreach (int count in new[] { 0, 1, 4 })
        {
            var peer = new Peer();
            if (count == 0) peer.Add("a", "warm", GameDataLayer.Multiplier, 0, Percent(100)).Dispose();
            for (int index = 0; index < count; index++)
                peer.Add("a", "l" + index, (GameDataLayer)(1 + index % 4), index, index % 4 == 0 ? (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Add(1); return true; }
                    : index % 4 == 1 ? Percent(150) : index % 4 == 2 ? (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Set(cell.BaseValue); return cell.Row == 1; }
                    : (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Range(0, 100000); return true; });
            peer.StartMission();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 500; round++) { peer.Prices.ResetMap(); peer.Composer.Apply(); }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, count + " layers allocate");
        }
    }
}

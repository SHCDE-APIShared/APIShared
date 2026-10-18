using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using APIShared.GameTables;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // One phase over all 25 trade base prices: the production composer against today's direct loop
    // (read each default, multiply buy and sell by a double, round, write), both through the same
    // array-backed store calls. Every phase starts from the game's defaults, so each mode does its full
    // work. Desktop CLR on the test host, not Unity Mono; the Script Extender calls are not included.
    [TestClass]
    [DoNotParallelize]
    public sealed class GameDataBenchmarkTests
    {
        public TestContext TestContext { get; set; }
        private const int Phases = 20000, Rounds = 31;

        private sealed class Prices : IGameDataTableStore
        {
            internal readonly int[] Defaults = new int[50], Live = new int[50];
            public int Rows => 25;
            public int Columns => 2;
            internal Prices() { for (int good = 1; good < 25; good++) { Defaults[good * 2] = 10 * good + 3; Defaults[good * 2 + 1] = 5 * good + 1; } }
            internal void ResetMap() => Array.Copy(Defaults, Live, Live.Length);
            public bool TryReadBase(int row, int[] values) { values[0] = Defaults[row * 2]; values[1] = Defaults[row * 2 + 1]; return true; }
            public bool TryReadLive(int row, int[] values) { values[0] = Live[row * 2]; values[1] = Live[row * 2 + 1]; return true; }
            public void WriteRow(int row, int[] values) { Live[row * 2] = values[0]; Live[row * 2 + 1] = values[1]; }
        }

        [TestMethod]
        public void TradePriceMembersResolveAgainstTheInstalledScriptExtender() =>
            Assert.IsNull(TradeBasePriceStore.MemberContract().FindMismatch());

        private static GameDataLayerHandler Percent(int percent) => (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Percent(percent); return true; };

        [TestMethod]
        public void ComposedPhaseAgainstTodaysDirectLoop()
        {
            // Today's loop, as a mod writes it: defaults times a double per good, written back unconditionally.
            var direct = new Prices();
            var row = new int[2];
            double buyFactor = 1.5, sellFactor = 1.5;
            Action directLoop = () =>
            {
                for (int good = 0; good < 25; good++)
                {
                    direct.TryReadBase(good, row);
                    row[0] = row[0] == 0 ? 0 : (int)Math.Round(row[0] * buyFactor, MidpointRounding.AwayFromZero);
                    row[1] = row[1] == 0 ? 0 : (int)Math.Round(row[1] * sellFactor, MidpointRounding.AwayFromZero);
                    direct.WriteRow(good, row);
                }
            };
            (Prices Store, GameDataTableComposer Composer) Composer(params (GameDataLayer Layer, GameDataLayerHandler Handler)[] layers)
            {
                var store = new Prices();
                var composer = new GameDataTableComposer(GameDataTable.TradeBasePrices, store, () => null, null);
                for (int index = 0; index < layers.Length; index++)
                    Assert.IsTrue(composer.TryAdd("bench", "l" + index, layers[index].Layer, index, layers[index].Handler, out _, out string reason), reason);
                return (store, composer);
            }
            var one = Composer((GameDataLayer.Multiplier, Percent(150)));
            var neutral = Composer((GameDataLayer.Multiplier, Percent(100)));
            var four = Composer(
                (GameDataLayer.Additive, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Add(0); return true; }),
                (GameDataLayer.Multiplier, Percent(150)),
                (GameDataLayer.Override, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Set(500); return cell.Row == 3; }),
                (GameDataLayer.Clamp, (GameDataCell cell, out GameDataContribution c) => { c = GameDataContribution.Range(0, 100000); return true; }));
            var modes = new (string Name, Prices Store, Action Phase)[]
            {
                ("direct loop, x1.5 (today)", direct, directLoop),
                ("composer, 1 layer x1.5", one.Store, () => one.Composer.Apply()),
                ("composer, 1 layer x1.0 (writes nothing)", neutral.Store, () => neutral.Composer.Apply()),
                ("composer, 4 layers", four.Store, () => four.Composer.Apply()),
            };
            var samples = modes.Select(_ => new List<double>()).ToArray();
            var bytes = new long[modes.Length];
            var process = Process.GetCurrentProcess(); var priority = process.PriorityClass; var threadPriority = Thread.CurrentThread.Priority;
            try
            {
                process.PriorityClass = ProcessPriorityClass.High; Thread.CurrentThread.Priority = ThreadPriority.Highest;
                for (int round = -2; round < Rounds; round++)
                    for (int index = 0; index < modes.Length; index++)
                    {
                        int mode = (round & 1) == 0 ? index : modes.Length - 1 - index;
                        var (_, store, phase) = modes[mode];
                        long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                        for (int call = 0; call < Phases; call++) { store.ResetMap(); phase(); }
                        long elapsed = Stopwatch.GetTimestamp() - start, allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                        if (round < 0) continue;
                        samples[mode].Add(elapsed * (1e9 / Stopwatch.Frequency) / Phases);
                        bytes[mode] += allocated;
                    }
            }
            finally { process.PriorityClass = priority; Thread.CurrentThread.Priority = threadPriority; }
            for (int mode = 0; mode < modes.Length; mode++)
            {
                var sorted = samples[mode].OrderBy(value => value).ToArray();
                TestContext.WriteLine("GAMEDATA_BENCH mode={0},bytesTotal={1},nsPerPhaseMin={2:F1},nsPerPhaseMedian={3:F1},samples={4}x{5}",
                    modes[mode].Name, bytes[mode], sorted[0], sorted[sorted.Length / 2], Rounds, Phases);
            }
            for (int mode = 0; mode < modes.Length; mode++) Assert.AreEqual(0L, bytes[mode], modes[mode].Name + " allocates.");
            Assert.AreEqual(0, one.Composer.CallbackFailures + neutral.Composer.CallbackFailures + four.Composer.CallbackFailures);
            CollectionAssert.AreEqual(direct.Live, one.Store.Live, "the composed x1.5 equals today's loop");
            CollectionAssert.AreEqual(neutral.Store.Defaults, neutral.Store.Live);
        }
    }
}

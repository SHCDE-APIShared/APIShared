using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Settle-path cost of the production UnitStatComposer against a consumer's own single write, on real
    // GameUnit records (the installed interop layout) in private memory. The store reads and writes the same
    // fields as the production GameUnitHealthStore; only the UnitAccess slot lookup is replaced by an index.
    // Each call goes through an Action<UnitSettledNotification>, the shape UnitSettled invokes.
    // Desktop CLR on the test host, not Unity Mono.
    [TestClass]
    [DoNotParallelize]
    public sealed unsafe class UnitStatBenchmarkTests
    {
        public TestContext TestContext { get; set; }
        private const int Units = 64, Calls = 200000, Rounds = 31;

        private sealed class RecordStore : IUnitHealthStore
        {
            internal readonly GameUnit* Units;
            internal RecordStore(GameUnit* units) { this.Units = units; }
            public bool TryRead(int unitId, out int globalId, out eChimps unitType, out uint current, out uint max)
            {
                GameUnit* unit = Units + (unitId - 1);
                globalId = unchecked((int)unit->r_GlobalId); unitType = unit->r_UnitChimp;
                current = unit->r_CurrentHealth; max = unit->r_MaxHealth;
                return globalId != 0;
            }
            public void Write(int unitId, uint current, uint max, ushort percent, uint healthBarBlocks)
            {
                GameUnit* unit = Units + (unitId - 1);
                unit->r_MaxHealth = max; unit->r_CurrentHealth = current; unit->r_CurrentHealthPercentage = percent; unit->r_HealthBarBlocks = healthBarBlocks;
            }
        }

        [TestMethod]
        public void SettlePathCostAgainstAHandWrittenWrite()
        {
            var units = (GameUnit*)Marshal.AllocHGlobal(sizeof(GameUnit) * Units).ToPointer();
            try
            {
                for (int index = 0; index < sizeof(GameUnit) * Units; index++) ((byte*)units)[index] = 0;
                for (int id = 1; id <= Units; id++) { units[id - 1].r_GlobalId = (uint)(1000 + id); units[id - 1].r_UnitChimp = (eChimps)22; }
                var store = new RecordStore(units);

                // A consumer's own UnitSettled callback today: read, scale by 3/2 keeping the wound share, write.
                Action<UnitSettledNotification> hand = notification =>
                {
                    GameUnit* unit = units + (notification.UnitId - 1);
                    if (unit->r_GlobalId != (uint)notification.GlobalId || unit->r_MaxHealth == 0) return;
                    uint max = unit->r_MaxHealth, target = (uint)Math.Max(1UL, ((ulong)max * 3 + 1) / 2);
                    uint current = unit->r_CurrentHealth == 0 ? 0 : (uint)Math.Max(1UL, Math.Min(target, ((ulong)Math.Min(unit->r_CurrentHealth, max) * target + max / 2) / max));
                    ushort percent = (ushort)Math.Min(100UL, ((ulong)current * 100 + target / 2) / target);
                    unit->r_MaxHealth = target; unit->r_CurrentHealth = current; unit->r_CurrentHealthPercentage = percent; unit->r_HealthBarBlocks = (uint)(percent / 10);
                };
                UnitStatComposer Composer(int modifiers)
                {
                    var composer = new UnitStatComposer(store, () => null);
                    if (modifiers == 0) { Assert.IsTrue(composer.TryAdd("bench", "warm", UnitStat.MaxHealth, UnitStatLayer.Multiplier, 0, Never, out var warm, out _)); warm.Dispose(); }
                    for (int index = 0; index < modifiers; index++)
                    {
                        UnitStatLayer layer = modifiers == 1 ? UnitStatLayer.Multiplier : (UnitStatLayer)(index % 5);
                        Assert.IsTrue(composer.TryAdd("bench", "m" + index, UnitStat.MaxHealth, layer, index, Modifier(layer), out _, out string reason), reason);
                    }
                    return composer;
                }
                var modes = new (string Name, Action<UnitSettledNotification> Settle)[]
                {
                    ("hand-written single write", hand),
                    ("composer, 0 modifiers", Composer(0).OnSettled),
                    ("composer, 1 modifier", Composer(1).OnSettled),
                    ("composer, 8 modifiers", Composer(8).OnSettled),
                };
                var notifications = new UnitSettledNotification[Units];
                for (int id = 1; id <= Units; id++)
                    notifications[id - 1] = new UnitSettledNotification(id, 1000 + id, 2, (eChimps)22, default, UnitSettledCause.Created, 1, 1);
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
                            Action<UnitSettledNotification> settle = modes[mode].Settle;
                            long before = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
                            for (int call = 0; call < Calls; call++)
                            {
                                // Every settlement starts from native 100/80, so each mode writes every time.
                                int slot = call & (Units - 1);
                                units[slot].r_MaxHealth = 100; units[slot].r_CurrentHealth = 80;
                                settle(notifications[slot]);
                            }
                            long elapsed = Stopwatch.GetTimestamp() - start, allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                            if (round < 0) continue;
                            samples[mode].Add(elapsed * (1e9 / Stopwatch.Frequency) / Calls);
                            bytes[mode] += allocated;
                        }
                }
                finally { process.PriorityClass = priority; Thread.CurrentThread.Priority = threadPriority; }
                for (int mode = 0; mode < modes.Length; mode++)
                {
                    var sorted = samples[mode].OrderBy(value => value).ToArray();
                    TestContext.WriteLine("UNIT_STAT_BENCH mode={0},bytesPerCall={1:F2},nsPerCallMin={2:F2},nsPerCallMedian={3:F2},samples={4}x{5}",
                        modes[mode].Name, bytes[mode] / (double)(Rounds * Calls), sorted[0], sorted[sorted.Length / 2], Rounds, Calls);
                    Assert.AreEqual(0L, bytes[mode], modes[mode].Name + " allocates.");
                }
                Assert.AreEqual((150u, 120u), (units[0].r_MaxHealth, units[0].r_CurrentHealth), "the last mode (8 modifiers) wrote its composition");
            }
            finally { Marshal.FreeHGlobal(new IntPtr(units)); }
        }

        private static bool Never(UnitStatSubject unit, out UnitStatContribution contribution) { contribution = default; return false; }

        // Eight modifiers over all five layers; the composition ends at 150 for a native 100.
        private static UnitStatModifier Modifier(UnitStatLayer layer)
        {
            switch (layer)
            {
                case UnitStatLayer.Base: return (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Set(UnitStatLayer.Base, unit.NativeValue); return true; };
                case UnitStatLayer.Additive: return (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Add(0); return unit.PlayerId > 0; };
                case UnitStatLayer.Multiplier: return (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Scale(3, 2); return unit.UnitType == (eChimps)22; };
                case UnitStatLayer.Override: return (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Set(UnitStatLayer.Override, 150); return unit.Cause == UnitSettledCause.Created; };
                default: return (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Range(1, 100000); return true; };
            }
        }
    }
}

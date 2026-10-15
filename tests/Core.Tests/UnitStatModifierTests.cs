using APIShared.Events;
using APIShared.Savegames;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APIShared.Core.Tests;

// Production UnitStatComposer (src/Units/Stats) over an array-backed unit store and the production
// versioned save registry. UnitSettled delivery itself is covered by UnitSettledTrackerTests.
[TestClass]
[DoNotParallelize]
public class UnitStatModifierTests
{
    private sealed class FakeUnits : IUnitHealthStore
    {
        internal readonly UnitHealthState[] Slots = new UnitHealthState[64];
        internal readonly ushort[] Percent = new ushort[64];
        internal readonly uint[] Blocks = new uint[64];
        internal int Reads, Writes;
        public UnitHealthState Read(int unitId) { Reads++; return unitId > 0 && unitId < Slots.Length ? Slots[unitId] : default; }
        public void Write(int unitId, uint current, uint max, ushort percent, uint healthBarBlocks)
        {
            Writes++;
            Slots[unitId].Current = current; Slots[unitId].Max = max; Percent[unitId] = percent; Blocks[unitId] = healthBarBlocks;
        }
        internal void Spawn(int unitId, int globalId, eChimps type, uint current, uint max) =>
            Slots[unitId] = new UnitHealthState { Found = true, GlobalId = globalId, UnitType = type, Current = current, Max = max };
    }

    // One simulated process: composer, unit store and the versioned save registry it registers with.
    private sealed class Peer
    {
        internal readonly FakeUnits Units = new();
        internal readonly UnitStatComposer Composer;
        internal readonly VersionedSaveDataRegistry Saves;
        internal int Activations;
        private Func<VersionedSaveContext, byte[]> save;
        private Action<byte[]> load;

        internal Peer()
        {
            Saves = new VersionedSaveDataRegistry((id, capture, deliver) => { save = capture; load = deliver; return true; }, _ => { }, _ => { });
            Composer = new UnitStatComposer(Units, () =>
            {
                Activations++;
                var record = new VersionedSaveDataRegistration("APIShared_Serp", UnitStatComposer.SaveIdentifier, 1, 1,
                    new[] { UnitStatComposer.MaxHealthEntry }, Composer.Capture, Composer.Loaded);
                return Saves.TryRegister(record, out _, out string reason) ? null : reason;
            });
        }

        internal UnitStatRegistration Add(string owner, string id, UnitStatLayer layer, int order, UnitStatModifier modifier)
        {
            Assert.IsTrue(Composer.TryAdd(owner, id, UnitStat.MaxHealth, layer, order, modifier, out UnitStatRegistration handle, out string reason), reason);
            return handle;
        }

        internal void Settle(int unitId, UnitSettledCause cause = UnitSettledCause.Created, int tick = 100)
        {
            UnitHealthState unit = Units.Slots[unitId];
            Composer.OnSettled(new UnitSettledNotification(unitId, unit.GlobalId, 2, unit.UnitType, default, cause, tick, 1));
        }

        internal byte[] SaveGame() => save(new VersionedSaveContext(true, false, "test.sav"));

        internal void LoadGame(byte[] record)
        {
            Saves.BeginLoad(VersionedSaveLoadSource.SaveGame, "test.sav");
            load(record);
            Saves.EndLoad();
        }

        internal void LoadMap()
        {
            Saves.BeginLoad(VersionedSaveLoadSource.MapToPlay, "map.map");
            Saves.EndLoad();
        }
    }

    private static UnitStatModifier Set(UnitStatLayer layer, int value) => (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Set(layer, value); return true; };
    private static UnitStatModifier Add(int value) => (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Add(value); return true; };
    private static UnitStatModifier Scale(int numerator, int denominator) => (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Scale(numerator, denominator); return true; };
    private static UnitStatModifier Range(int minimum, int maximum) => (UnitStatSubject unit, out UnitStatContribution c) => { c = UnitStatContribution.Range(minimum, maximum); return true; };

    [TestMethod]
    public void LayersComposeInLayerThenOrderOwnerIdIndependentOfRegistrationTime()
    {
        var peer = new Peer();
        peer.Add("b", "clamp", UnitStatLayer.Clamp, 0, Range(1, 1000));
        peer.Add("a", "percent", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        peer.Add("a", "override-later", UnitStatLayer.Override, 5, Set(UnitStatLayer.Override, 999));
        peer.Add("a", "base", UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 200));
        peer.Add("b", "third", UnitStatLayer.Multiplier, 0, Scale(1, 3));
        peer.Add("a", "plus", UnitStatLayer.Additive, 0, Add(7));
        peer.Add("a", "override-first", UnitStatLayer.Override, 1, Set(UnitStatLayer.Override, 5));
        peer.Units.Spawn(1, 501, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        peer.Settle(1);
        // Base 200 (not native 100), +7 = 207, *1.5 = 310.5 -> 311, *1/3 = 103.67 -> 104, overrides 5 then 999, clamp 1..1000.
        Assert.AreEqual(999u, peer.Units.Slots[1].Max);

        var steps = new Peer();
        steps.Add("a", "plus", UnitStatLayer.Additive, 0, Add(7));
        steps.Add("a", "base", UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 200));
        steps.Add("b", "third", UnitStatLayer.Multiplier, 0, Scale(1, 3));
        steps.Add("a", "percent", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        steps.Add("b", "clamp", UnitStatLayer.Clamp, 0, Range(1, 100));
        steps.Units.Spawn(1, 501, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        steps.Settle(1);
        Assert.AreEqual(100u, steps.Units.Slots[1].Max, "owner a's multiplier runs before owner b's at the same order; then the clamp");
        var unclamped = new Peer();
        unclamped.Add("a", "plus", UnitStatLayer.Additive, 0, Add(7));
        unclamped.Add("a", "base", UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 200));
        unclamped.Add("b", "third", UnitStatLayer.Multiplier, 0, Scale(1, 3));
        unclamped.Add("a", "percent", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        unclamped.Units.Spawn(1, 501, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        unclamped.Settle(1);
        Assert.AreEqual(104u, unclamped.Units.Slots[1].Max, "207 * 150/100 = 311 (rounded), then 311 / 3 = 104 (rounded)");
    }

    [TestMethod]
    public void OneWritePerSettlementPreservesTheWoundShareAndSkipsUnchangedValues()
    {
        var peer = new Peer();
        for (int index = 0; index < 8; index++) peer.Add("owner", "m" + index, UnitStatLayer.Multiplier, index, Scale(index == 0 ? 3 : 1, index == 0 ? 2 : 1));
        peer.Units.Spawn(1, 11, eChimps.CHIMP_TYPE_MACEMAN, 30, 100);
        peer.Settle(1);
        Assert.AreEqual(1, peer.Units.Writes, "eight modifiers, one composed write");
        Assert.AreEqual((45u, 150u, (ushort)30, 3u), (peer.Units.Slots[1].Current, peer.Units.Slots[1].Max, peer.Units.Percent[1], peer.Units.Blocks[1]));

        var shrink = new Peer();
        shrink.Add("owner", "tenth", UnitStatLayer.Multiplier, 0, Scale(1, 10));
        shrink.Units.Spawn(1, 12, eChimps.CHIMP_TYPE_MACEMAN, 1, 100);
        shrink.Units.Spawn(2, 13, eChimps.CHIMP_TYPE_MACEMAN, 0, 100);
        shrink.Settle(1); shrink.Settle(2);
        Assert.AreEqual((1u, 10u), (shrink.Units.Slots[1].Current, shrink.Units.Slots[1].Max), "a living unit keeps at least 1");
        Assert.AreEqual((0u, 10u), (shrink.Units.Slots[2].Current, shrink.Units.Slots[2].Max), "a unit at 0 is never revived");

        var unchanged = new Peer();
        unchanged.Add("owner", "none", UnitStatLayer.Multiplier, 0, (UnitStatSubject unit, out UnitStatContribution c) => { c = default; return false; });
        unchanged.Add("owner", "one", UnitStatLayer.Multiplier, 1, Scale(1, 1));
        unchanged.Units.Spawn(1, 14, eChimps.CHIMP_TYPE_MACEMAN, 50, 100);
        unchanged.Settle(1);
        Assert.AreEqual(0, unchanged.Units.Writes, "a result equal to the live value writes nothing");
        Assert.IsTrue(unchanged.Composer.TryGetRecord(1, out var record) && record.NativeMax == 100 && record.AppliedMax == 100);
    }

    [TestMethod]
    public void FailingOrMismatchedModifiersAreIsolatedAndCounted()
    {
        var reports = new List<string>();
        Action<string, Exception> previous = InterceptionDiagnostics.Error;
        InterceptionDiagnostics.Error = (registration, error) => reports.Add(registration);
        try
        {
            var peer = new Peer();
            peer.Add("a", "throws", UnitStatLayer.Multiplier, 0, (UnitStatSubject unit, out UnitStatContribution c) => throw new InvalidOperationException("boom"));
            peer.Add("a", "wrong-layer", UnitStatLayer.Multiplier, 1, Add(1000));
            peer.Add("a", "zero-denominator", UnitStatLayer.Multiplier, 2, Scale(2, 0));
            peer.Add("a", "inverted-range", UnitStatLayer.Clamp, 0, Range(10, 5));
            peer.Add("b", "double", UnitStatLayer.Multiplier, 9, Scale(2, 1));
            peer.Units.Spawn(1, 21, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
            peer.Settle(1);
            Assert.AreEqual(200u, peer.Units.Slots[1].Max, "only the valid modifier contributes");
            Assert.AreEqual(4, peer.Composer.CallbackFailures);
            Assert.AreEqual(4, reports.Count);
        }
        finally { InterceptionDiagnostics.Error = previous; }
    }

    [TestMethod]
    public void NothingIsActivatedOrReadWithoutModifiers()
    {
        var peer = new Peer();
        peer.Units.Spawn(1, 31, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        peer.Settle(1);
        Assert.AreEqual((0, 0, 0), (peer.Activations, peer.Units.Reads, peer.Units.Writes));
        Assert.IsFalse(peer.Composer.TryAdd("a", " ", UnitStat.MaxHealth, UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 1), out _, out _));
        Assert.IsFalse(peer.Composer.TryAdd("a", "x", (UnitStat)7, UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 1), out _, out _));
        Assert.AreEqual(0, peer.Activations, "invalid registrations never activate");
        UnitStatRegistration handle = peer.Add("a", "x", UnitStatLayer.Multiplier, 0, Scale(2, 1));
        Assert.IsFalse(peer.Composer.TryAdd("a", "x", UnitStat.MaxHealth, UnitStatLayer.Base, 0, Set(UnitStatLayer.Base, 1), out _, out string reason));
        StringAssert.Contains(reason, "already registered");
        handle.Dispose(); handle.Dispose();
        peer.Settle(1);
        Assert.AreEqual((1, 0, 0), (peer.Activations, peer.Units.Reads, peer.Units.Writes), "after the last disposal a settlement reads nothing");
        peer.Add("a", "x", UnitStatLayer.Multiplier, 0, Scale(2, 1));
        Assert.AreEqual(1, peer.Activations, "activation happens once per process");
    }

    [TestMethod]
    public void SettledUnitsKeepTheirValuesAndLaterSettlementsUseTheCurrentModifiers()
    {
        var peer = new Peer();
        UnitStatRegistration half = peer.Add("a", "x1.5", UnitStatLayer.Multiplier, 0, Scale(3, 2));
        peer.Units.Spawn(1, 41, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        peer.Settle(1);
        Assert.AreEqual(150u, peer.Units.Slots[1].Max);
        half.Dispose();
        peer.Add("a", "x2", UnitStatLayer.Multiplier, 0, Scale(2, 1));
        Assert.AreEqual(150u, peer.Units.Slots[1].Max, "registration changes do not touch settled units");
        peer.Settle(1);
        Assert.AreEqual(200u, peer.Units.Slots[1].Max, "a second report recomposes from the recorded native 100, never from 150");
        peer.Settle(1);
        Assert.AreEqual(200u, peer.Units.Slots[1].Max, "and is idempotent");

        // Recruitment re-initializes the slot for its new type: the new live value is the new native value.
        peer.Units.Slots[1].UnitType = eChimps.CHIMP_TYPE_SWORDSMAN; peer.Units.Slots[1].Max = 300; peer.Units.Slots[1].Current = 300;
        peer.Settle(1, UnitSettledCause.EuropeanBarracks);
        Assert.AreEqual(600u, peer.Units.Slots[1].Max);
        // A new unit in the same slot (another GlobalId) starts from its own value.
        peer.Units.Spawn(1, 42, eChimps.CHIMP_TYPE_SWORDSMAN, 600, 600);
        peer.Settle(1);
        Assert.AreEqual(1200u, peer.Units.Slots[1].Max);
    }

    [TestMethod]
    public void SaveReloadAndSettleAgainNeverDoubleApplies()
    {
        var host = new Peer();
        host.Add("ExtraFeatures_Serp", "lord", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        host.Units.Spawn(5, 77, eChimps.CHIMP_TYPE_SWORDSMAN, 80, 100);
        host.Settle(5);
        Assert.AreEqual((120u, 150u), (host.Units.Slots[5].Current, host.Units.Slots[5].Max));
        byte[] saved = host.SaveGame();

        // A new process loads the save: the units come back with their written values.
        var reloaded = new Peer();
        reloaded.Add("ExtraFeatures_Serp", "lord", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        reloaded.Units.Slots[5] = host.Units.Slots[5];
        reloaded.LoadGame(saved);
        Assert.IsTrue(reloaded.Composer.TryGetRecord(5, out var record));
        Assert.AreEqual((77, 100u, 150u), (record.GlobalId, record.NativeMax, record.AppliedMax));
        reloaded.Settle(5);
        Assert.AreEqual((120u, 150u), (reloaded.Units.Slots[5].Current, reloaded.Units.Slots[5].Max), "the marker prevents a second x1.5");
        Assert.AreEqual(0, reloaded.Units.Writes);
        CollectionAssert.AreEqual(saved, reloaded.SaveGame(), "the same markers give the same record");

        // Control: the same reload without the record compounds, which is what the marker exists for.
        var unmarked = new Peer();
        unmarked.Add("ExtraFeatures_Serp", "lord", UnitStatLayer.Multiplier, 0, Scale(150, 100));
        unmarked.Units.Slots[5] = host.Units.Slots[5];
        unmarked.LoadMap();
        unmarked.Settle(5);
        Assert.AreEqual(225u, unmarked.Units.Slots[5].Max);
    }

    [TestMethod]
    public void SettleSaveReloadSettleThroughTheProductionSettleTracker()
    {
        // Two processes, each with the production UnitSettledTracker delivering to the composer at int.MinValue.
        (Peer Peer, UnitSettledTracker Tracker, FakeUnitSlots Slots) Process()
        {
            var peer = new Peer();
            peer.Add("ExtraFeatures_Serp", "lord", UnitStatLayer.Multiplier, 0, Scale(150, 100));
            var slots = new FakeUnitSlots();
            var tracker = new UnitSettledTracker(slots, null);
            Assert.IsTrue(tracker.TryRegister("APIShared_Serp", "UnitStatModifiers", peer.Composer.OnSettled, int.MinValue, out string reason), reason);
            return (peer, tracker, slots);
        }
        var first = Process();
        first.Peer.Units.Spawn(9, 900, eChimps.CHIMP_TYPE_SWORDSMAN, 60, 100);
        first.Slots.Alive(9, 900, 2, eChimps.CHIMP_TYPE_SWORDSMAN);
        first.Tracker.Enqueue(9, UnitSettledCause.Created);
        first.Tracker.Tick(1);
        Assert.AreEqual((90u, 150u), (first.Peer.Units.Slots[9].Current, first.Peer.Units.Slots[9].Max));
        byte[] saved = first.Peer.SaveGame();

        var second = Process();
        second.Peer.Units.Slots[9] = first.Peer.Units.Slots[9];
        second.Slots.Alive(9, 900, 2, eChimps.CHIMP_TYPE_SWORDSMAN);
        second.Peer.LoadGame(saved);
        // The game reports the restored unit again (for example through its spawn function).
        second.Tracker.Enqueue(9, UnitSettledCause.Created);
        second.Tracker.Tick(2);
        Assert.AreEqual((90u, 150u), (second.Peer.Units.Slots[9].Current, second.Peer.Units.Slots[9].Max), "settled, saved, reloaded and settled again: still x1.5");
        Assert.AreEqual(0, second.Peer.Units.Writes);
    }

    [TestMethod]
    public void EveryLoadReplacesTheMarkers()
    {
        var peer = new Peer();
        peer.Add("a", "x2", UnitStatLayer.Multiplier, 0, Scale(2, 1));
        peer.Units.Spawn(3, 61, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        peer.Settle(3);
        Assert.IsTrue(peer.Composer.TryGetRecord(3, out _));
        peer.LoadMap();
        Assert.IsFalse(peer.Composer.TryGetRecord(3, out _), "a map load starts a mission without markers");
        peer.Units.Spawn(3, 61, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
        peer.Settle(3);
        byte[] damaged = peer.SaveGame();
        damaged[damaged.Length - 1] ^= 0xFF;
        peer.LoadGame(damaged);
        Assert.IsFalse(peer.Composer.TryGetRecord(3, out _), "a damaged record restores nothing");
    }

    [TestMethod]
    public void PeersWithTheSameModifiersWriteTheSameValuesWhateverTheRegistrationOrder()
    {
        var first = new Peer();
        var second = new Peer();
        var modifiers = new (string Owner, string Id, UnitStatLayer Layer, int Order, UnitStatModifier Modifier)[]
        {
            ("tweaker", "hp", UnitStatLayer.Multiplier, 0, Scale(125, 100)),
            ("byzantine", "profile", UnitStatLayer.Base, 0, (UnitStatSubject unit, out UnitStatContribution c) =>
                { c = UnitStatContribution.Set(UnitStatLayer.Base, unit.NativeValue * 3 / 10); return unit.UnitType == eChimps.CHIMP_TYPE_MACEMAN; }),
            ("extra", "lord", UnitStatLayer.Multiplier, 10, (UnitStatSubject unit, out UnitStatContribution c) =>
                { c = UnitStatContribution.Percent(unit.PlayerId == 2 ? 300 : 100); return true; }),
            ("cap", "max", UnitStatLayer.Clamp, 0, Range(1, 50000)),
        };
        foreach (var m in modifiers) first.Add(m.Owner, m.Id, m.Layer, m.Order, m.Modifier);
        foreach (var m in modifiers.Reverse()) second.Add(m.Owner, m.Id, m.Layer, m.Order, m.Modifier);
        foreach (Peer peer in new[] { first, second })
            for (int unit = 1; unit < 40; unit++)
            {
                peer.Units.Spawn(unit, 1000 + unit, unit % 3 == 0 ? eChimps.CHIMP_TYPE_MACEMAN : eChimps.CHIMP_TYPE_ARCHER, (uint)(unit * 37 % 500 + 1), 500);
                peer.Settle(unit, tick: unit);
            }
        for (int unit = 1; unit < 40; unit++)
            Assert.AreEqual((first.Units.Slots[unit].Current, first.Units.Slots[unit].Max), (second.Units.Slots[unit].Current, second.Units.Slots[unit].Max), "unit " + unit);
        CollectionAssert.AreEqual(first.SaveGame(), second.SaveGame());
    }

    [TestMethod]
    public void RegistrationChurnDuringSettlementsIsSafe()
    {
        var peer = new Peer();
        peer.Add("steady", "x2", UnitStatLayer.Multiplier, 0, Scale(2, 1));
        int stop = 0; Exception failure = null;
        var churn = new Thread(() =>
        {
            try
            {
                for (int round = 0; round < 3000; round++)
                {
                    Assert.IsTrue(peer.Composer.TryAdd("churn", "r" + (round % 4), UnitStat.MaxHealth, UnitStatLayer.Clamp, round % 3,
                        Range(1, int.MaxValue), out UnitStatRegistration handle, out string reason), reason);
                    handle.Dispose();
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref stop, 1); }
        });
        churn.Start();
        int settled = 0;
        while (Volatile.Read(ref stop) == 0 || settled < 1000)
        {
            int unit = 1 + settled % 60;
            peer.Units.Spawn(unit, 5000 + settled, eChimps.CHIMP_TYPE_ARCHER, 100, 100);
            peer.Settle(unit);
            Assert.AreEqual(200u, peer.Units.Slots[unit].Max);
            settled++;
        }
        churn.Join();
        Assert.IsNull(failure, failure?.ToString());
        Assert.AreEqual(0, peer.Composer.CallbackFailures);
        Assert.AreEqual(1, peer.Composer.ActiveCount);
    }

    [TestMethod]
    public void SettlementAllocatesNothingWithZeroOneOrEightModifiers()
    {
        foreach (int count in new[] { 0, 1, 8 })
        {
            var peer = new Peer();
            if (count == 0)
            {
                peer.Add("a", "warm", UnitStatLayer.Multiplier, 0, Scale(1, 1)).Dispose();
            }
            for (int index = 0; index < count; index++)
                peer.Add("a", "m" + index, (UnitStatLayer)(index % 5), index, index % 5 == 0 ? Set(UnitStatLayer.Base, 100) :
                    index % 5 == 1 ? Add(1) : index % 5 == 2 ? Scale(3, 2) : index % 5 == 3 ? Set(UnitStatLayer.Override, 150) : Range(1, 1000));
            for (int unit = 1; unit < 50; unit++) { peer.Units.Spawn(unit, unit, eChimps.CHIMP_TYPE_ARCHER, 100, 100); peer.Settle(unit); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 2000; round++)
            {
                int unit = 1 + round % 49;
                peer.Units.Slots[unit].Max = 100; peer.Units.Slots[unit].Current = 100;
                peer.Settle(unit);
            }
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before, count + " modifiers allocate");
        }
    }
}

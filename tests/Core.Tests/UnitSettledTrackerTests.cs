using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APIShared.Core.Tests;

[TestClass]
public class UnitSettledTrackerTests
{
    private static readonly Action<UnitSettledNotification> Ignore = _ => { };

    [TestMethod]
    public void ScriptExtenderDemandStartsWithTheFirstValidRegistrationOnly()
    {
        int activations = 0;
        var slots = new FakeUnitSlots();
        var tracker = new UnitSettledTracker(slots, () => activations++);
        for (int tick = 0; tick < 100; tick++) tracker.Tick(tick);
        Assert.IsFalse(tracker.TryRegister(" ", "id", Ignore, 0, out string reason));
        Assert.IsFalse(string.IsNullOrEmpty(reason));
        Assert.IsFalse(tracker.TryRegister("owner", "id", null, 0, out _));
        Assert.AreEqual(0, activations, "Dormant ticks and rejected registrations create no SE demand.");
        Assert.AreEqual(0, slots.Reads);

        Assert.IsTrue(tracker.TryRegister("owner", "a", Ignore, 0, out reason));
        Assert.AreEqual(string.Empty, reason);
        Assert.IsTrue(tracker.TryRegister("owner", "b", Ignore, 0, out _));
        Assert.IsFalse(tracker.TryRegister("owner", "a", Ignore, 5, out reason), "Duplicate owner-local IDs are rejected.");
        Assert.IsTrue(tracker.TryRegister("other", "a", Ignore, 0, out _), "The same ID under another owner is independent.");
        Assert.AreEqual(1, activations);
        Assert.IsTrue(tracker.IsActivated);
    }

    [TestMethod]
    public void UnavailableScriptExtenderRejectsRegistrationAndALaterRegistrationRetries()
    {
        int attempts = 0;
        var tracker = new UnitSettledTracker(new FakeUnitSlots(), () => {
            if (++attempts == 1) throw new InvalidOperationException("time API missing");
        });
        Assert.IsFalse(tracker.TryRegister("owner", "a", Ignore, 0, out string reason));
        StringAssert.Contains(reason, "time API missing");
        Assert.IsFalse(tracker.IsActivated);
        Assert.IsTrue(tracker.TryRegister("owner", "a", Ignore, 0, out _), "The rejected ID was not retained.");
        Assert.AreEqual(2, attempts);
    }

    [TestMethod]
    public void CreatedUnitSettlesOnceWhenAliveWithAGlobalId()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out var received);
        slots.Transient(5, 0, 1, eChimps.CHIMP_TYPE_SWORDSMAN); // NeedsInit at OnUnitCreate Post
        tracker.Enqueue(5, UnitSettledCause.Created);
        tracker.Tick(10);
        Assert.AreEqual(0, received.Count);
        slots.Alive(5, 77, 1, eChimps.CHIMP_TYPE_SWORDSMAN);
        tracker.Tick(11);
        tracker.Tick(12);
        Assert.AreEqual(1, received.Count);
        UnitSettledNotification unit = received[0];
        Assert.AreEqual(5, unit.UnitId);
        Assert.AreEqual(77, unit.GlobalId);
        Assert.AreEqual(1, unit.PlayerId);
        Assert.AreEqual(eChimps.CHIMP_TYPE_SWORDSMAN, unit.UnitType);
        Assert.AreEqual(eChimps.CHIMP_TYPE_NULL, unit.PreviousUnitType);
        Assert.AreEqual(UnitSettledCause.Created, unit.Cause);
        Assert.AreEqual(11, unit.SimulationTick);
        Assert.AreEqual(2, unit.TicksObserved);
        Assert.IsFalse(unit.IsRecruitment);
        Assert.AreEqual(0, tracker.PendingUnits);
    }

    [TestMethod]
    public void RecruitSettlesWhenItsSlotHasTakenTheNewType()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out var received);
        slots.Alive(6, 40, 2, eChimps.CHIMP_TYPE_PEASANT);
        tracker.Enqueue(6, UnitSettledCause.EuropeanBarracks);
        // Vanilla keeps the peasant for 33 updates, then one transient update, then the new type.
        for (int tick = 1; tick <= 33; tick++) tracker.Tick(tick);
        slots.Transient(6, 40, 2, eChimps.CHIMP_TYPE_PEASANT);
        tracker.Tick(34);
        Assert.AreEqual(0, received.Count);
        slots.Alive(6, 40, 2, eChimps.CHIMP_TYPE_MACEMAN);
        tracker.Tick(35);
        Assert.AreEqual(1, received.Count);
        Assert.AreEqual(eChimps.CHIMP_TYPE_MACEMAN, received[0].UnitType);
        Assert.AreEqual(eChimps.CHIMP_TYPE_PEASANT, received[0].PreviousUnitType);
        Assert.AreEqual(35, received[0].TicksObserved);
        Assert.IsTrue(received[0].IsRecruitment);
    }

    [TestMethod]
    public void UnitsSettleInTriggerOrderAndReachRegistrationsInOrderOwnerIdOrder()
    {
        var slots = new FakeUnitSlots();
        var tracker = new UnitSettledTracker(slots, null);
        var calls = new List<string>();
        tracker.TryRegister("z", "1", u => calls.Add(u.UnitId + ":z1"), 0, out _);
        tracker.TryRegister("a", "2", u => calls.Add(u.UnitId + ":a2"), 0, out _);
        tracker.TryRegister("a", "1", u => calls.Add(u.UnitId + ":a1"), 0, out _);
        tracker.TryRegister("m", "0", u => calls.Add(u.UnitId + ":m0"), -1, out _);
        foreach (int id in new[] { 3, 1, 2 })
        {
            slots.Alive(id, 100 + id, 1, eChimps.CHIMP_TYPE_ARCHER);
            tracker.Enqueue(id, UnitSettledCause.Created);
        }
        tracker.Tick(1);
        CollectionAssert.AreEqual(new[] {
            "3:m0", "3:a1", "3:a2", "3:z1", "1:m0", "1:a1", "1:a2", "1:z1", "2:m0", "2:a1", "2:a2", "2:z1",
        }, calls);
    }

    [TestMethod]
    public void CallbackFailuresAreIsolatedCountedAndLoggedAtExponentialCounts()
    {
        var slots = new FakeUnitSlots();
        var tracker = new UnitSettledTracker(slots, null);
        var logged = new List<string>();
        tracker.SetErrorSink((registration, error) => logged.Add(registration));
        int delivered = 0;
        tracker.TryRegister("a", "throws", _ => throw new InvalidOperationException(), 0, out _);
        tracker.TryRegister("b", "counts", _ => delivered++, 0, out _);
        for (int id = 1; id <= 5; id++)
        {
            slots.Alive(id, id, 1, eChimps.CHIMP_TYPE_ARCHER);
            tracker.Enqueue(id, UnitSettledCause.Created);
        }
        tracker.Tick(1);
        Assert.AreEqual(5, delivered);
        Assert.AreEqual(5L, tracker.CallbackFailures);
        Assert.AreEqual(3, logged.Count, "Failures 1, 2 and 4 are logged.");
        StringAssert.StartsWith(logged[0], "a/throws");
    }

    [TestMethod]
    public void RegistrationsAndTriggersFromCallbacksJoinTheNextTick()
    {
        var slots = new FakeUnitSlots();
        var tracker = new UnitSettledTracker(slots, null);
        var late = new List<int>();
        var early = new List<int>();
        tracker.TryRegister("early", "1", u => {
            early.Add(u.UnitId);
            if (u.UnitId == 1)
            {
                tracker.TryRegister("late", "1", v => late.Add(v.UnitId), -10, out _);
                slots.Alive(9, 90, 1, eChimps.CHIMP_TYPE_ARCHER);
                tracker.Enqueue(9, UnitSettledCause.Created); // for example a callback that spawns a unit
            }
        }, 0, out _);
        slots.Alive(1, 10, 1, eChimps.CHIMP_TYPE_ARCHER);
        slots.Alive(2, 20, 1, eChimps.CHIMP_TYPE_ARCHER);
        tracker.Enqueue(1, UnitSettledCause.Created);
        tracker.Enqueue(2, UnitSettledCause.Created);
        tracker.Tick(1);
        CollectionAssert.AreEqual(new[] { 1, 2 }, early);
        Assert.AreEqual(0, late.Count, "A registration made during notification starts with the next tick.");
        Assert.AreEqual(1, tracker.PendingUnits);
        tracker.Tick(2);
        CollectionAssert.AreEqual(new[] { 1, 2, 9 }, early);
        CollectionAssert.AreEqual(new[] { 9 }, late);
    }

    [TestMethod]
    public void LostIdentityDeathTimeoutAndWorldChangesDropWithoutNotification()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out var received);
        slots.Dead(1);
        tracker.Enqueue(1, UnitSettledCause.Created);           // dead at the trigger
        tracker.Enqueue(2, UnitSettledCause.Created);           // unresolved at the trigger
        tracker.Enqueue(0, UnitSettledCause.Created);           // not a unit ID: ignored, not counted
        Assert.AreEqual(2L, tracker.DroppedUnits);
        Assert.AreEqual(0, tracker.PendingUnits);

        slots.Alive(3, 30, 1, eChimps.CHIMP_TYPE_PEASANT); tracker.Enqueue(3, UnitSettledCause.MercenaryOutpost);
        slots.Alive(4, 40, 1, eChimps.CHIMP_TYPE_PEASANT); tracker.Enqueue(4, UnitSettledCause.MercenaryOutpost);
        slots.Alive(5, 50, 1, eChimps.CHIMP_TYPE_PEASANT); tracker.Enqueue(5, UnitSettledCause.MercenaryOutpost);
        slots.Alive(6, 60, 1, eChimps.CHIMP_TYPE_PEASANT); tracker.Enqueue(6, UnitSettledCause.Worker);
        slots.Dead(3);                                          // killed while training
        slots.Alive(4, 40, 2, eChimps.CHIMP_TYPE_ARCHER);       // another owner
        slots.Alive(5, 51, 1, eChimps.CHIMP_TYPE_ARCHER);       // slot reused by another unit
        tracker.Tick(1);
        Assert.AreEqual(5L, tracker.DroppedUnits);
        Assert.AreEqual(1, tracker.PendingUnits);

        // A transition whose type never changes waits for the documented maximum, then drops.
        for (int tick = 2; tick < UnitSettledTracker.MaximumObservedTicks; tick++) tracker.Tick(tick);
        Assert.AreEqual(1, tracker.PendingUnits);
        tracker.Tick(UnitSettledTracker.MaximumObservedTicks);
        Assert.AreEqual(0, tracker.PendingUnits);
        Assert.AreEqual(6L, tracker.DroppedUnits);

        slots.Alive(7, 70, 1, eChimps.CHIMP_TYPE_PEASANT);
        tracker.Enqueue(7, UnitSettledCause.Disband);
        tracker.Enqueue(7, UnitSettledCause.Worker);
        tracker.Discard();                                      // OnUnloadMap/OnLoadMap/OnLoadSave Pre
        slots.Alive(7, 70, 1, eChimps.CHIMP_TYPE_WOODCUTTER);
        tracker.Tick(5000);
        Assert.AreEqual(8L, tracker.DroppedUnits);
        Assert.AreEqual(0, received.Count);
    }

    [TestMethod]
    public void PendingQueueIsBounded()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out _);
        slots.Transient(1, 0, 1, eChimps.CHIMP_TYPE_ARCHER);
        for (int i = 0; i < UnitSettledTracker.MaximumPendingUnits + 3; i++) tracker.Enqueue(1, UnitSettledCause.Created);
        Assert.AreEqual(UnitSettledTracker.MaximumPendingUnits, tracker.PendingUnits);
        Assert.AreEqual(3L, tracker.DroppedUnits);
    }

    [TestMethod]
    public void IdleTicksWithRegistrationsAllocateNothingAfterWarmup()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out _);
        for (int i = 0; i < 5; i++) MeasureTicks(tracker, 0, 50000);
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++) best = Math.Min(best, MeasureTicks(tracker, 0, 50000));
        Assert.AreEqual(0L, best);
        Assert.AreEqual(0, slots.Reads, "An idle tick reads no unit.");
    }

    [TestMethod]
    public void WaitingUnitsAllocateNothingPerTickAfterWarmup()
    {
        var slots = new FakeUnitSlots();
        var tracker = Registered(slots, out var received);
        for (int id = 1; id <= 64; id++)
        {
            slots.Alive(id, id, 1, eChimps.CHIMP_TYPE_PEASANT);
            tracker.Enqueue(id, UnitSettledCause.EuropeanBarracks);
        }
        for (int i = 0; i < 5; i++) MeasureTicks(tracker, 1000 + i * 100, 100);
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++) best = Math.Min(best, MeasureTicks(tracker, 2000 + i * 100, 100));
        Assert.AreEqual(0L, best);
        Assert.AreEqual(64, tracker.PendingUnits);
        Assert.AreEqual(0, received.Count);
    }

    [TestMethod]
    public void TriggerSettlementAndDispatchAllocateNothingAfterWarmup()
    {
        var slots = new FakeUnitSlots();
        var tracker = new UnitSettledTracker(slots, null);
        long sum = 0;
        for (int index = 0; index < 3; index++)
            tracker.TryRegister("owner", index.ToString(), u => sum += u.GlobalId, 0, out _);
        for (int id = 1; id <= 32; id++) slots.Alive(id, id, 1, eChimps.CHIMP_TYPE_ARCHER);
        for (int i = 0; i < 5; i++) MeasureCycles(tracker, 200);
        long best = long.MaxValue;
        for (int i = 0; i < 5; i++) best = Math.Min(best, MeasureCycles(tracker, 200));
        Assert.AreEqual(0L, best);
        Assert.IsTrue(sum > 0);
    }

    private static UnitSettledTracker Registered(FakeUnitSlots slots, out List<UnitSettledNotification> received)
    {
        var tracker = new UnitSettledTracker(slots, null);
        var list = new List<UnitSettledNotification>();
        Assert.IsTrue(tracker.TryRegister("owner", "observer", list.Add, 0, out _));
        received = list;
        return tracker;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureTicks(UnitSettledTracker tracker, int firstTick, int count)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int tick = firstTick; tick < firstTick + count; tick++) tracker.Tick(tick);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // One cycle: 32 created units are queued (SE OnUnitCreate Post) and settle on the next tick.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static long MeasureCycles(UnitSettledTracker tracker, int cycles)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            for (int id = 1; id <= 32; id++) tracker.Enqueue(id, UnitSettledCause.Created);
            tracker.Tick(cycle);
        }
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}

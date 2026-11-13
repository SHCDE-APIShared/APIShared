using APIShared;
using SHCDESE.API;
using SHCDESE.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace APIShared.Core.Tests;

[TestClass, DoNotParallelize]
public unsafe class BuildingAccessTests
{
    [TestInitialize]
    public void ResetManager() { GameBuildingManagerAPI.Current = null; GameBuildingManagerAPI.InstanceReads = 0; }
    [TestCleanup]
    public void ReleaseManager() { GameBuildingManagerAPI.Current = null; }

    [TestMethod]
    public void MissingIdsNeverAccessTheManagerAndFailureAlwaysClearsThePointer()
    {
        foreach (int id in new[] { int.MinValue, -1, 0 })
        {
            Assert.IsFalse(BuildingAccess.TryGetById(id, out var pointer, out var reason));
            Assert.IsTrue(pointer == null);
            Assert.AreEqual(BuildingLookupFailure.InvalidId, reason);
            Assert.AreEqual(0, GameBuildingManagerAPI.InstanceReads);
            Assert.IsFalse(BuildingAccess.TryGetById(null, id, out pointer, out reason));
            Assert.IsTrue(pointer == null);
            Assert.AreEqual(BuildingLookupFailure.InvalidId, reason);
        }
        Assert.IsFalse(BuildingAccess.TryGetById(1, out var missing, out var missingReason));
        Assert.IsTrue(missing == null);
        Assert.AreEqual(BuildingLookupFailure.ManagerUnavailable, missingReason);
        Assert.IsFalse(BuildingAccess.TryGetById(null, 1, out missing, out missingReason));
        Assert.AreEqual(BuildingLookupFailure.ManagerUnavailable, missingReason);

        var manager = new GameBuildingManagerAPI();
        GameBuilding value = default;
        manager.Pointer = &value;
        foreach (int id in new[] { manager.Capacity + 1, int.MaxValue })
        {
            Assert.IsFalse(BuildingAccess.TryGetById(manager, id, out var pointer, out var reason));
            Assert.IsTrue(pointer == null);
            Assert.AreEqual(BuildingLookupFailure.OutOfRange, reason);
        }
        Assert.AreEqual(0, manager.Reads, "Out-of-range IDs must not reach the logging SDK lookup.");
        manager.ReturnFalse = true;
        Assert.IsFalse(BuildingAccess.TryGetById(manager, 1, out var failed, out var failure));
        Assert.IsTrue(failed == null, "Even a false SDK result with a non-null pointer must be cleared.");
        Assert.AreEqual(BuildingLookupFailure.Unavailable, failure);
        manager.ReturnFalse = false;
        manager.Pointer = null;
        Assert.IsFalse(BuildingAccess.TryGetById(manager, 1, out failed, out failure));
        Assert.IsTrue(failed == null);
        Assert.AreEqual(BuildingLookupFailure.Unavailable, failure);
    }

    [TestMethod]
    public void InclusiveGameIdsUseCurrentStorageWithoutFilteringIdentityOwnerOrLifeState()
    {
        var manager = new GameBuildingManagerAPI();
        GameBuildingManagerAPI.Current = manager;
        GameBuilding record = new GameBuilding { GlobalId = 0, Owner = 0 };
        manager.Pointer = &record;
        foreach (var life in Enum.GetValues<SHCDESE.Interop.Enums.AliveState>())
        foreach (int id in new[] { 1, manager.Capacity })
        {
            record.Life = life;
            Assert.IsTrue(BuildingAccess.TryGetById(id, out var pointer, out var failure));
            Assert.IsTrue(pointer == &record);
            Assert.AreEqual(BuildingLookupFailure.None, failure);
            Assert.AreEqual(id, manager.LastId, "The one-based ID must be passed unchanged.");
            Assert.AreEqual(life, pointer->Life);
            Assert.AreEqual(0, pointer->GlobalId);
            Assert.AreEqual(0, pointer->Owner);
        }
        manager.Capacity = 1;
        Assert.IsFalse(BuildingAccess.TryGetById(2, out var unavailable, out var reason));
        Assert.AreEqual(BuildingLookupFailure.OutOfRange, reason);
        manager.Capacity = 2;
        GameBuilding replacement = new GameBuilding { GlobalId = 99, Owner = 3 };
        manager.Pointer = &replacement;
        Assert.IsTrue(BuildingAccess.TryGetById(2, out var reloaded, out reason));
        Assert.IsTrue(reloaded == &replacement, "No pointer or capacity is cached across storage replacement.");
        manager.Capacity = 0;
        Assert.IsFalse(BuildingAccess.TryGetById(1, out unavailable, out reason));
        Assert.AreEqual(BuildingLookupFailure.OutOfRange, reason);
    }

    [TestMethod]
    public void FrequentLookupsAndIndexChecksAllocateNothingAfterWarmup()
    {
        var manager = new GameBuildingManagerAPI();
        GameBuilding record = default;
        manager.Pointer = &record;
        // Warm the entire measurement routine, not only its inner helper. Keeping it outside
        // the assertion/benchmark method excludes first-use test-harness/JIT setup from the sample.
        for (int i = 0; i < 4; i++) MeasureChecks(manager, out _);
        long allocated = MeasureChecks(manager, out bool correct);
        Assert.IsTrue(correct);
        Assert.AreEqual(0L, allocated, "Production helper success/rejection/index checks must allocate nothing.");

        // SDK-double microbenchmark only: shows managed call cost, not native throughput or in-game FPS.
        // Alternate equivalent existing guarded lookups and the production helper after both warm up.
        for (int sample = 0; sample < 4; sample++)
        {
            bool helper = (sample & 1) != 0;
            long start = Stopwatch.GetTimestamp();
            int accepted = 0;
            for (int i = 0; i < 1000000; i++)
            {
                bool found = helper ? BuildingAccess.TryGetById(manager, 1, out _, out _) :
                    manager.IsValidId(1) && manager.TryGetBuildingById(1, out var building) && building != null;
                if (found) accepted++;
            }
            double milliseconds = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            Assert.AreEqual(1000000, accepted);
            Console.WriteLine("building lookup SDK-double sample={0} helper={1} ms={2:F3}", sample, helper, milliseconds);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long MeasureChecks(GameBuildingManagerAPI manager, out bool correct)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        correct = true;
        for (int i = 0; i < 100000; i++) correct &= RunChecks(manager);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static bool RunChecks(GameBuildingManagerAPI manager) =>
        !BuildingAccess.TryGetById(0, out var missing, out var invalid) && missing == null && invalid == BuildingLookupFailure.InvalidId &&
        !BuildingAccess.TryGetById(manager, manager.Capacity + 1, out var outside, out var range) && outside == null && range == BuildingLookupFailure.OutOfRange &&
        BuildingAccess.TryGetById(manager, 1, out var valid, out var none) && valid != null && none == BuildingLookupFailure.None &&
        IndexBounds.IsValidIndex(0, 1) && !IndexBounds.IsValidIndex(-1, int.MaxValue);
}

[TestClass]
public class IndexBoundsTests
{
    [TestMethod]
    public void ZeroBasedBoundariesAndNegativeLengths()
    {
        foreach (int length in new[] { int.MinValue, -1, 0, 1, 2, int.MaxValue })
        foreach (int index in new[] { int.MinValue, -1, 0, 1, 2, int.MaxValue - 1, int.MaxValue })
        {
            bool expected = index >= 0 && (long)index < length;
            Assert.AreEqual(expected, IndexBounds.IsValidIndex(index, length), $"index={index}, length={length}");
        }
    }
}

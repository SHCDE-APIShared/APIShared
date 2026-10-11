using APIShared;
using SHCDESE.API;
using SHCDESE.Interop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace APIShared.Core.Tests;
[TestClass, DoNotParallelize]
public class UnitAccessTests
{
    [TestMethod] public unsafe void LookupBoundariesAndLifeState()
    {

    foreach (var state in Enum.GetValues<SHCDESE.Interop.Enums.AliveState>())
    foreach (uint marker in new uint[] { 0, 1, 0xFFFF, 0x10000, 0xFFFF0000, 0xFFFFFFFF })
    {
        GameUnit life = new GameUnit { r_AliveState = state, r_IsKilledByProjectile = (ushort)marker, r_InterestingTodo4MaybeRandomPathing = (ushort)(marker >> 16), r_CurrentHealth = 0 };
        bool expected = state == SHCDESE.Interop.Enums.AliveState.IsAlive && (marker & 0xFFFF) == 0;
        Check(UnitAccess.IsReallyAlive(in life) == expected, "reference life predicate");
        Check(UnitAccess.IsReallyAlive(&life) == expected, "pointer life predicate");
        life.r_CurrentHealth = uint.MaxValue;
        Check(UnitAccess.IsReallyAlive(in life) == expected, "health is not an additional rule");
    }
    Check(!UnitAccess.IsReallyAlive((GameUnit*)null), "null life view");
    var queryManager = new GameUnitManagerAPI();
    GameUnitManagerAPI.Current = queryManager;
    Check(UnitAccess.GetAllReallyAliveUnits().Length == 0, "empty query");
    queryManager.QueryRecords = new[] {
        new GameUnit { r_AliveState = SHCDESE.Interop.Enums.AliveState.IsAlive },
        new GameUnit { r_AliveState = SHCDESE.Interop.Enums.AliveState.IsAlive, r_IsKilledByProjectile = 1 },
        new GameUnit { r_AliveState = SHCDESE.Interop.Enums.AliveState.NeedsInit },
        new GameUnit { r_AliveState = SHCDESE.Interop.Enums.AliveState.IsAlive, r_IsKilledByProjectile = 0, r_InterestingTodo4MaybeRandomPathing = 1 },
        new GameUnit { r_AliveState = SHCDESE.Interop.Enums.AliveState.MarkedForDeletion },
    };
    int[] livingIds = UnitAccess.GetAllReallyAliveUnits();
    Check(livingIds.SequenceEqual(new[] { 1, 4 }), "query filters death low word and returns ordered one-based IDs");
    queryManager.QueryRecords[0].r_IsKilledByProjectile = 1;
    Check(livingIds.SequenceEqual(new[] { 1, 4 }) && UnitAccess.GetAllReallyAliveUnits().SequenceEqual(new[] { 4 }),
        "query is a momentary copy, later life changes require revalidation");
    GameUnit record = new GameUnit { Alive = 4, GlobalId = 0 }; // NeedsInit is still resolvable.
    var manager = new GameUnitManagerAPI { Pointer = &record };
    GameUnitManagerAPI.Current = manager;
    foreach (int id in new[] { int.MinValue, -1, 0 })
    {
        int before = GameUnitManagerAPI.InstanceReads;
        Check(!UnitAccess.TryGetById(id, out GameUnit* unit, out var reason) && unit == null && reason == UnitLookupFailure.InvalidId, "invalid ID");
        Check(GameUnitManagerAPI.InstanceReads == before && manager.Reads == 0 && manager.Validations == 0, "invalid ID never touches manager");
    }
    Check(!UnitAccess.TryGetById(null, 1, out GameUnit* absent, out var missing) && absent == null && missing == UnitLookupFailure.ManagerUnavailable, "missing manager");
    foreach (int id in new[] { 10000, int.MaxValue })
    {
        Check(!UnitAccess.TryGetById(manager, id, out GameUnit* unit, out var reason) && unit == null && reason == UnitLookupFailure.OutOfRange, "upper bound");
        Check(manager.Reads == 0, "out-of-range never enters SDK lookup");
    }
    foreach (int id in new[] { 1, 9999 })
    {
        Check(UnitAccess.TryGetById(manager, id, out GameUnit* unit, out var reason) && unit == &record && reason == UnitLookupFailure.None, "inclusive bounds");
        Check(manager.LastId == id && unit->Alive == 4 && unit->GlobalId == 0, "no ID conversion or life/identity filtering");
    }
    manager.Pointer = null;
    Check(!UnitAccess.TryGetById(manager, 1, out GameUnit* unloaded, out var unavailable) && unloaded == null && unavailable == UnitLookupFailure.Unavailable, "unavailable pointer after unload");
    manager.Pointer = &record; manager.ReturnFalse = true;
    Check(!UnitAccess.TryGetById(manager, 1, out GameUnit* failed, out var failure) && failed == null && failure == UnitLookupFailure.Unavailable, "false SDK result clears even non-null pointer");
    manager.ReturnFalse = false;
    Check(UnitAccess.TryGetById(manager, 1, out GameUnit* reloaded, out _) && reloaded == &record, "reload uses current manager storage");

    }
    [TestMethod] public unsafe void RejectedLookupsAllocateNothingAndKeepCallerFailureReasons()
    {
        // Warm the exact measured loop, including tiered compilation/OSR bookkeeping.
        // A different warm-up loop can leave a one-time runtime allocation in the sample.
        for (int i = 0; i < 4; i++) RunRejectedLookups();
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool allRejected = RunRejectedLookups();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Check(allRejected, "failure reason remains available for consumer diagnostics");
        Assert.AreEqual(0L, allocated, "no automatic site tracking, formatting or logging allocations");
        Check(GameUnitManagerAPI.InstanceReads == 0, "sentinel rejections never instantiate the manager");
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static unsafe bool RunRejectedLookups()
    {
        bool allRejected = true;
        for (int i = 0; i < 10000; i++)
        {
            bool found = UnitAccess.TryGetById(null, 0, out var unit, out var failure,
                "Mod/src/Worker.cs", "OnEvent", i);
            allRejected &= !found && unit == null && failure == UnitLookupFailure.InvalidId;
        }
        return allRejected;
    }
    [TestInitialize]
    public void ResetExternalBoundary()
    {
        GameUnitManagerAPI.Current = null;
        GameUnitManagerAPI.InstanceReads = 0;
    }
    [TestCleanup]
    public void ReleaseExternalBoundary()
    {
        GameUnitManagerAPI.Current = null;
    }
    private static void Check(bool condition, string message) => Assert.IsTrue(condition, message);
}

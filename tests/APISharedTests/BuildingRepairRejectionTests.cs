using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.Threading;
using BepInEx.Logging;
using APIShared;
using APIShared.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Core.Memory.Managed;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public unsafe sealed class BuildingRepairRejectionTests
    {
        private sealed class World : IDisposable
        {
            private readonly IntPtr memory = Marshal.AllocHGlobal(sizeof(GameBuilding) + sizeof(GamePlayerResources));
            internal GameBuilding* Building;
            internal readonly GameBuildingManagerAPI Buildings;
            internal readonly GamePlayerManagerAPI Players;
            internal readonly BuildingRepairEventArgs Args = new BuildingRepairEventArgs(EventHookPhase.Pre, 1, 1, 0, 0, 99);
            internal World()
            {
                new Span<byte>((void*)memory, sizeof(GameBuilding) + sizeof(GamePlayerResources)).Clear();
                Building = (GameBuilding*)memory;
                Buildings = (GameBuildingManagerAPI)FormatterServices.GetUninitializedObject(typeof(GameBuildingManagerAPI));
                Players = (GamePlayerManagerAPI)FormatterServices.GetUninitializedObject(typeof(GamePlayerManagerAPI));
                Set(Buildings, "_buildingArray", new SimpleNativeArray<GameBuilding>((byte*)Building, 1));
                Set(Players, "_playerResources", new SimpleNativeArray<GamePlayerResources>((byte*)Building + sizeof(GameBuilding), 1));
                ResetBuilding();
            }
            internal void ResetBuilding()
            {
                *Building = default;
                Building->r_PlayerIdOwner = 1;
                Building->r_GlobalId = 99;
                Building->r_AliveState = AliveState.IsAlive;
                Building->r_MaxHealth = 100;
                Building->r_CurrentHealth = 50;
                Building->r_BuildingType = eStructs.STRUCT_GARDEN_LARGE;
            }
            internal RepairRejectionReason Validate(out RepairRejectionSnapshot snapshot) =>
                BuildingRepairService.ValidateRepair(Buildings, Players, Args, out _, out snapshot);
            public void Dispose() => Marshal.FreeHGlobal(memory);
        }
        private static void Set(object target, string field, object value) => target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        [TestMethod]
        public void AcquisitionGuardsDoNotReadUnavailableNativeMemory()
        {
            var args = new BuildingRepairEventArgs(EventHookPhase.Pre, 1, 0, 0, 0, 99);
            Assert.AreEqual(RepairRejectionReason.BuildingManagerUnavailable,
                BuildingRepairService.ValidateRepair(null, null, args, out _, out var empty));
            Assert.IsNull(empty.Owner);
            using (var world = new World())
            {
                Assert.AreEqual(RepairRejectionReason.PlayerManagerUnavailable,
                    BuildingRepairService.ValidateRepair(world.Buildings, null, args, out _, out _));
                Assert.AreEqual(RepairRejectionReason.InvalidBuildingId,
                    BuildingRepairService.ValidateRepair(world.Buildings, world.Players, args, out _, out _));
                Set(world.Players, "_playerResources", default(SimpleNativeArray<GamePlayerResources>));
                Assert.AreEqual(RepairRejectionReason.PlayerResourcesUnavailable, world.Validate(out var unavailable));
                Assert.IsNull(unavailable.LookupFailure);
                Assert.IsNull(unavailable.Owner);
            }
            using (var world = new World())
            {
                Set(world.Buildings, "_buildingArray", default(SimpleNativeArray<GameBuilding>));
                Assert.AreEqual(RepairRejectionReason.BuildingUnavailable, world.Validate(out var unavailable));
                Assert.AreEqual(BuildingLookupFailure.OutOfRange, unavailable.LookupFailure);
                Assert.IsNull(unavailable.Owner);
                Set(world.Buildings, "_buildingArray", new SimpleNativeArray<GameBuilding>((byte*)0, 1));
                Assert.AreEqual(RepairRejectionReason.BuildingUnavailable, world.Validate(out unavailable));
                Assert.AreEqual(BuildingLookupFailure.Unavailable, unavailable.LookupFailure);
            }
        }

        [TestMethod]
        public void BuildingGuardsRetainOrderAndOnlyCaptureFieldsAlreadyRead()
        {
            using (var world = new World())
            {
                world.Building->r_PlayerIdOwner = 2;
                world.Building->r_GlobalId = 100;
                Assert.AreEqual(RepairRejectionReason.OwnerMismatch, world.Validate(out var snapshot));
                Assert.AreEqual(2, snapshot.Owner);
                Assert.IsNull(snapshot.GlobalId);
                world.Building->r_PlayerIdOwner = 1;
                Assert.AreEqual(RepairRejectionReason.GlobalIdMismatch, world.Validate(out snapshot));
                Assert.IsNull(snapshot.Alive);
                world.ResetBuilding(); world.Building->r_AliveState = (AliveState)0;
                Assert.AreEqual(RepairRejectionReason.NotAlive, world.Validate(out snapshot));
                Assert.IsNull(snapshot.MaximumHealth);
                world.ResetBuilding(); world.Building->r_MaxHealth = 0;
                Assert.AreEqual(RepairRejectionReason.ZeroMaximumHealth, world.Validate(out snapshot));
                Assert.IsNull(snapshot.Health);
                world.Building->r_MaxHealth = ushort.MaxValue;
                Assert.AreEqual(RepairRejectionReason.ExcessiveMaximumHealth, world.Validate(out _));
                world.ResetBuilding(); world.Building->r_CurrentHealth = -1;
                Assert.AreEqual(RepairRejectionReason.NegativeHealth, world.Validate(out snapshot));
                Assert.IsNull(snapshot.Type);
                world.ResetBuilding(); world.Building->r_BuildingType = eStructs.STRUCT_NULL;
                Assert.AreEqual(RepairRejectionReason.InvalidBuildingType, world.Validate(out _));
                world.Building->r_BuildingType = (eStructs)((int)eStructs.STRUCT_GARDEN_LARGE + 1);
                Assert.AreEqual(RepairRejectionReason.InvalidBuildingType, world.Validate(out _));
                world.ResetBuilding();
                Assert.AreEqual(RepairRejectionReason.None, world.Validate(out snapshot));
                Assert.AreEqual(50, snapshot.Health);
                world.Building->r_CurrentHealth = 100;
                Assert.AreEqual(RepairRejectionReason.None, world.Validate(out _), "Full HP remains a normal early return, not rejection.");
            }
        }

        [TestMethod]
        public void RejectionsHaveBoundedBucketsIndependentReasonsAndProcessCounters()
        {
            long now = 0;
            var diagnostics = new RepairRejectionDiagnostics(() => now, 1);
            for (int id = 1; id <= 10000; id++) diagnostics.Record(RepairRejectionReason.GlobalIdMismatch,
                new RepairRejectionSnapshot { BuildingId = id, Session = 7 });
            Assert.IsTrue(diagnostics.TryTake(RepairRejectionReason.GlobalIdMismatch, out var first));
            Assert.AreEqual(10000L, first.Delta);
            Assert.AreEqual(10000L, first.ProcessTotal);
            Assert.AreEqual(10000, first.Snapshot.BuildingId);
            diagnostics.Record(RepairRejectionReason.GlobalIdMismatch, new RepairRejectionSnapshot { Session = null });
            now = 29;
            Assert.IsFalse(diagnostics.TryTake(RepairRejectionReason.GlobalIdMismatch, out _));
            diagnostics.Record(RepairRejectionReason.OwnerMismatch, default);
            Assert.IsTrue(diagnostics.TryTake(RepairRejectionReason.OwnerMismatch, out _));
            now = 30;
            Assert.IsTrue(diagnostics.TryTake(RepairRejectionReason.GlobalIdMismatch, out var second));
            Assert.AreEqual(1L, second.Delta);
            Assert.AreEqual(10001L, second.ProcessTotal);
            Assert.IsNull(second.Snapshot.Session);
            Assert.IsTrue(second.Describe().Contains("session=unavailable"));
            Assert.IsFalse(diagnostics.TryTake(RepairRejectionReason.GlobalIdMismatch, out _));
            Assert.AreEqual(long.MaxValue, RepairRejectionDiagnostics.Increment(long.MaxValue));
            Assert.AreEqual(long.MaxValue, RepairRejectionDiagnostics.Increment(long.MaxValue - 1));
            var buckets = (Array)typeof(RepairRejectionDiagnostics).GetField("buckets", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(diagnostics);
            Assert.AreEqual((int)RepairRejectionReason.Count, buckets.Length, "Fixed bounded storage is the safety contract under changing identities.");
        }

        [TestMethod]
        public void AdditionalRepairCostsRetainZeroMinimumAndRoundingRules()
        {
            var cost = typeof(BuildingRepairService).GetMethod("RepairCost", BindingFlags.Static | BindingFlags.NonPublic);
            int Amount(int basis, int health, int maximum) => (int)cost.Invoke(null, new object[] { basis, health, maximum });
            Assert.AreEqual(0, Amount(0, 50, 100));
            Assert.AreEqual(0, Amount(20, 100, 100));
            Assert.AreEqual(1, Amount(1, 99, 100));
            Assert.AreEqual(6, Amount(20, 67, 100));
            Assert.AreEqual(int.MaxValue, Amount(int.MaxValue, 0, 100));
        }

        [TestMethod]
        public void DormantCollectorSkipsLockAndNewRecordsBecomeVisibleAndEmitOutsideLocks()
        {
            var diagnostics = new RepairRejectionDiagnostics(() => 0, 1);
            object gate = typeof(RepairRejectionDiagnostics).GetField("sync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(diagnostics);
            bool completed = false;
            lock (gate)
            {
                var dormant = new Thread(() => { diagnostics.TryTake(RepairRejectionReason.GlobalIdMismatch, out _); completed = true; });
                dormant.Start();
                Assert.IsTrue(dormant.Join(2000), "Dormant HUD reads cannot wait for the collector lock.");
            }
            Assert.IsTrue(completed);
            diagnostics.Record(RepairRejectionReason.GlobalIdMismatch, new RepairRejectionSnapshot { BuildingId = 7 });
            var service = (BuildingRepairService)FormatterServices.GetUninitializedObject(typeof(BuildingRepairService));
            object errors = new object();
            Set(service, "errorSync", errors);
            Set(service, "pendingErrors", new Queue<string>());
            Set(service, "repairRejections", diagnostics);
            using (var logger = new ManualLogSource("repair-rejection-test"))
            {
                Set(service, "log", logger);
                int emitted = 0;
                logger.LogEvent += (_, args) => {
                    Assert.IsFalse(Monitor.IsEntered(gate));
                    Assert.IsFalse(Monitor.IsEntered(errors));
                    Assert.IsTrue(args.Data.ToString().Contains("reason=GlobalIdMismatch"));
                    emitted++;
                };
                typeof(BuildingRepairService).GetMethod("FlushErrors", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(service, null);
                Assert.AreEqual(1, emitted);
            }
        }

        [TestMethod]
        public void RealPrePostHandlerRejectsSafelyAndOnlyChargesCompletedMatchingRepairsOnce()
        {
            var buildingSingleton = typeof(GameBuildingManagerAPI).GetField("_lazy", BindingFlags.Static | BindingFlags.NonPublic);
            var playerSingleton = typeof(GamePlayerManagerAPI).GetField("_lazy", BindingFlags.Static | BindingFlags.NonPublic);
            object previousBuildings = buildingSingleton.GetValue(null), previousPlayers = playerSingleton.GetValue(null);
            var costTables = typeof(ConstructionCosts).GetField("tables", BindingFlags.Static | BindingFlags.NonPublic);
            object previousCostTables = costTables.GetValue(null);
            var handler = typeof(BuildingRepairService).GetMethod("OnBuildingRepair", BindingFlags.Instance | BindingFlags.NonPublic);
            var service = (BuildingRepairService)FormatterServices.GetUninitializedObject(typeof(BuildingRepairService));
            Set(service, "activeOwners", 1);
            Set(service, "errorSync", new object());
            Set(service, "seenErrors", new HashSet<string>());
            Set(service, "pendingErrors", new Queue<string>());
            // Null diagnostics deliberately exercises the production failure-isolation boundary.
            using (var world = new World())
            {
                int length = ConstructionCosts.NativeRowCount * 5;
                IntPtr costs = Marshal.AllocHGlobal(length * sizeof(int));
                try
                {
                    new Span<byte>((void*)costs, length * sizeof(int)).Clear();
                    IntPtr goldCosts = Marshal.AllocHGlobal(length * sizeof(int));
                    try
                    {
                        new Span<byte>((void*)goldCosts, length * sizeof(int)).Clear();
                        var target = ConstructionCostTarget.FromStructure(eStructs.STRUCT_GARDEN_LARGE);
                        // Exercise the migrated production reader and actual canonical row, not SE's
                        // oversized array facade. Garden tooltip 120 is not a native cost-table index.
                        ((int*)goldCosts)[target.Row * 5 + 4] = 20;
                        costTables.SetValue(null, new ConstructionCostTables(costs, goldCosts,
                            ConstructionCosts.NativeRowCount, new int[(ConstructionCosts.NativeRowCount - 1) * 5]));
                        Assert.IsTrue(ConstructionCosts.TryGetCurrent(target, out var fixtureCost, out string fixtureReason), fixtureReason);
                        Assert.AreEqual(20, fixtureCost.Gold);
                        Assert.AreEqual(0, fixtureCost.Iron);
                        Assert.AreEqual(0, fixtureCost.Pitch);
                        buildingSingleton.SetValue(null, new Lazy<GameBuildingManagerAPI>(() => world.Buildings));
                        playerSingleton.SetValue(null, new Lazy<GamePlayerManagerAPI>(() => world.Players));
                        world.Players.TryGetPlayerResourcesById(1, out var resources);
                        resources->r_TotalGoodsGold = 100;
                        BuildingRepairEventArgs Args(EventHookPhase phase) => new BuildingRepairEventArgs(phase, 1, 1, 0, 0, 99);
                        void Invoke(BuildingRepairEventArgs args) => handler.Invoke(service, new object[] { args });

                        world.Building->r_GlobalId = 100;
                        var rejected = Args(EventHookPhase.Pre);
                        Invoke(rejected);
                        Assert.IsTrue(rejected.SkipOriginalFunction, "A failed diagnostic recorder must leave the rejection set.");
                        Assert.AreEqual(100, resources->r_TotalGoodsGold);
                        world.ResetBuilding();

                        var admitted = Args(EventHookPhase.Pre);
                        Invoke(admitted);
                        Assert.IsFalse(admitted.SkipOriginalFunction);
                        Assert.AreEqual(100, resources->r_TotalGoodsGold, "Pre never charges.");
                        world.Building->r_CurrentHealth = 100; // Model the original native repair completion.
                        Invoke(Args(EventHookPhase.Post));
                        Assert.AreEqual(90, resources->r_TotalGoodsGold);
                        Invoke(Args(EventHookPhase.Post));
                        Assert.AreEqual(90, resources->r_TotalGoodsGold, "Duplicate Post cannot charge twice.");

                        world.ResetBuilding(); Invoke(Args(EventHookPhase.Pre));
                        world.Building->r_GlobalId = 100; world.Building->r_CurrentHealth = 100;
                        Invoke(Args(EventHookPhase.Post));
                        Assert.AreEqual(90, resources->r_TotalGoodsGold, "Replaced identity cannot be charged.");
                        world.ResetBuilding(); Invoke(Args(EventHookPhase.Pre));
                        world.Building->r_CurrentHealth = 75;
                        Invoke(Args(EventHookPhase.Post));
                        Assert.AreEqual(90, resources->r_TotalGoodsGold, "Uncompleted native repair cannot be charged.");

                        world.ResetBuilding(); resources->r_TotalGoodsGold = 9;
                        var poor = Args(EventHookPhase.Pre); Invoke(poor);
                        Assert.IsTrue(poor.SkipOriginalFunction);
                        world.Building->r_CurrentHealth = 100; Invoke(Args(EventHookPhase.Post));
                        Assert.AreEqual(9, resources->r_TotalGoodsGold, "Rejected Pre leaves no pending charge.");
                    }
                    finally { costTables.SetValue(null, previousCostTables); Marshal.FreeHGlobal(goldCosts); }
                }
                finally
                {
                    buildingSingleton.SetValue(null, previousBuildings);
                    playerSingleton.SetValue(null, previousPlayers);
                    Marshal.FreeHGlobal(costs);
                }
            }
        }
    }
}

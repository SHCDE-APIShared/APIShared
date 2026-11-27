using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared;
using APIShared.Economy;
using APIShared.GameModes;
using APIShared.Goods;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    // The production typed projection reads real packed SDK actor buffers. Lifecycle tests use
    // the actual native-clock state machine. Native managers, hooks and game execution are absent.
    [TestClass, DoNotParallelize]
    public sealed unsafe class WeaponProductionWorkerBatchTests
    {
        private const int UnitId = 51, HomeId = 12;
        private const uint UnitGlobalId = 0xF0000001, HomeGlobalId = 0x80000002;

        private sealed class Actors : IDisposable
        {
            private readonly IntPtr worker = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameUnit)));
            private readonly IntPtr home = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameBuilding)));
            internal GameUnit* Worker => (GameUnit*)worker;
            internal GameBuilding* Home => (GameBuilding*)home;
            internal Actors(eStructs type = eStructs.STRUCT_FLETCHERS_WORKSHOP, eChimps role = eChimps.CHIMP_TYPE_FLETCHER)
            {
                new Span<byte>((void*)worker, Marshal.SizeOf(typeof(GameUnit))).Clear();
                new Span<byte>((void*)home, Marshal.SizeOf(typeof(GameBuilding))).Clear();
                Worker->r_GlobalId = UnitGlobalId; Worker->r_AliveState = AliveState.IsAlive;
                Worker->r_ControllableForPlayerId = 1; Worker->r_UnitChimp = role;
                Worker->r_LinkedProductionBuildingId = HomeId; Worker->r_LinkedProductionBuildingGlobalId = HomeGlobalId;
                Home->r_GlobalId = HomeGlobalId; Home->r_AliveState = AliveState.IsAlive;
                Home->r_PlayerIdOwner = 1; Home->r_BuildingType = type;
            }
            internal byte[] WorkerBytes() => new ReadOnlySpan<byte>((void*)worker, Marshal.SizeOf(typeof(GameUnit))).ToArray();
            internal byte[] HomeBytes() => new ReadOnlySpan<byte>((void*)home, Marshal.SizeOf(typeof(GameBuilding))).ToArray();
            public void Dispose() { Marshal.FreeHGlobal(worker); Marshal.FreeHGlobal(home); }
        }

        private static WeaponProductionOption Recipe(string id = "recipe", bool stored = false,
            eStructs type = eStructs.STRUCT_FLETCHERS_WORKSHOP, eGoods template = eGoods.STORED_BOWS)
        {
            var definition = new WeaponProductionOptionDefinition(type, template, eGoods.STORED_SWORDS, "Recipe", 2, 60);
            if (stored) definition = definition.WithStoredOutput(new NativeStoredGood(new CustomGoodId("owner", "armor"), StoragePool.Armory));
            return new WeaponProductionOption(new WeaponProductionOptionId("owner", id), definition, false);
        }
        private static WeaponProductionCatalog Catalog(params WeaponProductionOption[] options) =>
            WeaponProductionCatalog.Compile(options.Select(option => new WeaponProductionRegistration(option)), _ => true);
        private static bool Observe(Actors actors, WeaponProductionCatalog catalog, WeaponProductionOption option,
            out WeaponProductionWorkerBatch snapshot, int phase = 2, int unitId = UnitId, uint unitGlobalId = UnitGlobalId,
            int homeId = HomeId, uint homeGlobalId = HomeGlobalId) =>
            WeaponProductionRuntime.TryObserveWorkerBatch(catalog, unitId, unitGlobalId, homeId, homeGlobalId,
                option, phase, actors.Worker, actors.Home, out snapshot);

        [TestMethod]
        [DataRow(0, false)]
        [DataRow(1, false)]
        [DataRow(2, false)]
        [DataRow(0, true)]
        [DataRow(1, true)]
        [DataRow(2, true)]
        public void SnapshotKeepsTheLatchedOptionPhaseAndUnsignedIdentitiesWithoutWritingActors(int phase, bool stored)
        {
            var option = Recipe(stored: stored); var next = Recipe("next"); var catalog = Catalog(option, next);
            using (var actors = new Actors())
            {
                byte[] worker = actors.WorkerBytes(), home = actors.HomeBytes();
                Assert.IsTrue(Observe(actors, catalog, option, out var first, phase));
                Assert.AreEqual(UnitId, first.UnitId); Assert.AreEqual(UnitGlobalId, first.UnitGlobalId);
                Assert.AreEqual(HomeId, first.HomeBuildingId); Assert.AreEqual(HomeGlobalId, first.HomeBuildingGlobalId);
                Assert.AreSame(option, first.Option); Assert.AreEqual((WeaponProductionBatchPhase)phase, first.Phase);
                CollectionAssert.AreEqual(worker, actors.WorkerBytes()); CollectionAssert.AreEqual(home, actors.HomeBytes());
                Assert.IsTrue(Observe(actors, catalog, next, out var later, 0));
                Assert.AreSame(next, later.Option); Assert.AreSame(option, first.Option);
                Assert.AreEqual((WeaponProductionBatchPhase)phase, first.Phase, "Later row changes cannot reinterpret a retained snapshot.");
                actors.Worker->r_GlobalId++;
                Assert.IsFalse(Observe(actors, catalog, option, out var missing, phase)); Assert.AreEqual(default(WeaponProductionWorkerBatch), missing);
                Assert.AreEqual(UnitGlobalId, first.UnitGlobalId);
            }
        }

        [TestMethod]
        [DataRow("worker-global")]
        [DataRow("home-global")]
        [DataRow("dead-worker")]
        [DataRow("dying-worker")]
        [DataRow("dead-home")]
        [DataRow("role")]
        [DataRow("home-type")]
        [DataRow("link-slot")]
        [DataRow("link-global")]
        [DataRow("worker-owner")]
        [DataRow("owner-zero")]
        [DataRow("owner-out-of-range")]
        public void RecycledRetiredOrUnlinkedActorsHaveNoCurrentBatchObservation(string change)
        {
            var option = Recipe(); var catalog = Catalog(option);
            using (var actors = new Actors())
            {
                switch (change)
                {
                    case "worker-global": actors.Worker->r_GlobalId++; break;
                    case "home-global": actors.Home->r_GlobalId++; break;
                    case "dead-worker": actors.Worker->r_AliveState = AliveState.None; break;
                    case "dying-worker": actors.Worker->r_IsKilledByProjectile = 1; break;
                    case "dead-home": actors.Home->r_AliveState = AliveState.None; break;
                    case "role": actors.Worker->r_UnitChimp = eChimps.CHIMP_TYPE_TANNER; break;
                    case "home-type": actors.Home->r_BuildingType = eStructs.STRUCT_TANNERS_WORKSHOP; break;
                    case "link-slot": actors.Worker->r_LinkedProductionBuildingId++; break;
                    case "link-global": actors.Worker->r_LinkedProductionBuildingGlobalId++; break;
                    case "worker-owner": actors.Worker->r_ControllableForPlayerId = 2; break;
                    case "owner-zero": actors.Home->r_PlayerIdOwner = 0; actors.Worker->r_ControllableForPlayerId = 0; break;
                    case "owner-out-of-range": actors.Home->r_PlayerIdOwner = 9; actors.Worker->r_ControllableForPlayerId = 9; break;
                    default: Assert.Fail(change); break;
                }
                Assert.IsFalse(Observe(actors, catalog, option, out var snapshot)); Assert.AreEqual(default(WeaponProductionWorkerBatch), snapshot);
            }
        }

        [TestMethod]
        public void NativePausedBatchActorIsObservableUntilItsDeathMarkerIsSet()
        {
            var option = Recipe(); var catalog = Catalog(option);
            using (var actors = new Actors())
            {
                actors.Worker->r_AliveState = AliveState.Paused;
                Assert.IsTrue(Observe(actors, catalog, option, out _));
                actors.Worker->r_IsKilledByProjectile = 1;
                Assert.IsFalse(Observe(actors, catalog, option, out var snapshot)); Assert.AreEqual(default(WeaponProductionWorkerBatch), snapshot);
            }
        }

        [TestMethod]
        [DataRow(eStructs.STRUCT_FLETCHERS_WORKSHOP, eChimps.CHIMP_TYPE_FLETCHER, eGoods.STORED_BOWS)]
        [DataRow(eStructs.STRUCT_POLETURNERS_WORKSHOP, eChimps.CHIMP_TYPE_POLETURNER, eGoods.STORED_SPEARS)]
        [DataRow(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, eChimps.CHIMP_TYPE_BLACKSMITH, eGoods.STORED_MACES)]
        [DataRow(eStructs.STRUCT_ARMOURERS_WORKSHOP, eChimps.CHIMP_TYPE_ARMOURER, eGoods.STORED_METAL_ARMOUR)]
        [DataRow(eStructs.STRUCT_TANNERS_WORKSHOP, eChimps.CHIMP_TYPE_TANNER, eGoods.STORED_LEATHER_ARMOUR)]
        public void OnlyTheWorkshopSpecificWorkerRoleMatches(eStructs type, eChimps role, eGoods template)
        {
            var option = Recipe(type: type, template: template); var catalog = Catalog(option);
            using (var actors = new Actors(type, role))
            {
                Assert.IsTrue(Observe(actors, catalog, option, out _));
                actors.Worker->r_UnitChimp = eChimps.CHIMP_TYPE_NULL;
                Assert.IsFalse(Observe(actors, catalog, option, out var snapshot)); Assert.AreEqual(default(WeaponProductionWorkerBatch), snapshot);
            }
        }

        [TestMethod]
        public void MissingVanillaUnadmittedOrOutOfBoundsBatchEvidenceIsNotGuessedFromNativeFields()
        {
            var option = Recipe(); var catalog = Catalog(option);
            using (var actors = new Actors())
            {
                foreach (var unsupported in new[] { null, Recipe(), catalog.For(option.BuildingType)[0] })
                { Assert.IsFalse(Observe(actors, catalog, unsupported, out var snapshot)); Assert.AreEqual(default(WeaponProductionWorkerBatch), snapshot); }
                Assert.IsFalse(Observe(actors, WeaponProductionCatalog.Empty, option, out _));
                Assert.IsFalse(Observe(actors, catalog, option, out _, phase: -1));
                Assert.IsFalse(Observe(actors, catalog, option, out _, phase: 3));
                Assert.IsFalse(Observe(actors, catalog, option, out _, unitId: 0));
                Assert.IsFalse(Observe(actors, catalog, option, out _, unitId: 10000));
                Assert.IsFalse(Observe(actors, catalog, option, out _, unitGlobalId: 0));
                Assert.IsFalse(Observe(actors, catalog, option, out _, homeId: 0));
                Assert.IsFalse(Observe(actors, catalog, option, out _, homeId: 4000));
                Assert.IsFalse(Observe(actors, catalog, option, out _, homeGlobalId: 0));
                Assert.IsFalse(WeaponProductionRuntime.TryObserveWorkerBatch(catalog, UnitId, UnitGlobalId, HomeId, HomeGlobalId,
                    option, 2, null, actors.Home, out _));
                Assert.IsFalse(WeaponProductionRuntime.TryObserveWorkerBatch(catalog, UnitId, UnitGlobalId, HomeId, HomeGlobalId,
                    option, 2, actors.Worker, null, out _));
            }
        }

        private static MissionContext Mission(long id, GameModeKind kind = GameModeKind.CustomGame) => new MissionContext(id,
            MissionStartKind.NewGame, new GameModeSnapshot(false, false, false, false, false, false, true, false, false,
                false, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, kind, default, 0, 0, 0, 0, 0, false), null, "batch fixture", false);

        [TestMethod]
        public void NestedInitialLoadBindsOnlyTheCompletedWorldAndMetadataRefreshRetainsIt()
        {
            var lifecycle = new MissionLifecycleState(error => Assert.Fail(error.ToString()));
            long id = lifecycle.Begin(Mission(0)); var receipt = new WeaponProductionObservationWorld(); receipt.Begin(id);
            lifecycle.BeginNativeLoad(id); lifecycle.BeginNativeLoad(id);
            lifecycle.CompleteNativeLoad(id, true); receipt.Bind(id, lifecycle.NativeClock?.World);
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            lifecycle.CompleteNativeLoad(id, true); receipt.Bind(id, lifecycle.NativeClock.World);
            Assert.IsTrue(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            bool notified = false;
            lifecycle.Register("fixture", "batch", note => {
                if (note.Phase == MissionInitializationPhase.NativeLoaded)
                    notified = receipt.IsCurrent(lifecycle.NativeClock, true, false);
            });
            lifecycle.Checkpoint(id, lifecycle.Context, MissionInitializationPhase.NativeLoaded);
            lifecycle.Ready(id, lifecycle.Context);
            Assert.IsTrue(notified); Assert.IsTrue(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, false, false));
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, true));
            Assert.IsFalse(receipt.IsCurrent(new MissionLifecycleState.NativeClockState(Mission(id, GameModeKind.Unknown), lifecycle.NativeClock.World), true, false));
        }

        [TestMethod]
        public void SameSessionReplacementAndOldCompletionCannotBlessRetainedBatchRows()
        {
            var lifecycle = new MissionLifecycleState(error => Assert.Fail(error.ToString()));
            long id = lifecycle.Begin(Mission(0)); var receipt = new WeaponProductionObservationWorld(); receipt.Begin(id);
            lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true); var first = lifecycle.NativeClock.World;
            var delayed = new WeaponProductionObservationWorld(); delayed.Begin(id);
            receipt.Bind(id, first); Assert.IsTrue(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            lifecycle.BeginNativeLoad(id); Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            lifecycle.BeginNativeLoad(id); lifecycle.CompleteNativeLoad(id, true);
            receipt.Bind(id, lifecycle.NativeClock?.World);
            lifecycle.CompleteNativeLoad(id, true); var replacement = lifecycle.NativeClock.World;
            Assert.AreNotSame(first, replacement);
            receipt.Bind(id, replacement);
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, false), "An old worker dictionary may survive this same-session load.");
            delayed.Bind(id, first);
            Assert.IsFalse(delayed.IsCurrent(lifecycle.NativeClock, true, false));
            delayed.Bind(id, replacement);
            Assert.IsFalse(delayed.IsCurrent(lifecycle.NativeClock, true, false),
                "A delayed completed token must bind permanently even after retirement; skipping it could bless old rows on the next completion.");
            long next = lifecycle.Begin(Mission(0)); receipt.End(); receipt.Begin(next);
            receipt.Bind(id, first);
            lifecycle.BeginNativeLoad(next); lifecycle.CompleteNativeLoad(next, true);
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            receipt.Bind(next, lifecycle.NativeClock.World); Assert.IsTrue(receipt.IsCurrent(lifecycle.NativeClock, true, false));
            lifecycle.End(MissionEndReason.Unloaded); receipt.End();
            Assert.IsFalse(receipt.IsCurrent(lifecycle.NativeClock, true, false));
        }

        [TestMethod]
        public void DormantReadAndSuccessfulTypedProjectionAllocateNothingAfterWarmup()
        {
            WeaponProductionRuntime.BeginWorld(WeaponProductionCatalog.Empty);
            try
            {
                for (int i = 0; i < 2000; i++) WeaponProduction.TryGetWorkerBatch(UnitId, UnitGlobalId, out _);
                long before = GC.GetAllocatedBytesForCurrentThread(); bool any = false;
                for (int i = 0; i < 20000; i++) any |= WeaponProduction.TryGetWorkerBatch(UnitId, UnitGlobalId, out _);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.IsFalse(any); Assert.AreEqual(0L, allocated);
                Assert.IsFalse(WeaponProduction.TryGetWorkerBatch(UnitId, UnitGlobalId, out var snapshot)); Assert.AreEqual(default(WeaponProductionWorkerBatch), snapshot);
            }
            finally { WeaponProductionRuntime.EndWorld(); }
            var option = Recipe(); var catalog = Catalog(option);
            using (var actors = new Actors())
            {
                for (int i = 0; i < 2000; i++) Observe(actors, catalog, option, out _);
                long before = GC.GetAllocatedBytesForCurrentThread(); int accepted = 0;
                for (int i = 0; i < 20000; i++) if (Observe(actors, catalog, option, out _)) accepted++;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(20000, accepted); Assert.AreEqual(0L, allocated,
                    "The successful production projection must return its immutable snapshot without allocating.");
            }
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(WeaponProduction).Assembly.Location))
            {
                var read = assembly.MainModule.GetType("APIShared.Economy.WeaponProductionRuntime").Methods.Single(method => method.Name == "TryGetWorkerBatch");
                Assert.IsFalse(read.Body.Instructions.Any(instruction => instruction.OpCode.Code == Code.Newobj ||
                    instruction.OpCode.Code == Code.Newarr || instruction.OpCode.Code == Code.Box),
                    "The read admission must not hoist an allocation ahead of the dormant/monitor guards.");
                Assert.IsTrue(read.Body.Instructions.Any(instruction => instruction.Operand is MethodReference call &&
                    call.DeclaringType.FullName == "System.Threading.Monitor" && call.Name == "IsEntered"),
                    "The public read must require the existing engine monitor before native lookup.");
            }
        }

        [TestMethod]
        public void OptionalBindingFailureCannotSkipTheNativeLoadedCheckpoint()
        {
            // The optional call may fail before its body through a TypeInitializationException.
            // Its compiled caller must therefore catch System.Exception around that call, while
            // retaining the existing Checkpoint outside the protected region and after its handler.
            using (var assembly = AssemblyDefinition.ReadAssembly(typeof(WeaponProduction).Assembly.Location))
            {
                var methods = assembly.MainModule.GetType("APIShared.MissionLifecycleService").NestedTypes
                    .SelectMany(type => type.Methods).Concat(assembly.MainModule.GetType("APIShared.MissionLifecycleService").Methods);
                var caller = methods.Single(method => method.HasBody && method.Body.Instructions.Any(instruction =>
                    instruction.Operand is MethodReference call && call.DeclaringType.FullName == "APIShared.Economy.WeaponProductionRuntime" && call.Name == "BindWorkerObservationWorld"));
                var bind = caller.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call &&
                    call.DeclaringType.FullName == "APIShared.Economy.WeaponProductionRuntime" && call.Name == "BindWorkerObservationWorld");
                var boundary = caller.Body.ExceptionHandlers.Single(handler => handler.HandlerType == ExceptionHandlerType.Catch &&
                    handler.CatchType.FullName == "System.Exception" && handler.TryStart.Offset <= bind.Offset && handler.TryEnd.Offset > bind.Offset);
                Assert.IsNotNull(boundary);
                var checkpoint = caller.Body.Instructions.Single(instruction => instruction.Operand is MethodReference call &&
                    call.DeclaringType.FullName == "APIShared.MissionLifecycleState" && call.Name == "Checkpoint");
                Assert.IsTrue(checkpoint.Offset >= boundary.HandlerEnd.Offset,
                    "A binding failure must continue to the previous NativeLoaded checkpoint.");
            }
        }
    }
}

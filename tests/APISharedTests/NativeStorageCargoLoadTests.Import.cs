using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    public sealed partial class NativeStorageCargoLoadTests
    {
        // Extend the existing real Runtime/registry/lifecycle fixture. The consumer record is
        // deliberately small and synthetic: these tests do not exercise MediumArmour's adapter,
        // native actor behavior, installed critical saving, physical probes or game acceptance.
        private sealed class ImportActor
        {
            internal const long CreationSequence = 41;
            internal const int Carried = 8, BaseYield = 64;
            internal readonly RuntimeBinding Fixture;
            internal readonly NativeStoredGood Good;
            internal readonly string Identifier;
            internal readonly CriticalSaveParticipant Participant;
            internal readonly VersionedSaveDataHandle Handle;
            internal readonly byte[] Original;
            internal readonly bool Carrying;
            internal NativeStorageImportPlan Plan, RepeatedPlan, ChangedPlan, RevokedPlan;
            internal VersionedSaveLoad Receiver;
            internal string Reason;
            internal bool Prepared, ChangedPreparationAccepted, WrongOwnerAccepted, CopiedReceiverAccepted, PrecommitCargoObserved;
            internal int Calls;
            private readonly Action<ImportActor, VersionedSaveLoad> extraPrepared;
            private readonly IReadOnlyList<NativeStorageImportAllocation> physicalAllocations;
            private bool replaceWindow;
            private readonly bool abandonReplacement;

            internal ImportActor(RuntimeBinding fixture, string phase = "carrying", bool replaceWindow = false,
                bool abandonReplacement = false, NativeStoredGood good = null, string identifier = "fish.actor",
                Action<ImportActor, VersionedSaveLoad> extraPrepared = null, int originalBaseYield = BaseYield,
                IReadOnlyList<NativeStorageImportAllocation> allocations = null)
            {
                Fixture = fixture; Good = good ?? fixture.StoredGood; Identifier = identifier; this.extraPrepared = extraPrepared; physicalAllocations = allocations;
                Carrying = phase == "carrying"; this.replaceWindow = replaceWindow; this.abandonReplacement = abandonReplacement;
                Original = VersionedSaveRecordCodec.Encode(2, 2, new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
                { ["value"] = new[] { (byte)(Carrying ? 3 : phase == "ready" ? 2 : 1), (byte)(Carrying ? Carried : originalBaseYield) } });
                var registration = new VersionedSaveDataRegistration(Good.OwnerGuid, Identifier, 3, 3,
                    new[] { "value", "cutover" }, writer => { writer.Write("value", new byte[] { 0 }); return true; }, _ => { })
                { Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = OnPrepared };
                Assert.IsTrue(fixture.Registry.TryRegister(registration, out Handle, out string reason), reason);
                Assert.IsTrue(fixture.Registry.TryTrackPreparedSource(Good.OwnerGuid, Handle, out var source, out reason), reason);
                Assert.IsTrue(fixture.Registry.TryEnrollCriticalSource(source, "cutover", 3, out var enrollment, out reason), reason);
                Assert.IsTrue(fixture.Registry.TryJoinCriticalWriterCohort(enrollment, out reason), reason);
                Participant = new CriticalSaveParticipant(source, enrollment, Handle, true);
                Action previous = fixture.BeforePrepare;
                fixture.BeforePrepare = () => { previous?.Invoke(); fixture.Loads[Identifier](Original); };
            }

            private NativeStorageImportCargo[] Cargo(int quantity) => Carrying
                ? new[] { new NativeStorageImportCargo(Good, CreationSequence, 1, quantity) }
                : Array.Empty<NativeStorageImportCargo>();

            private void OnPrepared(VersionedSaveLoad load)
            {
                Calls++; Receiver = load;
                var rows = physicalAllocations ?? Array.Empty<NativeStorageImportAllocation>();
                WrongOwnerAccepted = Fixture.Runtime.TryPrepareImport("another.owner", Participant, load, rows, Cargo(Carried), out _, out _);
                var copy = new VersionedSaveLoad(load.Status, load.Source, load.FilePath, load.RecordVersion, load.MinimumReaderVersion,
                    load.Entries, load.KeptEntryCount, load.Detail, load.PreparedOrigin, load.MissionSessionId);
                CopiedReceiverAccepted = Fixture.Runtime.TryPrepareImport(Good.OwnerGuid, Participant, copy, rows, Cargo(Carried), out _, out _);
                Prepared = Fixture.Runtime.TryPrepareImport(Good.OwnerGuid, Participant, load, rows, Cargo(Carried), out Plan, out Reason);
                Fixture.Runtime.TryPrepareImport(Good.OwnerGuid, Participant, load, rows, Cargo(Carried), out RepeatedPlan, out _);
                if (Carrying)
                    ChangedPreparationAccepted = Fixture.Runtime.TryPrepareImport(Good.OwnerGuid, Participant, load, rows, Cargo(Carried + 1), out ChangedPlan, out _);
                // Runtime cargo observation has its own installed critical-writer requirement.
                // The state assertions below independently prove that staging issued no lot.
                PrecommitCargoObserved = Fixture.Runtime.TryGetCargo(Good.OwnerGuid, CreationSequence, out _, out _);
                if (Prepared) extraPrepared?.Invoke(this, load);
                if (!replaceWindow) return;
                replaceWindow = false; RevokedPlan = Plan;
                // This is a second real registry window in the SAME owned mission attempt.
                // The outer origin is revoked even though every record byte and session match.
                if (abandonReplacement) Fixture.BeforePrepare = null;
                Fixture.Read(VersionedSaveRecordCodec.Encode(2, 2, Entries(State(false, out _))), Fixture.Session);
            }
        }

        // The actual production owner and import runtime are exercised. Only the closed
        // native resolver is replaced with real packed SDK buffers; this does not prove
        // installed native capabilities or a historical ingredient debit.
        private sealed class ManufacturingOwnerScope : IDisposable
        {
            private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
            private readonly string[] names = { "engine", "registrations", "active", "frozen", "buildings", "workers" };
            private readonly object[] before;
            private readonly string[] safetyNames = { "engineGate", "session", "reason", "notificationQueued", "faultReported", "log", "adoptionAdmission" };
            private readonly object[] safetyBefore;
            private readonly object runBoundaryBefore;
            private readonly byte[] safetyNative = new byte[32];
            private bool activated;
            internal ManufacturingOwnerScope()
            {
                Assert.IsFalse(WeaponProductionRuntime.HasHudDemand);
                Assert.IsFalse(WeaponProductionRuntime.HasTransferDemand);
                before = new object[names.Length];
                for (int i = 0; i < names.Length; i++) before[i] = Field(names[i]).GetValue(null);
                Assert.AreEqual(0, ((System.Collections.IDictionary)before[4]).Count);
                Assert.AreEqual(0, ((System.Collections.IDictionary)before[5]).Count);
                safetyBefore = new object[safetyNames.Length];
                for (int i = 0; i < safetyNames.Length; i++) safetyBefore[i] = SafetyField(safetyNames[i]).GetValue(null);
                runBoundaryBefore = RunBoundaryField.GetValue(RunPublication);
                Marshal.Copy(WorkerProductionWorldSafety.NativeFaultAddress, safetyNative, 0, safetyNative.Length);
            }
            private static FieldInfo Field(string name) => typeof(WeaponProductionRuntime).GetField(name, Flags);
            internal static FieldInfo SafetyField(string name) => typeof(WorkerProductionWorldSafety).GetField(name, Flags);
            // The run boundary is published by the shared run-publication owner (#96).
            private static object RunPublication => SafetyField("runPublication").GetValue(null);
            private static FieldInfo RunBoundaryField => typeof(WorkerProductionRunPublication).GetField("current",
                BindingFlags.Instance | BindingFlags.NonPublic);
            internal static void SetRunBoundary(WorkerProductionRunGate boundary) => RunBoundaryField.SetValue(RunPublication, boundary);
            internal WeaponProductionRegistration Register(NativeStoredGood output)
            {
                var definition = new WeaponProductionOptionDefinition(eStructs.STRUCT_ARMOURERS_WORKSHOP,
                    eGoods.STORED_METAL_ARMOUR, eGoods.STORED_METAL_ARMOUR, "Paid native charge", baseAmount: 2)
                    .WithStoredOutput(output);
                var recipe = new WeaponProductionRegistration(new WeaponProductionOption(
                    new WeaponProductionOptionId(output.OwnerGuid, "paid-native-charge"), definition, false));
                var current = (WeaponProductionRegistration[])Field("registrations").GetValue(null);
                var next = new WeaponProductionRegistration[current.Length + 1];
                Array.Copy(current, next, current.Length); next[current.Length] = recipe;
                Field("registrations").SetValue(null, next);
                return recipe;
            }
            internal void Activate(object gate, WeaponProductionRegistration recipe)
            {
                Assert.IsTrue(Monitor.IsEntered(gate));
                Field("engine").SetValue(null, gate);
                SafetyField("engineGate").SetValue(null, gate);
                WorkerProductionWorldSafety.Admit(MissionLifecycleService.ActiveContext.SessionId, true);
                WorkerProductionWorldSafety.ConfirmPrepared(false);
                activated = true;
                WeaponProductionRuntime.BeginWorld(WeaponProductionCatalog.Compile(new[] { recipe }, _ => true));
                Assert.IsTrue(WeaponProductionRuntime.Activate(true, out string reason), reason);
            }
            public void Dispose()
            {
                try { if (activated) WeaponProductionRuntime.EndWorld(); }
                finally
                {
                    Marshal.Copy(safetyNative, 0, WorkerProductionWorldSafety.NativeFaultAddress, safetyNative.Length);
                    for (int i = 0; i < safetyNames.Length; i++) SafetyField(safetyNames[i]).SetValue(null, safetyBefore[i]);
                    RunBoundaryField.SetValue(RunPublication, runBoundaryBefore);
                    for (int i = 0; i < names.Length; i++) Field(names[i]).SetValue(null, before[i]);
                }
            }
        }
        private sealed unsafe class ManufacturingActorBuffers : IWeaponManufacturingAdoptionSource, IDisposable
        {
            private readonly IntPtr unit = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameUnit)));
            private readonly IntPtr building = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(GameBuilding)));
            internal bool Available = true;
            internal int Reads;
            internal Action<int> BeforeRead;
            internal GameUnit* Unit => (GameUnit*)unit;
            internal ManufacturingActorBuffers()
            {
                new Span<byte>((void*)unit, Marshal.SizeOf(typeof(GameUnit))).Clear();
                new Span<byte>((void*)building, Marshal.SizeOf(typeof(GameBuilding))).Clear();
                Unit->r_GlobalId = 310; Unit->r_AliveState = AliveState.IsAlive;
                Unit->r_UnitChimp = eChimps.CHIMP_TYPE_ARMOURER; Unit->r_ControllableForPlayerId = 1;
                Unit->r_LinkedProductionBuildingId = 12; Unit->r_LinkedProductionBuildingGlobalId = 246;
                Unit->r_AIState = 5; Unit->r_TimeSinceDeathTicker = 37;
                var workshop = (GameBuilding*)building;
                workshop->r_GlobalId = 246; workshop->r_AliveState = AliveState.IsAlive;
                workshop->r_BuildingType = eStructs.STRUCT_ARMOURERS_WORKSHOP;
                workshop->r_PlayerIdOwner = 1; workshop->r_IronIngotsAmount = 19;
            }
            public bool IsRecipeReady(WeaponProductionOption option) => Available;
            public bool TryRead(WeaponProductionPaidManufacturing value, out GameUnit* worker, out GameBuilding* workshop)
            { Reads++; BeforeRead?.Invoke(Reads); worker = Unit; workshop = (GameBuilding*)building; return value.UnitId == 51 && value.BuildingId == 12; }
            internal byte[] UnitBytes => Bytes(unit, Marshal.SizeOf(typeof(GameUnit)));
            internal byte[] BuildingBytes => Bytes(building, Marshal.SizeOf(typeof(GameBuilding)));
            private static byte[] Bytes(IntPtr address, int count)
            { var result = new byte[count]; Marshal.Copy(address, result, 0, count); return result; }
            public void Dispose() { Marshal.FreeHGlobal(unit); Marshal.FreeHGlobal(building); }
        }
        private sealed class OnceCountManufacturing : IReadOnlyList<WeaponProductionPaidManufacturing>
        {
            internal readonly WeaponProductionPaidManufacturing[] Values;
            internal int Reads;
            internal OnceCountManufacturing(WeaponProductionPaidManufacturing value) { Values = new[] { value }; }
            public int Count { get { if (++Reads != 1) throw new InvalidOperationException("Count must be copied once."); return Values.Length; } }
            public WeaponProductionPaidManufacturing this[int index] => Values[index];
            public IEnumerator<WeaponProductionPaidManufacturing> GetEnumerator() => ((IEnumerable<WeaponProductionPaidManufacturing>)Values).GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => Values.GetEnumerator();
        }
        private static NativeStoredGood ManufacturingArmory(RuntimeBinding fixture)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var key = new CustomGoodId(fixture.Good.OwnerGuid, "paid-armour");
            var good = new NativeStoredGood(key, StoragePool.Armory);
            ((List<NativeStorageGoodRegistration>)typeof(NativeStorageRuntime).GetField("definitions", flags).GetValue(fixture.Runtime))
                .Add(new NativeStorageGoodRegistration(key, StoragePool.Armory));
            var handles = typeof(NativeStorageRuntime).GetField("registeredHandles", flags);
            var before = (NativeStoredGood[])handles.GetValue(fixture.Runtime);
            var after = new NativeStoredGood[before.Length + 1]; Array.Copy(before, after, before.Length); after[before.Length] = good;
            handles.SetValue(fixture.Runtime, after); return good;
        }
        private static NativeStorageProbe ManufacturingDestination => new NativeStorageProbe(true, 20, 300, 1, StoragePool.Armory, 0, 50);
        private static NativeStorageImportAllocation[] ManufacturingPhysicalRows(NativeStoredGood good) =>
            new[] { new NativeStorageImportAllocation(good, 20, 300, 1, 3) };
        private static void ManufacturingProbe(RuntimeBinding fixture)
        {
            // Replace only this fixture-owned runtime's native probe boundary. This is
            // not a public callback and does not replace import/publication logic.
            typeof(NativeStorageRuntime).GetField("readProbe", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(fixture.Runtime, (Func<int, NativeStorageProbe>)(slot => slot == 20 ? ManufacturingDestination : default));
        }
        private static byte[] ManufacturingWire(IntPtr address, int id, int stride)
        { var bytes = new byte[stride]; Marshal.Copy(address + id * stride, bytes, 0, stride); return bytes; }

        private static WeaponProductionPaidManufacturing ManufacturingValue(WeaponProductionRegistration recipe, uint global = 310) =>
            new WeaponProductionPaidManufacturing(recipe, 12, 246, 51, global, 1, 2, 1);

        [TestMethod]
        public void ActualImportColdManufacturingAttachmentCopiesOnceAndBindsExactOriginAndRecipe()
        {
            ImportActor actor = null;
            WeaponProductionManufacturingAdoption adoption = null, repeated = null;
            OnceCountManufacturing input = null;
            bool accepted = false, copied = true, foreign = true, changed = true, forged = true;
            using (var owner = new ManufacturingOwnerScope())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); var recipe = owner.Register(good);
                    input = new OnceCountManufacturing(ManufacturingValue(recipe));
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2, extraPrepared: (consumer, load) => {
                        var same = new[] { input.Values[0] };
                        foreign = value.Runtime.TryPrepareManufacturingAdoption("foreign", consumer.Participant, load, consumer.Plan, same, out _, out _);
                        forged = value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan,
                            new[] { ManufacturingValue(new WeaponProductionRegistration(recipe.Option)) }, out _, out _);
                        var copy = new VersionedSaveLoad(load.Status, load.Source, load.FilePath, load.RecordVersion, load.MinimumReaderVersion,
                            load.Entries, load.KeptEntryCount, load.Detail, load.PreparedOrigin, load.MissionSessionId);
                        copied = value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, copy, consumer.Plan, same, out _, out _);
                        accepted = value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan, input, out adoption, out _);
                        value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan, same, out repeated, out _);
                        changed = value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan,
                            new[] { ManufacturingValue(recipe, 311) }, out _, out _);
                    });
                }))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor);
                Assert.IsTrue(accepted); Assert.IsFalse(copied); Assert.IsFalse(foreign); Assert.IsFalse(changed); Assert.IsFalse(forged);
                Assert.AreEqual(1, input.Reads); Assert.AreSame(adoption, repeated); Assert.AreSame(adoption, actor.Plan.ManufacturingAdoption);
                Assert.AreEqual(0, actor.Plan.Cargo.Count); Assert.IsNull(adoption.PublishedWorld);
                input.Values[0] = ManufacturingValue(input.Values[0].Recipe, 999);
                Assert.AreEqual(310u, adoption.Actors[0].UnitGlobalId, "Cold staging must own a copy, not the caller's array.");
                Assert.IsFalse(fixture.Runtime.TryPrepareManufacturingAdoption(actor.Good.OwnerGuid, actor.Participant, actor.Receiver,
                    actor.Plan, new[] { adoption.Actors[0] }, out _, out _), "The Prepared receiver expires outside its callback.");
                Assert.IsFalse(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out _));
                Assert.IsNull(fixture.CurrentState); Assert.AreEqual(0, fixture.Captures);
            }
        }

        [TestMethod]
        public void ActualImportPublishesManufacturingOwnerAndStorageOnceWithoutActorWrites()
        {
            ImportActor actor = null; WeaponProductionRegistration recipe = null;
            WeaponProductionManufacturingAdoption adoption = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var buffers = new ManufacturingActorBuffers())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2, allocations: ManufacturingPhysicalRows(good), extraPrepared: (consumer, load) => {
                        Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan,
                            new[] { ManufacturingValue(recipe) }, out adoption, out string reason), reason);
                    });
                }))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor); ManufacturingProbe(fixture); owner.Activate(fixture.Gate, recipe);
                // Explicit test backend seam: retain the public cold-copy result while
                // substituting only the closed native resolver, never publication logic.
                var attached = new WeaponProductionManufacturingAdoption(actor.Plan, adoption.Values, buffers);
                actor.Plan.ManufacturingAdoption = attached;
                byte[] unit = buffers.UnitBytes, building = buffers.BuildingBytes;
                Assert.IsTrue(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status);
                Assert.AreSame(MissionLifecycleService.NativeWorld, attached.PublishedWorld);
                Assert.IsTrue(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out var option)); Assert.AreSame(recipe.Option, option);
                Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady); Assert.IsTrue(fixture.CurrentState.HasRows);
                Assert.AreEqual(3, fixture.CurrentState.GetQuantity(actor.Good.Key, ManufacturingDestination));
                Assert.IsFalse(fixture.CurrentState.HasCargoHistory); CollectionAssert.AreEqual(unit, buffers.UnitBytes);
                CollectionAssert.AreEqual(building, buffers.BuildingBytes);
                var committed = fixture.CurrentState; var bytes = ImportCapture(committed); int reads = buffers.Reads;
                buffers.Available = false;
                Assert.IsTrue(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out status, out reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, status); Assert.AreEqual(reads, buffers.Reads);
                Assert.AreSame(committed, fixture.CurrentState); ImportBytesEqual(bytes, committed);
                Assert.IsTrue(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out option)); Assert.AreSame(recipe.Option, option);
                var wire = new byte[WeaponProductionRuntime.WorkerWireBytes];
                Marshal.Copy(WeaponProductionRuntime.WorkerWireAddress + 51 * WeaponProductionRuntime.WorkerWireBytes, wire, 0, wire.Length);
                Assert.IsTrue(WeaponProductionRuntime.ValidateWorkerWire(51, 310, eChimps.CHIMP_TYPE_ARMOURER, wire));
                var homeWire = new byte[WeaponProductionRuntime.BuildingWireBytes];
                Marshal.Copy(WeaponProductionRuntime.BuildingWireAddress + 12 * WeaponProductionRuntime.BuildingWireBytes, homeWire, 0, homeWire.Length);
                Assert.IsTrue(WeaponProductionRuntime.ValidateBuildingWire(12, 246, eStructs.STRUCT_ARMOURERS_WORKSHOP, homeWire));
                CollectionAssert.AreEqual(unit, buffers.UnitBytes); CollectionAssert.AreEqual(building, buffers.BuildingBytes);
            }
        }

        [TestMethod]
        public unsafe void ActualImportRefusesChangedManufacturingActorWithoutEitherOwnerPublication()
        {
            ImportActor actor = null; WeaponProductionRegistration recipe = null;
            WeaponProductionManufacturingAdoption adoption = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var buffers = new ManufacturingActorBuffers())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2, allocations: ManufacturingPhysicalRows(good), extraPrepared: (consumer, load) => {
                        Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant, load, consumer.Plan,
                            new[] { ManufacturingValue(recipe) }, out adoption, out string reason), reason);
                    });
                }))
            lock (fixture.Gate)
            {
                ManufacturingProbe(fixture); owner.Activate(fixture.Gate, recipe);
                byte[] beforeWorkerWire = ManufacturingWire(WeaponProductionRuntime.WorkerWireAddress, 51, WeaponProductionRuntime.WorkerWireBytes);
                byte[] beforeBuildingWire = ManufacturingWire(WeaponProductionRuntime.BuildingWireAddress, 12, WeaponProductionRuntime.BuildingWireBytes);
                actor.Plan.ManufacturingAdoption = new WeaponProductionManufacturingAdoption(actor.Plan, adoption.Values, buffers);
                buffers.Unit->r_AIState = 6;
                byte[] unit = buffers.UnitBytes, building = buffers.BuildingBytes;
                AssertImportRefusedWithoutActivation(fixture, actor.Plan);
                Assert.IsNull(actor.Plan.ManufacturingAdoption.PublishedWorld);
                Assert.IsFalse(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out _));
                Assert.IsFalse(ImportPrepared(fixture).State.HasRows);
                CollectionAssert.AreEqual(beforeWorkerWire, ManufacturingWire(WeaponProductionRuntime.WorkerWireAddress, 51, WeaponProductionRuntime.WorkerWireBytes));
                CollectionAssert.AreEqual(beforeBuildingWire, ManufacturingWire(WeaponProductionRuntime.BuildingWireAddress, 12, WeaponProductionRuntime.BuildingWireBytes));
                CollectionAssert.AreEqual(unit, buffers.UnitBytes); CollectionAssert.AreEqual(building, buffers.BuildingBytes);
            }
        }

        [TestMethod]
        public void ActualManufacturingAttachmentCannotBorrowANestedReplacementWindowWithTheSameSession()
        {
            ImportActor actor = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); var recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2, replaceWindow: true,
                        extraPrepared: (consumer, load) => {
                            Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant,
                                load, consumer.Plan, new[] { ManufacturingValue(recipe) }, out _, out string reason), reason);
                        });
                }))
            lock (fixture.Gate)
            {
                Assert.AreEqual(2, actor.Calls); Assert.IsNotNull(actor.RevokedPlan);
                Assert.AreNotSame(actor.RevokedPlan, actor.Plan);
                Assert.IsNotNull(actor.RevokedPlan.ManufacturingAdoption); Assert.IsNotNull(actor.Plan.ManufacturingAdoption);
                Assert.AreEqual(actor.RevokedPlan.MissionSessionId, actor.Plan.MissionSessionId);
                AssertImportRefusedWithoutActivation(fixture, actor.RevokedPlan);
                Assert.IsNull(actor.RevokedPlan.ManufacturingAdoption.PublishedWorld);
                Assert.IsNull(actor.Plan.ManufacturingAdoption.PublishedWorld);
                Assert.IsFalse(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out _));
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public unsafe void ActualManufacturingFinalEngineImportBoundaryRefusesActorOrOriginChangeBeforeJointPublication(bool revokeOrigin)
        {
            ImportActor actor = null; WeaponProductionRegistration recipe = null;
            WeaponProductionManufacturingAdoption adoption = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var buffers = new ManufacturingActorBuffers())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2,
                        allocations: ManufacturingPhysicalRows(good), extraPrepared: (consumer, load) => {
                            Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant,
                                load, consumer.Plan, new[] { ManufacturingValue(recipe) }, out adoption, out string reason), reason);
                        });
                }))
            lock (fixture.Gate)
            {
                ManufacturingProbe(fixture); owner.Activate(fixture.Gate, recipe);
                var attached = new WeaponProductionManufacturingAdoption(actor.Plan, adoption.Values, buffers);
                actor.Plan.ManufacturingAdoption = attached;
                var staged = ImportPrepared(fixture).State; var beforeStorage = ImportCapture(staged);
                byte[] beforeWorkerWire = ManufacturingWire(WeaponProductionRuntime.WorkerWireAddress, 51, WeaponProductionRuntime.WorkerWireBytes);
                byte[] beforeBuildingWire = ManufacturingWire(WeaponProductionRuntime.BuildingWireAddress, 12, WeaponProductionRuntime.BuildingWireBytes);
                byte[] expectedUnit = buffers.UnitBytes, expectedBuilding = buffers.BuildingBytes;
                object importGate = typeof(NativeStorageRuntime).GetField("importSync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Runtime);
                bool injected = false;
                buffers.BeforeRead = _ => {
                    // Exercise the documented engine -> importSync final revalidation,
                    // after preallocation and before the registry commit lease. A call
                    // count is deliberately not part of this ordering contract.
                    if (injected || !Monitor.IsEntered(importGate)) return;
                    Assert.IsTrue(Monitor.IsEntered(fixture.Gate)); injected = true;
                    if (revokeOrigin) fixture.Registry.Unload();
                    else buffers.Unit->r_ControllableForPlayerId = 2;
                    expectedUnit = buffers.UnitBytes; // Allow only the fixture's intentional mutation.
                };
                Assert.IsFalse(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out var status, out string reason));
                Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); Assert.IsFalse(string.IsNullOrEmpty(reason));
                Assert.IsTrue(injected, "The final owner read must run under the actual import monitor.");
                Assert.IsNull(attached.PublishedWorld); Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
                Assert.IsFalse(staged.HasRows); ImportBytesEqual(beforeStorage, staged);
                Assert.IsFalse(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out _));
                CollectionAssert.AreEqual(beforeWorkerWire, ManufacturingWire(WeaponProductionRuntime.WorkerWireAddress, 51, WeaponProductionRuntime.WorkerWireBytes));
                CollectionAssert.AreEqual(beforeBuildingWire, ManufacturingWire(WeaponProductionRuntime.BuildingWireAddress, 12, WeaponProductionRuntime.BuildingWireBytes));
                CollectionAssert.AreEqual(expectedUnit, buffers.UnitBytes); CollectionAssert.AreEqual(expectedBuilding, buffers.BuildingBytes);
                Assert.AreEqual(0, fixture.Captures);
            }
        }

        // The actual run protocol reads actual Safety/registry state. Only Original is
        // a counting double: this does not execute native workers or reserve game buffers.
        private static WorkerProductionRunGate ManufacturingRunBoundary(RuntimeBinding fixture, Func<bool, int> original)
        {
            var boundary = new WorkerProductionRunGate(fixture.Gate, () => WorkerProductionWorldSafety.Generation,
                () => WorkerProductionWorldSafety.Enabled, () => WorkerProductionWorldSafety.IsFaulted,
                () => WorkerProductionWorldSafety.IsAdmitted, original, WorkerProductionWorldSafety.ObserveNativeFault);
            ManufacturingOwnerScope.SetRunBoundary(boundary);
            return boundary;
        }

        [TestMethod]
        public void ActualAdoptedAdmissionUsesDirectRegistryGenerationWhileItsImportObserverIsBlocked()
        {
            ImportActor actor = null; WeaponProductionRegistration recipe = null;
            WeaponProductionManufacturingAdoption adoption = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var buffers = new ManufacturingActorBuffers())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2,
                        allocations: ManufacturingPhysicalRows(good), extraPrepared: (consumer, load) => {
                            Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant,
                                load, consumer.Plan, new[] { ManufacturingValue(recipe) }, out adoption, out string reason), reason);
                        });
                }))
            {
                int calls = 0; Exception metadataFailure = null;
                var metadata = new Thread(() => { try { fixture.Registry.Unload(); } catch (Exception error) { metadataFailure = error; } })
                    { IsBackground = true };
                bool started = false;
                try
                {
                    lock (fixture.Gate)
                    {
                        ManufacturingProbe(fixture); owner.Activate(fixture.Gate, recipe);
                        var boundary = ManufacturingRunBoundary(fixture, _ => { Assert.IsTrue(Monitor.IsEntered(fixture.Gate)); calls++; return 73; });
                        Assert.IsFalse(WorkerProductionWorldSafety.Enabled, "No-stamp ConfirmPrepared(false) retains ordinary dormant execution.");
                        Assert.AreEqual(73, boundary.Run(false)); Assert.AreEqual(1, calls);
                        var attached = new WeaponProductionManufacturingAdoption(actor.Plan, adoption.Values, buffers);
                        actor.Plan.ManufacturingAdoption = attached;
                        Assert.IsTrue(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out _, out string reason), reason);
                        Assert.AreEqual(3, fixture.CurrentState.GetQuantity(actor.Good.Key, ManufacturingDestination));
                        Assert.IsTrue(WeaponProductionRuntime.TryGetBatch(12, 246, 51, 310, out _));
                        var stamp = (WorkerProductionAdoptionStamp)ManufacturingOwnerScope.SafetyField("adoptionAdmission").GetValue(null);
                        Assert.IsNotNull(stamp);
                        WorkerProductionWorldSafety.ConfirmPrepared(false);
                        Assert.AreEqual(0, Marshal.ReadInt32(WorkerProductionWorldSafety.NativeFaultAddress, WorkerProductionWorldSafety.GuardEnabledOffset));
                        Assert.IsTrue(WorkerProductionWorldSafety.Enabled, "An adopted source retains its managed admission check with native arithmetic guard off.");
                        Assert.AreEqual(73, boundary.Run(true)); Assert.AreEqual(2, calls);
                        long archiveGeneration = fixture.Registry.CurrentGeneration;
                        object boundWorld = fixture.BoundWorld;
                        object importGate = typeof(NativeStorageRuntime).GetField("importSync", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Runtime);
                        lock (importGate)
                        {
                            metadata.Start(); started = true;
                            Assert.IsTrue(SpinWait.SpinUntil(() => fixture.Registry.CurrentGeneration != archiveGeneration, TimeSpan.FromSeconds(5)),
                                "Registry metadata must advance before its blocked import observer completes.");
                            Assert.IsTrue(metadata.IsAlive, "The observer remains blocked until importSync is released.");
                            Assert.AreSame(boundWorld, fixture.BoundWorld, "The import observer has not yet revoked its old binding.");
                            Assert.IsFalse(WorkerProductionWorldSafety.IsAdmitted);
                            Assert.AreEqual(0, boundary.Run(false)); Assert.AreEqual(2, calls, "Direct generation mismatch must deny Original despite observer lag.");
                            WorkerProductionWorldSafety.ConfirmPrepared(false);
                            Assert.IsFalse(WorkerProductionWorldSafety.IsAdmitted, "ConfirmPrepared cannot erase the adopted source contract.");
                            Assert.IsFalse(WorkerProductionWorldSafety.TryPrepareAdoptionAdmission(fixture.Registry, fixture.Registry.CurrentGeneration, out _),
                                "Another metadata import cannot restamp a revoked native epoch.");
                            Assert.IsFalse(WorkerProductionWorldSafety.CanPublishAdoptionAdmission(stamp));
                            Assert.AreSame(stamp, ManufacturingOwnerScope.SafetyField("adoptionAdmission").GetValue(null));
                        }
                    }
                }
                finally
                {
                    if (started) Assert.IsTrue(metadata.Join(TimeSpan.FromSeconds(5)), "Metadata observer must finish once its monitor is released.");
                }
                Assert.IsNull(metadataFailure); Assert.IsNull(fixture.BoundWorld);
                lock (fixture.Gate)
                {
                    // Exercise only the actual safety epoch transitions here. A counting
                    // Original does not establish complete native mission reconstruction.
                    WorkerProductionWorldSafety.Admit(fixture.Session + 1, true);
                    Assert.IsNull(ManufacturingOwnerScope.SafetyField("adoptionAdmission").GetValue(null));
                    Assert.IsFalse(WorkerProductionWorldSafety.IsAdmitted);
                    Assert.AreEqual(0, WorkerProductionWorldSafety.RunBoundary.Run(false)); Assert.AreEqual(2, calls);
                    WorkerProductionWorldSafety.ConfirmPrepared(false);
                    Assert.IsFalse(WorkerProductionWorldSafety.Enabled);
                    Assert.AreEqual(73, WorkerProductionWorldSafety.RunBoundary.Run(false)); Assert.AreEqual(3, calls);
                    Assert.IsTrue(WorkerProductionWorldSafety.TryPrepareAdoptionAdmission(fixture.Registry, fixture.Registry.CurrentGeneration, out var next));
                    Assert.IsTrue(WorkerProductionWorldSafety.CanPublishAdoptionAdmission(next));
                    WorkerProductionWorldSafety.PublishAdoptionAdmission(next);
                    WorkerProductionWorldSafety.Retire();
                    Assert.IsNull(ManufacturingOwnerScope.SafetyField("adoptionAdmission").GetValue(null));
                    Assert.IsFalse(WorkerProductionWorldSafety.IsAdmitted);
                    Assert.AreEqual(0, WorkerProductionWorldSafety.RunBoundary.Run(false)); Assert.AreEqual(3, calls);
                }
            }
        }

        [TestMethod]
        public void ActualAdoptedRunAlreadyEnteredMayFinishButTheNextEntryIsDenied()
        {
            ImportActor actor = null; WeaponProductionRegistration recipe = null;
            WeaponProductionManufacturingAdoption adoption = null;
            using (var owner = new ManufacturingOwnerScope())
            using (var buffers = new ManufacturingActorBuffers())
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, activate: false,
                configure: value => {
                    var good = ManufacturingArmory(value); recipe = owner.Register(good);
                    actor = new ImportActor(value, phase: "forging", good: good, originalBaseYield: 2,
                        allocations: ManufacturingPhysicalRows(good), extraPrepared: (consumer, load) => {
                            Assert.IsTrue(value.Runtime.TryPrepareManufacturingAdoption(good.OwnerGuid, consumer.Participant,
                                load, consumer.Plan, new[] { ManufacturingValue(recipe) }, out adoption, out string reason), reason);
                        });
                }))
            lock (fixture.Gate)
            {
                ManufacturingProbe(fixture); owner.Activate(fixture.Gate, recipe);
                actor.Plan.ManufacturingAdoption = new WeaponProductionManufacturingAdoption(actor.Plan, adoption.Values, buffers);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out _, out string reason), reason);
                WorkerProductionWorldSafety.ConfirmPrepared(false);
                int calls = 0; long generation = fixture.Registry.CurrentGeneration;
                var boundary = ManufacturingRunBoundary(fixture, _ => { calls++; fixture.Registry.Unload(); return 73; });
                // Entry had already passed admission before this metadata revocation.
                // The unchanged run protocol promises denial of future entry, not an
                // asynchronous abort/rollback of one already-entered native run.
                Assert.AreEqual(73, boundary.Run(false)); Assert.AreEqual(1, calls);
                Assert.AreNotEqual(generation, fixture.Registry.CurrentGeneration);
                Assert.IsFalse(WorkerProductionWorldSafety.IsAdmitted);
                Assert.AreEqual(0, boundary.Run(false)); Assert.AreEqual(1, calls);
            }
        }

        private static NativeStoragePreparedLoad ImportPrepared(RuntimeBinding fixture) => (NativeStoragePreparedLoad)fixture.PreparedArchive;
        private static (byte[] Goods, byte[] Stores) ImportCapture(NativeStorageState state)
        { state.Capture(out byte[] goods, out byte[] stores); return (goods, stores); }
        private static void ImportBytesEqual((byte[] Goods, byte[] Stores) before, NativeStorageState state)
        {
            var after = ImportCapture(state);
            CollectionAssert.AreEqual(before.Goods, after.Goods); CollectionAssert.AreEqual(before.Stores, after.Stores);
        }
        private static void AssertImportStaged(RuntimeBinding fixture, ImportActor actor)
        {
            Assert.IsTrue(actor.Prepared, actor.Reason); Assert.IsNotNull(actor.Plan);
            Assert.AreSame(actor.Plan, actor.RepeatedPlan); Assert.IsFalse(actor.ChangedPreparationAccepted); Assert.IsNull(actor.ChangedPlan);
            Assert.IsFalse(actor.WrongOwnerAccepted); Assert.IsFalse(actor.CopiedReceiverAccepted); Assert.IsFalse(actor.PrecommitCargoObserved);
            Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
            Assert.IsNotNull(ImportPrepared(fixture).State); Assert.IsFalse(ImportPrepared(fixture).Blocked);
            Assert.IsFalse(ImportPrepared(fixture).State.HasRows); Assert.IsFalse(ImportPrepared(fixture).State.HasCargoHistory);
            Assert.IsFalse(ImportPrepared(fixture).State.TryGetCargo(fixture.Good.OwnerGuid, ImportActor.CreationSequence, out _));
            Assert.AreEqual(0, fixture.Captures);
            var prepared = ImportPrepared(fixture); var staged = prepared.State; var before = ImportCapture(staged);
            Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady);
            Assert.IsFalse(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out _), "A pending actor import must block ordinary first-query activation.");
            Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
            Assert.AreSame(staged, prepared.State); ImportBytesEqual(before, staged);
        }
        private static void AssertImportRefusedWithoutActivation(RuntimeBinding fixture, NativeStorageImportPlan plan, string owner = null)
        {
            var prepared = ImportPrepared(fixture); var staged = prepared.State; var before = ImportCapture(staged);
            NativeStorageState current = fixture.CurrentState; object world = fixture.BoundWorld;
            int captures = fixture.Captures;
            Assert.IsFalse(fixture.Runtime.TryCommitImport(owner ?? fixture.Good.OwnerGuid, plan, out var status, out string reason));
            Assert.AreEqual(GoodsTransactionStatus.InvalidRequest, status); Assert.IsFalse(string.IsNullOrEmpty(reason));
            Assert.AreSame(current, fixture.CurrentState); Assert.AreSame(world, fixture.BoundWorld);
            Assert.AreSame(prepared, fixture.PreparedArchive); Assert.AreSame(staged, prepared.State);
            Assert.AreEqual(captures, fixture.Captures); ImportBytesEqual(before, staged);
        }

        private static NativeStoredGood ImportAdditionalGood(RuntimeBinding fixture)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var key = new CustomGoodId("second.import.owner", "second-output");
            var good = new NativeStoredGood(key, StoragePool.Granary);
            var definitions = (List<NativeStorageGoodRegistration>)typeof(NativeStorageRuntime).GetField("definitions", flags).GetValue(fixture.Runtime);
            definitions.Add(new NativeStorageGoodRegistration(key, StoragePool.Granary));
            var field = typeof(NativeStorageRuntime).GetField("registeredHandles", flags);
            var before = (NativeStoredGood[])field.GetValue(fixture.Runtime);
            var handles = new NativeStoredGood[before.Length + 1]; Array.Copy(before, handles, before.Length); handles[before.Length] = good;
            field.SetValue(fixture.Runtime, handles);
            return good;
        }

        private static NativeStorageRuntime ImportRuntimeWithDemandSink(Action<bool> sink) =>
            (NativeStorageRuntime)Activator.CreateInstance(typeof(NativeStorageRuntime),
                BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { sink }, null);

        [TestMethod]
        public void ActualRuntimeDelayedDemandRefreshCannotReopenRevokedCargoOnlyImport()
        {
            var demand = new List<bool>(16);
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value), activate: false, demandSink: demand.Add))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(actor.Good.OwnerGuid, actor.Plan, out _, out string reason), reason);
                Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsFalse(fixture.CurrentState.HasRows);
                Assert.IsFalse(demand[demand.Count - 1]);
                var retained = fixture.CurrentState; var bytes = ImportCapture(retained);
                // This is the exact metadata operation which can overtake the final
                // refresh after publication. Native world metadata is still current.
                fixture.Registry.Unload();
                Assert.IsNull(fixture.BoundWorld);
                fixture.Runtime.RefreshImportPublicationDemand();
                Assert.IsTrue(demand[demand.Count - 1], "A late empty-stock refresh must retain revoked-world protection.");
                Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady);
                Assert.AreSame(retained, fixture.CurrentState); ImportBytesEqual(bytes, retained);
            }
        }

        [TestMethod]
        public void ActualRuntimeQualifiedReplacementMayClearConservativeDemand()
        {
            var demand = new List<bool>(16);
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, demandSink: demand.Add))
            lock (fixture.Gate)
            {
                Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady); Assert.IsFalse(demand[demand.Count - 1]);
                fixture.Registry.Unload(); fixture.Runtime.RefreshImportPublicationDemand();
                Assert.IsTrue(demand[demand.Count - 1]); Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady);
                fixture.Begin(MissionStartKind.LoadedSave);
                fixture.Read(null, fixture.Session); fixture.Ready();
                Assert.IsTrue(fixture.Runtime.TryGetStock(fixture.Good, 1, out var balance, out string reason), reason);
                Assert.AreEqual(0, balance.Quantity); Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsFalse(demand[demand.Count - 1], "An exact complete replacement may reopen empty capacity.");
                var retained = fixture.CurrentState; var bytes = ImportCapture(retained);
                fixture.RefreshMode(true);
                Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady, "Protected readiness must include its supported mode.");
                Assert.IsFalse(fixture.Runtime.TryGetStock(fixture.Good, 1, out _, out _));
                Assert.AreSame(retained, fixture.CurrentState); ImportBytesEqual(bytes, retained);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void ActualRuntimeMultiplayerActivationPreservesOrdinaryReadsAndRefusesProtectedState(bool protectedStorage)
        {
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: protectedStorage, activate: false))
            lock (fixture.Gate)
            {
                var prepared = ImportPrepared(fixture); var staged = prepared.State; var bytes = ImportCapture(staged);
                fixture.RefreshMode(true);
                bool accepted = fixture.Runtime.TryGetStock(fixture.Good, 1, out var balance, out _);
                Assert.AreEqual(!protectedStorage, accepted);
                Assert.AreEqual(!protectedStorage, fixture.Runtime.IsCurrentWorldReady);
                if (protectedStorage)
                {
                    Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
                    Assert.AreSame(staged, prepared.State); ImportBytesEqual(bytes, staged);
                }
                else Assert.AreEqual(0, balance.Quantity);
            }
        }

        [TestMethod]
        public void ActualRuntimeOrdinaryEmptyDemandRetainsItsOriginalFalseValue()
        {
            var demand = new List<bool>(16);
            using (var fixture = new RuntimeBinding(false, cargo: false, demandSink: demand.Add))
            lock (fixture.Gate)
            {
                fixture.Registry.Unload(); fixture.Runtime.RefreshImportPublicationDemand();
                Assert.IsFalse(demand[demand.Count - 1]);
                // Ordinary blocked-record protection predates import enrollment.
                // The shared publisher must preserve that original true input too.
                typeof(NativeStorageRuntime).GetMethod("Block", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(fixture.Runtime, new object[] { "fixture blocked record" });
                fixture.Runtime.RefreshImportPublicationDemand();
                Assert.IsTrue(demand[demand.Count - 1]);
            }
        }

        [TestMethod]
        public void AcceptedProtectedRequestRetainsDemandBeforeSourceTrackingCompletes()
        {
            var demand = new List<bool>(4);
            NativeStorageRuntime runtime = ImportRuntimeWithDemandSink(demand.Add);
            typeof(NativeStorageRuntime).GetField("criticalEnrollmentRequested", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(runtime, true);
            runtime.RefreshImportPublicationDemand();
            Assert.IsTrue(demand[demand.Count - 1], "Accepted protected startup cannot clear unclassified capacity before tracking succeeds.");
        }

        [TestMethod]
        public void ActualRuntimeImportsCargoFromUnactivatedStageOnceWithPreparedActorSequenceMapping()
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value), activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor);
                Assert.AreEqual(1, actor.Plan.Cargo.Count); Assert.AreSame(fixture.StoredGood, actor.Plan.Cargo[0].Good);
                Assert.AreEqual(ImportActor.CreationSequence, actor.Plan.Cargo[0].CreationSequence);
                Assert.AreEqual(ImportActor.Carried, actor.Plan.Cargo[0].Quantity);
                long preparedActorSequence = actor.Plan.Cargo[0].CreationSequence;
                var prepared = ImportPrepared(fixture); var staged = prepared.State; var original = ImportCapture(staged);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(fixture.Good.OwnerGuid, actor.Plan, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status);
                Assert.AreSame(MissionLifecycleService.NativeWorld, fixture.BoundWorld); Assert.IsNotNull(fixture.CurrentState);
                Assert.AreNotSame(staged, fixture.CurrentState); Assert.IsNull(prepared.State); ImportBytesEqual(original, staged);
                Assert.IsTrue(fixture.CurrentState.TryGetCargo(fixture.Good.OwnerGuid, preparedActorSequence, out var cargo));
                Assert.AreEqual(ImportActor.Carried, cargo.Issued); Assert.AreEqual(ImportActor.Carried, cargo.Remaining);
                Assert.AreEqual(0L, fixture.CurrentState.GetLastSequence(fixture.Good.OwnerGuid));
                Assert.IsFalse(fixture.CurrentState.HasRows); Assert.AreEqual(0, fixture.Captures);
                var committed = fixture.CurrentState; var committedBytes = ImportCapture(committed);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(fixture.Good.OwnerGuid, actor.Plan, out status, out reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.AlreadyApplied, status); Assert.AreSame(committed, fixture.CurrentState);
                ImportBytesEqual(committedBytes, committed); Assert.AreEqual(1, committed.CargoRecordCount);
            }
        }

        [TestMethod]
        public void ActualRuntimeRejectsWrongOwnerAndTheExpiredPreparedReceiverWithoutActivatingStorage()
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value), activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor);
                var staged = ImportPrepared(fixture).State; var before = ImportCapture(staged);
                Assert.IsFalse(fixture.Runtime.TryPrepareImport(fixture.Good.OwnerGuid, actor.Participant, actor.Receiver,
                    Array.Empty<NativeStorageImportAllocation>(), actor.Plan.Cargo, out var stalePlan, out string reason));
                Assert.IsNull(stalePlan); Assert.IsFalse(string.IsNullOrEmpty(reason));
                AssertImportRefusedWithoutActivation(fixture, actor.Plan, "another.owner");
                Assert.IsNull(fixture.BoundWorld); Assert.IsNull(fixture.CurrentState); ImportBytesEqual(before, staged);
            }
        }

        [TestMethod]
        public void ActualRuntimeRejectsAnUnloadedSourceBeforeAnyStagedStateOrReceiptPublication()
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value), activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor); fixture.Registry.Unload();
                AssertImportRefusedWithoutActivation(fixture, actor.Plan);
                Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
            }
        }

        [TestMethod]
        public void ActualRuntimeRejectsRevokedSameAttemptWindowButAcceptsItsExactReplacementPlan()
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value, replaceWindow: true), activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor); Assert.AreEqual(2, actor.Calls);
                Assert.IsNotNull(actor.RevokedPlan); Assert.AreNotSame(actor.RevokedPlan, actor.Plan);
                Assert.AreEqual(actor.RevokedPlan.MissionSessionId, actor.Plan.MissionSessionId);
                AssertImportRefusedWithoutActivation(fixture, actor.RevokedPlan);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(fixture.Good.OwnerGuid, actor.Plan, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status);
                Assert.IsTrue(fixture.CurrentState.TryGetCargo(fixture.Good.OwnerGuid, ImportActor.CreationSequence, out var cargo));
                Assert.AreEqual(ImportActor.Carried, cargo.Remaining); Assert.AreEqual(1, fixture.CurrentState.CargoRecordCount);
            }
        }

        [TestMethod]
        public void ActualRuntimeKeepsOrdinaryActivationBlockedUntilEveryPendingOwnerHasCommitted()
        {
            ImportActor first = null, second = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true, configure: value =>
            {
                first = new ImportActor(value);
                second = new ImportActor(value, good: ImportAdditionalGood(value), identifier: "second.actor");
            }, activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, first); AssertImportStaged(fixture, second);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(first.Good.OwnerGuid, first.Plan, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status);
                var partial = fixture.CurrentState; var before = ImportCapture(partial);
                Assert.IsTrue(partial.TryGetCargo(first.Good.OwnerGuid, ImportActor.CreationSequence, out _));
                Assert.IsFalse(partial.TryGetCargo(second.Good.OwnerGuid, ImportActor.CreationSequence, out _));
                Assert.IsFalse(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsFalse(fixture.Runtime.TryGetStock(second.Good.Key, 1, out _, out _));
                Assert.AreSame(partial, fixture.CurrentState); ImportBytesEqual(before, partial);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(second.Good.OwnerGuid, second.Plan, out status, out reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status); Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                Assert.IsTrue(fixture.Runtime.TryGetStock(second.Good.Key, 1, out var balance, out reason), reason); Assert.AreEqual(0, balance.Quantity);
                Assert.AreEqual(2, fixture.CurrentState.CargoRecordCount);
                Assert.IsTrue(fixture.CurrentState.TryGetCargo(first.Good.OwnerGuid, ImportActor.CreationSequence, out var firstLot));
                Assert.IsTrue(fixture.CurrentState.TryGetCargo(second.Good.OwnerGuid, ImportActor.CreationSequence, out var secondLot));
                Assert.AreEqual(ImportActor.Carried, firstLot.Remaining); Assert.AreEqual(ImportActor.Carried, secondLot.Remaining);
            }
        }

        [TestMethod]
        public void ActualRuntimeNewArchiveWindowClearsAbandonedImportBarrierWithoutReplayingItsStock()
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value, replaceWindow: true, abandonReplacement: true), activate: false))
            lock (fixture.Gate)
            {
                Assert.AreEqual(2, actor.Calls); Assert.IsNotNull(actor.RevokedPlan);
                Assert.IsFalse(actor.Prepared); Assert.IsNull(actor.Plan);
                Assert.IsNull(fixture.CurrentState); Assert.IsNull(fixture.BoundWorld);
                var staged = ImportPrepared(fixture).State; Assert.IsNotNull(staged); var before = ImportCapture(staged);
                AssertImportRefusedWithoutActivation(fixture, actor.RevokedPlan);
                Assert.IsTrue(fixture.Runtime.TryGetStock(fixture.Good, 1, out var balance, out string reason), reason);
                Assert.AreEqual(0, balance.Quantity); Assert.IsTrue(fixture.Runtime.IsCurrentWorldReady);
                ImportBytesEqual(before, fixture.CurrentState);
                Assert.IsFalse(fixture.CurrentState.HasCargoHistory); Assert.AreEqual(0, fixture.CurrentState.CargoRecordCount);
                Assert.AreEqual(0L, fixture.CurrentState.GetLastCargoSequence(fixture.Good.OwnerGuid));
            }
        }

        [TestMethod]
        [DataRow("forging")]
        [DataRow("ready")]
        public void ActualRuntimeActorOnlyImportPreservesProgressWithoutTurningBaseYieldIntoFinalCargo(string phase)
        {
            ImportActor actor = null;
            using (var fixture = new RuntimeBinding(false, cargo: false, protectedStorage: true,
                configure: value => actor = new ImportActor(value, phase), activate: false))
            lock (fixture.Gate)
            {
                AssertImportStaged(fixture, actor); Assert.AreEqual(0, actor.Plan.Cargo.Count);
                Assert.IsTrue(VersionedSaveRecordCodec.Decode(actor.Original, 3).Entries.TryGet("value", out byte[] progress));
                Assert.AreEqual(ImportActor.BaseYield, (int)progress[1]);
                var original = ImportCapture(ImportPrepared(fixture).State);
                Assert.IsTrue(fixture.Runtime.TryCommitImport(fixture.Good.OwnerGuid, actor.Plan, out var status, out string reason), reason);
                Assert.AreEqual(GoodsTransactionStatus.Applied, status); ImportBytesEqual(original, fixture.CurrentState);
                Assert.IsFalse(fixture.CurrentState.HasCargoHistory); Assert.AreEqual(0, fixture.CurrentState.CargoRecordCount);
                Assert.AreEqual(0L, fixture.CurrentState.GetLastCargoSequence(fixture.Good.OwnerGuid));
                Assert.IsFalse(fixture.CurrentState.TryGetCargo(fixture.Good.OwnerGuid, ImportActor.CreationSequence, out _));
                Assert.IsTrue(fixture.CurrentState.TryGetBalance(fixture.Good, 1, out var balance)); Assert.AreEqual(0, balance.Quantity);
                Assert.IsFalse(fixture.CurrentState.HasRows); Assert.AreEqual(0L, fixture.CurrentState.GetLastSequence(fixture.Good.OwnerGuid));
            }
        }
    }
}

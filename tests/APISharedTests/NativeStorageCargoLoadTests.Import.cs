using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            private bool replaceWindow;
            private readonly bool abandonReplacement;

            internal ImportActor(RuntimeBinding fixture, string phase = "carrying", bool replaceWindow = false,
                bool abandonReplacement = false, NativeStoredGood good = null, string identifier = "fish.actor")
            {
                Fixture = fixture; Good = good ?? fixture.StoredGood; Identifier = identifier;
                Carrying = phase == "carrying"; this.replaceWindow = replaceWindow; this.abandonReplacement = abandonReplacement;
                Original = VersionedSaveRecordCodec.Encode(2, 2, new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
                { ["value"] = new[] { (byte)(Carrying ? 3 : phase == "ready" ? 2 : 1), (byte)(Carrying ? Carried : BaseYield) } });
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
                var rows = Array.Empty<NativeStorageImportAllocation>();
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
                if (!replaceWindow) return;
                replaceWindow = false; RevokedPlan = Plan;
                // This is a second real registry window in the SAME owned mission attempt.
                // The outer origin is revoked even though every record byte and session match.
                if (abandonReplacement) Fixture.BeforePrepare = null;
                Fixture.Read(VersionedSaveRecordCodec.Encode(2, 2, Entries(State(false, out _))), Fixture.Session);
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

using System;
using System.Collections.Generic;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // The same prepared-load controller used by Runtime.Prepare is bound to the actual
    // versioned registry. The transport is managed; no native world or archive is executed.
    [TestClass]
    public sealed class NativeStorageCargoLoadTests
    {
        private sealed class Binding
        {
            internal readonly NativeStoragePreparedLoad Prepared = new NativeStoragePreparedLoad();
            internal readonly VersionedSaveDataRegistry Registry;
            internal readonly VersionedSaveDataHandle Handle;
            internal Func<VersionedSaveContext, byte[]> Save;
            internal Action<byte[]> Load;
            internal int Captures;
            internal Binding(IReadOnlyList<NativeStorageGoodRegistration> definitions)
            {
                Registry = new VersionedSaveDataRegistry((id, save, load) => { Save = save; Load = load; return true; }, _ => { }, _ => { });
                var registration = new VersionedSaveDataRegistration("cargo.test", "cargo-envelope", 2, 1, new[] { "ledger", "stores" },
                    writer => { Captures++; writer.Write("ledger", new byte[] { 99 }); writer.Write("stores", new byte[] { 99 }); return true; }, _ => { })
                { Scope = VersionedSaveScope.SaveGamesAndMaps, Prepared = load => Prepared.Prepare(definitions, load) };
                Assert.IsTrue(Registry.TryRegister(registration, out Handle, out string reason), reason);
            }
            internal void Read(byte[] record)
            {
                Registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "cargo.sav");
                if (record != null) Load(record);
                Registry.PrepareLoad(); Registry.EndLoad();
            }
            internal byte[] SaveCurrent() => Save(new VersionedSaveContext(true, false, "cargo.sav"));
        }
        private static NativeStorageState State(bool cargo, out CustomGoodId good)
        {
            var state = new NativeStorageState(); good = new CustomGoodId("fish.owner", "fish");
            Assert.IsTrue(state.TryRegister(good, StoragePool.Granary, out _));
            if (cargo)
            {
                Assert.AreEqual(GoodsTransactionStatus.Applied, state.PrepareCreateCargo(good, 1, 1, 8, out var plan, out _, out _));
                using (plan) plan.Publish();
            }
            return state;
        }
        private static SortedDictionary<string, byte[]> Entries(NativeStorageState state)
        {
            state.Capture(out byte[] ledger, out byte[] stores);
            return new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["ledger"] = ledger, ["stores"] = stores };
        }
        [TestMethod]
        [DataRow(1, 1)]
        [DataRow(2, 1)]
        public void ActualPreparedBindingRejectsUnderdeclaredCargoEnvelopeWithoutStagingAndPreservesOriginalBytes(int recordVersion, int minimumReader)
        {
            var state = State(true, out _); byte[] original = VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state));
            var before = (byte[])original.Clone(); var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            StringAssert.Contains(binding.Prepared.Reason, "minimum reader 2");
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord);
            CollectionAssert.AreEqual(before, binding.SaveCurrent()); CollectionAssert.AreEqual(before, original);
            Assert.AreEqual(0, binding.Captures, "Rejected cargo must never be rewritten from an empty/new candidate.");
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, binding.Prepared.State));
        }
        [TestMethod]
        [DataRow(1, 1)]
        [DataRow(2, 1)]
        [DataRow(5, 1)]
        public void LegacyPayloadAndFutureCompatibleEnvelopeRetainTheirExistingLoadBehavior(int recordVersion, int minimumReader)
        {
            var state = State(false, out _); var binding = new Binding(state.GetRegistrations());
            binding.Read(VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state)));
            Assert.IsFalse(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo);
            Assert.IsNotNull(binding.Prepared.State); Assert.IsFalse(binding.Prepared.State.HasCargoHistory);
            Assert.IsFalse(binding.Handle.KeepsLoadedRecord);
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(true, false, binding.Prepared.State, null));
        }
        [TestMethod]
        [DataRow(2, 2)]
        [DataRow(5, 2)]
        public void CargoPayloadWithQualifiedReaderMetadataStagesOnlyTheValidatedOwner(int recordVersion, int minimumReader)
        {
            var state = State(true, out var good); var binding = new Binding(state.GetRegistrations());
            binding.Read(VersionedSaveRecordCodec.Encode(recordVersion, minimumReader, Entries(state)));
            Assert.IsFalse(binding.Prepared.Blocked); Assert.IsNotNull(binding.Prepared.State); Assert.IsTrue(binding.Prepared.State.HasCargoHistory);
            Assert.IsTrue(binding.Prepared.State.TryGetCargo(good.OwnerGuid, 1, out var lot)); Assert.AreEqual(8, lot.Remaining);
            Assert.IsFalse(binding.Handle.KeepsLoadedRecord);
        }
        [TestMethod]
        public void MalformedProvablyLegacyStoresKeepOriginalSourceWithoutActivatingNewCargoSavePolicy()
        {
            var state = State(false, out _); var entries = Entries(state);
            // Keep the valid stores magic/schema-1 header, but truncate the old physical body.
            // This pair is invalid, yet it provably contains no schema-2 cargo tail.
            byte[] corrupt = new byte[entries["stores"].Length - 1]; Array.Copy(entries["stores"], corrupt, corrupt.Length); entries["stores"] = corrupt;
            byte[] original = VersionedSaveRecordCodec.Encode(1, 1, entries); var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(original, binding.SaveCurrent());
            Assert.IsFalse(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, null));
        }
        [TestMethod]
        [DataRow("too-new")]
        [DataRow("unrecognized")]
        [DataRow("missing-stores")]
        public void PassiveRecordsThatCannotProveLegacySemanticsRemainConservativelyCargoUnknown(string kind)
        {
            var state = State(true, out _); var entries = Entries(state);
            byte[] original;
            if (kind == "unrecognized") original = new byte[] { 1, 2, 3 };
            else
            {
                if (kind == "missing-stores") entries.Remove("stores");
                original = VersionedSaveRecordCodec.Encode(kind == "too-new" ? 3 : 1, kind == "too-new" ? 3 : 1, entries);
            }
            var binding = new Binding(state.GetRegistrations()); binding.Read(original);
            Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo); Assert.IsNull(binding.Prepared.State);
            Assert.IsTrue(binding.Handle.KeepsLoadedRecord); CollectionAssert.AreEqual(original, binding.SaveCurrent());
            Assert.IsTrue(NativeStorageRuntime.CargoSaveCheckpointRequired(true,
                binding.Prepared.Blocked && binding.Prepared.MayContainCargo, null, null));
        }
        [TestMethod]
        public void ANewNoRecordWorldClearsThePreviousPassiveClassification()
        {
            var state = State(true, out _); var binding = new Binding(state.GetRegistrations());
            binding.Read(new byte[] { 1, 2, 3 }); Assert.IsTrue(binding.Prepared.Blocked); Assert.IsTrue(binding.Prepared.MayContainCargo);
            binding.Read(null); Assert.IsFalse(binding.Prepared.Blocked); Assert.IsFalse(binding.Prepared.MayContainCargo);
            Assert.IsNotNull(binding.Prepared.State); Assert.IsFalse(binding.Prepared.State.HasCargoHistory);
        }
    }
}

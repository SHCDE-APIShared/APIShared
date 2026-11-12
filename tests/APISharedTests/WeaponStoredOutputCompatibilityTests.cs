using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.Goods;
using APIShared.Savegames;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Exercise admission and capture metadata on the production catalogue/runtime, with
    // no native actors, game calls, hook publication or proprietary-image execution.
    [TestClass, DoNotParallelize]
    public sealed class WeaponStoredOutputCompatibilityTests
    {
        private static WeaponProductionCatalog Catalog(bool stored)
        {
            var definition = new WeaponProductionOptionDefinition(eStructs.STRUCT_ARMOURERS_WORKSHOP,
                eGoods.STORED_METAL_ARMOUR, eGoods.STORED_METAL_ARMOUR, "Example armour");
            if (stored)
                definition = definition.WithStoredOutput(new NativeStoredGood(
                    new CustomGoodId("example.owner", "armour"), StoragePool.Armory));
            var option = new WeaponProductionOption(new WeaponProductionOptionId("example.owner", "recipe"), definition, false);
            return WeaponProductionCatalog.Compile(new[] { new WeaponProductionRegistration(option) }, _ => true);
        }

        [TestMethod]
        public void StoredOutputsRefuseRealMultiplayerWhileNativeRecipesKeepExistingAdmission()
        {
            var native = Catalog(false);
            var stored = Catalog(true);
            Assert.IsNull(WeaponProductionRuntime.StoredOutputModeDiagnostic(native, false));
            Assert.IsNull(WeaponProductionRuntime.StoredOutputModeDiagnostic(native, true));
            Assert.IsNull(WeaponProductionRuntime.StoredOutputModeDiagnostic(stored, false));
            Assert.IsFalse(string.IsNullOrWhiteSpace(WeaponProductionRuntime.StoredOutputModeDiagnostic(stored, true)));
        }

        [TestMethod]
        public void AnUnavailableUnusedWorkshopOwnerDoesNotBlockUnrelatedProductionSaves()
        {
            FieldInfo handleField = typeof(WeaponProductionRuntime).GetField("saveHandle", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = handleField.GetValue(null);
            try
            {
                handleField.SetValue(null, null);
                WeaponProductionRuntime.BeginWorld(WeaponProductionCatalog.Empty);
                Assert.IsTrue(WeaponProductionRuntime.Activate(true, out string reason), reason);
                WeaponProductionRuntime.FreezeSave();
                WeaponProductionRuntime.VerifySaveWritten(null);
            }
            finally
            {
                handleField.SetValue(null, previous);
                WeaponProductionRuntime.EndSaveCapture();
                WeaponProductionRuntime.EndWorld();
            }
        }

        [TestMethod]
        public void EmptyCheckpointCannotIgnoreAFailedCaptureOrKeepStaleWorkshopRows()
        {
            var catalog = Catalog(true);
            Assert.IsTrue(VersionedSaveKnownEntries.TryCreate(new[] { "workshops" }, out var known, out string reason), reason);
            var writer = new VersionedSaveWriter(known, true, false, null, 2, 1);
            var capture = (Func<VersionedSaveWriter, bool>)Delegate.CreateDelegate(typeof(Func<VersionedSaveWriter, bool>),
                typeof(WeaponProductionRuntime).GetMethod("CaptureSave", BindingFlags.Static | BindingFlags.NonPublic));
            FieldInfo handleField = typeof(WeaponProductionRuntime).GetField("saveHandle", BindingFlags.Static | BindingFlags.NonPublic);
            object previous = handleField.GetValue(null);
            var handle = new VersionedSaveDataHandle("fixture.owner", "fixture.workshops");
            try
            {
                WeaponProductionRuntime.BeginWorld(catalog);
                Assert.IsTrue(WeaponProductionRuntime.Activate(true, out reason), reason);
                WeaponProductionRuntime.FreezeSave(); // No actor rows: this is an actual empty checkpoint.
                Assert.IsTrue(capture(writer));
                Assert.AreEqual(0, writer.Count);
                handleField.SetValue(null, handle);
                foreach (var status in new[] { VersionedSaveWriteStatus.CaptureFailed,
                    VersionedSaveWriteStatus.NotWritten, VersionedSaveWriteStatus.KeptLoadedRecord })
                {
                    handle.SetLastWrite(status);
                    Assert.ThrowsExactly<InvalidDataException>(() => WeaponProductionRuntime.VerifySaveWritten(null));
                }
                handle.SetLastWrite(VersionedSaveWriteStatus.Written);
                Assert.ThrowsExactly<InvalidDataException>(() => WeaponProductionRuntime.VerifySaveWritten(null),
                    "An empty checkpoint still requires a completed archive rather than returning early.");
                byte[] empty = VersionedSaveRecordCodec.Encode(2, 2, Array.Empty<KeyValuePair<string, byte[]>>());
                byte[] stale = VersionedSaveRecordCodec.Encode(2, 2,
                    new[] { new KeyValuePair<string, byte[]>("workshops", new byte[] { 1 }) });
                Assert.IsTrue(WeaponProductionRuntime.MatchesFrozenWorkshopRecord(empty, null, 2));
                Assert.IsFalse(WeaponProductionRuntime.MatchesFrozenWorkshopRecord(stale, null, 2));
                Assert.IsFalse(WeaponProductionRuntime.MatchesFrozenWorkshopRecord(empty, null, 1));
                Assert.IsFalse(WeaponProductionRuntime.MatchesFrozenWorkshopRecord(null, null, 2));
            }
            finally
            {
                handleField.SetValue(null, previous);
                WeaponProductionRuntime.EndSaveCapture();
                WeaponProductionRuntime.EndWorld();
            }
        }

        [TestMethod]
        [DataRow(false, 1)]
        [DataRow(true, 2)]
        public void CaptureKeepsItsFrozenReaderRequirementAcrossAWorldReplacement(bool stored, int expectedVersion)
        {
            var catalog = Catalog(stored);
            Assert.IsTrue(VersionedSaveKnownEntries.TryCreate(new[] { "workshops" }, out var known, out string reason), reason);
            var writer = new VersionedSaveWriter(known, true, false, null, 2, 1);
            var capture = (Func<VersionedSaveWriter, bool>)Delegate.CreateDelegate(typeof(Func<VersionedSaveWriter, bool>),
                typeof(WeaponProductionRuntime).GetMethod("CaptureSave", BindingFlags.Static | BindingFlags.NonPublic));
            try
            {
                WeaponProductionRuntime.BeginWorld(catalog);
                Assert.IsTrue(WeaponProductionRuntime.Activate(true, out reason), reason);
                Assert.AreEqual(stored, WeaponProductionRuntime.HasStoredOutputDemand);
                Assert.AreEqual(0, (int)Marshal.ReadByte(WeaponProductionRuntime.StoredOutputOptionsAddress, 0));
                for (int index = 1; index < 32; index++)
                    Assert.AreEqual(catalog.At(index)?.StoredOutput != null ? 1 : 0,
                        (int)Marshal.ReadByte(WeaponProductionRuntime.StoredOutputOptionsAddress, index));
                WeaponProductionRuntime.FreezeSave();
                // A synchronous foreign save callback may replace the current catalogue.
                // The in-flight capture must not relabel custom semantics as a legacy record.
                WeaponProductionRuntime.EndWorld();
                for (int index = 0; index < 32; index++)
                    Assert.AreEqual(0, (int)Marshal.ReadByte(WeaponProductionRuntime.StoredOutputOptionsAddress, index));
                Assert.IsTrue(capture(writer));
                Assert.AreEqual(expectedVersion, writer.RecordVersion);
                Assert.AreEqual(expectedVersion, writer.MinimumReaderVersion);
            }
            finally
            {
                WeaponProductionRuntime.EndSaveCapture();
                WeaponProductionRuntime.EndWorld();
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Stored settings keep the property names of the mod version that wrote them. A declared rename
    // must carry the stored value to the current property wherever APIShared matches data by name.
    [TestClass]
    public sealed class ModSettingsMigrationTests
    {
        private sealed class RenamedSettings
        {
            [ModSettingRenamedFrom("OldGold", "OlderGold")] public int Gold { get; set; }
            public int Kept { get; set; }
        }

        private sealed class AmbiguousSettings
        {
            public int Existing { get; set; }
            [ModSettingRenamedFrom("Existing")] public int TakesAnExistingName { get; set; }
            [ModSettingRenamedFrom("Shared")] public int First { get; set; }
            [ModSettingRenamedFrom("Shared")] public int Second { get; set; }
        }

        // Two rejected names free two dictionary slots; the precedence of later names must not depend on them.
        private sealed class OrderAfterRejections
        {
            [ModSettingRenamedFrom("ClashA")] public int A1 { get; set; }
            [ModSettingRenamedFrom("ClashB")] public int B1 { get; set; }
            [ModSettingRenamedFrom("ClashA")] public int A2 { get; set; }
            [ModSettingRenamedFrom("ClashB")] public int B2 { get; set; }
            [ModSettingRenamedFrom("Newer", "Older")] public int Target { get; set; }
        }

        private sealed class ConvertingSettings : IModSettingsSnapshotMigration
        {
            [ModSettingRenamedFrom("OldFlag")] public bool Flag { get; set; }
            public Dictionary<string, byte[]> System_MigrateMissionSnapshot(Dictionary<string, byte[]> snapshot) =>
                new Dictionary<string, byte[]>(snapshot) { ["Converted"] = snapshot["Flag"] };
        }

        private class DelegateConvertingSettings : IModSettingsSnapshotMigration
        {
            internal Func<Dictionary<string, byte[]>, Dictionary<string, byte[]>> Convert;
            public Dictionary<string, byte[]> System_MigrateMissionSnapshot(Dictionary<string, byte[]> snapshot) => Convert(snapshot);
        }

        private sealed class RenamedDelegateConvertingSettings : DelegateConvertingSettings
        {
            [ModSettingRenamedFrom("OldFlag")] public bool Flag { get; set; }
        }

        private static Dictionary<string, byte[]> Stored(params string[] names)
        {
            var stored = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (int index = 0; index < names.Length; index++) stored[names[index]] = new[] { (byte)(index + 1) };
            return stored;
        }

        [TestMethod]
        public void AStoredFormerNameMovesToTheCurrentProperty()
        {
            var stored = Stored("OlderGold", "Kept");
            var renamed = ModSettingsMigration.ApplyRenames(typeof(RenamedSettings), stored);
            Assert.AreNotSame(stored, renamed);
            Assert.AreEqual(2, stored.Count, "The caller's stored data must stay untouched.");
            Assert.IsTrue(stored.ContainsKey("OlderGold"));
            Assert.AreEqual((byte)1, renamed["Gold"][0]);
            Assert.AreEqual((byte)2, renamed["Kept"][0]);
            Assert.IsFalse(renamed.ContainsKey("OlderGold"));
        }

        [TestMethod]
        public void TheNewestStoredNameWinsAndFormerNamesDisappear()
        {
            // Declared newest first: OldGold outranks OlderGold.
            var both = ModSettingsMigration.ApplyRenames(typeof(RenamedSettings), Stored("OlderGold", "OldGold"));
            Assert.AreEqual((byte)2, both["Gold"][0]);
            Assert.AreEqual(1, both.Count);
            // Data that already uses the current name is newer than a leftover former entry.
            var current = ModSettingsMigration.ApplyRenames(typeof(RenamedSettings), Stored("Gold", "OldGold"));
            Assert.AreEqual((byte)1, current["Gold"][0]);
            Assert.AreEqual(1, current.Count);
        }

        [TestMethod]
        public void DataWithoutFormerNamesIsReturnedUnchanged()
        {
            var stored = Stored("Gold", "Kept");
            Assert.AreSame(stored, ModSettingsMigration.ApplyRenames(typeof(RenamedSettings), stored));
            Assert.AreSame(stored, ModSettingsMigration.ApplyRenames(typeof(string), stored), "A class without declarations costs nothing.");
            Assert.AreSame(stored, ModSettingsMigration.ApplyRenames(null, stored), "Dynamic settings declare no renames.");
            Assert.AreSame(stored, ModSettingsMigration.MigrateStoredSnapshot(new RenamedSettings(), stored),
                "The public helper retains the unchanged fast path when no converter runs.");
        }

        [TestMethod]
        public void AmbiguousDeclarationsAreIgnoredAndReported()
        {
            var reports = new List<string>();
            Action<string> previous = ModSettingsMigration.ReportProblem;
            ModSettingsMigration.ReportProblem = reports.Add;
            try
            {
                var stored = Stored("Existing", "Shared");
                var renamed = ModSettingsMigration.ApplyRenames(typeof(AmbiguousSettings), stored);
                Assert.AreSame(stored, renamed, "A name that cannot be attributed must not move anywhere.");
                ModSettingsMigration.ApplyRenames(typeof(AmbiguousSettings), stored);
                Assert.AreEqual(1, reports.Count, "The declaration problem is reported once per settings class.");
                StringAssert.Contains(reports[0], "TakesAnExistingName <- Existing");
                StringAssert.Contains(reports[0], "<- Shared");
            }
            finally { ModSettingsMigration.ReportProblem = previous; }
        }

        [TestMethod]
        public void RejectedNamesDoNotDisturbTheDeclaredPrecedence()
        {
            Action<string> previous = ModSettingsMigration.ReportProblem;
            ModSettingsMigration.ReportProblem = null;
            try
            {
                var renamed = ModSettingsMigration.ApplyRenames(typeof(OrderAfterRejections), Stored("Older", "Newer", "ClashA"));
                Assert.AreEqual((byte)2, renamed["Target"][0], "The newest former name wins.");
                Assert.IsTrue(renamed.ContainsKey("ClashA"), "An ambiguous name stays where it is.");
                Assert.AreEqual(2, renamed.Count);
            }
            finally { ModSettingsMigration.ReportProblem = previous; }
        }

        [TestMethod]
        public void AMissionEntryMovesFormerNamesWithoutChangingTheDocumentEntry()
        {
            var settings = new RenamedSettings();
            var entry = new MissionModSettingsEntry { PlayerSettings = new[] { "Kept" } };
            entry.Overrides["OlderGold"] = 5;
            MissionModSettingsEntry renamed = MissionModSettingsPreparation.WithCurrentNames(settings, entry);
            Assert.AreNotSame(entry, renamed);
            Assert.IsTrue(entry.Overrides.ContainsKey("OlderGold"), "The caller's entry must stay untouched.");
            Assert.AreEqual(5, renamed.Overrides["Gold"]);
            Assert.AreEqual(1, renamed.Overrides.Count);
            CollectionAssert.AreEqual(new[] { "Kept" }, renamed.PlayerSettings);

            // A Player selection stays a Player selection; a stored current name outranks the former one.
            var player = new MissionModSettingsEntry { PlayerSettings = new[] { "Kept", "OldGold" } };
            CollectionAssert.AreEqual(new[] { "Gold", "Kept" },
                MissionModSettingsPreparation.WithCurrentNames(settings, player).PlayerSettings);
            var both = new MissionModSettingsEntry { PlayerSettings = new[] { "OldGold" } };
            both.Overrides["Gold"] = 7;
            MissionModSettingsEntry kept = MissionModSettingsPreparation.WithCurrentNames(settings, both);
            Assert.AreEqual(0, kept.PlayerSettings.Length);
            Assert.AreEqual(7, kept.Overrides["Gold"]);

            var untouched = new MissionModSettingsEntry { PlayerSettings = new[] { "Gold" } };
            Assert.AreSame(untouched, MissionModSettingsPreparation.WithCurrentNames(settings, untouched));
        }

        [TestMethod]
        public void TheConverterRunsOnTheRenamedSnapshot()
        {
            var stored = Stored("OldFlag");
            var migrated = ModSettingsMigration.MigrateStoredSnapshot(new ConvertingSettings(), stored);
            Assert.AreEqual((byte)1, migrated["Flag"][0]);
            Assert.AreEqual((byte)1, migrated["Converted"][0]);
            Assert.IsFalse(migrated.ContainsKey("OldFlag"));
            Assert.AreEqual(1, stored.Count);
        }

        // A converter may mutate its input and return it. Both success and rejection must leave
        // the caller's raw stored data reusable; a renamed dictionary alone does not isolate buffers.
        [DataTestMethod]
        [DataRow(false, false)]
        [DataRow(false, true)]
        [DataRow(true, false)]
        [DataRow(true, true)]
        public void InPlaceConversionCannotChangeStoredData(bool rename, bool throws)
        {
            var stored = Stored("OldFlag", "Kept");
            string flag = rename ? "Flag" : "OldFlag";
            var failure = new InvalidOperationException("Conversion rejected after editing.");
            Dictionary<string, byte[]> received = null;
            DelegateConvertingSettings settings = rename
                ? new RenamedDelegateConvertingSettings() : new DelegateConvertingSettings();
            settings.Convert = snapshot =>
            {
                received = snapshot;
                snapshot[flag][0] = 9;
                snapshot["Kept"][0] = 8;
                snapshot.Remove("Kept");
                snapshot["Converted"] = new byte[] { 7 };
                if (throws) throw failure;
                return snapshot;
            };

            if (throws)
                Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
                    ModSettingsMigration.MigrateStoredSnapshot(settings, stored)));
            else
            {
                var migrated = ModSettingsMigration.MigrateStoredSnapshot(settings, stored);
                Assert.AreSame(received, migrated, "Preserve the converter's chosen result identity.");
                Assert.AreEqual((byte)9, migrated[flag][0]);
                Assert.IsFalse(migrated.ContainsKey("Kept"));
                Assert.IsTrue(migrated.ContainsKey("Converted"));
            }
            Assert.AreNotSame(stored, received);
            Assert.AreEqual(2, stored.Count);
            Assert.IsTrue(stored.ContainsKey("OldFlag"));
            Assert.IsTrue(stored.ContainsKey("Kept"));
            Assert.AreEqual((byte)1, stored["OldFlag"][0]);
            Assert.AreEqual((byte)2, stored["Kept"][0]);
        }

        [TestMethod]
        public void ConverterPreservesNullValuesAndInputComparerAndItsChosenResult()
        {
            var stored = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase) { ["Nullable"] = null };
            var chosen = new Dictionary<string, byte[]>(StringComparer.Ordinal) { ["Different"] = new byte[] { 3 } };
            var settings = new DelegateConvertingSettings { Convert = snapshot =>
            {
                Assert.AreSame(stored.Comparer, snapshot.Comparer);
                Assert.IsTrue(snapshot.ContainsKey("nullable"));
                Assert.IsNull(snapshot["Nullable"]);
                return chosen;
            } };
            var migrated = ModSettingsMigration.MigrateStoredSnapshot(settings, stored);
            Assert.AreSame(chosen, migrated, "Do not replace or recopy a converter-selected dictionary.");
            Assert.AreSame(chosen.Comparer, migrated.Comparer);
            Assert.AreEqual(1, stored.Count);
            Assert.IsNull(stored["Nullable"]);
        }

        [TestMethod]
        public void AConverterReturningNullStillRejectsWithoutChangingStoredData()
        {
            var stored = Stored("Kept");
            var settings = new DelegateConvertingSettings { Convert = snapshot =>
            {
                snapshot["Kept"][0] = 9;
                snapshot.Clear();
                return null;
            } };
            Assert.ThrowsExactly<InvalidDataException>(() => ModSettingsMigration.MigrateStoredSnapshot(settings, stored));
            Assert.AreEqual(1, stored.Count);
            Assert.AreEqual((byte)1, stored["Kept"][0]);
        }
    }
}

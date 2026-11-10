using System;
using System.Collections.Generic;
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
    }
}

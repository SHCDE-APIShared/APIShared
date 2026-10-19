using APIShared.ModSettings;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;

namespace LobbyModSettingsPresetTests
{
    public partial class PresetTests
    {
        private string LegacyFile => Path.Combine(testRoot, "LobbyModSettings", ModName + ".msgpack");
        private string AssemblyFile => Path.Combine(testRoot, "PresetTest.dll");
        private string NewFile => new PersonalSettingsPaths(AssemblyFile, TargetGuid).SettingsFile;
        private string LegacyPresets => Path.Combine(testRoot, "LobbyModSettings", "Presets", "Override", TargetGuid);

        private FakeSettings StartDocuments() => Start(AssemblyFile, LegacyFile, () => false);

        private string WritePersonal(string directory, string id, int number)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "preset_" + id + ".json");
            File.WriteAllText(path, CreateLegacyPresetJson(id, id, true, number));
            return path;
        }

        [TestMethod]
        public void DocumentsMigrationPreservesOriginalAndNewWorkingStateWinsOnRestart()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyFile));
            WriteLegacy(LegacyFile, false, 41);
            byte[] original = File.ReadAllBytes(LegacyFile);
            var settings = StartDocuments();
            Assert(settings.Number == 41 && File.Exists(NewFile), "Legacy settings were not relocated.");
            settings.Number = 72;
            CollectionAssert.AreEqual(original, File.ReadAllBytes(LegacyFile));
            var beforeBinding = new FakeSettings();
            beforeBinding.PreparePresets(null, AssemblyFile, ModName);
            beforeBinding.LoadInitialPresetSettings();
            Assert(beforeBinding.Number == 72, "Initial load revived old settings before UI binding.");
            beforeBinding.ActivatePresets();
            Assert(beforeBinding.Number == 72, "Activation disagreed with the initial load.");
        }

        [TestMethod]
        public void CurrentSchemaIsRelocatedEvenWhenNoSchemaMigrationIsNeeded()
        {
            var first = StartDocuments();
            first.Number = 62;
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyFile));
            File.Move(NewFile, LegacyFile);
            byte[] original = File.ReadAllBytes(LegacyFile);
            Assert(StartDocuments().Number == 62 && File.Exists(NewFile), "Schema 3 was not relocated.");
            CollectionAssert.AreEqual(original, File.ReadAllBytes(LegacyFile));
        }

        [TestMethod]
        public void CorruptNewFileDoesNotLoadStaleInstallationSettings()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyFile));
            WriteLegacy(LegacyFile, false, 99);
            Directory.CreateDirectory(Path.GetDirectoryName(NewFile));
            File.WriteAllBytes(NewFile, new byte[] { 0xc1 });
            Assert(StartDocuments().Number == 5, "Corrupt Documents data revived old installation values.");
            Assert(Directory.GetFiles(Path.GetDirectoryName(NewFile), "settings.msgpack.corrupt-*").Length == 1,
                "Corrupt Documents data was not preserved.");
            Assert(MessagePackSerializer.Deserialize<int>(MessagePackSerializer.Deserialize<System.Collections.Generic.Dictionary<string, byte[]>>(
                File.ReadAllBytes(LegacyFile))[nameof(FakeSettings.Number)]) == 99, "Old data was changed.");
        }

        [TestMethod]
        public void PersonalOverlayRetainsIdentityAndResavingWritesOnlyDocuments()
        {
            string oldPath = WritePersonal(LegacyPresets, "shared", 31);
            string original = File.ReadAllText(oldPath);
            var settings = StartDocuments();
            var old = settings.System_TestPublishedPresets.Single(p => p.Id == "shared");
            Assert(old.SourcePath == oldPath, "Old personal preset was not discovered.");
            settings.System_TestLoadPreset(old.StableId);
            settings.Number = 47;
            string newPath = settings.System_SavePersonalPreset("shared", "shared", "", new[]
            {
                new PresetSaveSelection { PropertyName = nameof(FakeSettings.Number), Mode = PublishedPresetValueMode.Fixed }
            }, true);
            var current = settings.System_TestPublishedPresets.Single(p => p.Id == "shared");
            Assert(current.SourcePath == newPath && current.StableId == old.StableId,
                "Overlay duplicated the preset or changed its identity.");
            Assert(File.ReadAllText(oldPath) == original && newPath.StartsWith(PersonalDirectory(testRoot)),
                "Resaving changed the installation copy.");
            settings.System_TestLoadPreset(current.StableId);
            Assert(settings.Number == 47, "The Documents copy did not win.");
            settings.System_TestDeletePreset(current.StableId);
            Assert(!File.Exists(oldPath) && !File.Exists(newPath) &&
                settings.System_TestPublishedPresets.All(p => p.Id != "shared"), "Deletion revived the old copy.");
        }

        [TestMethod]
        public void InvalidNewPresetDoesNotHideValidOldPresetAndLocalDuplicatesRemainRejected()
        {
            string oldPath = WritePersonal(LegacyPresets, "shared", 31);
            string newPath = WritePersonal(PersonalDirectory(testRoot), "shared", 52);
            File.WriteAllText(newPath, File.ReadAllText(newPath).Replace("Number", "UnknownSetting"));
            var settings = StartDocuments();
            Assert(settings.System_TestPublishedPresets.Single(p => p.Id == "shared").SourcePath == oldPath,
                "An incompatible new preset hid a valid old preset.");
            WritePersonal(PersonalDirectory(testRoot), "shared", 52);
            File.Copy(newPath, Path.Combine(Path.GetDirectoryName(newPath), "preset_duplicate.json"));
            settings.System_TestRefreshPresetCatalog();
            Assert(settings.System_TestPublishedPresets.Single(p => p.Id == "shared").SourcePath == oldPath,
                "Duplicates within Documents were accepted.");
        }

        [TestMethod]
        public void DeleteRemovesCaseVariantsAndShadowedDuplicatesWithNonstandardNames()
        {
            string oldPath = WritePersonal(LegacyPresets, "SHARED", 31);
            string duplicate = Path.Combine(LegacyPresets, "preset_another-filename.json");
            File.Copy(oldPath, duplicate);
            string newPath = WritePersonal(PersonalDirectory(testRoot), "shared", 52);
            var settings = StartDocuments();
            string id = settings.System_TestPublishedPresets.Single(p => p.Id == "shared").StableId;
            settings.System_TestDeletePreset(id);
            Assert(!File.Exists(oldPath) && !File.Exists(duplicate) && !File.Exists(newPath),
                "Deletion left a shadowed copy that could reappear later.");
        }

        [TestMethod]
        public void DeleteDoesNotRemoveAFileWhoseIdentityChangedSinceDiscovery()
        {
            string oldPath = WritePersonal(LegacyPresets, "shared", 31);
            string newPath = WritePersonal(PersonalDirectory(testRoot), "shared", 52);
            var settings = StartDocuments();
            string id = settings.System_TestPublishedPresets.Single(p => p.Id == "shared").StableId;
            string replacement = CreateLegacyPresetJson("different", "different", true, 66);
            File.WriteAllText(oldPath, replacement);
            bool failed = false;
            try { settings.System_TestDeletePreset(id); }
            catch (IOException) { failed = true; }
            Assert(failed && !File.Exists(newPath) && File.ReadAllText(oldPath) == replacement,
                "Deletion removed a file that no longer belongs to the selected identity.");
        }

        [TestMethod]
        public void PartialDeletionReportsFailureAndRefreshesRemainingCopy()
        {
            string oldPath = WritePersonal(LegacyPresets, "shared", 31);
            string newPath = WritePersonal(PersonalDirectory(testRoot), "shared", 52);
            var settings = StartDocuments();
            string id = settings.System_TestPublishedPresets.Single(p => p.Id == "shared").StableId;
            using (var locked = new FileStream(oldPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { settings.System_TestDeletePreset(id); }
                catch (IOException) { failed = true; }
                Assert(failed && File.Exists(oldPath) && !File.Exists(newPath) &&
                    settings.System_TestPublishedPresets.Single(p => p.Id == "shared").SourcePath == oldPath,
                    "Partial deletion was hidden or left a stale catalog.");
            }
        }

        [TestMethod]
        public void InstallationsShareExplicitlyRedirectedDocuments()
        {
            PersonalSettingsPaths.TestDocumentsDirectory = _ => Path.Combine(testRoot, "SharedDocuments");
            StartDocuments().Number = 74;
            string other = Path.Combine(testRoot, "OtherInstallation");
            Assert(Start(Path.Combine(other, "PresetTest.dll"), Path.Combine(other, "LobbyModSettings", ModName + ".msgpack"),
                () => false).Number == 74, "Installations did not share Documents state.");
        }

        [TestMethod]
        public void UnreadableLegacyFileDoesNotCommitRelocationAndCanBeRetried()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyFile));
            byte[] corrupt = { 0xc1 };
            File.WriteAllBytes(LegacyFile, corrupt);
            StartDocuments();
            Assert(!File.Exists(NewFile), "A failed read committed a defaults-only relocation.");
            CollectionAssert.AreEqual(corrupt, File.ReadAllBytes(LegacyFile));
            WriteLegacy(LegacyFile, false, 36);
            Assert(StartDocuments().Number == 36 && File.Exists(NewFile), "Relocation could not be retried.");
        }

        [TestMethod]
        public void FailedWorkingFilePublicationRetriesWithoutChangingLegacyData()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LegacyFile));
            WriteLegacy(LegacyFile, false, 29);
            byte[] original = File.ReadAllBytes(LegacyFile);
            var model = new FakeSettings();
            model.PreparePresets(null, AssemblyFile, ModName);
            using (var blocked = new FileStream(NewFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                // An existing unreadable new file must not cause fallback to the old file.
                model.LoadInitialPresetSettings();
                model.ActivatePresets();
                Assert(model.Number == 5, "An unreadable new file revived old settings.");
            }
            File.Delete(NewFile);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(LegacyFile));
            Assert(StartDocuments().Number == 29, "Publication failure prevented a later legacy retry.");
        }

        // Regression: a Wine/Proton prefix without a Documents folder made the settings page fail.
        [TestMethod]
        public void MissingPlatformDocumentsUsesTheNextPerUserFolder()
        {
            var previousTest = PersonalSettingsPaths.TestDocumentsDirectory;
            var previousFolders = PersonalSettingsPaths.PlatformFolders;
            try
            {
                string fallback = Path.Combine(testRoot, "LocalAppData");
                PersonalSettingsPaths.TestDocumentsDirectory = null;
                PersonalSettingsPaths.PlatformFolders = () => new[] { "", "relative\\Documents", fallback };
                Assert(NewFile.StartsWith(fallback + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                    "A missing Documents folder did not fall back to the next per-user folder.");
                PersonalSettingsPaths.PlatformFolders = () => new string[] { null, " " };
                bool rejected = false;
                try { _ = NewFile; }
                catch (IOException) { rejected = true; }
                Assert(rejected, "Settings storage was resolved without any per-user folder.");
            }
            finally
            {
                PersonalSettingsPaths.TestDocumentsDirectory = previousTest;
                PersonalSettingsPaths.PlatformFolders = previousFolders;
            }
        }

        [TestMethod]
        public void MissingDocumentsDoesNotFallBackToInstallationWrites()
        {
            PersonalSettingsPaths.TestDocumentsDirectory = _ => "";
            bool rejected = false;
            try { StartDocuments(); }
            catch (IOException) { rejected = true; }
            Assert(rejected && !File.Exists(LegacyFile), "Missing Documents silently used installation storage.");
        }
    }
}

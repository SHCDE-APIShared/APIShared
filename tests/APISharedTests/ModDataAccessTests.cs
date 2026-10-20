using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using APIShared.ModData;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API;
using SHCDESE.API.Components.Archive;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public class ModDataAccessTests
    {
        private static ModDataReadResult Data(string guid, int value) =>
            ModDataDocumentJson.ReadNamespace("{\"" + guid + "\":{\"value\":" + value + "}}", guid, "confirmed test source");

        [TestMethod]
        public void NoProviderAndInvalidRequestsDoNotGuessLocalData()
        {
            Assert.IsFalse(ModDataAccess.HasSelectedLordSource);
            Assert.AreEqual(ModDataReadStatus.HostDataUnavailable, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
            Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataAccess.ReadSelectedLordNamespace(0, "a").Status);
            Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataAccess.ReadSelectedLordNamespace(GamePlayerManagerAPI.MAX_PLAYERS + 1, "a").Status);
            Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataAccess.ReadSelectedLordNamespace(1, " ").Status);
        }

        [TestMethod]
        public void SourceIsQueriedFreshAndOldResultsStayDetached()
        {
            int value = 1;
            using (ModDataAccess.RegisterSelectedLordSource("third.party", "selection", (_, guid) => Data(guid, value)))
            {
                Assert.IsTrue(ModDataAccess.HasSelectedLordSource);
                var first = ModDataAccess.ReadSelectedLordNamespace(1, "a"); value = 2;
                Assert.AreEqual(1, Convert.ToInt32(first.Data["value"]));
                Assert.AreEqual(2, Convert.ToInt32(ModDataAccess.ReadSelectedLordNamespace(1, "a").Data["value"]));
                Assert.ThrowsExactly<InvalidOperationException>(() => ModDataAccess.RegisterSelectedLordSource("another", "selection", (_, guid) => Data(guid, 3)));
            }
            Assert.IsFalse(ModDataAccess.HasSelectedLordSource);
        }

        [TestMethod]
        public void ProviderFailuresAndRecursiveReadsAreContained()
        {
            using (ModDataAccess.RegisterSelectedLordSource("third.party", "throwing", (_, guid) => throw new Exception("provider failed")))
                Assert.AreEqual(ModDataReadStatus.ReadError, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
            using (ModDataAccess.RegisterSelectedLordSource("third.party", "not-ready", (_, guid) => null))
                Assert.AreEqual(ModDataReadStatus.HostDataUnavailable, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
            using (ModDataAccess.RegisterSelectedLordSource("third.party", "recursive", (id, guid) => ModDataAccess.ReadSelectedLordNamespace(id, guid)))
                Assert.AreEqual(ModDataReadStatus.HostDataUnavailable, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
            using (ModDataAccess.RegisterSelectedLordSource("third.party", "wrong-guid", (_, guid) => Data("different", 3)))
                Assert.AreEqual(ModDataReadStatus.ReadError, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
        }

        [TestMethod]
        public void SourceReplacementDuringReadRejectsStaleResultsAndOldTokenCannotRemoveNewSource()
        {
            IDisposable first = null, replacement = null;
            try
            {
                first = ModDataAccess.RegisterSelectedLordSource("first", "selection", (_, guid) => {
                    first.Dispose(); replacement = ModDataAccess.RegisterSelectedLordSource("second", "selection", (id, mod) => Data(mod, 2));
                    return Data(guid, 1);
                });
                Assert.AreEqual(ModDataReadStatus.HostDataUnavailable, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
                first.Dispose(); Assert.AreEqual(2, Convert.ToInt32(ModDataAccess.ReadSelectedLordNamespace(1, "a").Data["value"]));
            }
            finally { first?.Dispose(); replacement?.Dispose(); }
        }

        [TestMethod]
        public void ExplicitLocalReadUsesSidecarLimitsAndDoesNotRegisterASelection()
        {
            string directory = Path.Combine(Path.GetTempPath(), "ModData-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string lord = Path.Combine(directory, "aggressive.v2.lordjson"); string sidecar = Path.Combine(directory, "aggressive.v2.modlord.json");
            try
            {
                Assert.AreEqual(ModDataReadStatus.InvalidRequest, ModDataAccess.ReadLocalLordNamespace("bad.txt", "a").Status);
                Assert.AreEqual(ModDataReadStatus.FileNotFound, ModDataAccess.ReadLocalLordNamespace(lord, "a").Status);
                File.WriteAllText(sidecar, "{\"a\":{\"value\":7}}", new UTF8Encoding(true));
                Assert.IsTrue(ModDataAccess.ReadLocalLordNamespace(lord, "a").Success);
                Assert.IsFalse(ModDataAccess.HasSelectedLordSource);
                Assert.AreEqual(ModDataReadStatus.HostDataUnavailable, ModDataAccess.ReadSelectedLordNamespace(1, "a").Status);
                File.WriteAllBytes(sidecar, new byte[] { 0xff });
                Assert.AreEqual(ModDataReadStatus.InvalidDocument, ModDataAccess.ReadLocalLordNamespace(lord, "a").Status);
                File.WriteAllText(sidecar, "{}" + new string(' ', ModDataDocumentJson.MaximumDocumentBytes));
                Assert.AreEqual(ModDataReadStatus.InvalidDocument, ModDataAccess.ReadLocalLordNamespace(lord, "a").Status);
            }
            finally { Directory.Delete(directory, true); }
        }

        [TestMethod]
        public void CurrentMapUsesRealInstalledArchiveReaderAndDoesNotCachePreviousContents()
        {
            var manager = GameMapArchiveManagerAPI.Instance;
            var archiveField = typeof(GameMapArchiveManagerAPI).GetField("_mapArchive", BindingFlags.NonPublic | BindingFlags.Instance);
            var pathField = typeof(GameMapArchiveManagerAPI).GetField("_currentFilePath", BindingFlags.NonPublic | BindingFlags.Instance);
            object oldArchive = archiveField.GetValue(manager), oldPath = pathField.GetValue(manager);
            using (var bytes = new MemoryStream())
            {
                using (var writer = new ZipOutputStream(bytes) { IsStreamOwner = false })
                {
                    writer.PutNextEntry(new ZipEntry("MODMAP.JSON"));
                    byte[] json = Encoding.UTF8.GetBytes("\uFEFF{\"a\":{\"value\":7}}"); writer.Write(json, 0, json.Length); writer.CloseEntry(); writer.Finish();
                }
                bytes.Position = 0;
                using (var zip = new ZipFile(bytes) { IsStreamOwner = false })
                {
                    // The test substitutes only archive construction/context; the production read uses real SDK IL.
                    var archive = (MapArchive)FormatterServices.GetUninitializedObject(typeof(MapArchive));
                    typeof(MapArchive).GetField("<Archive>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(archive, zip);
                    try
                    {
                        archiveField.SetValue(manager, archive); pathField.SetValue(manager, "fixture.map");
                        var result = ModDataAccess.ReadCurrentMapNamespace("a");
                        Assert.IsTrue(result.Success, result.Diagnostic); Assert.AreEqual("fixture.map::modmap.json", result.Source);
                        archiveField.SetValue(manager, null); pathField.SetValue(manager, null);
                        Assert.AreEqual(ModDataReadStatus.FileNotFound, ModDataAccess.ReadCurrentMapNamespace("a").Status);
                        Assert.AreEqual(7, Convert.ToInt32(result.Data["value"]));
                    }
                    finally { archiveField.SetValue(manager, oldArchive); pathField.SetValue(manager, oldPath); }
                }
            }
        }
    }
}

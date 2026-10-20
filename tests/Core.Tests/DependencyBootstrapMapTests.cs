using APIShared.Internal;
using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace APIShared.Core.Tests;

[TestClass]
public sealed class DependencyBootstrapMapTests
{
    private const string Plugin = "BepInEx/plugins/APIShared_Serp/";
    private const string Tools = "BepInEx/tools/APISharedWorkshopPublisher/";
    private const string Bootstrap = "BepInEx/patchers/APIShared.DependencyBootstrap.dll";
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string Json(object value) => DependencyFreeJson.Serialize(value);
    private static string Inventory(string guid, Dictionary<string, string> files) => Json(new Dictionary<string, object> {
        {"SchemaVersion",1},{"Guid",guid},{"Version","1.2.3"},
        {"Files",files.Select(f => new Dictionary<string,object>{{"Path",f.Key},{"Sha256",Hash(f.Value)}}).ToList()}, {"LegacyFiles",new List<object>()} });

    [TestMethod]
    public void ActualMapValidatorChecksExactPairAndPreservesHistoricalPackages()
    {
        foreach (string scenario in new[] { "valid", "historical", "missing", "mixed", "foreign", "schema", "wrong-version", "duplicate",
            "content-valid", "content-missing", "content-mixed", "content-unclaimed", "content-unpaired" })
        {
            string root = Path.Combine(AppContext.BaseDirectory, "bootstrap-map-fixtures", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var plugin = new Dictionary<string, string> { { "APIShared.dll", "runtime bytes" }, { "info.json", "{\"GUID\":\"APIShared_Serp\",\"Version\":\"1.2.3\",\"Manifest\":1}" } };
            if (scenario.StartsWith("content-") && scenario != "content-missing")
                plugin.Add("APIShared.Content.dll", scenario == "content-mixed" ? "wrong content bytes" : "content bytes");
            var pair = new Dictionary<string, object> {
                {"Schema",scenario == "schema" ? 2 : 1},{"Guid","APIShared_Serp"},{"Version",scenario == "wrong-version" ? "2.0.0" : "1.2.3"},
                {"RuntimeSHA256",Hash(plugin["APIShared.dll"])},{"BootstrapSHA256",Hash("bootstrap bytes")} };
            if (scenario.StartsWith("content-") && scenario != "content-unclaimed") pair.Add("ContentSHA256", Hash("content bytes"));
            if (scenario != "historical" && scenario != "content-unpaired") plugin.Add("dependency-bootstrap-pair.json", Json(pair));
            var files = plugin.ToDictionary(p => Plugin + p.Key, p => p.Value);
            files.Add(Plugin + "apishared-workshop-files.json", Inventory("APIShared_Serp", plugin));
            files.Add("info.json", plugin["info.json"]);
            var publisher = new[] { "UploadMod.bat", "UploadMod-linux.sh", "WorkshopUploader.exe", "WorkshopUploader.exe.config", "com.rlabrecque.steamworks.net.dll", "ICSharpCode.SharpZipLib.dll", "steam_api64.dll", "UnityEngine.CoreModule.dll", "UnityEngine.SharedInternalsModule.dll" }.ToDictionary(n => n, _ => "fixture");
            foreach (var file in publisher) files.Add(Tools + file.Key, file.Value);
            files.Add(Tools + "publisher-files.json", Inventory("APISharedWorkshopPublisher", publisher));
            if (scenario != "historical" && scenario != "missing" && scenario != "content-unpaired") files.Add(Bootstrap, scenario == "mixed" ? "wrong bytes" : "bootstrap bytes");
            if (scenario == "foreign") files.Add("BepInEx/patchers/Foreign.dll", "foreign");
            using (var zip = ZipFile.Open(Path.Combine(root, "APIShared.map"), ZipArchiveMode.Create))
            {
                foreach (var file in files) { using var writer = new StreamWriter(zip.CreateEntry(file.Key).Open(), new UTF8Encoding(false)); writer.Write(file.Value); }
                if (scenario == "duplicate") { using var writer = new StreamWriter(zip.CreateEntry(Bootstrap.ToUpperInvariant()).Open()); writer.Write("duplicate"); }
            }
            var package = new WorkshopPackage { PackageId = "APIShared_Serp", Version = "1.2.3" };
            if (scenario == "valid" || scenario == "historical" || scenario == "content-valid") WorkshopMapPackage.Verify(package, root);
            else Assert.ThrowsExactly<InvalidDataException>(() => WorkshopMapPackage.Verify(package, root), scenario);
        }
    }
}

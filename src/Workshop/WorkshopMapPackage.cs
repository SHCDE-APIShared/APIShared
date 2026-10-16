using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Only the maintainer's offline publisher accepts APIShared itself as a map.
    // Loose-package validation and installation never accept protected manager roots.
    internal static class WorkshopMapPackage
    {
        internal static void Verify(WorkshopPackage package, string source)
        {
            string[] files = WorkshopPaths.Enumerate(source).ToArray();
            if (files.Length != 1 || Path.GetFileName(files[0]) != "APIShared.map" || package.PackageId != "APIShared_Serp")
                throw new InvalidDataException("The APIShared content snapshot must contain only APIShared.map.");
            using (var map = new WorkshopMapArchive(files[0])) {
                if (WorkshopPackageCodec.Text(map.Info, "GUID") != package.PackageId || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(map.Info, "Version")) != WorkshopPackageCodec.Version(package.Version))
                    throw new InvalidDataException("APIShared map identity/version mismatch.");
                if (!map.Info.TryGetValue("Manifest", out var manifest) || !(manifest is int number) || number != 1) throw new InvalidDataException("APIShared map requires BepInEx manifest 1.");
                const string root = "BepInEx/plugins/APIShared_Serp/";
                const string tools = "BepInEx/tools/APISharedWorkshopPublisher/";
                const string bootstrap = "BepInEx/patchers/APIShared.DependencyBootstrap.dll";
                const string pairPath = root + "dependency-bootstrap-pair.json";
                var pairEntry = map.Archive.GetEntry(pairPath);
                var bootstrapEntry = map.Archive.GetEntry(bootstrap);
                string bootstrapHash = null;
                if (pairEntry != null || bootstrapEntry != null)
                {
                    if (pairEntry == null || bootstrapEntry == null) throw new InvalidDataException("Incomplete dependency bootstrap pair.");
                    var pair = WorkshopPackageCodec.Object(APIShared.Internal.DependencyFreeJson.Parse(map.ReadText(pairPath)));
                    if (pair.Count != 5 || !pair.TryGetValue("Schema", out var schema) || !(schema is int n) || n != 1 ||
                        WorkshopPackageCodec.Text(pair, "Guid") != "APIShared_Serp" || WorkshopPackageCodec.Text(pair, "Version") != package.Version)
                        throw new InvalidDataException("Invalid dependency bootstrap pair.");
                    bootstrapHash = WorkshopPackageCodec.Text(pair, "BootstrapSHA256");
                    VerifyPairEntry(map, bootstrapEntry, bootstrapHash);
                    VerifyPairEntry(map, map.Archive.GetEntry(root + "APIShared.dll"), WorkshopPackageCodec.Text(pair, "RuntimeSHA256"));
                }
                var inventory = WorkshopInventory.Read(map.ReadText(root + WorkshopInventory.FileName), package.PackageId, package.Version);
                var publisher = WorkshopInventory.Read(map.ReadText(tools + "publisher-files.json"), "APISharedWorkshopPublisher", package.Version);
                var allowed = new HashSet<string>(new[] { "UploadMod.bat", "UploadMod-linux.sh", "WorkshopUploader.exe", "WorkshopUploader.exe.config",
                    "com.rlabrecque.steamworks.net.dll", "ICSharpCode.SharpZipLib.dll", "steam_api64.dll", "UnityEngine.CoreModule.dll", "UnityEngine.SharedInternalsModule.dll" }, StringComparer.Ordinal);
                if (!allowed.SetEquals(publisher.Files.Keys)) throw new InvalidDataException("Incomplete or unexpected publisher inventory.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipEntry entry in map.Archive) {
                    string name = entry.Name.TrimEnd('/'); WorkshopPaths.Relative(name);
                    if (!seen.Add(name) || (entry.ExternalFileAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Duplicate or linked map entry.");
                    if (entry.IsDirectory) { if (name != "BepInEx" && name != "BepInEx/plugins" && name != "BepInEx/tools" && !(bootstrapHash != null && name == "BepInEx/patchers") && name != tools.TrimEnd('/') && name != root.TrimEnd('/') && !name.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Foreign map directory."); continue; }
                    if (name == "info.json" || name == "preview.png") continue;
                    if (name == bootstrap && bootstrapHash != null) continue;
                    bool tool = name.StartsWith(tools, StringComparison.Ordinal);
                    if (!tool && !name.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Foreign map file.");
                    string relative = name.Substring(tool ? tools.Length : root.Length);
                    if (relative == (tool ? "publisher-files.json" : WorkshopInventory.FileName)) continue;
                    if (!(tool ? publisher : inventory).Files.TryGetValue(relative, out string expected)) throw new InvalidDataException("File missing from APIShared inventory: " + relative);
                    using (var input = map.Archive.GetInputStream(entry))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != expected) throw new InvalidDataException("APIShared map hash mismatch.");
                }
                if (inventory.Files.Keys.Any(path => !seen.Contains(root + path))) throw new InvalidDataException("Incomplete APIShared map.");
                if (publisher.Files.Keys.Any(path => !seen.Contains(tools + path))) throw new InvalidDataException("Incomplete publisher in APIShared map.");
                var pluginInfo = WorkshopPackageCodec.Object(APIShared.Internal.DependencyFreeJson.Parse(map.ReadText(root + "info.json")));
                if (WorkshopPackageCodec.Text(pluginInfo, "GUID") != package.PackageId || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(pluginInfo, "Version")) != WorkshopPackageCodec.Version(package.Version)) throw new InvalidDataException("APIShared plugin metadata mismatch.");
            }
        }
        private static void VerifyPairEntry(WorkshopMapArchive map, ZipEntry entry, string expected)
        {
            if (entry == null || expected == null || expected.Length != 64 || expected.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new InvalidDataException("Invalid pair hash or entry.");
            using (var input = map.Archive.GetInputStream(entry))
            using (var sha = System.Security.Cryptography.SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != expected)
                    throw new InvalidDataException("Dependency bootstrap hash mismatch.");
        }
    }
}

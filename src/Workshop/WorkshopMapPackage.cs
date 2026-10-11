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
                var inventory = WorkshopInventory.Read(map.ReadText(root + WorkshopInventory.FileName), package.PackageId, package.Version);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipEntry entry in map.Archive) {
                    string name = entry.Name.TrimEnd('/'); WorkshopPaths.Relative(name);
                    if (!seen.Add(name) || (entry.ExternalFileAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Duplicate or linked map entry.");
                    if (entry.IsDirectory) { if (name != "BepInEx" && name != "BepInEx/plugins" && name != root.TrimEnd('/') && !name.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Foreign map directory."); continue; }
                    if (name == "info.json" || name == "preview.png") continue;
                    if (!name.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException("Foreign map file.");
                    string relative = name.Substring(root.Length);
                    if (relative == WorkshopInventory.FileName) continue;
                    if (!inventory.Files.TryGetValue(relative, out string expected)) throw new InvalidDataException("File missing from APIShared inventory: " + relative);
                    using (var input = map.Archive.GetInputStream(entry))
                    using (var sha = System.Security.Cryptography.SHA256.Create())
                        if (BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant() != expected) throw new InvalidDataException("APIShared map hash mismatch.");
                }
                if (inventory.Files.Keys.Any(path => !seen.Contains(root + path))) throw new InvalidDataException("Incomplete APIShared map.");
                var pluginInfo = WorkshopPackageCodec.Object(APIShared.Internal.DependencyFreeJson.Parse(map.ReadText(root + "info.json")));
                if (WorkshopPackageCodec.Text(pluginInfo, "GUID") != package.PackageId || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(pluginInfo, "Version")) != WorkshopPackageCodec.Version(package.Version)) throw new InvalidDataException("APIShared plugin metadata mismatch.");
            }
        }
    }
}

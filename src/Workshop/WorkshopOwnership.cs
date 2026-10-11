using ICSharpCode.SharpZipLib.Zip;
using SHCDESE.API.Components.Archive;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopOwnership
    {
        // Use the same subscription list and first-map rule as MapModManager.
        // Inspect every subscribed payload, including packages without inventories.
        internal static HashSet<string> GetCompetingPaths(string currentMap, string guid, string pluginRoot)
        {
            return InspectSubscriptions(Platform_Workshop.Instance.GetListOfSubscribedItemsPaths(), currentMap, guid, pluginRoot);
        }

        internal static HashSet<string> InspectSubscriptions(IEnumerable<string> subscriptions, string currentMap, string guid, string pluginRoot)
        {
            var protectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int owners = 0;
            foreach (string directory in subscriptions)
            {
                if (!Directory.Exists(directory)) throw new IOException("Workshop subscription is not available: " + directory);
                string map = Directory.EnumerateFiles(directory, "*.map").FirstOrDefault();
                if (map == null) continue;
                using (var archive = new MapArchive(map))
                {
                    if (!archive.IsValid) continue; // ordinary maps have no appended package
                    if (string.Equals(archive.Info?.GUID, guid, StringComparison.OrdinalIgnoreCase)) owners++;
                    if (string.Equals(Path.GetFullPath(map), Path.GetFullPath(currentMap), StringComparison.OrdinalIgnoreCase)) continue;
                    if (archive.Archive == null) continue;
                    foreach (ZipEntry entry in archive.Archive)
                    {
                        if (!entry.IsFile) continue;
                        string name = entry.Name.Replace('\\', '/');
                        const string prefix = "BepInEx/plugins/";
                        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                        string relative = name.Substring(prefix.Length);
                        if (relative.Split('/').Any(x => x == "" || x == "." || x == "..") || relative.Contains(":"))
                            throw new InvalidDataException("Unsafe competing Workshop payload.");
                        string target = Path.GetFullPath(Path.Combine(pluginRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
                        if (!WorkshopInventory.IsContainedWithoutLinks(pluginRoot, target))
                            throw new InvalidDataException("Linked competing Workshop payload.");
                        protectedPaths.Add(target);
                    }
                }
            }
            if (owners != 1) throw new InvalidDataException("Workshop package GUID does not have exactly one subscribed owner: " + guid);
            return protectedPaths;
        }
    }
}

using APIShared.Internal;
using ICSharpCode.SharpZipLib.Zip;
using SHCDESE.API.Components.Archive;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopSubscriptionOwnership
    {
        // Include foreign map claims even though APIShared never installs or cleans those packages.
        internal static void Verify(WorkshopRuntime.Subscription[] subscriptions, ulong ownId, WorkshopPackage package)
        {
            foreach (var other in subscriptions.Where(s => s.Id != ownId && WorkshopRuntime.Stable(s)))
            {
                string manifest = WorkshopPaths.Inside(other.Root, WorkshopPackage.ManifestFileName);
                if (File.Exists(manifest)) {
                    var declaration = WorkshopPackageCodec.Read(File.ReadAllText(manifest)); WorkshopPaths.Verify(declaration, other.Root);
                    if (declaration.Roots.Intersect(package.Roots, StringComparer.OrdinalIgnoreCase).Any() || declaration.ModGuids.Intersect(package.ModGuids, StringComparer.OrdinalIgnoreCase).Any() || declaration.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase))
                        throw WorkshopDiagnostics.Blocked("Another subscribed package claims the same identity or targets: " + other.Id);
                }
                string map = Directory.EnumerateFiles(other.Root, "*.map").FirstOrDefault();
                if (map == null) continue;
                using (var archive = new MapArchive(map)) {
                    if (!archive.IsValid || archive.Archive == null) continue;
                    foreach (ZipEntry entry in archive.Archive) {
                        if (!entry.IsFile) continue;
                        string path = entry.Name.Replace('\\', '/');
                        if (package.Roots.Any(root => path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))) throw WorkshopDiagnostics.Blocked("A subscribed map owns this payload folder: " + other.Id);
                        if (path.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/info.json", StringComparison.OrdinalIgnoreCase)) {
                            var info = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(archive.TryReadTextFile(entry.Name)));
                            if (package.ModGuids.Contains(WorkshopPackageCodec.Text(info, "GUID"), StringComparer.OrdinalIgnoreCase)) throw WorkshopDiagnostics.Blocked("A subscribed map owns this mod GUID: " + other.Id);
                        }
                    }
                }
            }
        }
    }
}

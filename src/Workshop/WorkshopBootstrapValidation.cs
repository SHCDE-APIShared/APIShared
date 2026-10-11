using APIShared.Internal;
using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.Workshop
{
    internal static class WorkshopBootstrapValidation
    {
        internal static void Verify(WorkshopPackage package, string source)
        {
            if (package.Bootstrap == null) return;
            var bootstrap = package.Bootstrap;
            using (var archive = new WorkshopMapArchive(WorkshopPaths.Inside(source, bootstrap.MapFile)))
            {
                CheckInfo(archive.ReadText("info.json"), bootstrap);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ZipEntry entry in archive.Archive)
                {
                    string name = entry.Name.TrimEnd('/'); WorkshopPaths.Relative(name);
                    if (!seen.Add(name)) throw new InvalidDataException("Duplicate bootstrap archive entry.");
                    // SE stages all BepInEx entries. Reject every target outside the explicitly reserved bootstrap folder.
                    bool allowed = name == "info.json" || name == bootstrap.PluginFolder || name.StartsWith(bootstrap.PluginFolder + "/", StringComparison.Ordinal) ||
                        entry.IsDirectory && (name == "BepInEx" || name == "BepInEx/plugins");
                    if (!allowed || (entry.ExternalFileAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Bootstrap owns an undeclared or linked target: " + name);
                }
                CheckInfo(archive.ReadText(bootstrap.PluginFolder + "/info.json"), bootstrap);
                if (!seen.Contains(bootstrap.PluginFolder + "/info.json")) throw new InvalidDataException("Missing bootstrap plugin metadata.");
            }
        }
        private static void CheckInfo(string json, WorkshopBootstrap bootstrap)
        {
            var info = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(json));
            if (WorkshopPackageCodec.Text(info, "GUID") != bootstrap.Guid || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(info, "Version")) != WorkshopPackageCodec.Version(bootstrap.Version) ||
                !info.TryGetValue("Manifest", out var manifest) || !(manifest is int number) || number != 1)
                throw new InvalidDataException("Bootstrap identity/version/BepInEx manifest mismatch.");
        }
    }
}

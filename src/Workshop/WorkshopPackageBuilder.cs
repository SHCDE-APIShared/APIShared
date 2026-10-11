using System;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopPackageBuilder
    {
        internal static string Manifest(string source, WorkshopPackageDraft draft)
        {
            if (draft == null) throw new ArgumentNullException(nameof(draft));
            string payload = WorkshopPaths.Inside(source, "Payload");
            string plugins = WorkshopPaths.Inside(payload, "BepInEx/plugins");
            var roots = Directory.GetDirectories(plugins).Select(p => WorkshopPaths.PluginRoot("BepInEx/plugins/" + Path.GetFileName(p))).OrderBy(p => p, StringComparer.Ordinal).ToArray();
            var files = WorkshopPaths.Enumerate(payload).Select(path => new WorkshopFile(WorkshopPaths.DeliveredPath(path.Substring(payload.Length + 1).Replace('\\', '/')), new FileInfo(path).Length, WorkshopInventory.Hash(path))).ToArray();
            string json = WorkshopPackageCodec.Write(new { SchemaVersion = 1, draft.PackageId, draft.Version, draft.MinimumAPISharedVersion,
                draft.MinimumScriptExtenderVersion, draft.MaximumScriptExtenderVersion, Roots = roots, Files = files, draft.Dependencies, draft.Bootstrap, draft.LegacyReleases });
            WorkshopPaths.Verify(WorkshopPackageCodec.Read(json), source);
            return json;
        }
    }
}

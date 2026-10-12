using APIShared.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // A small explicit schema adapter: no serializer assembly and no inferred installation targets.
    internal static class WorkshopPackageCodec
    {
        internal static Dictionary<string, object> Object(object value) => value as Dictionary<string, object> ?? throw new InvalidDataException("Expected a JSON object.");
        internal static string Text(Dictionary<string, object> row, string key, bool optional = false)
        {
            if (!row.TryGetValue(key, out object value) && optional) return "";
            return value as string ?? throw new InvalidDataException("Missing or invalid string: " + key);
        }
        internal static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> row, string key, bool optional = false)
        {
            if (!row.TryGetValue(key, out object value) && optional) yield break;
            if (!(value is IList list)) throw new InvalidDataException("Expected an array: " + key);
            foreach (object item in list) yield return Object(item);
        }
        internal static void Schema(Dictionary<string, object> row)
        {
            if (!row.TryGetValue("SchemaVersion", out object value) || !(value is int number) || number != 1)
                throw new InvalidDataException("Unsupported Workshop schema.");
        }
        internal static Version Version(string text)
        {
            if (!System.Version.TryParse(text, out var version) || version.Major < 0 || version.Minor < 0 || version.Build < 0)
                throw new InvalidDataException("Expected a three- or four-part numeric version: " + text);
            return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
        }
        internal static IReadOnlyList<WorkshopFile> Files(Dictionary<string, object> row)
        {
            var files = new List<WorkshopFile>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Rows(row, "Files"))
            {
                string path = WorkshopPaths.DeliveredPath(Text(entry, "Path"));
                if (!seen.Add(path)) throw new InvalidDataException("Duplicate file: " + path);
                if (!entry.TryGetValue("Size", out object size) || !(size is int || size is long) || Convert.ToInt64(size, CultureInfo.InvariantCulture) < 0)
                    throw new InvalidDataException("Invalid file size: " + path);
                files.Add(new WorkshopFile(path, Convert.ToInt64(size, CultureInfo.InvariantCulture), WorkshopInventory.CheckHash(Text(entry, "Sha256"))));
            }
            return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        internal static WorkshopPackage Read(string json)
        {
            var row = Object(DependencyFreeJson.Parse(json)); Schema(row);
            var package = new WorkshopPackage {
                PackageId = Text(row, "PackageId"), Version = Text(row, "Version"),
                MinimumAPISharedVersion = Text(row, "MinimumAPISharedVersion"),
                MinimumScriptExtenderVersion = Text(row, "MinimumScriptExtenderVersion", true),
                MaximumScriptExtenderVersion = Text(row, "MaximumScriptExtenderVersion", true), Files = Files(row)
            };
            WorkshopInventory.CheckGuid(package.PackageId); Version(package.Version); Version(package.MinimumAPISharedVersion);
            if (package.MinimumScriptExtenderVersion.Length > 0) Version(package.MinimumScriptExtenderVersion);
            if (package.MaximumScriptExtenderVersion.Length > 0) Version(package.MaximumScriptExtenderVersion);
            if (package.MinimumScriptExtenderVersion.Length > 0 && package.MaximumScriptExtenderVersion.Length > 0 &&
                Version(package.MinimumScriptExtenderVersion) > Version(package.MaximumScriptExtenderVersion)) throw new InvalidDataException("Inverted Extender version range.");
            if (!row.TryGetValue("Roots", out object roots) || !(roots is IList list) || list.Count == 0) throw new InvalidDataException("Missing package roots.");
            var folders = new List<string>();
            foreach (object root in list)
            {
                string folder = root as string ?? throw new InvalidDataException("Invalid root.");
                WorkshopPaths.PluginRoot(folder);
                if (folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Duplicate root.");
                folders.Add(folder);
            }
            package.Roots = folders.AsReadOnly();
            if (row.TryGetValue("Bootstrap", out object bootstrap) && bootstrap != null)
            {
                var b = Object(bootstrap);
                package.Bootstrap = new WorkshopBootstrap { MapFile = Text(b, "MapFile"), Guid = Text(b, "Guid"), Version = Text(b, "Version"), PluginFolder = Text(b, "PluginFolder") };
                WorkshopInventory.CheckGuid(package.Bootstrap.Guid); Version(package.Bootstrap.Version);
                WorkshopPaths.PluginRoot(package.Bootstrap.PluginFolder);
                WorkshopInventory.CheckGuid(package.Bootstrap.MapFile);
                if (!package.Bootstrap.MapFile.EndsWith(".map", StringComparison.OrdinalIgnoreCase) || folders.Contains(package.Bootstrap.PluginFolder, StringComparer.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid or overlapping bootstrap.");
            }
            package.Dependencies = Dependencies(row);
            var legacy = new List<WorkshopLegacyRelease>();
            foreach (var release in Rows(row, "LegacyReleases", true))
            {
                string version = Text(release, "Version"); Version(version);
                legacy.Add(new WorkshopLegacyRelease { Version = version, Files = Files(release) });
            }
            package.LegacyReleases = legacy.AsReadOnly();
            foreach (WorkshopFile file in package.Files.Concat(legacy.SelectMany(x => x.Files)))
                if (!folders.Any(root => file.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("File outside declared roots: " + file.Path);
            if (package.Files.Count == 0) throw new InvalidDataException("Empty package.");
            foreach (string root in folders)
                if (!package.Files.Any(f => f.Path.Equals(root + "/info.json", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Root lacks info.json: " + root);
            return package;
        }
        private static IReadOnlyList<WorkshopDependency> Dependencies(Dictionary<string, object> row)
        {
            var dependencies = new List<WorkshopDependency>();
            foreach (var d in Rows(row, "Dependencies", true))
            {
                var dependency = new WorkshopDependency { Guid = Text(d, "Guid"), MinimumVersion = Text(d, "MinimumVersion", true), MaximumVersion = Text(d, "MaximumVersion", true), WorkshopId = Text(d, "WorkshopId", true) };
                WorkshopInventory.CheckGuid(dependency.Guid);
                if (dependencies.Any(x => x.Guid.Equals(dependency.Guid, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Duplicate dependency.");
                if (dependency.MinimumVersion.Length > 0) Version(dependency.MinimumVersion);
                if (dependency.MaximumVersion.Length > 0) Version(dependency.MaximumVersion);
                if (dependency.MinimumVersion.Length > 0 && dependency.MaximumVersion.Length > 0 && Version(dependency.MinimumVersion) > Version(dependency.MaximumVersion)) throw new InvalidDataException("Inverted dependency range.");
                if (dependency.WorkshopId.Length > 0) Id(dependency.WorkshopId);
                dependencies.Add(dependency);
            }
            return dependencies.AsReadOnly();
        }
        internal static ulong Id(string value) => ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) && id != 0 ? id : throw new InvalidDataException("Invalid Workshop ID.");
        internal static WorkshopReceipt Receipt(string json)
        {
            var row = Object(DependencyFreeJson.Parse(json)); Schema(row);
            var receipt = new WorkshopReceipt { WorkshopId = Text(row, "WorkshopId"), PackageId = Text(row, "PackageId"), Version = Text(row, "Version"), ManifestHash = Text(row, "ManifestHash"), TransactionId = Text(row, "TransactionId"), Files = Files(row) };
            Id(receipt.WorkshopId); WorkshopInventory.CheckGuid(receipt.PackageId); Version(receipt.Version);
            WorkshopInventory.CheckHash(receipt.ManifestHash); WorkshopInventory.CheckGuid(receipt.TransactionId);
            if (!row.TryGetValue("ModGuids", out var ids) || !(ids is IList list)) throw new InvalidDataException("Missing receipt mod identities.");
            var guids = new List<string>();
            foreach (var id in list) { string guid = id as string ?? throw new InvalidDataException("Invalid receipt GUID."); WorkshopInventory.CheckGuid(guid); if (guids.Contains(guid, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Duplicate receipt GUID."); guids.Add(guid); }
            receipt.ModGuids = guids.AsReadOnly();
            if (row.TryGetValue("Dependencies", out var declared) && declared != null) receipt.Dependencies = Dependencies(row);
            return receipt;
        }
        internal static string Write(object value) => DependencyFreeJson.Serialize(value);
    }
}

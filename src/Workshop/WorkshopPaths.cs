using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopPaths
    {
        internal static string Relative(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(c => c < 32 || c == 127)) throw new InvalidDataException("Control character or empty path.");
            foreach (string part in value.Split('/')) WorkshopInventory.CheckGuid(part);
            if (value.Contains("\\") || value.Contains(":")) throw new InvalidDataException("Use relative forward-slash paths.");
            return value;
        }
        internal static string PluginRoot(string root)
        {
            Relative(root);
            string[] segments = root.Split('/');
            if (segments.Length != 3 || segments[0] != "BepInEx" || segments[1] != "plugins" ||
                segments[2].Equals("APIShared_Serp", StringComparison.OrdinalIgnoreCase) || segments[2].Equals("000shcdese", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A root must be a non-infrastructure BepInEx/plugins/<folder>.");
            return root;
        }
        internal static string DeliveredPath(string path)
        {
            Relative(path); string[] parts = path.Split('/');
            if (parts.Length < 4) throw new InvalidDataException("Expected a plugin file.");
            PluginRoot(string.Join("/", parts.Take(3)));
            if (parts.Skip(3).Any(x => x.Equals("config", StringComparison.OrdinalIgnoreCase) || x.Equals("LobbyModSettings", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Mutable settings must not be delivered.");
            return path;
        }
        internal static string Inside(string root, string relative)
        {
            Relative(relative);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Escaping path.");
            CheckLinks(fullRoot); CheckLinks(target); return target;
        }
        internal static void CheckLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Linked path: " + current); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        internal static IEnumerable<string> Enumerate(string root)
        {
            CheckLinks(root);
            foreach (string file in Directory.EnumerateFiles(root)) { CheckLinks(file); yield return file; }
            foreach (string directory in Directory.EnumerateDirectories(root))
                foreach (string file in Enumerate(directory)) yield return file;
        }
        internal static void Verify(WorkshopPackage package, string source)
        {
            string payload = Inside(source, "Payload");
            var actual = Enumerate(payload).Select(f => f.Substring(payload.Length + 1).Replace('\\', '/')).ToArray();
            var actualPaths = new HashSet<string>(actual, StringComparer.Ordinal);
            if (actual.Length != package.Files.Count) throw new InvalidDataException("Payload differs from complete file inventory.");
            foreach (var file in package.Files)
            {
                string path = Inside(payload, file.Path);
                if (!actualPaths.Contains(file.Path) || new FileInfo(path).Length != file.Size || WorkshopInventory.Hash(path) != file.Sha256)
                    throw new InvalidDataException("Payload hash/size mismatch: " + file.Path);
            }
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var metadata in package.Files.Where(f => Path.GetFileName(f.Path).Equals("info.json", StringComparison.OrdinalIgnoreCase)))
            {
                var info = WorkshopPackageCodec.Object(APIShared.Internal.DependencyFreeJson.Parse(File.ReadAllText(Inside(payload, metadata.Path))));
                string guid = WorkshopPackageCodec.Text(info, "GUID"); WorkshopInventory.CheckGuid(guid);
                if (guid.Equals("APIShared_Serp", StringComparison.OrdinalIgnoreCase) || guid.Equals("000shcdese", StringComparison.OrdinalIgnoreCase) || !guids.Add(guid) || guid.Equals(package.Bootstrap?.Guid, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Reserved or duplicate mod GUID.");
                versions.Add(guid, WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(info, "Version")));
            }
            package.ModGuids = guids.OrderBy(g => g, StringComparer.Ordinal).ToList().AsReadOnly();
            package.ModVersions = new System.Collections.ObjectModel.ReadOnlyDictionary<string, Version>(versions);
            string[] maps = Directory.GetFiles(source).Where(f => Path.GetExtension(f).Equals(".map", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (package.Bootstrap == null && maps.Length != 0 || package.Bootstrap != null && (maps.Length != 1 || Path.GetFileName(maps[0]) != package.Bootstrap.MapFile))
                throw new InvalidDataException("Mixed packages require exactly their declared bootstrap map.");
        }
    }
}

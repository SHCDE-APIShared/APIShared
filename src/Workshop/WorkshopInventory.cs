using APIShared.Internal;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace APIShared.Workshop
{
    /// <summary>Opt-in package inventory contract. Paths are relative to the owner's
    /// BepInEx/plugins/GUID directory. Mutable settings must never be inventoried.
    /// LegacyFiles describes audited older deliveries, not arbitrary deletion requests.</summary>
    public static class WorkshopInventory
    {
        /// <summary>Inventory filename inside the package GUID directory. The inventory does not list itself.</summary>
        public const string FileName = "apishared-workshop-files.json";
        internal sealed class Inventory
        {
            internal string Guid, Version;
            internal readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, HashSet<string>> Legacy = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        internal static Inventory Read(string json, string guid, string version)
        {
            var root = DependencyFreeJson.Parse(json) as Dictionary<string, object>;
            if (root == null || !root.TryGetValue("SchemaVersion", out object schema) || !(schema is int number) || number != 1 ||
                Text(root, "Guid") != guid || Text(root, "Version") != version)
                throw new InvalidDataException("Workshop inventory identity/version mismatch.");
            CheckGuid(guid);
            var result = new Inventory { Guid = guid, Version = version };
            foreach (var row in Rows(root, "Files"))
            {
                string path = CheckPath(Text(row, "Path")), hash = CheckHash(Text(row, "Sha256"));
                if (result.Files.ContainsKey(path)) throw new InvalidDataException("Duplicate inventory path: " + path);
                result.Files.Add(path, hash);
            }
            foreach (var row in Rows(root, "LegacyFiles"))
            {
                string path = CheckPath(Text(row, "Path")), hash = CheckHash(Text(row, "Sha256"));
                if (!result.Legacy.TryGetValue(path, out var hashes)) result.Legacy.Add(path, hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                hashes.Add(hash);
            }
            if (result.Files.Count == 0) throw new InvalidDataException("Empty Workshop inventory.");
            return result;
        }

        internal static IEnumerable<Dictionary<string, object>> Rows(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value)) yield break;
            if (!(value is IList rows)) throw new InvalidDataException("Invalid inventory collection: " + key);
            foreach (object row in rows)
                yield return row as Dictionary<string, object> ?? throw new InvalidDataException("Invalid inventory file record.");
        }

        internal static string Text(Dictionary<string, object> row, string key) =>
            row.TryGetValue(key, out object value) && value is string text ? text : throw new InvalidDataException("Missing inventory string: " + key);

        // Read-only copies: both calls return a new array each time, once per path segment otherwise.
        private static readonly char[] InvalidFileNameChars = Path.GetInvalidFileNameChars();
        private static readonly char[] WildcardChars = "*?[]".ToCharArray();

        internal static void CheckGuid(string guid)
        {
            if (string.IsNullOrWhiteSpace(guid) || guid == "." || guid.Contains("..") ||
                guid.IndexOfAny(InvalidFileNameChars) >= 0 || guid.IndexOfAny(WildcardChars) >= 0 || guid.Contains("/") || guid.Contains("\\") || guid.TrimEnd(' ', '.') != guid || IsDeviceName(guid))
                throw new InvalidDataException("Unsafe inventory GUID.");
        }

        internal static string CheckPath(string path)
        {
            string[] segments = path?.Split('/') ?? Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(path) || path.Contains("\\") || path.Contains(":") || path.IndexOfAny(WildcardChars) >= 0 ||
                Path.IsPathRooted(path) || segments.Any(x => x == "" || x == "." || x == "..") ||
                segments.Any(x => x.IndexOfAny(InvalidFileNameChars) >= 0 || x.TrimEnd(' ', '.') != x ||
                    IsDeviceName(x)) ||
                segments.Any(x => string.Equals(x, "LobbyModSettings", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x, "config", StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(path, FileName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsafe or mutable inventory path: " + path);
            return path;
        }

        private static bool IsDeviceName(string segment)
        {
            string name = segment.Split('.')[0].ToUpperInvariant();
            return name == "CON" || name == "PRN" || name == "AUX" || name == "NUL" ||
                (name.Length == 4 && (name.StartsWith("COM") || name.StartsWith("LPT")) && name[3] >= '1' && name[3] <= '9');
        }

        internal static void VerifyFiles(Inventory inventory, string root)
        {
            foreach (var file in inventory.Files)
            {
                string path = Path.Combine(root, file.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!IsContainedWithoutLinks(root, path) || !File.Exists(path) || Hash(path) != file.Value)
                    throw new InvalidDataException("Workshop inventory payload mismatch: " + file.Key);
            }
        }

        internal static string CheckHash(string hash)
        {
            if (hash == null || hash.Length != 64 || hash.Any(x => !Uri.IsHexDigit(x)))
                throw new InvalidDataException("Invalid inventory SHA-256.");
            return hash.ToLowerInvariant();
        }

        internal static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static bool IsContainedWithoutLinks(string root, string path)
        {
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            path = Path.GetFullPath(path);
            // The Windows SE updater uses -Path, so absolute wildcard paths are unsafe too.
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || path.IndexOfAny("*?[]".ToCharArray()) >= 0) return false;
            for (string current = path; current != null; current = Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }

        internal static IEnumerable<string> Plan(Inventory previous, Inventory next, string installedRoot, string stagedRoot, Action<string> warn)
        {
            var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (previous != null)
                foreach (var file in previous.Files) candidates[file.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.Value };
            foreach (var file in next.Legacy)
            {
                if (!candidates.TryGetValue(file.Key, out var hashes)) candidates[file.Key] = hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                hashes.UnionWith(file.Value);
            }
            foreach (var file in candidates)
            {
                if (next.Files.ContainsKey(file.Key)) continue;
                string target = Path.Combine(installedRoot, file.Key.Replace('/', Path.DirectorySeparatorChar));
                string staged = Path.Combine(stagedRoot, file.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!IsContainedWithoutLinks(installedRoot, target) || !IsContainedWithoutLinks(stagedRoot, staged))
                    throw new InvalidDataException("Linked or escaping Workshop path: " + file.Key);
                if (File.Exists(staged) || !File.Exists(target)) continue;
                if (!file.Value.Contains(Hash(target))) { warn("Preserved modified package file: " + target); continue; }
                yield return target;
            }
        }
    }
}

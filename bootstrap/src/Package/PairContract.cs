using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using APIShared.Internal;

namespace APIShared.DependencyBootstrap
{
    // This contract is compiled into the independent bootstrap and the offline tests.
    // Paths are constants, never taken from a package-supplied manifest.
    internal sealed class PairContract
    {
        internal const string RuntimePath = "BepInEx/plugins/APIShared_Serp/APIShared.dll";
        internal const string BootstrapPath = "BepInEx/patchers/APIShared.DependencyBootstrap.dll";
        internal const string ManifestPath = "BepInEx/plugins/APIShared_Serp/dependency-bootstrap-pair.json";
        internal string Root, RuntimeHash, BootstrapHash, Version;
        internal static string SafePath(string root, string relative)
        {
            if (relative != RuntimePath && relative != BootstrapPath && relative != ManifestPath)
                throw new InvalidDataException("Unknown pair path");
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pair path escaped root");
            for (string part = path; part != null; part = Path.GetDirectoryName(part))
                if ((File.Exists(part) || Directory.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Linked pair path");
            return path;
        }
        internal static string Hash(string path) => ProbeIdentity.Hash(path).ToLowerInvariant();
        internal static PairContract Read(string root)
        {
            var json = DependencyFreeJson.Parse(File.ReadAllText(SafePath(root, ManifestPath))) as Dictionary<string, object>;
            string[] fields = { "Schema", "Guid", "Version", "RuntimeSHA256", "BootstrapSHA256" };
            if (json == null || json.Count != fields.Length || fields.Any(f => !json.ContainsKey(f)) ||
                !(json["Schema"] is int schema) || schema != 1 || (json["Guid"] as string) != "APIShared_Serp")
                throw new InvalidDataException("Invalid bootstrap pair schema");
            string version = json["Version"] as string;
            if (!System.Version.TryParse(version, out _)) throw new InvalidDataException("Invalid pair version");
            var pair = new PairContract { Root = Path.GetFullPath(root), Version = version,
                RuntimeHash = Digest(json["RuntimeSHA256"]), BootstrapHash = Digest(json["BootstrapSHA256"]) };
            pair.VerifyFiles();
            return pair;
        }
        private static string Digest(object value)
        {
            string text = value as string;
            if (text == null || text.Length != 64 || text.Any(c => !(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')))
                throw new InvalidDataException("Invalid pair hash");
            return text;
        }
        internal void VerifyFiles()
        {
            if (Hash(SafePath(Root, RuntimePath)) != RuntimeHash || Hash(SafePath(Root, BootstrapPath)) != BootstrapHash)
                throw new InvalidDataException("Incomplete or mixed bootstrap pair");
        }
        internal bool MatchesSelected(string guid, string version, string location)
        {
            try
            {
                VerifyFiles();
                return guid == "APIShared_Serp" && System.Version.Parse(version).Equals(System.Version.Parse(Version)) &&
                    string.Equals(Path.GetFullPath(location), SafePath(Root, RuntimePath), StringComparison.OrdinalIgnoreCase) && Hash(location) == RuntimeHash;
            }
            catch { return false; }
        }
        internal void VerifyBootstrap(string loadedLocation, int instances, bool probePresent)
        {
            VerifyFiles();
            if (instances != 1 || probePresent || !string.Equals(Path.GetFullPath(loadedLocation), SafePath(Root, BootstrapPath), StringComparison.OrdinalIgnoreCase) || Hash(loadedLocation) != BootstrapHash)
                throw new InvalidDataException("Bootstrap ownership or loaded identity mismatch");
        }
    }
}

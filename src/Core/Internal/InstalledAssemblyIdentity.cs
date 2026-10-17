using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using Mono.Cecil;

namespace APIShared.Internal
{
    // Unity's byte-loaded game assembly has no Location. The installed image is
    // selected explicitly; a hash alone must never identify a different loaded module.
    internal static class InstalledAssemblyIdentity
    {
        internal static string GameAssemblyPath(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot))
                throw new ArgumentException("BepInEx game root is unavailable.", nameof(gameRoot));
            return Path.Combine(Path.GetFullPath(gameRoot), "Stronghold Crusader Definitive Edition_Data",
                "Managed", "Assembly-CSharp.dll");
        }

        internal static string UnityCoreAssemblyPath(string gameRoot) =>
            Path.Combine(Path.GetDirectoryName(GameAssemblyPath(gameRoot)), "UnityEngine.CoreModule.dll");

        internal static void Verify(Assembly loaded, string path, params string[] expectedHashes)
        {
            if (expectedHashes == null || expectedHashes.Length == 0)
                throw new ArgumentException("A hash-bound contract requires expected hashes.", nameof(expectedHashes));
            VerifyCore(loaded, path, expectedHashes);
        }

        // For managed participants whose behavior is validated at the actual hook
        // boundary. This is module provenance, not whole-file behavior approval.
        internal static void VerifyModule(Assembly loaded, string path) => VerifyCore(loaded, path, null);

        private static void VerifyCore(Assembly loaded, string path, string[] expectedHashes)
        {
            if (loaded == null || loaded.IsDynamic || string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("Assembly identity/path unavailable: " + (loaded?.FullName ?? "<null>"));
            // One read-only handle binds the hashed bytes and Cecil metadata to the
            // same file, excluding replacement/writes between the two checks.
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (expectedHashes != null)
                {
                    string hash;
                    using (var sha = SHA256.Create())
                        hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    if (!Array.Exists(expectedHashes,
                        expected => string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Unsupported binary: " + Path.GetFileName(path));
                    stream.Position = 0;
                }
                using (var disk = AssemblyDefinition.ReadAssembly(stream))
                    if (disk.Name.FullName != loaded.FullName ||
                        disk.MainModule.Mvid != loaded.ManifestModule.ModuleVersionId)
                        throw new InvalidOperationException("Loaded/disk module identity differs: " + Path.GetFileName(path));
            }
        }
    }
}

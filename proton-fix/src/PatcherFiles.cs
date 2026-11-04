using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Reflection;

namespace APIShared.ProtonWorkshopFix
{
    // The independent patcher owns only its cache and retry record, never APIShared's files.
    internal static class PatcherFiles
    {
        internal static string Inside(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOfAny(new[] { ':', '\r', '\n', '\t', '\0' }) >= 0)
                throw new InvalidDataException("Invalid patcher relative path.");
            foreach (string part in relative.Replace('\\', '/').Split('/'))
                if (part.Length == 0 || part == "." || part == "..")
                    throw new InvalidDataException("Invalid patcher relative path component.");
            string fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
            string path = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Patcher path escaped its root.");
            CheckLinks(fullRoot); CheckLinks(path);
            return path;
        }

        internal static void CheckLinks(string path)
        {
            for (string current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Linked patcher path: " + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static bool SamePath(string left, string right) => string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        internal static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string ManagedAssemblyName(string path)
        {
            // Valid native helpers are normal in patcher folders. Unreadable/malformed images are not proof of another owner.
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 64 || reader.ReadUInt16() != 0x5a4d) throw new InvalidDataException("Invalid patcher image: " + path);
                stream.Position = 0x3c; int pe = reader.ReadInt32();
                if (pe < 64 || pe > stream.Length - 24) throw new InvalidDataException("Invalid patcher PE header: " + path);
                stream.Position = pe;
                if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("Invalid patcher PE signature: " + path);
                stream.Position = pe + 20; int size = reader.ReadUInt16();
                long optional = pe + 24;
                if (size < 2 || optional + size > stream.Length) throw new InvalidDataException("Invalid patcher optional header: " + path);
                stream.Position = optional; ushort magic = reader.ReadUInt16();
                int directories = magic == 0x20b ? 112 : magic == 0x10b ? 96 : -1;
                if (directories < 0 || size < directories + 15 * 8) throw new InvalidDataException("Invalid patcher data directories: " + path);
                stream.Position = optional + directories + 14 * 8;
                if (reader.ReadUInt32() == 0) return null;
            }
            return AssemblyName.GetAssemblyName(path).Name;
        }

        private static string RetryPath(string configDirectory) => Inside(configDirectory, "APIShared.ProtonWorkshopFix/retry-v1.state");

        internal static bool RetryAvailable(string configDirectory)
        {
            string path = RetryPath(configDirectory);
            try { File.GetAttributes(path); return false; }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            // Access errors, directories and damaged existing records never grant another try.
        }

        internal static bool ClaimRetry(string configDirectory)
        {
            string path = RetryPath(configDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            CheckLinks(path);
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes("APISHARED-PROTON-SE-RETRY-1\r\n" +
                        DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                return true;
            }
            catch (IOException)
            {
                if (!RetryAvailable(configDirectory)) return false;
                throw;
            }
        }
    }
}

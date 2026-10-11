using APIShared.Internal;
using ICSharpCode.SharpZipLib.Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace APIShared.Workshop
{
    // Matches the audited Extender's first-ZIP-header convention without loading
    // Unity or BepInEx in the offline publisher. Owns the stream until disposal.
    internal sealed class WorkshopMapArchive : IDisposable
    {
        internal readonly ZipFile Archive;
        internal readonly Dictionary<string, object> Info;
        internal WorkshopMapArchive(string path)
        {
            WorkshopPaths.CheckLinks(path);
            byte[] bytes = File.ReadAllBytes(path);
            int offset = -1;
            for (int i = 0; i <= bytes.Length - 4; i++)
                if (bytes[i] == 80 && bytes[i + 1] == 75 && bytes[i + 2] == 3 && bytes[i + 3] == 4) { offset = i; break; }
            if (offset < 0) throw new InvalidDataException("Map has no ZIP payload.");
            var stream = new MemoryStream(bytes, offset, bytes.Length - offset, false);
            try {
                Archive = new ZipFile(stream) { IsStreamOwner = true };
                ValidateEntries();
                Info = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(ReadText("info.json")));
            } catch { Archive?.Close(); stream.Dispose(); throw; }
        }
        private void ValidateEntries()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ZipEntry entry in Archive) {
                string name = entry.Name.TrimEnd('/'); WorkshopPaths.Relative(name);
                if (!names.Add(name) || (entry.ExternalFileAttributes >> 16 & 0xF000) == 0xA000) throw new InvalidDataException("Duplicate or linked map entry.");
                if (entry.IsFile) files.Add(name);
            }
            foreach (string name in names) {
                string parent = name;
                while (parent.LastIndexOf('/') >= 0) {
                    parent = parent.Substring(0, parent.LastIndexOf('/'));
                    if (files.Contains(parent)) throw new InvalidDataException("File/directory overlap in map archive.");
                }
            }
        }
        internal string ReadText(string path)
        {
            ZipEntry entry = Archive.GetEntry(path);
            if (entry == null || !entry.IsFile) throw new InvalidDataException("Missing map entry: " + path);
            using (var input = Archive.GetInputStream(entry))
            using (var reader = new StreamReader(input, Encoding.UTF8, true)) return reader.ReadToEnd();
        }
        public void Dispose() { Archive.Close(); }
    }
}

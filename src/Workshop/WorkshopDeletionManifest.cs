using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopDeletionManifest
    {
        // Atomically extend the SE's existing manifest; never deploy temporary files.
        internal static void Append(string seRoot, IEnumerable<string> victims)
        {
            string staging = Path.Combine(seRoot, ".staging");
            string manifest = Path.Combine(staging, "delete_list.txt");
            if (!WorkshopInventory.IsContainedWithoutLinks(seRoot, manifest)) throw new InvalidDataException("Linked staging directory.");
            var paths = new HashSet<string>(File.Exists(manifest) ? File.ReadAllLines(manifest) : new string[0], StringComparer.OrdinalIgnoreCase);
            paths.UnionWith(victims);
            Directory.CreateDirectory(staging);
            string temporary = Path.Combine(seRoot, ".cleanup-delete-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temporary, string.Join("\r\n", paths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) + "\r\n");
                AtomicFileReplacement.Replace(temporary, manifest);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}

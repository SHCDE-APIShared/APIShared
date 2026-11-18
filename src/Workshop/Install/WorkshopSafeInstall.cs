using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace APIShared.Workshop.Install
{
    // Safe installation of loose Workshop packages (docs/WORKSHOP.md, "Installation").
    // Compiled into APIShared.dll (staging, requests) and into APIShared.DependencyBootstrap
    // (swap and recovery in the preloader's Finish, before the chainloader opens anything in
    // BepInEx/plugins). Only System types: the preloader must not touch Unity, Extender or
    // Steam types, and the offline tests compile this file unchanged.
    //
    //   Stage    the runtime copies every package root into _APIShared/I/s/<id>.tmp, verifies
    //            the copies against the manifest, writes plan, receipt and a completion
    //            marker, then publishes the container with one rename and writes a request.
    //   Apply    the preloader journals the transaction, carries listed player files over,
    //            renames every installed root to a backup, renames every staged root into
    //            BepInEx/plugins, writes the receipt and deletes backups, container, journal.
    //   Recover  the preloader finishes or undoes an interrupted transaction from the actual
    //            file system state before applying anything else.
    // A plugin folder is never changed file by file, so BepInEx always sees the complete
    // old or the complete new version of every root.
    // Simulated process death for tests (see InstallSwap.Fault).
    internal sealed class InstallCrashSimulation : Exception
    {
        internal InstallCrashSimulation(string step) : base("simulated crash at " + step) { }
    }

    internal sealed class InstallFile
    {
        internal readonly string Path;   // relative to its root, '/'-separated
        internal readonly long Size;
        internal readonly string Sha256;
        internal InstallFile(string path, long size, string sha256) { Path = path; Size = size; Sha256 = sha256; }
    }

    internal sealed class InstallRoot
    {
        internal readonly string Target;  // game-relative, e.g. BepInEx/plugins/Example
        internal readonly string Source;  // absolute source folder (Workshop Payload root)
        internal readonly IList<InstallFile> Files;
        internal InstallRoot(string target, string source, IList<InstallFile> files) { Target = target; Source = source; Files = files; }
    }

    internal static class InstallTree
    {
        internal const string CompleteName = ".apishared-complete";
        internal const string MarkerName = ".apishared-install";

        internal static string Sha256(string file)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        internal static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

        // Relative '/'-separated paths of all files below root, excluding installer metadata.
        // Links are rejected: an installation never follows or moves reparse points.
        internal static List<string> Files(string root)
        {
            var result = new List<string>();
            if (!Directory.Exists(root)) return result;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string sub in Directory.GetDirectories(directory))
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) throw new IOException("Link inside a package folder: " + sub);
                    pending.Push(sub);
                }
                foreach (string file in Directory.GetFiles(directory))
                {
                    string relative = file.Substring(root.Length + 1).Replace('\\', '/');
                    if (relative != CompleteName && relative != MarkerName && relative != InstallKeep.StateName) result.Add(relative);
                }
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        internal static void WriteDurable(string path, string text)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(true);
            }
        }

        internal static string ReadMarker(string root)
        {
            string path = Path.Combine(root, MarkerName);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }

        internal static int CopyOver(string source, string target)
        {
            int count = 0;
            foreach (string relative in Files(source))
            {
                string to = Path.Combine(target, Native(relative));
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(Path.Combine(source, Native(relative)), to, true);
                count++;
            }
            return count;
        }

        // Deletes a tree without following links. Read-only attributes are cleared first.
        internal static void Delete(string root)
        {
            if (!Directory.Exists(root)) return;
            foreach (string sub in Directory.GetDirectories(root))
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) Directory.Delete(sub, false);
                else Delete(sub);
            }
            foreach (string file in Directory.GetFiles(root))
            {
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            Directory.Delete(root, false);
        }

        internal static void DeleteFileIfExists(string path)
        {
            if (File.Exists(path)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        }
    }

    // apishared-keep.txt, the only file a mod author maintains (docs/WORKSHOP.md, "Keep list").
    // Any folder of a root may contain one; each line is a path relative to that folder:
    //   settings.json        file kept across updates
    //   config/              folder (trailing '/')
    //   presets.json 2       reset revision: raising it replaces the player's copy once
    //   config/ persist      also kept when the package is uninstalled
    // '#' starts a comment. No wildcards, no absolute paths, no '..', no duplicates.
    internal static class InstallKeep
    {
        internal const string ListName = "apishared-keep.txt";
        internal const string StateName = ".apishared-keep.state";

        internal sealed class Entry
        {
            internal string Path;      // relative to the root, '/'-separated
            internal bool Folder;
            internal int Revision;
            internal bool Persist;
        }

        internal static List<Entry> Read(string root)
        {
            var entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            foreach (string list in InstallTree.Files(root).Where(p => p == ListName || p.EndsWith("/" + ListName, StringComparison.OrdinalIgnoreCase)))
            {
                string prefix = list.Length == ListName.Length ? "" : list.Substring(0, list.Length - ListName.Length);
                int number = 0;
                foreach (string raw in File.ReadAllLines(Path.Combine(root, InstallTree.Native(list))))
                {
                    number++;
                    string line = raw;
                    int comment = line.IndexOf('#');
                    if (comment >= 0) line = line.Substring(0, comment);
                    var tokens = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    if (tokens.Count == 0) continue;
                    var entry = new Entry();
                    // Options follow the path; a path containing spaces is not supported on purpose.
                    for (int i = tokens.Count - 1; i > 0; i--)
                    {
                        if (tokens[i] == "persist" && !entry.Persist) { entry.Persist = true; tokens.RemoveAt(i); }
                        else if (int.TryParse(tokens[i], NumberStyles.None, CultureInfo.InvariantCulture, out int revision) && entry.Revision == 0 && revision > 0) { entry.Revision = revision; tokens.RemoveAt(i); }
                        else break;
                    }
                    if (tokens.Count != 1) throw Invalid(list, number, raw);
                    string path = tokens[0].Replace('\\', '/');
                    entry.Folder = path.EndsWith("/", StringComparison.Ordinal);
                    path = path.TrimEnd('/');
                    if (path.Length == 0 || path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0 || path.IndexOfAny(new[] { '*', '?' }) >= 0 ||
                        path.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                        throw Invalid(list, number, raw);
                    entry.Path = prefix + path;
                    if (entries.ContainsKey(entry.Path)) throw new InvalidDataException(list + " line " + number + ": duplicate entry " + entry.Path);
                    entries[entry.Path] = entry;
                }
            }
            return entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
        }

        private static InvalidDataException Invalid(string list, int number, string raw) =>
            new InvalidDataException(list + " line " + number + ": invalid entry '" + raw.Trim() + "'");

        internal static Dictionary<string, int> ReadState(string root)
        {
            var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            string path = Path.Combine(root, StateName);
            if (!File.Exists(path)) return state; // first installation with this system: revision 0
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split('\t');
                if (parts.Length == 2 && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int revision)) state[parts[0]] = revision;
            }
            return state;
        }

        // Whether a file of the root is covered by a keep entry (excluded from receipt checks).
        internal static bool Covers(IEnumerable<Entry> entries, string relative) =>
            entries.Any(e => e.Folder ? relative.StartsWith(e.Path + "/", StringComparison.OrdinalIgnoreCase) : string.Equals(relative, e.Path, StringComparison.OrdinalIgnoreCase));

        // Copies the listed player files from source (installed root or persist parking) into
        // the staged, not yet loaded root and writes the applied revisions. Every attempt first
        // undoes the previous attempt (undo folder), so the result depends only on the current
        // player state: newest data wins, a file deleted meanwhile falls back to the shipped one.
        internal static string CarryOver(string source, string staged, string undo)
        {
            Undo(staged, undo);
            var entries = Read(staged);
            var applied = source == null ? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) : ReadState(source);
            var state = new StringBuilder();
            var copies = new List<string>();
            int kept = 0, reset = 0;
            foreach (var entry in entries)
            {
                state.Append(entry.Path).Append('\t').Append(entry.Revision.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if (source == null) continue;
                string from = Path.Combine(source, InstallTree.Native(entry.Path));
                bool present = entry.Folder ? Directory.Exists(from) : File.Exists(from);
                if (!present) continue;
                // The new version ships the other kind (file vs folder) at this path: shipped wins.
                string shippedPath = Path.Combine(staged, InstallTree.Native(entry.Path));
                if (entry.Folder ? File.Exists(shippedPath) : Directory.Exists(shippedPath)) continue;
                applied.TryGetValue(entry.Path, out int old);
                if (entry.Revision > old) { reset++; continue; } // shipped version (or nothing) wins once
                if (entry.Folder)
                {
                    if ((File.GetAttributes(from) & FileAttributes.ReparsePoint) != 0) throw new IOException("Link in kept folder " + from);
                    copies.AddRange(InstallTree.Files(from).Select(p => entry.Path + "/" + p));
                }
                else copies.Add(entry.Path);
                kept++;
            }
            // A deeper file/folder clash with the new version (e.g. old folder config/x, new file
            // config/x): the shipped version wins for that path instead of failing at every start.
            copies.RemoveAll(relative => Clashes(staged, relative));
            if (copies.Count > 0)
            {
                foreach (string relative in copies)
                {
                    string shipped = Path.Combine(staged, InstallTree.Native(relative));
                    if (!File.Exists(shipped)) continue;
                    string save = Path.Combine(undo, "shipped", InstallTree.Native(relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(save));
                    File.Copy(shipped, save, true);
                }
                Directory.CreateDirectory(undo);
                // Written before the first overwrite: a crash afterwards is undone completely.
                InstallTree.WriteDurable(Path.Combine(undo, "carried.txt"), string.Join("\r\n", copies.ToArray()) + "\r\n");
                foreach (string relative in copies)
                {
                    string to = Path.Combine(staged, InstallTree.Native(relative));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(Path.Combine(source, InstallTree.Native(relative)), to, true);
                }
            }
            if (entries.Count > 0) InstallTree.WriteDurable(Path.Combine(staged, StateName), state.ToString());
            return "kept=" + kept + " reset=" + reset + " files=" + copies.Count;
        }

        // True when copying relative into staged would need a folder where the staged root has a
        // file (any parent) or a file where it has a folder.
        private static bool Clashes(string staged, string relative)
        {
            string path = Path.Combine(staged, InstallTree.Native(relative));
            if (Directory.Exists(path)) return true;
            for (string dir = Path.GetDirectoryName(path); dir.Length > staged.Length; dir = Path.GetDirectoryName(dir))
                if (File.Exists(dir)) return true;
            return false;
        }

        // Restores the pristine staged root after an interrupted or failed attempt.
        internal static void Undo(string staged, string undo)
        {
            if (!Directory.Exists(undo)) return;
            string carried = Path.Combine(undo, "carried.txt");
            if (File.Exists(carried))
                foreach (string relative in File.ReadAllLines(carried).Where(l => l.Length > 0))
                {
                    string file = Path.Combine(staged, InstallTree.Native(relative));
                    if (!File.Exists(file)) continue;
                    InstallTree.DeleteFileIfExists(file);
                    for (string dir = Path.GetDirectoryName(file);
                         dir.Length > staged.Length && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
                         dir = Path.GetDirectoryName(dir))
                        Directory.Delete(dir);
                }
            string shipped = Path.Combine(undo, "shipped");
            if (Directory.Exists(shipped)) InstallTree.CopyOver(shipped, staged);
            InstallTree.DeleteFileIfExists(Path.Combine(staged, StateName));
            InstallTree.Delete(undo);
        }

        // Copies persist entries (and the applied revisions) of an installed root into the parking
        // folder before an uninstallation removes the root. Idempotent; replaces older parking.
        internal static int Park(string root, string parking)
        {
            // Installed lists were validated at staging; one edited into an invalid state must not
            // block the removal at every start, so it parks nothing.
            List<Entry> entries;
            try { entries = Read(root).Where(e => e.Persist).ToList(); }
            catch (InvalidDataException) { entries = new List<Entry>(); }
            if (Directory.Exists(parking)) InstallTree.Delete(parking);
            int count = 0;
            foreach (var entry in entries)
            {
                string from = Path.Combine(root, InstallTree.Native(entry.Path));
                string to = Path.Combine(parking, InstallTree.Native(entry.Path));
                if (entry.Folder && Directory.Exists(from)) count += InstallTree.CopyOver(from, to);
                else if (!entry.Folder && File.Exists(from))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(from, to, true);
                    count++;
                }
            }
            if (count > 0 && File.Exists(Path.Combine(root, StateName))) File.Copy(Path.Combine(root, StateName), Path.Combine(parking, StateName), true);
            return count;
        }
    }

    // Folder layout below <game>/_APIShared (same volume as BepInEx/plugins, short paths).
    internal sealed class InstallPaths
    {
        internal readonly string Game, Work, Staging, Backups, Journals, Requests, Parking, Receipts;
        internal InstallPaths(string game)
        {
            Game = Path.GetFullPath(game);
            Work = Path.Combine(Game, "_APIShared", "I");
            Staging = Path.Combine(Work, "s");
            Backups = Path.Combine(Work, "b");
            Journals = Path.Combine(Work, "j");
            Requests = Path.Combine(Work, "r");
            Parking = Path.Combine(Work, "k");
            Receipts = Path.Combine(Game, "_APIShared", "Workshop", "Receipts");
        }
        internal string Container(ulong id) => Path.Combine(Staging, id.ToString(CultureInfo.InvariantCulture));
        internal string Request(ulong id) => Path.Combine(Requests, id.ToString(CultureInfo.InvariantCulture) + ".request");
        internal string Journal(ulong id) => Path.Combine(Journals, id.ToString(CultureInfo.InvariantCulture) + ".journal");
        internal string Receipt(ulong id) => Path.Combine(Receipts, id.ToString(CultureInfo.InvariantCulture) + ".json");
        internal string Backup(ulong id, int index) => Path.Combine(Backups, id.ToString(CultureInfo.InvariantCulture) + "-" + index.ToString(CultureInfo.InvariantCulture));
        internal string Park(string packageId, string target) => Path.Combine(Parking, packageId, Path.GetFileName(target.Replace('/', Path.DirectorySeparatorChar)));
        internal string Target(string relative) => Path.Combine(Game, InstallTree.Native(relative));
    }

    // plan.txt inside a staged container: what the preloader may do with it.
    internal sealed class InstallPlanFile
    {
        internal const string Protocol = "APISHARED-INSTALL-1";
        internal const string Name = "plan.txt";
        internal const string ReceiptName = "receipt.json";
        internal ulong WorkshopId;
        internal string Action;           // "install" or "remove"
        internal string PackageId;
        internal string TransactionId;    // 32 hex, written into every staged root's marker
        internal List<string> Targets = new List<string>(); // game-relative roots, index = staged folder name
        // Install only: roots of the previous version that the new version no longer ships.
        // They leave BepInEx/plugins in the same transaction (persist entries are parked), so an
        // update never keeps stale plugins of a dropped folder. Written as "-<root>" lines.
        internal List<string> Dropped = new List<string>();

        internal static string Format(InstallPlanFile plan)
        {
            var text = new StringBuilder();
            text.Append(Protocol).Append("\r\n")
                .Append(plan.WorkshopId.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                .Append(plan.Action).Append("\r\n")
                .Append(plan.PackageId).Append("\r\n")
                .Append(plan.TransactionId).Append("\r\n");
            foreach (string target in plan.Targets) text.Append(target).Append("\r\n");
            foreach (string target in plan.Dropped) text.Append('-').Append(target).Append("\r\n");
            return text.ToString();
        }

        internal static InstallPlanFile Read(string container)
        {
            string[] lines = File.ReadAllLines(Path.Combine(container, Name));
            if (lines.Length < 5 || lines[0] != Protocol) throw new InvalidDataException("Unknown installation plan in " + container);
            var plan = new InstallPlanFile
            {
                WorkshopId = ulong.Parse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture),
                Action = lines[2], PackageId = lines[3], TransactionId = lines[4],
                Targets = lines.Skip(5).Where(l => l.Length > 0 && l[0] != '-').ToList(),
                Dropped = lines.Skip(5).Where(l => l.Length > 1 && l[0] == '-').Select(l => l.Substring(1)).ToList()
            };
            if ((plan.Action != "install" && plan.Action != "remove") || plan.TransactionId.Length != 32 || plan.PackageId.Length == 0 ||
                plan.PackageId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || plan.Action == "remove" && plan.Dropped.Count > 0)
                throw new InvalidDataException("Invalid installation plan in " + container);
            foreach (string target in plan.Targets.Concat(plan.Dropped))
                if (!IsPluginRoot(target)) throw new InvalidDataException("Installation target outside BepInEx/plugins: " + target);
            if (plan.Targets.Concat(plan.Dropped).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.Targets.Count + plan.Dropped.Count)
                throw new InvalidDataException("Duplicate installation target in " + container);
            return plan;
        }

        // Exactly one folder directly below BepInEx/plugins.
        internal static bool IsPluginRoot(string target)
        {
            string[] parts = target.Split('/');
            return parts.Length == 3 && parts[0] == "BepInEx" && parts[1] == "plugins" && parts[2].Length > 0 && parts[2] != "." && parts[2] != ".." &&
                parts[2].IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
    }

    internal static class InstallStaging
    {
        // Test hook: called after each copied file.
        internal static Action<string> AfterCopy { get; set; }

        // Builds the complete container for an installation and publishes it with one rename;
        // then writes the request. receiptJson is written by the preloader as the transaction's
        // last step. An incomplete leftover container is never trusted.
        internal static string StageInstall(InstallPaths paths, ulong id, string packageId, IList<InstallRoot> roots, string receiptJson, Action<string> log,
            IList<string> dropped = null, bool request = true)
        {
            string container = paths.Container(id);
            string temporary = container + ".tmp";
            Withdraw(paths, id);
            if (Directory.Exists(temporary)) InstallTree.Delete(temporary);
            Directory.CreateDirectory(temporary);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string transaction = Guid.NewGuid().ToString("N");
            long bytes = 0;
            try
            {
                var plan = new InstallPlanFile { WorkshopId = id, Action = "install", PackageId = packageId, TransactionId = transaction };
                foreach (string target in dropped ?? new string[0])
                {
                    if (!InstallPlanFile.IsPluginRoot(target)) throw new InvalidDataException("Removal target outside BepInEx/plugins: " + target);
                    plan.Dropped.Add(target);
                }
                for (int index = 0; index < roots.Count; index++)
                {
                    var root = roots[index];
                    if (!InstallPlanFile.IsPluginRoot(root.Target)) throw new InvalidDataException("Installation target outside BepInEx/plugins: " + root.Target);
                    string staged = Path.Combine(temporary, index.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(staged);
                    foreach (var file in root.Files)
                    {
                        string target = Path.Combine(staged, InstallTree.Native(file.Path));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var input = new FileStream(Path.Combine(root.Source, InstallTree.Native(file.Path)), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
                        using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                        {
                            input.CopyTo(output, 1 << 16);
                            output.Flush(true);
                        }
                        bytes += file.Size;
                        AfterCopy?.Invoke(file.Path);
                    }
                    // Verify the copies, not the source: whatever Steam does meanwhile, only
                    // bytes that match the manifest are ever published.
                    var present = InstallTree.Files(staged);
                    if (present.Count != root.Files.Count) throw new InvalidDataException("Staged file count " + present.Count + " != " + root.Files.Count + " for " + root.Target);
                    foreach (var file in root.Files)
                    {
                        string copy = Path.Combine(staged, InstallTree.Native(file.Path));
                        if (new FileInfo(copy).Length != file.Size || InstallTree.Sha256(copy) != file.Sha256)
                            throw new InvalidDataException("Workshop file changed while staging: " + root.Target + "/" + file.Path);
                    }
                    InstallKeep.Read(staged); // a malformed keep list is rejected here, never at the swap
                    InstallTree.WriteDurable(Path.Combine(staged, InstallTree.MarkerName), transaction + "\r\n");
                    plan.Targets.Add(root.Target);
                }
                InstallTree.WriteDurable(Path.Combine(temporary, InstallPlanFile.ReceiptName), receiptJson);
                InstallTree.WriteDurable(Path.Combine(temporary, InstallPlanFile.Name), InstallPlanFile.Format(plan));
                InstallTree.WriteDurable(Path.Combine(temporary, InstallTree.CompleteName), transaction + "\r\n");
                Directory.Move(temporary, container);
                if (request) WriteRequest(paths, id);
            }
            catch
            {
                try { InstallTree.Delete(temporary); } catch { }
                throw;
            }
            log("STAGED " + id + " roots=" + roots.Count + (dropped != null && dropped.Count > 0 ? " dropped=" + dropped.Count : "") + " bytes=" + bytes + " ms=" + watch.ElapsedMilliseconds);
            return container;
        }

        // A removal needs no files: the plan names the roots, the preloader parks persist entries.
        internal static string StageRemove(InstallPaths paths, ulong id, string packageId, IList<string> targets, Action<string> log, bool request = true)
        {
            string container = paths.Container(id);
            string temporary = container + ".tmp";
            foreach (string target in targets)
                if (!InstallPlanFile.IsPluginRoot(target)) throw new InvalidDataException("Removal target outside BepInEx/plugins: " + target);
            Withdraw(paths, id);
            if (Directory.Exists(temporary)) InstallTree.Delete(temporary);
            Directory.CreateDirectory(temporary);
            var plan = new InstallPlanFile { WorkshopId = id, Action = "remove", PackageId = packageId, TransactionId = Guid.NewGuid().ToString("N"), Targets = targets.ToList() };
            InstallTree.WriteDurable(Path.Combine(temporary, InstallPlanFile.Name), InstallPlanFile.Format(plan));
            InstallTree.WriteDurable(Path.Combine(temporary, InstallTree.CompleteName), plan.TransactionId + "\r\n");
            Directory.Move(temporary, container);
            if (request) WriteRequest(paths, id);
            log("STAGED_REMOVAL " + id + " roots=" + targets.Count);
            return container;
        }

        // Publishes a container staged with request: false. A batch stages every container first and
        // publishes the requests last, so quitting the game while it still copies never leaves a
        // part of a batch validated as a whole to be applied alone. A container without request is
        // ignored by the preloader and replaced by the next staging of its item.
        internal static void Publish(InstallPaths paths, ulong id)
        {
            if (!File.Exists(Path.Combine(paths.Container(id), InstallTree.CompleteName))) throw new InvalidOperationException("Staged installation incomplete: " + paths.Container(id));
            WriteRequest(paths, id);
        }

        private static void WriteRequest(InstallPaths paths, ulong id)
        {
            Directory.CreateDirectory(paths.Requests);
            InstallTree.WriteDurable(paths.Request(id), InstallPlanFile.Protocol + "\r\n");
        }

        // Withdraws a pending (not yet applied) request and its container. Never touches an
        // open journal: an interrupted swap belongs to the preloader.
        internal static bool Withdraw(InstallPaths paths, ulong id)
        {
            if (File.Exists(paths.Journal(id))) throw new InvalidOperationException("An interrupted installation of this item is completed at the next game start.");
            bool had = File.Exists(paths.Request(id)) || Directory.Exists(paths.Container(id));
            InstallTree.DeleteFileIfExists(paths.Request(id));
            if (Directory.Exists(paths.Container(id))) InstallTree.Delete(paths.Container(id));
            return had;
        }

        internal static bool Pending(InstallPaths paths, ulong id) =>
            File.Exists(paths.Request(id)) && File.Exists(Path.Combine(paths.Container(id), InstallTree.CompleteName));
    }

    internal static class InstallSwap
    {
        internal const string Protocol = "APISHARED-SWAP-1";
        // Test hook: called at each step. Throwing InstallCrashSimulation models process death at
        // that point: no in-process cleanup or recovery runs, exactly as after a real kill.
        internal static Action<string> Fault { get; set; }
        private static void Step(string name) => Fault?.Invoke(name);

        private sealed class RootState
        {
            internal string Target, Backup, Staged, Undo, Park;
            internal bool Drop; // previous root the new version no longer ships (install only)
        }

        // Applies one staged container. A failure before anything moved leaves everything as it
        // was and keeps the request; a failure after the first move is finished or undone by
        // Recover right away, and otherwise by the next start.
        internal static string Apply(InstallPaths paths, ulong id, Action<string> log)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string journal = paths.Journal(id);
            if (File.Exists(journal)) Recover(paths, journal, log);
            string container = paths.Container(id);
            if (!File.Exists(Path.Combine(container, InstallTree.CompleteName))) throw new InvalidOperationException("Staged installation incomplete: " + container);
            var plan = InstallPlanFile.Read(container);
            if (plan.WorkshopId != id) throw new InvalidDataException("Staged installation belongs to another item.");
            var roots = Roots(paths, plan, container);
            if (plan.Action == "install" && roots.Any(root => !root.Drop && InstallTree.ReadMarker(root.Staged) != plan.TransactionId))
            {
                // A damaged container (e.g. partly deleted cache) can never be applied: discard it
                // instead of failing at every start; the startup offer prepares it again.
                InstallStaging.Withdraw(paths, id);
                throw new InvalidDataException("Staged installation was damaged and has been discarded; prepare it again in Mod Options or at the next startup offer.");
            }
            foreach (var root in roots)
            {
                // A linked plugin folder is never moved: Finish would delete the link's target.
                if (Directory.Exists(root.Target) && (File.GetAttributes(root.Target) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked plugin folder is not installed over: " + root.Target);
                // Without a journal a backup is a finished swap's leftover, never the only copy.
                if (Directory.Exists(root.Backup)) InstallTree.Delete(root.Backup);
                Directory.CreateDirectory(Path.GetDirectoryName(root.Backup));
                Directory.CreateDirectory(Path.GetDirectoryName(root.Target));
            }
            Directory.CreateDirectory(paths.Journals);
            string temporary = journal + ".tmp";
            InstallTree.WriteDurable(temporary, Protocol + "\r\n" + id.ToString(CultureInfo.InvariantCulture) + "\r\n");
            if (File.Exists(journal)) File.Delete(journal);
            File.Move(temporary, journal);
            Step("journal");

            string carried = "";
            foreach (var root in roots)
            {
                if (plan.Action == "install" && !root.Drop)
                {
                    string source = Directory.Exists(root.Target) ? root.Target : Directory.Exists(root.Park) ? root.Park : null;
                    carried += InstallKeep.CarryOver(source, root.Staged, root.Undo) + ";";
                }
                else if (Directory.Exists(root.Target)) InstallKeep.Park(root.Target, root.Park);
            }
            Step("carried");

            int moved = 0;
            try
            {
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root.Target)) continue;
                    Directory.Move(root.Target, root.Backup);
                    moved++;
                    Step("old-moved");
                }
            }
            catch (Exception ex) when (!(ex is InstallCrashSimulation))
            {
                if (moved == 0)
                {
                    // Nothing moved (e.g. a file in an installed folder is held open): drop the
                    // journal so the old version stays as it is; the request is kept.
                    File.Delete(journal);
                    throw;
                }
                if (RecoverAfterFailure(paths, journal, log)) return plan.Action;
                throw;
            }
            if (plan.Action == "install")
            {
                try
                {
                    foreach (var root in roots.Where(r => !r.Drop))
                    {
                        Directory.Move(root.Staged, root.Target);
                        Step("new-moved");
                    }
                }
                catch (Exception ex) when (!(ex is InstallCrashSimulation)) { if (RecoverAfterFailure(paths, journal, log)) return plan.Action; throw; }
            }
            Finish(paths, plan, roots, journal, log);
            int dropped = roots.Count(r => r.Drop);
            log((plan.Action == "install" ? "INSTALLED " : "REMOVED ") + id + " roots=" + (roots.Count - dropped) + (dropped > 0 ? " dropped=" + dropped : "") +
                (carried.Length > 0 ? " " + carried : "") + " ms=" + watch.ElapsedMilliseconds);
            return plan.Action;
        }

        // True when the immediate recovery completed the change (its RECOVERED line is the outcome):
        // a transient failure, e.g. a briefly locked root, must not be reported as not installed.
        private static bool RecoverAfterFailure(InstallPaths paths, string journal, Action<string> log)
        {
            try { string outcome = Recover(paths, journal, log); return outcome == "completed" || outcome == "rolled-forward" || outcome == "removed"; }
            catch (Exception ex) { log("RECOVERY_DEFERRED " + journal + " " + ex.Message); return false; }
        }

        // Shipped roots first (index = staged folder name), then dropped roots.
        private static List<RootState> Roots(InstallPaths paths, InstallPlanFile plan, string container) =>
            plan.Targets.Select(target => new { target, drop = false }).Concat(plan.Dropped.Select(target => new { target, drop = true }))
            .Select((entry, index) => new RootState
            {
                Target = paths.Target(entry.target),
                Backup = paths.Backup(plan.WorkshopId, index),
                Staged = Path.Combine(container, index.ToString(CultureInfo.InvariantCulture)),
                Undo = Path.Combine(container, "u" + index.ToString(CultureInfo.InvariantCulture)),
                Park = paths.Park(plan.PackageId, entry.target),
                Drop = entry.drop,
            }).ToList();

        // Receipt, then cleanup. A failing cleanup must not report the swap as failed: the kept
        // journal finishes it at the next start.
        private static void Finish(InstallPaths paths, InstallPlanFile plan, List<RootState> roots, string journal, Action<string> log)
        {
            string receipt = paths.Receipt(plan.WorkshopId);
            string container = paths.Container(plan.WorkshopId);
            if (plan.Action == "install")
            {
                Directory.CreateDirectory(paths.Receipts);
                string temporary = receipt + ".tmp";
                File.Copy(Path.Combine(container, InstallPlanFile.ReceiptName), temporary, true);
                if (File.Exists(receipt)) File.Delete(receipt);
                File.Move(temporary, receipt);
            }
            else InstallTree.DeleteFileIfExists(receipt);
            Step("receipt");
            try
            {
                // The request goes first: a deferred cleanup must not show the applied change as
                // still pending for the rest of the session. The journal alone finishes it.
                InstallTree.DeleteFileIfExists(paths.Request(plan.WorkshopId));
                foreach (var root in roots) InstallTree.Delete(root.Backup);
                // Parked persist entries were restored into the shipped roots; a dropped root's
                // parking stays for a later reinstallation.
                if (plan.Action == "install") foreach (var root in roots.Where(r => !r.Drop)) InstallTree.Delete(root.Park);
                Step("backup-deleted");
                // plan.txt goes first: Recover treats a container without it as finished, so the
                // receipt copy above is never repeated from a half-deleted container (deletion
                // order of the remaining files depends on the file system).
                InstallTree.DeleteFileIfExists(Path.Combine(container, InstallPlanFile.Name));
                InstallTree.Delete(container);
                File.Delete(journal);
                Step("done");
            }
            catch (IOException ex) { log("CLEANUP_DEFERRED " + plan.WorkshopId + " " + ex.Message); }
            catch (UnauthorizedAccessException ex) { log("CLEANUP_DEFERRED " + plan.WorkshopId + " " + ex.Message); }
        }

        // Decides from the file system and the staged roots' transaction markers, so a crash
        // between any rename and any journal write is covered. Roll forward whenever the staged
        // data is complete; otherwise restore the old roots. Idempotent.
        internal static string Recover(InstallPaths paths, string journal, Action<string> log)
        {
            string[] lines = File.ReadAllLines(journal);
            if (lines.Length < 2 || lines[0] != Protocol) throw new InvalidDataException("Unknown installation journal " + journal);
            ulong id = ulong.Parse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture);
            string container = paths.Container(id);
            if (!File.Exists(Path.Combine(container, InstallPlanFile.Name)))
            {
                // Finish already removed the plan: only cleanup leftovers and the journal remain. A
                // request is necessarily this finished one's (staging refuses while a journal exists).
                try { InstallTree.DeleteFileIfExists(paths.Request(id)); InstallTree.Delete(container); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                File.Delete(journal);
                log("RECOVERED " + id + " completed");
                return "completed";
            }
            var plan = InstallPlanFile.Read(container);
            var roots = Roots(paths, plan, container);
            string outcome;
            if (plan.Action == "install")
            {
                var shipped = roots.Where(r => !r.Drop).ToList();
                bool complete = shipped.All(r => InstallTree.ReadMarker(r.Target) == plan.TransactionId) && roots.Where(r => r.Drop).All(r => !Directory.Exists(r.Target));
                bool anyMoved = shipped.Any(r => InstallTree.ReadMarker(r.Target) == plan.TransactionId) || roots.Any(r => Directory.Exists(r.Backup));
                if (complete) outcome = "completed";
                else if (!anyMoved) { File.Delete(journal); log("RECOVERED " + id + " not-started"); return "not-started"; }
                else
                {
                    try
                    {
                        foreach (var root in roots)
                        {
                            if (!root.Drop && InstallTree.ReadMarker(root.Target) == plan.TransactionId) continue;
                            if (!root.Drop && !Directory.Exists(root.Staged)) throw new IOException("Staged root missing: " + root.Staged);
                            if (Directory.Exists(root.Target))
                            {
                                if ((File.GetAttributes(root.Target) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked plugin folder is not installed over: " + root.Target);
                                if (Directory.Exists(root.Backup)) throw new IOException("Old root and backup both present: " + root.Target);
                                if (root.Drop) InstallKeep.Park(root.Target, root.Park);
                                Directory.Move(root.Target, root.Backup);
                            }
                            if (root.Drop) continue;
                            Directory.Move(root.Staged, root.Target);
                            Step("recover-new-moved");
                        }
                        outcome = "rolled-forward";
                    }
                    catch (Exception forward) when (!(forward is InstallCrashSimulation))
                    {
                        // Undo: staged data goes back to the container, old roots return.
                        foreach (var root in roots)
                        {
                            if (!root.Drop && InstallTree.ReadMarker(root.Target) == plan.TransactionId && !Directory.Exists(root.Staged)) Directory.Move(root.Target, root.Staged);
                            if (!Directory.Exists(root.Target) && Directory.Exists(root.Backup)) Directory.Move(root.Backup, root.Target);
                        }
                        return RolledBack(id, plan, roots, journal, forward, log);
                    }
                }
            }
            else
            {
                try
                {
                    foreach (var root in roots)
                    {
                        if (!Directory.Exists(root.Target)) continue;
                        InstallKeep.Park(root.Target, root.Park);
                        if (Directory.Exists(root.Backup)) InstallTree.Delete(root.Backup);
                        Directory.Move(root.Target, root.Backup);
                    }
                    outcome = "removed";
                }
                catch (Exception forward) when (!(forward is InstallCrashSimulation))
                {
                    // A root that cannot be removed (e.g. a locked file) undoes the removal, so this
                    // start never loads a package with some of its roots missing.
                    foreach (var root in roots)
                        if (!Directory.Exists(root.Target) && Directory.Exists(root.Backup)) Directory.Move(root.Backup, root.Target);
                    return RolledBack(id, plan, roots, journal, forward, log);
                }
            }
            Finish(paths, plan, roots, journal, log);
            log("RECOVERED " + id + " " + outcome);
            return outcome;
        }

        // The old roots are back; the request stays and is retried at the next start.
        private static string RolledBack(ulong id, InstallPlanFile plan, List<RootState> roots, string journal, Exception reason, Action<string> log)
        {
            // A backup that cannot return (a folder was recreated at its place) may hold the only
            // copy of player files. Without a journal the next Apply deletes backups as leftovers,
            // so it is moved aside and named in the log instead.
            foreach (var root in roots.Where(r => Directory.Exists(r.Backup)))
            {
                string kept = root.Backup + "-kept-" + plan.TransactionId.Substring(0, 8);
                for (int n = 2; Directory.Exists(kept); n++) kept = root.Backup + "-kept-" + plan.TransactionId.Substring(0, 8) + "-" + n.ToString(CultureInfo.InvariantCulture);
                Directory.Move(root.Backup, kept);
                log("RECOVERY_KEPT_OLD_FOLDER " + root.Target + " -> " + kept);
            }
            File.Delete(journal);
            log("RECOVERED " + id + " rolled-back (" + reason.Message + ")");
            return "rolled-back";
        }
    }

    // Preloader entry: everything that must happen before BepInEx loads plugins. Outcomes are
    // also written to _APIShared/I/result.txt, which the plugin reports once (TakeResult).
    internal static class InstallRunner
    {
        internal const string ResultName = "result.txt";
        // AppDomain data the preloader sets when its installation pass ran in this process. The
        // runtime stages only then: without an active preloader a staged change would wait forever.
        internal const string ActiveKey = "APIShared.WorkshopInstall.Active";

        internal sealed class ResultLine
        {
            internal bool Error;
            internal string Text;
        }

        // managed: items an external mod manager took over (WorkshopManagerPolicy); their staged,
        // not yet started changes are discarded instead of applied. Interrupted swaps are always
        // finished or undone first, so no folder stays partial.
        internal static void Run(string game, Action<string> info, Action<string> error, Func<ulong, bool> managed = null)
        {
            var paths = new InstallPaths(game);
            if (!Directory.Exists(paths.Work)) return;
            bool hasWork = Directory.Exists(paths.Journals) && Directory.EnumerateFiles(paths.Journals, "*.journal").Any() ||
                Directory.Exists(paths.Requests) && Directory.EnumerateFiles(paths.Requests, "*.request").Any();
            if (!hasWork) return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = new StringBuilder();
            Action<string> ok = message => { info(message); result.Append("I\t").Append(message).Append("\r\n"); };
            Action<string> fail = message => { error(message); result.Append("E\t").Append(message).Append("\r\n"); };
            if (Directory.Exists(paths.Journals))
                foreach (string journal in Directory.GetFiles(paths.Journals, "*.journal"))
                {
                    try { InstallSwap.Recover(paths, journal, ok); }
                    catch (Exception ex) { fail("recovery of " + Path.GetFileName(journal) + " failed: " + ex.Message); }
                }
            if (Directory.Exists(paths.Requests))
                foreach (string request in Directory.GetFiles(paths.Requests, "*.request"))
                {
                    if (!ulong.TryParse(Path.GetFileNameWithoutExtension(request), NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0) continue;
                    if (managed != null && managed(id))
                    {
                        try { InstallStaging.Withdraw(paths, id); ok("WITHDRAWN " + id + " managed by an external mod manager"); }
                        catch (Exception ex) { fail("item " + id + " is managed by an external mod manager; its staged change could not be discarded: " + ex.Message); }
                        continue;
                    }
                    try { InstallSwap.Apply(paths, id, ok); }
                    catch (Exception ex)
                    {
                        // A kept request is retried; a discarded one (damaged container) is offered anew.
                        fail("item " + id + " not installed" + (File.Exists(request) ? ", retried at the next start: " : ": ") + ex.Message);
                    }
                }
            info("INSTALL_PASS_DONE ms=" + watch.ElapsedMilliseconds);
            try { File.AppendAllText(Path.Combine(paths.Work, ResultName), result.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { error("Workshop installation result not recorded: " + ex.Message); }
        }

        // Reads and removes the preloader outcome of this start (plugin side).
        internal static List<ResultLine> TakeResult(InstallPaths paths)
        {
            var lines = new List<ResultLine>();
            string path = Path.Combine(paths.Work, ResultName);
            if (!File.Exists(path)) return lines;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length < 3 || line[1] != '\t') continue;
                lines.Add(new ResultLine { Error = line[0] == 'E', Text = line.Substring(2) });
            }
            File.Delete(path);
            return lines;
        }
    }
}

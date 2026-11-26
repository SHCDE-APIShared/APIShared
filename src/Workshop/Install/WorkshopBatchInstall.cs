using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace APIShared.Workshop.Install
{
    internal static class InstallInventory
    {
        private const string Protocol = "APISHARED-INVENTORY-1";
        internal const string Name = "inventory.txt";

        internal sealed class Snapshot
        {
            internal InstallPlanFile Plan;
            internal string Receipt, PreviousReceipt;
            internal Dictionary<string, InstallFile>[] Roots;
        }

        internal static void Write(string container, IList<InstallRoot> roots, string previousReceipt)
        {
            var lines = new List<string> {
                Protocol, InstallTree.Sha256(Path.Combine(container, InstallPlanFile.Name)),
                InstallTree.FileHash(Path.Combine(container, InstallPlanFile.ReceiptName)), previousReceipt
            };
            for (int index = 0; index < roots.Count; index++)
                foreach (var file in roots[index].Files.OrderBy(file => file.Path, StringComparer.Ordinal))
                    lines.Add(Number(index) + "\t" + file.Size.ToString(CultureInfo.InvariantCulture) + "\t" + file.Sha256 + "\t" + file.Path);
            InstallTree.WriteDurable(Path.Combine(container, Name), string.Join("\r\n", lines) + "\r\n");
        }

        internal static Snapshot Verify(InstallPaths paths, ulong id, string expectedTransaction = null, string expectedInventory = null)
        {
            var snapshot = Read(paths, id, expectedTransaction, expectedInventory);
            string receipt = paths.Receipt(id);
            InstallTree.CheckUnlinked(receipt);
            if (Directory.Exists(receipt) || InstallTree.FileHash(receipt) != snapshot.PreviousReceipt)
                throw new InvalidDataException("Installed receipt changed for item " + id + ". Confirm the package again.");
            for (int index = 0; index < snapshot.Roots.Length; index++)
                VerifyRoot(Path.Combine(paths.Container(id), Number(index)), snapshot.Plan.Targets[index], snapshot.Plan.TransactionId, snapshot.Roots[index]);
            return snapshot;
        }

        private static Snapshot Read(InstallPaths paths, ulong id, string expectedTransaction, string expectedInventory)
        {
            string container = paths.Container(id);
            string inventory = Path.Combine(container, Name);
            foreach (string name in new[] { Name, InstallPlanFile.Name, InstallPlanFile.ReceiptName, InstallTree.CompleteName })
                InstallTree.CheckUnlinked(Path.Combine(container, name));
            if (!File.Exists(inventory))
                throw new InvalidDataException("Prepared files lack a verification inventory. Confirm the package again.");
            if (expectedInventory != null && InstallTree.Sha256(inventory) != expectedInventory)
                throw new InvalidDataException("Prepared verification inventory changed for item " + id + ".");
            string[] lines = File.ReadAllLines(inventory);
            var plan = InstallPlanFile.Read(container);
            if (lines.Length < 4 || lines[0] != Protocol || plan.WorkshopId != id ||
                expectedTransaction != null && expectedTransaction != plan.TransactionId ||
                File.ReadAllText(Path.Combine(container, InstallTree.CompleteName)).Trim() != plan.TransactionId ||
                InstallTree.Sha256(Path.Combine(container, InstallPlanFile.Name)) != lines[1] ||
                InstallTree.FileHash(Path.Combine(container, InstallPlanFile.ReceiptName)) != lines[2] ||
                (plan.Action == "install") != Hash(lines[2]))
                throw new InvalidDataException("Prepared installation plan changed for item " + id + ".");
            if (!(lines[3] == "missing" || Hash(lines[3])))
                throw new InvalidDataException("Invalid prior receipt identity for item " + id + ".");
            var roots = plan.Action == "install" ? plan.Targets.Select(_ => new Dictionary<string, InstallFile>(StringComparer.OrdinalIgnoreCase)).ToArray()
                : new Dictionary<string, InstallFile>[0];
            foreach (string line in lines.Skip(4))
            {
                string[] fields = line.Split('\t');
                if (fields.Length != 4 || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index >= roots.Length ||
                    !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out long size) || !Hash(fields[2]) ||
                    !fields[3].Split('/').All(InstallPlanFile.ValidSegment) || roots[index].ContainsKey(fields[3]))
                    throw new InvalidDataException("Invalid verification inventory for item " + id + ".");
                roots[index].Add(fields[3], new InstallFile(fields[3], size, fields[2]));
            }
            return new Snapshot { Plan = plan, Receipt = lines[2], PreviousReceipt = lines[3], Roots = roots };
        }

        internal static void VerifyLegacyRecovery(InstallPaths paths, InstallPlanFile plan, IList<string> roots)
        {
            string container = paths.Container(plan.WorkshopId);
            if (File.Exists(Path.Combine(container, Name)))
            {
                var snapshot = Read(paths, plan.WorkshopId, plan.TransactionId, null);
                for (int index = 0; index < roots.Count; index++)
                    VerifyRoot(roots[index], plan.Targets[index], plan.TransactionId, snapshot.Roots[index], allowCarriedFiles: true);
                return;
            }
            // The oldest preloader wrote no complete inventory. Its receipt can prove only a
            // payload with no mutable keep entries. Otherwise recovery must use the backups.
            string receipt = Path.Combine(container, InstallPlanFile.ReceiptName);
            InstallTree.CheckUnlinked(receipt);
            var value = APIShared.Internal.DependencyFreeJson.Parse(File.ReadAllText(receipt)) as Dictionary<string, object>;
            if (value == null || !value.TryGetValue("Files", out object rows) || !(rows is List<object> entries) || entries.Count == 0)
                throw new InvalidDataException("Old Workshop journal has no usable file inventory.");
            var files = plan.Targets.Select(_ => new Dictionary<string, InstallFile>(StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (object entry in entries)
            {
                var row = entry as Dictionary<string, object>;
                if (row == null || !row.TryGetValue("Path", out object path) || !(path is string relative) ||
                    !row.TryGetValue("Sha256", out object digest) || !(digest is string hash) || !Hash(hash) ||
                    !row.TryGetValue("Size", out object size) || !(size is int || size is long))
                    throw new InvalidDataException("Invalid file in old Workshop receipt.");
                int index = plan.Targets.FindIndex(target => relative.StartsWith(target + "/", StringComparison.OrdinalIgnoreCase));
                if (index < 0) throw new InvalidDataException("Old Workshop receipt names a file outside the staged roots.");
                string local = relative.Substring(plan.Targets[index].Length + 1);
                if (!local.Split('/').All(InstallPlanFile.ValidSegment) || files[index].ContainsKey(local))
                    throw new InvalidDataException("Invalid file path in old Workshop receipt.");
                files[index].Add(local, new InstallFile(local, Convert.ToInt64(size, CultureInfo.InvariantCulture), hash));
            }
            for (int index = 0; index < roots.Count; index++)
            {
                if (InstallKeep.Read(roots[index]).Count != 0)
                    throw new InvalidDataException("Old Workshop receipt cannot verify a root that keeps player files.");
                VerifyRoot(roots[index], plan.Targets[index], plan.TransactionId, files[index]);
            }
        }

        internal static void VerifyRoot(string directory, string target, string transaction, Dictionary<string, InstallFile> expected, bool allowCarriedFiles = false)
        {
            InstallTree.CheckUnlinked(Path.Combine(directory, InstallTree.MarkerName));
            if (InstallTree.ReadMarker(directory) != transaction)
                throw new InvalidDataException("Prepared installation root is incomplete: " + target + ".");
            var files = InstallTree.Files(directory);
            var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            if (seen.Count != files.Count || !expected.Keys.All(seen.Contains) || !allowCarriedFiles && seen.Count != expected.Count)
                throw new InvalidDataException("Prepared file count changed: " + target + ".");
            var kept = allowCarriedFiles ? InstallKeep.Read(directory) : new List<InstallKeep.Entry>();
            foreach (string relative in files)
            {
                if (allowCarriedFiles && !string.Equals(Path.GetFileName(relative), InstallKeep.ListName, StringComparison.OrdinalIgnoreCase) &&
                    InstallKeep.Covers(kept, relative)) continue;
                string file = Path.Combine(directory, InstallTree.Native(relative));
                if (!expected.TryGetValue(relative, out var entry) || new FileInfo(file).Length != entry.Size || InstallTree.Sha256(file) != entry.Sha256)
                    throw new InvalidDataException("Prepared file changed: " + target + "/" + relative + ".");
            }
        }

        internal static bool Hash(string value) => value.Length == 64 && value.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
        internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    }

    // A request is the consent boundary for the complete group. No per-item request is written.
    // An active transaction directory is published before any live rename. Until its commit
    // marker exists, recovery rolls the whole group back using the recorded object fingerprints.
    internal static class InstallBatch
    {
        private const string RequestProtocol = "APISHARED-BATCH-1";
        private const string JournalProtocol = "APISHARED-BATCH-JOURNAL-1";
        private const string JournalName = "journal.txt";
        private const string RequestName = "request.txt";
        private const string Prepared = "prepared";
        private const string Committed = "commit";
        private const string RolledBack = "rollback";
        private const string Missing = "missing";
        internal static Action<string> Fault { get; set; }

        internal sealed class Member
        {
            internal ulong Item;
            internal string Plan, Inventory;
        }

        internal sealed class Request
        {
            internal string Id, Path, Content;
            internal List<Member> Members;
        }

        private sealed class Change
        {
            internal string Relative, Target, Before, After;
            internal string Backup, Candidate;
        }

        private static string Requests(InstallPaths paths) => Path.Combine(paths.Work, "q");
        private static string Transactions(InstallPaths paths) => Path.Combine(paths.Work, "x");
        private static string PendingPath(InstallPaths paths, string id) => Path.Combine(Requests(paths), id + ".batch");
        private static string ActivePath(InstallPaths paths, string id) => Path.Combine(Transactions(paths), id);
        private static string Number(int index) => InstallInventory.Number(index);
        private static void Step(string name) => Fault?.Invoke(name);

        internal static string Publish(InstallPaths paths, IReadOnlyList<ulong> ids)
        {
            if (ids.Count == 0 || ids.Any(id => id == 0) || ids.Distinct().Count() != ids.Count)
                throw new InvalidDataException("A Workshop batch requires distinct item IDs.");
            string batch = Guid.NewGuid().ToString("N");
            var lines = new List<string> { RequestProtocol, batch };
            foreach (ulong id in ids)
            {
                if (Find(paths, id, out _) || File.Exists(paths.Request(id)) || File.Exists(paths.Journal(id)))
                    throw new InvalidOperationException("Item already has an installation request: " + id + ".");
                var plan = InstallInventory.Verify(paths, id).Plan;
                string inventory = InstallTree.Sha256(Path.Combine(paths.Container(id), InstallInventory.Name));
                lines.Add(id.ToString(CultureInfo.InvariantCulture) + "\t" + plan.TransactionId + "\t" + inventory);
            }
            string request = PendingPath(paths, batch);
            InstallTree.CheckUnlinked(request);
            Directory.CreateDirectory(Requests(paths));
            Step("before-publish");
            InstallTree.WriteAtomic(request, string.Join("\r\n", lines) + "\r\n");
            Step("published");
            return batch;
        }

        internal static bool Find(InstallPaths paths, ulong id, out Request batch)
        {
            batch = Pending(paths).Select(ReadRequest).FirstOrDefault(request => request.Members.Any(member => member.Item == id));
            return batch != null;
        }

        internal static bool Withdraw(InstallPaths paths, Request batch)
        {
            if (Directory.Exists(ActivePath(paths, batch.Id)))
                throw new InvalidOperationException("An interrupted installation of this group must be recovered first.");
            Discard(paths, batch);
            return true;
        }

        internal static string[] Pending(InstallPaths paths)
        {
            InstallTree.CheckUnlinked(Requests(paths));
            return Directory.Exists(Requests(paths)) ? Directory.GetFiles(Requests(paths), "*.batch") : new string[0];
        }

        internal static string[] Active(InstallPaths paths, bool permitLinkedInspection = false)
        {
            if (!permitLinkedInspection) InstallTree.CheckUnlinked(Transactions(paths));
            return Directory.Exists(Transactions(paths))
                ? Directory.GetDirectories(Transactions(paths)).Where(dir => Guid.TryParseExact(Path.GetFileName(dir), "N", out _)).ToArray()
                : new string[0];
        }

        internal static void CleanAbandoned(InstallPaths paths, Action<string> log)
        {
            string directory = Transactions(paths);
            if (!Directory.Exists(directory)) return;
            foreach (string path in Directory.GetDirectories(directory))
            {
                string name = Path.GetFileName(path);
                string extension = Path.GetExtension(name);
                string id = Path.GetFileNameWithoutExtension(name);
                if (!Guid.TryParseExact(id, "N", out _) || extension != ".done" && extension != ".tmp") continue;
                if (extension == ".tmp" && File.Exists(PendingPath(paths, id))) continue;
                try { InstallTree.Delete(path); }
                catch (IOException ex) { log("Workshop group staging cleanup deferred: " + ex.Message); }
                catch (UnauthorizedAccessException ex) { log("Workshop group staging cleanup deferred: " + ex.Message); }
            }
        }

        private static Request ReadRequest(string path)
        {
            InstallTree.CheckUnlinked(path);
            string content = File.ReadAllText(path);
            var lines = new List<string>();
            using (var reader = new StringReader(content))
                for (string line = reader.ReadLine(); line != null; line = reader.ReadLine()) lines.Add(line);
            if (lines.Count < 3 || lines[0] != RequestProtocol || !Guid.TryParseExact(lines[1], "N", out _) ||
                Path.GetFileName(path) != RequestName && Path.GetFileNameWithoutExtension(path) != lines[1])
                throw new InvalidDataException("Invalid Workshop group request " + path + ".");
            var seen = new HashSet<ulong>();
            var members = new List<Member>();
            foreach (string line in lines.Skip(2))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 3 || !ulong.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong id) || id == 0 ||
                    !seen.Add(id) || !Guid.TryParseExact(parts[1], "N", out _) || !InstallInventory.Hash(parts[2]))
                    throw new InvalidDataException("Invalid Workshop group membership " + path + ".");
                members.Add(new Member { Item = id, Plan = parts[1], Inventory = parts[2] });
            }
            return new Request { Id = lines[1], Path = path, Content = content, Members = members };
        }

        internal static bool Apply(InstallPaths paths, string requestPath, Action<string> log, Action<string> error, Func<ulong, bool> managed)
        {
            Request request = null;
            string active = null;
            string draft = null;
            try
            {
                request = ReadRequest(requestPath);
                Claim(paths, request.Id, log);
                active = ActivePath(paths, request.Id);
                draft = active + ".tmp";
                if (managed != null && request.Members.Any(member => managed(member.Item)))
                    throw new InvalidDataException("A package in the group is managed by an external mod manager.");
                var plans = request.Members.Select(member => InstallInventory.Verify(paths, member.Item, member.Plan, member.Inventory)).ToArray();
                var targets = plans.SelectMany(entry => entry.Plan.Targets.Concat(entry.Plan.Dropped)).ToArray();
                if (targets.Length != targets.Distinct(StringComparer.OrdinalIgnoreCase).Count())
                    throw new InvalidDataException("Multiple packages in the group claim the same installation folder.");
                InstallTree.CheckUnlinked(draft);
                InstallTree.Delete(draft);
                Directory.CreateDirectory(Path.Combine(draft, "n"));
                Directory.CreateDirectory(Path.Combine(draft, "b"));
                var changes = new List<Change>();
                foreach (var plan in plans) Prepare(paths, plan, draft, changes);
                Step("prepared");
                WriteJournal(draft, request, changes);
                Mark(draft, Prepared);
                Directory.Move(draft, active);
                Step("journal");
                changes = ReadJournal(paths, active);
                foreach (var change in changes) Apply(change);
                VerifyTargets(changes, before: false);
                Mark(active, Committed);
                Step("committed");
                Finalize(paths, active, request, log, Committed);
                return true;
            }
            catch (Exception ex) when (!(ex is InstallCrashSimulation))
            {
                if (active != null && Directory.Exists(active))
                {
                    try
                    {
                        string result = Recover(paths, active, log);
                        if (result == Committed) return true;
                    }
                    catch (Exception recovery) when (!(recovery is InstallCrashSimulation))
                    {
                        error("Workshop group " + request.Id + " cannot be recovered: " + recovery.Message);
                        return false;
                    }
                }
                else
                {
                    try
                    {
                        if (request != null) Discard(paths, request);
                        else InstallTree.DeleteFileIfExists(requestPath);
                        if (draft != null) InstallTree.Delete(draft);
                    }
                    catch (Exception cleanup) { log("Workshop group staging cleanup deferred: " + cleanup.Message); }
                }
                error("Workshop group " + (request == null ? requestPath : request.Id) + " was not installed: " + ex.Message);
                return true;
            }
        }

        private static void Claim(InstallPaths paths, string batch, Action<string> log)
        {
            string directory = Path.Combine(paths.Work, "restart", batch);
            try
            {
                if (!Directory.Exists(directory)) return;
                string path = Path.Combine(directory, "claimed");
                InstallTree.CheckUnlinked(path);
                if (!File.Exists(path)) InstallTree.WriteAtomic(path, batch + "\r\n");
            }
            catch (IOException ex) { log("Workshop restart claim could not be recorded: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { log("Workshop restart claim could not be recorded: " + ex.Message); }
        }

        private static void Prepare(InstallPaths paths, InstallInventory.Snapshot snapshot, string draft, List<Change> changes)
        {
            var plan = snapshot.Plan;
            string container = paths.Container(plan.WorkshopId);
            for (int index = 0; index < plan.Targets.Count + plan.Dropped.Count; index++)
            {
                bool install = plan.Action == "install" && index < plan.Targets.Count;
                string relative = index < plan.Targets.Count ? plan.Targets[index] : plan.Dropped[index - plan.Targets.Count];
                string target = paths.Target(relative);
                string park = paths.Park(plan.PackageId, relative);
                var root = AddChange(paths, draft, changes, target);
                var parking = AddChange(paths, draft, changes, park);
                if (File.Exists(target) || File.Exists(park)) throw new IOException("Expected an installation folder at " + relative + ".");
                if (install)
                {
                    string staged = Path.Combine(container, Number(index));
                    InstallTree.CheckUnlinked(staged);
                    Directory.Move(staged, root.Candidate);
                    var files = snapshot.Roots[index];
                    InstallInventory.VerifyRoot(root.Candidate, relative, plan.TransactionId, files);
                    if (File.Exists(Path.Combine(root.Candidate, InstallKeep.StateName)) || File.Exists(Path.Combine(root.Candidate, InstallTree.CompleteName)))
                        throw new InvalidDataException("Unexpected installer metadata in " + relative + ".");
                    string source = Directory.Exists(target) ? target : Directory.Exists(park) ? park : null;
                    InstallKeep.CarryOver(source, root.Candidate, null, file => files[file.Path] = file);
                    root.After = Fingerprint(root.Candidate);
                    InstallInventory.VerifyRoot(root.Candidate, relative, plan.TransactionId, files);
                }
                else if (Directory.Exists(target))
                {
                    InstallKeep.Park(target, parking.Candidate);
                    parking.After = Fingerprint(parking.Candidate);
                }
                else
                {
                    // Removing an already absent root must keep any previously parked data.
                    changes.RemoveAt(changes.Count - 1);
                }
            }
            var receipt = AddChange(paths, draft, changes, paths.Receipt(plan.WorkshopId));
            if (receipt.Before != (snapshot.PreviousReceipt == Missing ? Missing : "f:" + snapshot.PreviousReceipt))
                throw new IOException("Installed receipt changed for item " + plan.WorkshopId + ".");
            if (plan.Action == "install")
            {
                File.Move(Path.Combine(container, InstallPlanFile.ReceiptName), receipt.Candidate);
                receipt.After = "f:" + snapshot.Receipt;
                AssertFingerprint(receipt.Candidate, receipt.After);
            }
        }

        private static Change AddChange(InstallPaths paths, string journal, List<Change> changes, string target)
        {
            string relative = target.Substring(paths.Game.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
            var change = NewChange(paths, journal, changes.Count, relative);
            change.Before = Fingerprint(target);
            change.After = Missing;
            changes.Add(change);
            return change;
        }

        private static Change NewChange(InstallPaths paths, string journal, int index, string relative)
        {
            string[] parts = relative.Split('/');
            bool receipt = parts.Length == 4 && parts[0] == "_APIShared" && parts[1] == "Workshop" && parts[2] == "Receipts" &&
                parts[3].EndsWith(".json", StringComparison.Ordinal) && ulong.TryParse(parts[3].Substring(0, parts[3].Length - 5), NumberStyles.None, CultureInfo.InvariantCulture, out _);
            bool parking = parts.Length == 5 && parts[0] == "_APIShared" && parts[1] == "I" && parts[2] == "k" && parts.All(InstallPlanFile.ValidSegment);
            if (!receipt && !parking && !InstallPlanFile.IsPluginRoot(relative))
                throw new InvalidDataException("Invalid Workshop transaction target: " + relative);
            return new Change {
                Relative = relative, Target = paths.Target(relative),
                Backup = Path.Combine(journal, "b", Number(index)), Candidate = Path.Combine(journal, "n", Number(index))
            };
        }

        private static string Fingerprint(string path)
        {
            InstallTree.CheckUnlinked(path);
            if (File.Exists(path)) return "f:" + InstallTree.Sha256(path);
            if (!Directory.Exists(path)) return Missing;
            var text = new StringBuilder();
            foreach (string relative in InstallTree.Files(path, includeMetadata: true))
                text.Append(Number(relative.Length)).Append(':').Append(relative).Append(InstallTree.Sha256(Path.Combine(path, InstallTree.Native(relative))));
            using (var sha = SHA256.Create())
                return "d:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        private static bool ValidFingerprint(string value) => value == Missing ||
            (value.StartsWith("f:", StringComparison.Ordinal) || value.StartsWith("d:", StringComparison.Ordinal)) && InstallInventory.Hash(value.Substring(2));

        private static void WriteJournal(string draft, Request request, List<Change> changes)
        {
            InstallTree.CheckUnlinked(request.Path);
            if (!string.Equals(File.ReadAllText(request.Path), request.Content, StringComparison.Ordinal))
                throw new InvalidDataException("Workshop group request changed during preparation: " + request.Path + ".");
            InstallTree.WriteDurable(Path.Combine(draft, RequestName), request.Content);
            var lines = changes.Select(change => change.Relative + "\t" + change.Before + "\t" + change.After);
            InstallTree.WriteDurable(Path.Combine(draft, JournalName), JournalProtocol + "\r\n" +
                InstallTree.Sha256(Path.Combine(draft, RequestName)) + "\r\n" + string.Join("\r\n", lines) + "\r\n");
        }

        private static List<Change> ReadJournal(InstallPaths paths, string directory)
        {
            string path = Path.Combine(directory, JournalName);
            InstallTree.CheckUnlinked(path);
            if (!HasMark(directory, Prepared)) throw new InvalidDataException("Workshop journal is missing its preparation seal: " + path + ".");
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3 || lines[0] != JournalProtocol || lines[1] != InstallTree.Sha256(Path.Combine(directory, RequestName)))
                throw new InvalidDataException("Invalid Workshop group journal " + path + ".");
            var changes = new List<Change>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(2))
            {
                string[] parts = line.Split('\t');
                if (parts.Length != 3 || !targets.Add(parts[0]) || !ValidFingerprint(parts[1]) || !ValidFingerprint(parts[2]))
                    throw new InvalidDataException("Invalid Workshop group journal entry " + path + ".");
                var change = NewChange(paths, directory, changes.Count, parts[0]);
                change.Before = parts[1]; change.After = parts[2];
                changes.Add(change);
            }
            return changes;
        }

        private static void Apply(Change change)
        {
            AssertFingerprint(change.Target, change.Before);
            AssertFingerprint(change.Candidate, change.After);
            AssertFingerprint(change.Backup, Missing);
            if (change.Before != Missing)
            {
                Move(change.Target, change.Backup, change.Before);
                Step("old-moved");
            }
            if (change.After != Missing)
            {
                Move(change.Candidate, change.Target, change.After);
                Step(change.Relative.EndsWith(".json", StringComparison.Ordinal) ? "receipt-moved" : "new-moved");
            }
        }

        private static void VerifyTargets(List<Change> changes, bool before)
        {
            foreach (var change in changes) AssertFingerprint(change.Target, before ? change.Before : change.After);
        }

        private static void AssertFingerprint(string path, string expected)
        {
            if (Fingerprint(path) != expected)
                throw new IOException("Workshop installation path changed: " + path + ".");
        }

        private static void Move(string source, string destination, string fingerprint)
        {
            InstallTree.CheckUnlinked(source);
            InstallTree.CheckUnlinked(destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            if (fingerprint.StartsWith("d:", StringComparison.Ordinal)) Directory.Move(source, destination);
            else File.Move(source, destination);
        }

        private static void Restore(Change change)
        {
            string backup = Fingerprint(change.Backup);
            string target = Fingerprint(change.Target);
            string candidate = Fingerprint(change.Candidate);
            if (backup == Missing && target == change.Before) return;
            if (backup != change.Before || target != Missing && (target != change.After || candidate != Missing))
                throw new IOException("Restore cannot replace a changed path; retained journal and backups for " + change.Target + ".");
            if (target != Missing)
            {
                Move(change.Target, change.Candidate, change.After);
                Step("rollback-new");
            }
            if (backup != Missing)
            {
                Move(change.Backup, change.Target, change.Before);
                Step("rollback-old");
            }
        }

        private static void Mark(string directory, string phase) =>
            InstallTree.WriteAtomic(Path.Combine(directory, phase), InstallTree.Sha256(Path.Combine(directory, JournalName)) + "\r\n");

        private static bool HasMark(string directory, string phase)
        {
            string marker = Path.Combine(directory, phase);
            InstallTree.CheckUnlinked(marker);
            if (!File.Exists(marker)) return false;
            if (File.ReadAllText(marker).Trim() != InstallTree.Sha256(Path.Combine(directory, JournalName)))
                throw new InvalidDataException("Damaged Workshop transaction phase marker: " + marker + ".");
            return true;
        }

        internal static string Recover(InstallPaths paths, string directory, Action<string> log)
        {
            var request = ReadRequest(Path.Combine(directory, RequestName));
            if (request.Id != Path.GetFileName(directory))
                throw new InvalidDataException("Workshop transaction belongs to another request.");
            if (!HasMark(directory, Prepared))
                throw new InvalidDataException("Workshop transaction is missing its preparation seal: " + directory + ".");
            string phase = Committed;
            if (!HasMark(directory, Committed))
            {
                phase = RolledBack;
                if (!HasMark(directory, RolledBack))
                {
                    var changes = ReadJournal(paths, directory);
                    for (int index = changes.Count - 1; index >= 0; index--) Restore(changes[index]);
                    VerifyTargets(changes, before: true);
                    Mark(directory, RolledBack);
                    Step("rolled-back");
                }
            }
            Finalize(paths, directory, request, log, phase);
            return phase;
        }

        private static void Finalize(InstallPaths paths, string directory, Request request, Action<string> log, string phase)
        {
            try
            {
                Discard(paths, request);
                string retired = directory + ".done";
                InstallTree.CheckUnlinked(retired);
                Directory.Move(directory, retired);
                Step("retired");
                InstallTree.Delete(retired);
            }
            catch (IOException ex) { log("Workshop group " + request.Id + " cleanup deferred: " + ex.Message); }
            catch (UnauthorizedAccessException ex) { log("Workshop group " + request.Id + " cleanup deferred: " + ex.Message); }
            log("Workshop group " + request.Id + " " + phase + " (" + string.Join(", ", request.Members.Select(member => member.Item)) + ").");
        }

        private static void Discard(InstallPaths paths, Request request)
        {
            string path = PendingPath(paths, request.Id);
            InstallTree.CheckUnlinked(path);
            InstallTree.DeleteFileIfExists(path);
            foreach (var member in request.Members)
            {
                string container = paths.Container(member.Item);
                string marker = Path.Combine(container, InstallTree.CompleteName);
                InstallTree.CheckUnlinked(marker);
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == member.Plan) InstallTree.Delete(container);
            }
        }
    }
}

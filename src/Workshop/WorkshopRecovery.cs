using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace APIShared.Workshop
{
    // Internal recovery protocol; existing public plans and v1/v2 operations keep
    // their contracts. Inspection never writes to the installation or journal.
    internal sealed class WorkshopInterruptedTransaction
    {
        internal string Directory, OperationsHash, JournalHash, Failure, RollbackError;
        internal string[] Targets = new string[0];
        internal ulong[] Items = new ulong[0];
        internal WorkshopRecoveryProgress Progress = new WorkshopRecoveryProgress();
        internal bool CanRollback => RollbackError == null;
        internal string Reference => Path.GetFileName(Directory) + "\t" + OperationsHash + "\t" + JournalHash;
    }

    // Presentation only: malformed progress must never alter recovery eligibility.
    internal sealed class WorkshopRecoveryProgress
    {
        internal bool Reliable;
        internal int Completed, Total;
        internal string LastCompleted, Pending;

        internal string Describe(bool german)
        {
            if (!Reliable) return german ? "Fortschritt nicht zuverlässig bestimmbar." : "Progress cannot be determined reliably.";
            string text = german ? Completed + " von " + Total + " Operationen als abgeschlossen protokolliert."
                : Completed + " of " + Total + " operations recorded as completed.";
            if (LastCompleted != null) text += "\n" + (german ? "Zuletzt abgeschlossen: " : "Last completed: ") + LastCompleted;
            if (Pending != null) text += "\n" + (german ? "Begonnen, ohne protokollierten Abschluss: " : "Started without a recorded completion: ") + Pending;
            return text;
        }
    }

    internal static class WorkshopRecovery
    {
        internal enum Choice { Later, Repair, Rollback, CompleteRepair }
        internal static Choice Decide(bool rollback, int answer) => rollback
            ? answer == 6 ? Choice.Repair : answer == 7 ? Choice.Rollback : Choice.Later
            : answer == 6 ? Choice.CompleteRepair : Choice.Later;

        internal static string[] PreservedAssemblies(string game, WorkshopPackage package)
        {
            string full = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var delivered = new HashSet<string>(package.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in package.Roots) {
                string directory = WorkshopPaths.Inside(game, root);
                if (!System.IO.Directory.Exists(directory)) continue;
                foreach (string file in WorkshopPaths.Enumerate(directory)) {
                    string relative = file.Substring(full.Length).Replace('\\', '/');
                    if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase) && !delivered.Contains(relative) &&
                        !relative.Split('/').Any(p => p.Equals("config", StringComparison.OrdinalIgnoreCase) || p.Equals("LobbyModSettings", StringComparison.OrdinalIgnoreCase)))
                        result.Add(relative);
                }
            }
            return result.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        internal const string Protocol = "APISHARED-WORKSHOP-RECOVERY-1";
        internal static string Describe(WorkshopInterruptedTransaction entry, bool german)
        {
            return (entry.Failure ?? (german ? "Unterbrochen ohne protokollierten Fehler." : "Interrupted without a recorded error."))
                + "\n" + entry.Progress.Describe(german);
        }

        internal static WorkshopRecoveryProgress Progress(string[] journal, string[][] rows)
        {
            var progress = new WorkshopRecoveryProgress { Total = rows.Length };
            int next = 0;
            bool pending = false;
            foreach (string line in journal) {
                // These markers record the sequential application, not rollback.
                if (!Regex.IsMatch(line, "^(begin|done)(\\s|$)")) continue;
                string[] marker = line.Split('\t');
                if (marker.Length != 2 || !int.TryParse(marker[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) ||
                    ordinal != next || ordinal < 0 || ordinal >= rows.Length || marker[1] != ordinal.ToString(CultureInfo.InvariantCulture)) return progress;
                if (marker[0] == "begin" && !pending) { pending = true; progress.Pending = rows[ordinal][1]; }
                else if (marker[0] == "done" && pending) {
                    pending = false; next++; progress.Completed = next;
                    progress.LastCompleted = rows[ordinal][1]; progress.Pending = null;
                } else return progress;
            }
            progress.Reliable = true;
            return progress;
        }
        internal static string[] Journal(string directory) => File.Exists(WorkshopPaths.Inside(directory, "journal.log"))
            ? File.ReadAllLines(WorkshopPaths.Inside(directory, "journal.log")) : new string[0];
        internal static bool Finished(string directory) => Journal(directory).Any(x => x == "committed" || x == "restored");

        // Supersession is evidence in a committed replacement transaction, never
        // an early mutation of the failed transaction. Its original bytes remain.
        internal static bool Superseded(string root, string directory)
        {
            foreach (string candidate in System.IO.Directory.EnumerateDirectories(root)) {
                string file = WorkshopPaths.Inside(candidate, "supersedes.tsv");
                if (!Journal(candidate).Contains("committed") || !File.Exists(file)) continue;
                string pin = WorkshopPaths.Inside(candidate, "supersedes.sha256");
                if (!File.Exists(pin) || File.ReadAllText(pin) != WorkshopInventory.Hash(file)) throw new IOException("Committed replacement references changed: " + candidate);
                foreach (string line in File.ReadAllLines(file)) {
                    string[] row = Reference(line);
                    if (row[0] == Path.GetFileName(directory) && row[1] == WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(directory, "operations.tsv")) &&
                        row[2] == WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(directory, "journal.log"))) return true;
                }
            }
            return false;
        }
        internal static string[] Reference(string line)
        {
            string[] row = line.Split('\t');
            if (row.Length != 3 || !Regex.IsMatch(row[0], "^[a-f0-9]{32}$") || !Regex.IsMatch(row[1], "^[a-f0-9]{64}$") || !Regex.IsMatch(row[2], "^[a-f0-9]{64}$"))
                throw new InvalidDataException("Invalid recovery transaction reference.");
            return row;
        }
        internal static IReadOnlyList<WorkshopInterruptedTransaction> Inspect(string game)
        {
            string root = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(game), "Transactions");
            var result = new List<WorkshopInterruptedTransaction>();
            if (!System.IO.Directory.Exists(root)) return result;
            foreach (string directory in System.IO.Directory.EnumerateDirectories(root)) {
                WorkshopPaths.CheckLinks(directory);
                string[] journal = Journal(directory);
                if (!journal.Contains("applying") || Finished(directory) || Superseded(root, directory)) continue;
                var entry = new WorkshopInterruptedTransaction {
                    Directory = directory,
                    OperationsHash = WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(directory, "operations.tsv")),
                    JournalHash = WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(directory, "journal.log")),
                    Failure = journal.LastOrDefault(x => x.StartsWith("failure ", StringComparison.Ordinal))
                };
                try {
                    var rows = ReadOperations(game, directory);
                    entry.Progress = Progress(journal, rows);
                    entry.Targets = rows.Select(r => r[1]).ToArray();
                    entry.Items = rows.Where(r => r[0].StartsWith("receipt-", StringComparison.Ordinal))
                        .Select(r => ulong.Parse(Path.GetFileNameWithoutExtension(r[1]), CultureInfo.InvariantCulture)).ToArray();
                    var started = Started(journal, rows.Length);
                    foreach (string[] row in rows.Where(r => started.Contains(r[2]))) {
                        string target = WorkshopPaths.Inside(game, row[1]);
                        string current = WorkshopInstallation.CurrentHash(target);
                        if (System.IO.Directory.Exists(target)) throw new IOException("Recovery target is a directory: " + row[1]);
                        if (current == row[3]) continue; // Already restored, including an interrupted rollback.
                        if (row[3] != "missing" && WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(directory, "Backup/" + row[2])) != row[3])
                            throw new IOException("Recovery backup missing or damaged: " + row[1]);
                        // A partial copy may have any hash. Preserve those bytes in
                        // the recovery request before restoring the verified backup.
                    }
                } catch (Exception ex) { entry.RollbackError = ex.Message; }
                result.Add(entry);
            }
            foreach (var entry in result)
                if (result.Any(other => other != entry && other.Targets.Intersect(entry.Targets, StringComparer.OrdinalIgnoreCase).Any()))
                    entry.RollbackError = "Overlapping unfinished transactions require a complete repair.";
            return result;
        }
        internal static HashSet<string> Started(string[] journal, int count)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in journal.Where(x => x.StartsWith("begin\t", StringComparison.Ordinal))) {
                string value = line.Substring(6);
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int ordinal) || ordinal < 0 || ordinal >= count || value != ordinal.ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("Invalid recovery journal ordinal.");
                result.Add(value);
            }
            return result;
        }
        internal static string[][] ReadOperations(string game, string directory)
        {
            string[] lines = File.ReadAllLines(WorkshopPaths.Inside(directory, "operations.tsv"));
            if (lines.Length < 2 || (lines[0] != WorkshopTransaction.Protocol && lines[0] != WorkshopTransaction.BatchProtocol)) throw new InvalidDataException("Unknown recovery operation protocol.");
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool receiptPhase = false;
            var rows = lines.Skip(1).Select(x => x.Split('\t')).ToArray();
            for (int index = 0; index < rows.Length; index++) {
                var r = rows[index];
                if (r.Length != 5 || r[2] != index.ToString(CultureInfo.InvariantCulture) || !Regex.IsMatch(r[3], "^(missing|[a-f0-9]{64})$") || !Regex.IsMatch(r[4], "^(missing|[a-f0-9]{64})$")) throw new InvalidDataException("Malformed recovery operation.");
                bool receipt = r[0] == "receipt-write" || r[0] == "receipt-delete";
                if (receipt) {
                    if (!Regex.IsMatch(r[1], "^_APIShared/Workshop/Receipts/[1-9][0-9]*\\.json$") || (lines[0] == WorkshopTransaction.Protocol && index != rows.Length - 1)) throw new InvalidDataException("Invalid receipt phase.");
                    receiptPhase = true;
                } else {
                    if (receiptPhase || (r[0] != "write" && r[0] != "delete")) throw new InvalidDataException("Invalid payload operation.");
                    WorkshopPaths.DeliveredPath(r[1]);
                }
                if (!targets.Add(r[1])) throw new InvalidDataException("Duplicate recovery target.");
                WorkshopPaths.Inside(game, r[1]);
                if (r[0].EndsWith("write", StringComparison.Ordinal) ? r[4] == "missing" : r[4] != "missing") throw new InvalidDataException("Invalid recovery hash.");
            }
            if (!receiptPhase) throw new InvalidDataException("Missing recovery receipt.");
            return rows;
        }
        internal static void ValidateReferences(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries)
        {
            var actual = Inspect(game);
            if (!actual.Select(x => x.Reference).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(entries.Select(x => x.Reference).OrderBy(x => x, StringComparer.Ordinal)))
                throw new IOException("Interrupted transactions changed; inspect and confirm recovery again.");
        }
        internal static string StageRollback(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries, bool restart)
        {
            WorkshopTransaction.StateGate.Wait();
            try { return StageRollbackLocked(game, entries, restart); }
            finally { WorkshopTransaction.StateGate.Release(); }
        }
        private static string StageRollbackLocked(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries, bool restart)
        {
            ValidateReferences(game, entries);
            if (entries.Count == 0 || entries.Any(x => !x.CanRollback)) throw new InvalidOperationException("Rollback is not available for every interrupted transaction.");
            string directory = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(game), "Transactions/" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            File.WriteAllLines(WorkshopPaths.Inside(directory, "operations.tsv"), new[] { Protocol, restart ? "restart" : "stop" }.Concat(entries.Select(x => x.Reference)), new UTF8Encoding(false));
            return directory;
        }
        internal static void AssertRepairCoverage(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries, IReadOnlyList<WorkshopInstallPlan> plans)
        {
            ValidateReferences(game, entries);
            var items = new HashSet<ulong>(plans.Select(x => x.WorkshopId));
            foreach (var entry in entries) {
                var rows = ReadOperations(game, entry.Directory);
                if (entry.Items.Length == 0 || entry.Items.Any(id => !items.Contains(id))) throw new InvalidOperationException("A complete repair requires every affected Workshop package to be available and subscribed.");
                foreach (var row in rows.Where(r => !r[0].StartsWith("receipt-", StringComparison.Ordinal)))
                    if (!plans.Any(p => p.Package != null && p.Package.Roots.Any(root => row[1].StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidOperationException("Interrupted target is outside the confirmed repair packages: " + row[1]);
            }
        }
    }
}

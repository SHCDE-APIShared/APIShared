using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace APIShared.Workshop
{
    // Read-only mirror of the planning half of workshop-maintenance.ps1/.sh
    // (Maintenance-Scan/Visit and maintenance_scan/visit). Startup spawns the
    // external pass only when that pass could change files: a pending cleanup
    // worklist, or a planned action (whole-directory retirement of validated,
    // resolved history outside retention, or removal of New/ staging copies kept
    // with retained history). Checks that need payload or pin hashing are assumed
    // to pass, and invalid nodes contribute no keep references; both can only add
    // planned work. The result may therefore over-approximate (an unnecessary
    // spawn) but never skips work either script would do.
    internal static class WorkshopMaintenancePlan
    {
        // Patterns are built on the first transaction read, not on the empty-history
        // fast path that runs on every start.
        private static class Rx
        {
            internal static readonly Regex Id = new Regex("^[a-f0-9]{32}$");
            internal static readonly Regex Folder = new Regex("^(New|Backup|RecoveryBackup)(/[a-f0-9]{32})?$", RegexOptions.IgnoreCase);
            internal static readonly Regex Allowed = new Regex(@"^(operations\.tsv|operations\.sha256|diagnostics\.txt|journal\.log|ready\.flag|cancel\.flag|recovery-cancel\.flag|supersedes\.tsv|supersedes\.sha256|completed\.utc|New/(0|[1-9][0-9]*)|Backup/(0|[1-9][0-9]*)|RecoveryBackup/[a-f0-9]{32}/(0|[1-9][0-9]*))$");
            internal static readonly Regex Payload = new Regex("^(New|Backup)/([0-9]+)$");
            internal static readonly Regex Hash = new Regex("^(missing|[a-f0-9]{64})$", RegexOptions.IgnoreCase);
            internal static readonly Regex Number = new Regex("^(0|[1-9][0-9]*)$");
            internal static readonly Regex ReceiptTarget = new Regex(@"^_APIShared/Workshop/Receipts/[1-9][0-9]*\.json$");
            internal static readonly Regex PluginTarget = new Regex("^BepInEx/plugins/[^/]+/.+");
            internal static readonly Regex ProtectedTarget = new Regex("^BepInEx/plugins/(APIShared_Serp|000shcdese)/|/(config|LobbyModSettings)(/|$)", RegexOptions.IgnoreCase);
            internal static readonly Regex ReferenceId = new Regex("^[a-f0-9]{32}$", RegexOptions.IgnoreCase);
            internal static readonly Regex ReferenceHash = new Regex("^[a-f0-9]{64}$", RegexOptions.IgnoreCase);
        }
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private sealed class Node
        {
            internal string Id;
            internal bool Valid, Terminal, Committed, Resolved, Request, Keep;
            internal string[] Items = new string[0], Refs = new string[0];
            internal List<string> Files = new List<string>();
            internal long Ticks, Seconds;
            internal Node Copy() => (Node)MemberwiseClone();
        }

        // True when workshop-updater -Maintain could delete or record anything.
        // Exceptions propagate; callers treat them as "run the external pass".
        internal static bool MayChange(string game)
        {
            string state = Path.Combine(Path.GetFullPath(game), "_APIShared", "Workshop");
            string root = Path.Combine(state, "Transactions");
            // Both scripts exit before taking the lock when Transactions is absent.
            if (!Directory.Exists(root)) return false;
            // A worklist always resumes first; nothing else runs while it remains.
            if (File.Exists(Path.Combine(state, "cleanup.pending.tsv"))) return true;
            var scanned = new List<Node>();
            foreach (var directory in new DirectoryInfo(root).EnumerateDirectories()) scanned.Add(Read(directory));
            if (scanned.Count == 0) return false;
            // Windows orders legacy journals by tick time and keys nodes case-
            // insensitively; Proton uses whole seconds and case-sensitive IDs.
            return Plans(scanned, false) || Plans(scanned, true);
        }

        // One directory listing per transaction; file existence comes from that
        // listing, and only operations, journal, supersedes and completion are read.
        private static Node Read(DirectoryInfo info)
        {
            string directory = info.FullName;
            var node = new Node { Id = info.Name, Keep = true };
            if (!Rx.Id.IsMatch(node.Id) || !Files(info, "", node.Files) || node.Files.Any(f => !Rx.Allowed.IsMatch(f))) return node;
            var present = new HashSet<string>(node.Files, StringComparer.Ordinal);
            string operationsPath = Path.Combine(directory, "operations.tsv"), journalPath = Path.Combine(directory, "journal.log");
            if (!present.Contains("operations.tsv") || !present.Contains("journal.log")) return node;
            string[] lines = File.ReadAllLines(operationsPath), journal = File.ReadAllLines(journalPath);
            node.Committed = journal.Contains("committed");
            node.Terminal = node.Committed || journal.Contains("restored");
            node.Resolved = node.Terminal;
            string[] refs = new string[0];
            if (lines.Length >= 3 && lines[0] == WorkshopRecovery.Protocol) {
                if (lines[1] != "restart" && lines[1] != "stop") return node;
                node.Request = true;
                refs = References(lines.Skip(2));
                if (refs == null) return node;
                foreach (string file in node.Files) {
                    if (file.StartsWith("New/", StringComparison.Ordinal) || file.StartsWith("Backup/", StringComparison.Ordinal)) return node;
                    if (file.StartsWith("RecoveryBackup/", StringComparison.Ordinal) && !refs.Contains(file.Split('/')[1], StringComparer.Ordinal)) return node;
                }
            } else {
                var rows = Rows(lines);
                if (rows == null) return node;
                node.Items = rows.Where(r => r[0].StartsWith("receipt-", StringComparison.Ordinal)).Select(r => Path.GetFileNameWithoutExtension(r[1])).ToArray();
                foreach (string line in journal) {
                    if (!line.StartsWith("begin\t", StringComparison.Ordinal)) continue;
                    string value = line.Substring(6);
                    if (!Rx.Number.IsMatch(value) || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal) || ordinal >= rows.Length) return node;
                }
                foreach (string file in node.Files) {
                    Match match = Rx.Payload.Match(file);
                    if (match.Success) {
                        if (!long.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out long ordinal) || ordinal >= rows.Length) return node;
                        // The payload hash comparison itself is assumed to pass.
                        string expected = match.Groups[1].Value == "New" ? rows[ordinal][4] : rows[ordinal][3];
                        if (string.Equals(expected, "missing", StringComparison.OrdinalIgnoreCase)) return node;
                    } else if (file.StartsWith("RecoveryBackup/", StringComparison.Ordinal)) return node;
                }
                if (present.Contains("supersedes.tsv")) {
                    // The pin must exist; its hash comparison is assumed to pass.
                    if (!present.Contains("supersedes.sha256")) return node;
                    refs = References(File.ReadAllLines(Path.Combine(directory, "supersedes.tsv")));
                    if (refs == null) return node;
                } else if (present.Contains("supersedes.sha256")) return node;
            }
            DateTime time;
            if (present.Contains("completed.utc")) {
                // Windows rejects a trailing newline that Bash command substitution strips.
                if (!DateTime.TryParseExact(File.ReadAllText(Path.Combine(directory, "completed.utc")).TrimEnd('\r', '\n'), "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out time)) return node;
            } else time = File.GetLastWriteTimeUtc(journalPath);
            node.Ticks = time.Ticks; node.Seconds = (long)Math.Floor((time - Epoch).TotalSeconds);
            node.Refs = refs; node.Valid = true; node.Keep = false;
            return node;
        }

        // Maintenance-Files: only the fixed payload folders may nest; links and
        // unexpected folders make the transaction invalid (retained).
        private static bool Files(DirectoryInfo directory, string prefix, List<string> files)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
            foreach (var entry in directory.EnumerateFileSystemInfos()) {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                string relative = prefix + entry.Name;
                if ((entry.Attributes & FileAttributes.Directory) != 0) {
                    if (!Rx.Folder.IsMatch(relative) || !Files((DirectoryInfo)entry, relative + "/", files)) return false;
                } else files.Add(relative);
            }
            return true;
        }

        // Read-Reference: three cells, transaction ID plus two SHA-256 pins.
        private static string[] References(IEnumerable<string> lines)
        {
            var ids = new List<string>();
            foreach (string line in lines) {
                string[] cells = line.Split('\t');
                if (cells.Length != 3 || !Rx.ReferenceId.IsMatch(cells[0]) || !Rx.ReferenceHash.IsMatch(cells[1]) || !Rx.ReferenceHash.IsMatch(cells[2])) return null;
                ids.Add(cells[0]);
            }
            return ids.ToArray();
        }

        // Read-RecoveryRows without the game-path containment/link checks, which
        // APIShared-staged rows always pass.
        private static string[][] Rows(string[] data)
        {
            if (data.Length < 2) return null;
            bool single = string.Equals(data[0], WorkshopTransaction.Protocol, StringComparison.OrdinalIgnoreCase);
            if (!single && !string.Equals(data[0], WorkshopTransaction.BatchProtocol, StringComparison.OrdinalIgnoreCase)) return null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var rows = new string[data.Length - 1][];
            bool receipt = false;
            for (int i = 1; i < data.Length; i++) {
                string[] r = data[i].Split('\t');
                if (r.Length != 5 || r[2] != (i - 1).ToString(CultureInfo.InvariantCulture) || !Rx.Hash.IsMatch(r[3]) || !Rx.Hash.IsMatch(r[4]) || !seen.Add(r[1])) return null;
                if (r[0] == "receipt-write" || r[0] == "receipt-delete") {
                    if (!Rx.ReceiptTarget.IsMatch(r[1]) || (single && i != data.Length - 1)) return null;
                    receipt = true;
                } else if (receipt || (r[0] != "write" && r[0] != "delete") || !Rx.PluginTarget.IsMatch(r[1]) || Rx.ProtectedTarget.IsMatch(r[1])) return null;
                bool missing = string.Equals(r[4], "missing", StringComparison.OrdinalIgnoreCase);
                if (r[0].EndsWith("write", StringComparison.Ordinal) ? missing : !missing) return null;
                rows[i - 1] = r;
            }
            return receipt ? rows : null;
        }

        private static bool Plans(List<Node> scanned, bool proton)
        {
            var nodes = new Dictionary<string, Node>(proton ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
            foreach (var original in scanned) nodes[original.Id] = original.Copy();
            var all = nodes.Values.ToList();
            // A validated committed replacement resolves the originals it pins
            // (the pinned hashes are assumed to match).
            foreach (var node in all) {
                if (!node.Valid || node.Request || !node.Committed) continue;
                foreach (string id in node.Refs) if (nodes.TryGetValue(id, out var old)) old.Resolved = true;
            }
            foreach (var node in all) {
                if (node.Valid && node.Request && !node.Terminal) {
                    bool resolved = node.Refs.Length > 0;
                    foreach (string id in node.Refs) if (!nodes.TryGetValue(id, out var old) || !old.Valid || !old.Resolved) resolved = false;
                    node.Resolved = resolved;
                }
                if (!node.Valid || !node.Resolved) node.Keep = true;
            }
            // The two newest validated terminal transactions per Workshop item.
            var items = new Dictionary<string, List<Node>>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in all) {
                if (!node.Valid || node.Request || !node.Terminal) continue;
                foreach (string item in node.Items) {
                    if (!items.TryGetValue(item, out var list)) items[item] = list = new List<Node>();
                    list.Add(node);
                }
            }
            foreach (var list in items.Values)
                foreach (var node in list.OrderByDescending(n => proton ? n.Seconds : n.Ticks).ThenByDescending(n => n.Id, StringComparer.Ordinal).Take(2)) node.Keep = true;
            // Retained originals keep their proof/request; retained requests keep originals.
            bool changed;
            do {
                changed = false;
                foreach (var node in all)
                    foreach (string id in node.Refs) {
                        if (!nodes.TryGetValue(id, out var old)) continue;
                        if (old.Keep && !node.Keep) { node.Keep = true; changed = true; }
                        else if (node.Request && node.Keep && !old.Keep) { old.Keep = true; changed = true; }
                    }
            } while (changed);
            // Visit: retire unretained resolved history; strip New/ from retained history.
            return all.Any(n => n.Valid && n.Resolved && (!n.Keep || n.Files.Any(f => f.StartsWith("New/", StringComparison.Ordinal))));
        }
    }

    internal enum WorkshopMaintenanceExit { Skipped, Completed, Stopped, StillRunning }
    internal sealed class WorkshopMaintenanceResult
    {
        internal WorkshopMaintenanceExit Exit;
        internal int ExitCode;
        internal string Output = "", Error = "";
    }

    // Runs one external maintenance pass while owning the transaction state gate.
    // The gate is released only after the child has exited: a child that outlives
    // its timeout is killed and awaited; if it still survives, a waiter keeps the
    // gate until the real exit and the caller defers recovery and staging.
    internal static class WorkshopMaintenanceRunner
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20), KillWait = TimeSpan.FromSeconds(5);
        // Staging waits at most this long for a running maintenance pass.
        internal static readonly TimeSpan GateWait = TimeSpan.FromSeconds(30);

        internal static async Task<WorkshopMaintenanceResult> RunAsync(SemaphoreSlim gate, Func<bool> needed, Func<ProcessStartInfo> start, TimeSpan timeout, TimeSpan killWait, Action<Process> kill = null)
        {
            if (!await gate.WaitAsync(GateWait)) return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.StillRunning, Error = "Transaction state is still owned by another operation." };
            Process process = null;
            bool release = true;
            try {
                if (!needed()) return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.Skipped };
                var info = start();
                info.RedirectStandardOutput = true; info.RedirectStandardError = true;
                process = Process.Start(info);
                if (process == null) throw new IOException("Maintenance process could not be started.");
                var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
                var child = process;
                var result = new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.Completed };
                if (!await Task.Run(() => child.WaitForExit(Milliseconds(timeout)))) {
                    result.Exit = WorkshopMaintenanceExit.Stopped;
                    if (!await StopAsync(child, killWait, kill)) { release = false; Handoff(child, gate); process = null; return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.StillRunning }; }
                }
                child.WaitForExit();
                result.ExitCode = child.ExitCode; result.Output = (await output).Trim(); result.Error = (await errors).Trim();
                return result;
            } catch (Exception ex) {
                var child = process;
                if (child == null || !Alive(child)) throw;
                if (!await StopAsync(child, killWait, kill)) { release = false; Handoff(child, gate); process = null; return new WorkshopMaintenanceResult { Exit = WorkshopMaintenanceExit.StillRunning, Error = ex.Message }; }
                throw;
            } finally {
                if (process != null) process.Dispose();
                if (release) gate.Release();
            }
        }
        private static int Milliseconds(TimeSpan value) => (int)Math.Min(int.MaxValue, Math.Max(0, value.TotalMilliseconds));
        private static bool Alive(Process process)
        {
            try { return !process.HasExited; } catch (InvalidOperationException) { return false; }
        }
        private static async Task<bool> StopAsync(Process process, TimeSpan wait, Action<Process> kill)
        {
            try { (kill ?? (p => p.Kill()))(process); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            return await Task.Run(() => process.WaitForExit(Milliseconds(wait)));
        }
        private static void Handoff(Process process, SemaphoreSlim gate) =>
            Task.Run(() => { try { process.WaitForExit(); } finally { process.Dispose(); gate.Release(); } });
    }
}

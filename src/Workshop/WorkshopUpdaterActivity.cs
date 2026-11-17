using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace APIShared.Workshop
{
    // A record is evidence only while the actual platform writer lock is held.
    // A stale record or PID never authorizes terminating a game by itself.
    internal sealed class WorkshopUpdaterActivity
    {
        internal bool Busy, Active;
        internal string Transaction, OperationsHash, Phase;
        internal const string Header = "APISHARED-WORKSHOP-ACTIVE-1";
        private static string ReadRecord(string path)
        {
            using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream)) {
                var buffer = new char[512]; int count = reader.ReadBlock(buffer, 0, buffer.Length);
                return count == buffer.Length ? "" : new string(buffer, 0, count);
            }
        }
        internal static WorkshopUpdaterActivity Parse(string text, string platform, Func<int, string, bool> owner)
        {
            var unknown = new WorkshopUpdaterActivity { Busy = true };
            string[] r = text.TrimEnd('\r', '\n').Split('\t');
            if (r.Length != 8 || r[0] != Header || r[1] != platform ||
                !int.TryParse(r[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
                !Regex.IsMatch(r[3], "^[0-9]+$") || !owner(pid, r[3])) return unknown;
            if (r[4] == "maintenance" || r[5] == "completed") return unknown;
            if ((r[4] != "install" && r[4] != "recover") ||
                !Regex.IsMatch(r[5], "^(waiting|backup|apply|restore|paused)$") ||
                !Regex.IsMatch(r[6], "^[a-f0-9]{32}$") || !Regex.IsMatch(r[7], "^[a-f0-9]{64}$")) return unknown;
            return new WorkshopUpdaterActivity { Busy = true, Active = true, Transaction = r[6], OperationsHash = r[7], Phase = r[5] };
        }
        // Diagnostics only. The Windows updater empties apply.lock in its finally block, so a
        // record that remains while no writer holds the lock was left by a process that was
        // terminated from outside (killed, logoff, shutdown or power loss). Returns a readable
        // description or null; never authorizes or blocks anything.
        internal static string DescribeStaleWindows(string game)
        {
            try {
                string path = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(game), "apply.lock");
                if (!File.Exists(path)) return null;
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                return DescribeRecord(ReadRecord(path));
            } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is InvalidDataException) {
                return null; // Held by a live writer or unreadable: nothing stale to report.
            }
        }
        internal static string DescribeRecord(string text)
        {
            string[] r = (text ?? "").TrimEnd('\r', '\n').Split('\t');
            if (r.Length != 8 || r[0] != Header) return null;
            string started = long.TryParse(r[3], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) && ticks > 0 && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) : "unknown";
            return "platform=" + r[1] + "; pid=" + r[2] + "; processStart=" + started + "; mode=" + r[4] + "; phase=" + r[5] + "; transaction=" + r[6];
        }
        internal static WorkshopUpdaterActivity ReadWindows(string game)
        {
            string state = WorkshopInstallation.StateRoot(game), path = WorkshopPaths.Inside(state, "apply.lock");
            if (!File.Exists(path)) return new WorkshopUpdaterActivity();
            try {
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { }
                return new WorkshopUpdaterActivity();
            } catch (IOException) { }
            try {
                string text = ReadRecord(path);
                var result = Parse(text, "windows", (pid, start) => {
                    try {
                        using (var process = Process.GetProcessById(pid))
                            return !process.HasExited && process.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase) &&
                                process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) == start;
                    } catch { return false; }
                });
                if (!result.Active) return result;
                string directory = WorkshopInstallation.FindTransaction(game, result.Transaction);
                if (directory == null || !File.Exists(WorkshopPaths.Inside(directory, "ready.flag")) ||
                    File.ReadAllText(WorkshopPaths.Inside(directory, "ready.flag")) != result.OperationsHash ||
                    WorkshopInventory.Hash(WorkshopPaths.Inside(directory, "operations.tsv")) != result.OperationsHash || WorkshopRecovery.Finished(directory))
                    return new WorkshopUpdaterActivity { Busy = true };
                // The owner may have finished while metadata was read.
                try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite)) { } return new WorkshopUpdaterActivity(); }
                catch (IOException) { return ReadRecord(path) == text ? result : new WorkshopUpdaterActivity { Busy = true }; }
            } catch { return new WorkshopUpdaterActivity { Busy = true }; }
        }
    }
}

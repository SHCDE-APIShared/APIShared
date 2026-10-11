using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace APIShared.Workshop
{
    // The updater reads a tab-separated operation protocol. No command strings or package-provided scripts are executed.
    internal static class WorkshopTransaction
    {
        internal const string Protocol = "APISHARED-WORKSHOP-1";
        internal static string Stage(WorkshopInstallPlan plan)
        {
            string transactionRoot = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(plan.GameRoot), "Transactions");
            if (Directory.Exists(transactionRoot))
                foreach (string previous in Directory.EnumerateDirectories(transactionRoot)) {
                    string journal = WorkshopPaths.Inside(previous, "journal.log");
                    if (!File.Exists(journal)) continue;
                    string[] history = File.ReadAllLines(journal);
                    if (history.Contains("applying") && !history.Contains("committed") && !history.Contains("restored"))
                        throw new InvalidOperationException("Recover the interrupted transaction before applying another plan: " + previous);
                }
            if (WorkshopInstallation.CurrentHash(WorkshopInstallation.ReceiptPath(plan.GameRoot, plan.WorkshopId)) != plan.ReceiptHash)
                throw new InvalidOperationException("Installation receipt changed; prepare a new plan.");
            foreach (var target in plan.TargetHashes)
                if (WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(plan.GameRoot, target.Key)) != target.Value) throw new InvalidOperationException("Target changed after planning: " + target.Key);
            if (plan.Package != null)
            {
                if (WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(plan.Source, WorkshopPackage.ManifestFileName)) != plan.ManifestHash) throw new InvalidOperationException("Package changed; prepare again.");
                WorkshopPaths.Verify(plan.Package, plan.Source); WorkshopInstallation.AssertOwnership(plan.GameRoot, plan.WorkshopId, plan.Package);
            }
            string directory = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(plan.GameRoot), "Transactions/" + plan.OperationId);
            if (Directory.Exists(directory)) throw new InvalidOperationException("Plan has already been staged.");
            Directory.CreateDirectory(directory);
            var lines = new List<string> { Protocol };
            int ordinal = 0;
            foreach (var file in plan.Package?.Files ?? new WorkshopFile[0])
            {
                string staged = WorkshopPaths.Inside(directory, "New/" + ordinal.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(staged));
                File.Copy(WorkshopPaths.Inside(WorkshopPaths.Inside(plan.Source, "Payload"), file.Path), staged);
                if (WorkshopInventory.Hash(staged) != file.Sha256) throw new IOException("Payload changed while staging.");
                lines.Add(Row("write", file.Path, ordinal++, plan.TargetHashes[file.Path], file.Sha256));
            }
            var warnings = new List<string>();
            foreach (string file in plan.RemovedFiles)
                lines.Add(Row("delete", file, ordinal++, plan.TargetHashes[file], "missing"));
            string receipt = "_APIShared/Workshop/Receipts/" + plan.WorkshopId.ToString(CultureInfo.InvariantCulture) + ".json";
            if (plan.Action == WorkshopAction.Remove) lines.Add(Row("receipt-delete", receipt, ordinal++, plan.ReceiptHash, "missing"));
            else
            {
                string next = WorkshopPaths.Inside(directory, "New/" + ordinal.ToString(CultureInfo.InvariantCulture));
                Directory.CreateDirectory(Path.GetDirectoryName(next));
                File.WriteAllText(next, WorkshopPackageCodec.Write(new WorkshopReceipt {
                    WorkshopId = plan.WorkshopId.ToString(CultureInfo.InvariantCulture), PackageId = plan.PackageId, Version = plan.Version,
                    Files = plan.Package.Files, ModGuids = plan.Package.ModGuids, ManifestHash = plan.ManifestHash, TransactionId = plan.OperationId
                }), new UTF8Encoding(false));
                lines.Add(Row("receipt-write", receipt, ordinal++, plan.ReceiptHash, WorkshopInventory.Hash(next)));
            }
            foreach (var target in plan.TargetHashes)
                if (WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(plan.GameRoot, target.Key)) != target.Value)
                    throw new InvalidOperationException("Target changed during staging: " + target.Key);
            File.WriteAllLines(WorkshopPaths.Inside(directory, "operations.tsv"), lines, new UTF8Encoding(false));
            File.WriteAllLines(WorkshopPaths.Inside(directory, "diagnostics.txt"), warnings, new UTF8Encoding(false));
            // Hashing the complete operation list ties consent to the prepared, immutable transaction.
            File.WriteAllText(WorkshopPaths.Inside(directory, "operations.sha256"), WorkshopInventory.Hash(WorkshopPaths.Inside(directory, "operations.tsv")), new UTF8Encoding(false));
            return directory;
        }
        private static string Row(string kind, string target, int ordinal, string before, string after) =>
            string.Join("\t", kind, target, ordinal.ToString(CultureInfo.InvariantCulture), before, after);
    }
}

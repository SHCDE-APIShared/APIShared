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
        internal const string BatchProtocol = "APISHARED-WORKSHOP-2";
        internal static string Stage(WorkshopInstallPlan plan) => StageCore(new[] { plan }, plan.OperationId, Protocol);
        internal static string StageBatch(IReadOnlyList<WorkshopInstallPlan> plans, string operationId) => StageCore(plans, operationId, BatchProtocol);
        private static string StageCore(IReadOnlyList<WorkshopInstallPlan> plans, string operationId, string protocol)
        {
            if (plans == null || plans.Count == 0) throw new ArgumentException("An installation batch cannot be empty.");
            var first = plans[0];
            if (plans.Any(p => p.GameRoot != first.GameRoot || p.Owner != first.Owner) || plans.Select(p => p.WorkshopId).Distinct().Count() != plans.Count)
                throw new InvalidOperationException("Batch plans must share an owner and game, with unique Workshop IDs.");
            foreach (var candidate in plans) Validate(candidate);
            string directory = WorkshopPaths.Inside(WorkshopInstallation.StateRoot(first.GameRoot), "Transactions/" + operationId);
            if (Directory.Exists(directory)) throw new InvalidOperationException("Plan has already been staged.");
            Directory.CreateDirectory(directory);
            var lines = new List<string> { protocol };
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();
            int ordinal = 0;
            // All delivered-file changes precede all receipt commits. The updater
            // backs up the complete batch and rolls receipts back first on failure.
            foreach (var plan in plans) {
                warnings.AddRange(plan.Diagnostics);
                foreach (var file in plan.Package?.Files ?? new WorkshopFile[0]) {
                    if (!targets.Add(file.Path)) throw new InvalidOperationException("Overlapping batch target: " + file.Path);
                    string staged = WorkshopPaths.Inside(directory, "New/" + ordinal.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(Path.GetDirectoryName(staged));
                    File.Copy(WorkshopPaths.Inside(WorkshopPaths.Inside(plan.Source, "Payload"), file.Path), staged);
                    if (WorkshopInventory.Hash(staged) != file.Sha256) throw new IOException("Payload changed while staging.");
                    lines.Add(Row("write", file.Path, ordinal++, plan.TargetHashes[file.Path], file.Sha256));
                }
                foreach (string file in plan.RemovedFiles) {
                    if (!targets.Add(file)) throw new InvalidOperationException("Overlapping batch target: " + file);
                    lines.Add(Row("delete", file, ordinal++, plan.TargetHashes[file], "missing"));
                }
            }
            foreach (var plan in plans) {
                string receipt = "_APIShared/Workshop/Receipts/" + plan.WorkshopId.ToString(CultureInfo.InvariantCulture) + ".json";
                if (plan.Action == WorkshopAction.Remove) lines.Add(Row("receipt-delete", receipt, ordinal++, plan.ReceiptHash, "missing"));
                else {
                    string next = WorkshopPaths.Inside(directory, "New/" + ordinal.ToString(CultureInfo.InvariantCulture));
                    Directory.CreateDirectory(Path.GetDirectoryName(next));
                    File.WriteAllText(next, WorkshopPackageCodec.Write(new WorkshopReceipt {
                        WorkshopId = plan.WorkshopId.ToString(CultureInfo.InvariantCulture), PackageId = plan.PackageId, Version = plan.Version,
                        Files = plan.Package.Files, ModGuids = plan.Package.ModGuids, Dependencies = plan.Package.Dependencies, ManifestHash = plan.ManifestHash, TransactionId = operationId
                    }), new UTF8Encoding(false));
                    lines.Add(Row("receipt-write", receipt, ordinal++, plan.ReceiptHash, WorkshopInventory.Hash(next)));
                }
            }
            foreach (var plan in plans) Validate(plan);
            File.WriteAllLines(WorkshopPaths.Inside(directory, "operations.tsv"), lines, new UTF8Encoding(false));
            File.WriteAllLines(WorkshopPaths.Inside(directory, "diagnostics.txt"), warnings, new UTF8Encoding(false));
            File.WriteAllText(WorkshopPaths.Inside(directory, "operations.sha256"), WorkshopInventory.Hash(WorkshopPaths.Inside(directory, "operations.tsv")), new UTF8Encoding(false));
            return directory;
        }
        private static void Validate(WorkshopInstallPlan plan)
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
        }
        private static string Row(string kind, string target, int ordinal, string before, string after) =>
            string.Join("\t", kind, target, ordinal.ToString(CultureInfo.InvariantCulture), before, after);
    }
}

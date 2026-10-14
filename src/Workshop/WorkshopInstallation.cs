using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal static class WorkshopInstallation
    {
        internal static string StateRoot(string game) => WorkshopPaths.Inside(game, "_APIShared/Workshop");
        internal static string ReceiptPath(string game, ulong id) => WorkshopPaths.Inside(StateRoot(game), "Receipts/" + id.ToString(CultureInfo.InvariantCulture) + ".json");
        internal static string CurrentHash(string path) => File.Exists(path) ? WorkshopInventory.Hash(path) : "missing";
        internal static WorkshopReceipt ReadReceipt(string game, ulong id)
        {
            string path = ReceiptPath(game, id);
            if (!File.Exists(path)) return null;
            var receipt = WorkshopPackageCodec.Receipt(File.ReadAllText(path));
            if (WorkshopPackageCodec.Id(receipt.WorkshopId) != id) throw new InvalidDataException("Receipt ID mismatch.");
            return receipt;
        }
        internal static IEnumerable<WorkshopReceipt> Receipts(string game, ulong exceptId = 0)
        {
            string directory = WorkshopPaths.Inside(StateRoot(game), "Receipts");
            if (!Directory.Exists(directory)) yield break;
            foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
            {
                if (exceptId != 0 && Path.GetFileNameWithoutExtension(path) == exceptId.ToString(CultureInfo.InvariantCulture)) continue;
                WorkshopPaths.CheckLinks(path);
                var receipt = WorkshopPackageCodec.Receipt(File.ReadAllText(path));
                if (Path.GetFileNameWithoutExtension(path) != receipt.WorkshopId) throw new InvalidDataException("Receipt filename mismatch.");
                yield return receipt;
            }
        }
        internal static WorkshopInstallPlan Prepare(string owner, string game, ulong id, string source, WorkshopAction action)
        {
            if (id == 0) throw new ArgumentException("A Steam Workshop ID is required.");
            if (!Enum.IsDefined(typeof(WorkshopAction), action)) throw new ArgumentException("Unknown Workshop action.");
            game = Path.GetFullPath(game); WorkshopPaths.CheckLinks(game);
            var warnings = new List<string>();
            WorkshopReceipt previous = null;
            try { previous = ReadReceipt(game, id); }
            catch (InvalidDataException) { if (action == WorkshopAction.Remove || action == WorkshopAction.Update) throw; warnings.Add("Invalid receipt: explicit installation will preserve unknown old files."); }
            WorkshopPackage package = null; string hash = null;
            if (action != WorkshopAction.Remove)
            {
                source = Path.GetFullPath(source);
                string manifest = WorkshopPaths.Inside(source, WorkshopPackage.ManifestFileName);
                package = WorkshopPackageCodec.Read(File.ReadAllText(manifest)); WorkshopPaths.Verify(package, source); hash = CurrentHash(manifest);
                if (previous != null && previous.PackageId != package.PackageId) throw new InvalidDataException("Subscribed package identity changed.");
                if (previous != null)
                {
                    int order = WorkshopPackageCodec.Version(package.Version).CompareTo(WorkshopPackageCodec.Version(previous.Version));
                    if (action == WorkshopAction.Install || action == WorkshopAction.Update && order <= 0 || action == WorkshopAction.Downgrade && order >= 0)
                        throw new InvalidOperationException("Use Update for a newer release, Reinstall for an explicit replacement, or Downgrade for an older release.");
                }
                else if (action == WorkshopAction.Update || action == WorkshopAction.Downgrade) throw new InvalidOperationException("No valid installation receipt.");
                foreach (string root in package.Roots)
                    if (Directory.Exists(WorkshopPaths.Inside(game, root))) warnings.Add("Existing installation will be backed up: " + root);
                AssertOwnership(game, id, package);
            }
            else if (previous == null) throw new InvalidOperationException("No valid receipt to remove.");
            var plan = new WorkshopInstallPlan {
                Owner = owner, GameRoot = game, Source = source, Package = package, Previous = previous,
                ManifestHash = hash, ReceiptHash = CurrentHash(ReceiptPath(game, id)), OperationId = Guid.NewGuid().ToString("N"),
                WorkshopId = id, Action = action, PackageId = package?.PackageId ?? previous.PackageId,
                Version = package?.Version ?? previous.Version, Diagnostics = warnings.AsReadOnly()
            };
            plan.Files = package?.Files ?? new WorkshopFile[0];
            var targets = plan.Files.ToDictionary(file => file.Path, file => CurrentHash(WorkshopPaths.Inside(game, file.Path)), StringComparer.OrdinalIgnoreCase);
            plan.RemovedFiles = Obsolete(plan, warnings.Add, targets).ToList().AsReadOnly();
            plan.TargetHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string,string>(targets);
            return plan;
        }
        internal static void AssertOwnership(string game, ulong id, WorkshopPackage package)
        {
            foreach (var other in Receipts(game, id))
            {
                if (WorkshopPackageCodec.Id(other.WorkshopId) == id) continue;
                if (other.PackageId.Equals(package.PackageId, StringComparison.OrdinalIgnoreCase) || other.ModGuids.Intersect(package.ModGuids, StringComparer.OrdinalIgnoreCase).Any() || other.Files.Any(f => package.Roots.Any(root => f.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))))
                    throw WorkshopDiagnostics.Blocked("Another installed Workshop item owns this package or plugin folder.");
            }
        }
        internal static IEnumerable<string> Obsolete(WorkshopInstallPlan plan, Action<string> warn, IDictionary<string, string> targets = null)
        {
            var candidates = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (plan.Previous != null)
                foreach (var file in plan.Previous.Files) candidates[file.Path] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { file.Sha256 };
            // Historical hashes are declarations, never permission for recursive folder deletion.
            if (plan.Package != null)
                foreach (var file in plan.Package.LegacyReleases.SelectMany(x => x.Files))
                {
                    if (!candidates.TryGetValue(file.Path, out var hashes)) candidates.Add(file.Path, hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    hashes.Add(file.Sha256);
                }
            var delivered = new HashSet<string>(plan.Package?.Files.Select(x => x.Path) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var pair in candidates.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (delivered.Contains(pair.Key)) continue;
                string target = WorkshopPaths.Inside(plan.GameRoot, pair.Key);
                if (!File.Exists(target)) continue;
                string observed = CurrentHash(target);
                if (!pair.Value.Contains(observed)) { warn("Preserved modified obsolete file: " + pair.Key); continue; }
                targets?.Add(pair.Key, observed);
                yield return pair.Key;
            }
        }
    }
}

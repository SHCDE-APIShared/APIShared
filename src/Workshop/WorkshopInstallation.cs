using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
            catch (InvalidDataException) { if (action == WorkshopAction.Remove || action == WorkshopAction.Update) throw; warnings.Add("Invalid receipt: the installation replaces the package folders completely; files not kept by the package are removed."); }
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
            plan.RemovedFiles = Removed(plan).ToList().AsReadOnly();
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
        // Preview of what an applied plan removes: the swap replaces whole plugin folders, so
        // every file in the affected roots that the new package neither delivers nor keeps
        // (apishared-keep.txt) disappears; a removal deletes the roots completely, and so does an
        // update for roots the new version no longer ships.
        internal static IEnumerable<string> Removed(WorkshopInstallPlan plan)
        {
            var roots = plan.Package == null
                ? plan.Previous.Files.Select(file => WorkshopStaging.PluginRoot(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : plan.Package.Roots.Concat(WorkshopStaging.DroppedRoots(plan)).ToList();
            var delivered = new HashSet<string>(plan.Package?.Files.Select(x => x.Path) ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.OrderBy(x => x, StringComparer.Ordinal))
            {
                string target = WorkshopPaths.Inside(plan.GameRoot, root);
                if (!Directory.Exists(target)) continue;
                var keep = plan.Package == null ? new List<Install.InstallKeep.Entry>()
                    : Install.InstallKeep.Read(WorkshopPaths.Inside(WorkshopPaths.Inside(plan.Source, "Payload"), root));
                foreach (string relative in Install.InstallTree.Files(target))
                {
                    string path = root + "/" + relative;
                    if (delivered.Contains(path) || Install.InstallKeep.Covers(keep, relative)) continue;
                    yield return path;
                }
            }
        }
    }
}

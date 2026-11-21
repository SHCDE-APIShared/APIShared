using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    internal enum WorkshopLocalStatus { Missing, Modified, Verified }

    internal sealed class WorkshopLocalState
    {
        internal WorkshopLocalStatus Status { get; }
        internal string Difference { get; }

        private WorkshopLocalState(WorkshopLocalStatus status, string difference = null)
        {
            Status = status;
            Difference = difference;
        }

        internal static WorkshopLocalState Inspect(string game, WorkshopCatalogItem item)
        {
            if (item.Receipt == null) {
                bool present = item.Package != null && item.Package.Files.Any(file => File.Exists(WorkshopPaths.Inside(game, file.Path)));
                return new WorkshopLocalState(present ? WorkshopLocalStatus.Modified : WorkshopLocalStatus.Missing);
            }
            return InspectReceipt(game, item.Receipt);
        }

        internal static WorkshopLocalState InspectReceipt(string game, WorkshopReceipt receipt)
        {
            string difference = null;
            bool found = false;
            foreach (var file in receipt.Files) {
                string path = WorkshopPaths.Inside(game, file.Path);
                bool exists = File.Exists(path);
                found |= exists;
                if (difference == null) {
                    if (!exists) difference = "missing " + file.Path;
                    else if (new FileInfo(path).Length != file.Size || WorkshopInventory.Hash(path) != file.Sha256)
                        difference = "changed " + file.Path;
                }
                if (difference != null && found) break;
            }
            if (!found) return new WorkshopLocalState(WorkshopLocalStatus.Missing, difference);
            return new WorkshopLocalState(difference == null ? WorkshopLocalStatus.Verified : WorkshopLocalStatus.Modified, difference);
        }

        // Startup offers (the dialog remains the consent). An unsubscribed receipted installation
        // is removed. A subscribed receipted package is offered when a newer release exists, and
        // also when its delivered files are missing or changed: a clean installation from Steam
        // repairs it, for example after an interrupted installation of the former updater.
        // repair: the former external updater was interrupted while installing this item
        // (WorkshopStartup.InspectFormerUpdaterState); a clean installation is offered regardless
        // of disabled offers and of a missing receipt until the files match the package.
        internal static WorkshopAction? StartupAction(WorkshopCatalogItem item, WorkshopLocalStatus status, bool offersEnabled, bool repair = false)
        {
            // Taken over by an external mod manager: APIShared changes nothing for this item.
            if (item.ManagedBy != null) return null;
            // The catalog describes an unsubscribed installation by its receipt version only;
            // it attaches the receipt object to subscribed items alone.
            if (!item.IsSubscribed && item.InstalledVersion != null) return WorkshopAction.Remove;
            if (repair && item.IsSubscribed && item.CanInstall && item.Package != null && status != WorkshopLocalStatus.Verified) return ManualAction(item);
            if (!offersEnabled || !item.CanInstall || item.Package == null) return null;
            // Subscribed, but not installed by APIShared yet: offer the first installation (it also
            // adopts files that already match). Folders owned by other mods block it in planning.
            if (item.Receipt == null) return item.IsSubscribed ? WorkshopAction.Install : (WorkshopAction?)null;
            if (status != WorkshopLocalStatus.Verified) return ManualAction(item);
            return WorkshopPackageCodec.Version(item.Package.Version) > WorkshopPackageCodec.Version(item.Receipt.Version) ? WorkshopAction.Update : (WorkshopAction?)null;
        }
        internal static WorkshopAction ManualAction(WorkshopCatalogItem item)
        {
            if (item.Receipt == null) return WorkshopAction.Install;
            int order = WorkshopPackageCodec.Version(item.Package.Version).CompareTo(WorkshopPackageCodec.Version(item.Receipt.Version));
            return order < 0 ? WorkshopAction.Downgrade : order > 0 ? WorkshopAction.Update : WorkshopAction.Reinstall;
        }

        // Comma-separated Workshop IDs in the APIShared configuration. Unparseable entries
        // and zero are ignored, so a hand-edited value can never select an item by accident.
        internal static HashSet<ulong> ParseIds(string value) => new HashSet<ulong>((value ?? string.Empty).Split(',')
            .Select(text => ulong.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) ? id : 0)
            .Where(id => id != 0));
        internal static string FormatIds(IEnumerable<ulong> ids) =>
            string.Join(",", ids.Distinct().OrderBy(id => id).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Former transaction format: journal.log gains "applying" when files start to change and
        // "committed" or "restored" when finished; operations.tsv rows "receipt-write|receipt-delete
        // <tab> _APIShared/Workshop/Receipts/<id>.json ..." name the items. Null means the
        // old transaction could not be classified. Keep its backups.
        internal static IReadOnlyList<ulong> ReadFormerTransaction(string transaction)
        {
            try {
                string journal = Path.Combine(transaction, "journal.log"), operations = Path.Combine(transaction, "operations.tsv");
                if (!File.Exists(journal)) return null;
                var steps = File.ReadAllLines(journal);
                if (steps.Contains("committed") || steps.Contains("restored")) return Array.Empty<ulong>();
                if (!steps.Contains("applying")) return steps.Contains("prepared") ? Array.Empty<ulong>() : null;
                var ids = File.ReadAllLines(operations).Select(line => line.Split('\t'))
                    .Where(row => row.Length > 1 && row[0].StartsWith("receipt-", StringComparison.Ordinal))
                    .Select(row => ulong.TryParse(Path.GetFileNameWithoutExtension(row[1]), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) ? id : 0)
                    .ToArray();
                return ids.Length == 0 || ids.Contains(0ul) ? null : ids;
            } catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        internal static string RepairPath(string game) => Path.Combine(new Install.InstallPaths(game).Work, "repair.txt");

        internal static HashSet<ulong> ReadRepairs(string game)
        {
            string path = RepairPath(game);
            return File.Exists(path) ? ParseIds(File.ReadAllText(path)) : new HashSet<ulong>();
        }

        internal static void WriteRepairs(string game, HashSet<ulong> ids)
        {
            string path = RepairPath(game);
            if (ids.Count == 0) { Install.InstallTree.DeleteFileIfExists(path); return; }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + ".tmp";
            Install.InstallTree.WriteDurable(temporary, FormatIds(ids) + "\r\n");
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        internal static IReadOnlyList<string> ReceiptRoots(WorkshopReceipt receipt) =>
            receipt.Files.Select(file => string.Join("/", file.Path.Split('/').Take(3)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

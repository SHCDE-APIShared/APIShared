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

        internal static WorkshopAction? StartupAction(WorkshopCatalogItem item, WorkshopLocalStatus status, bool offersEnabled)
        {
            // The catalog describes an unsubscribed installation by its receipt version only;
            // it attaches the receipt object to subscribed items alone.
            if (!item.IsSubscribed && item.InstalledVersion != null) return WorkshopAction.Remove;
            if (offersEnabled && item.CanInstall && item.Package != null && item.Receipt != null &&
                status == WorkshopLocalStatus.Verified &&
                WorkshopPackageCodec.Version(item.Package.Version) > WorkshopPackageCodec.Version(item.Receipt.Version))
                return WorkshopAction.Update;
            return null;
        }

        internal static WorkshopAction ManualAction(WorkshopCatalogItem item)
        {
            if (item.Receipt == null) return WorkshopAction.Install;
            int order = WorkshopPackageCodec.Version(item.Package.Version).CompareTo(WorkshopPackageCodec.Version(item.Receipt.Version));
            return order < 0 ? WorkshopAction.Downgrade : order > 0 ? WorkshopAction.Update : WorkshopAction.Reinstall;
        }

        internal static IReadOnlyList<string> ReceiptRoots(WorkshopReceipt receipt) =>
            receipt.Files.Select(file => string.Join("/", file.Path.Split('/').Take(3)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

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

        internal static WorkshopAction? StartupAction(WorkshopCatalogItem item, WorkshopLocalStatus status, bool offersEnabled, bool requested = false)
        {
            // The catalog describes an unsubscribed installation by its receipt version only;
            // it attaches the receipt object to subscribed items alone.
            if (!item.IsSubscribed && item.InstalledVersion != null) return WorkshopAction.Remove;
            // An installation requested in Mod Options is offered like the explicit choice it
            // replaces: independent of the local state and of disabled update offers. The
            // startup dialog remains the consent.
            if (requested && item.IsSubscribed && item.CanInstall && item.Package != null) return ManualAction(item);
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

        // Comma-separated Workshop IDs in the APIShared configuration. Unparseable entries
        // and zero are ignored, so a hand-edited value can never select an item by accident.
        internal static HashSet<ulong> ParseIds(string value) => new HashSet<ulong>((value ?? string.Empty).Split(',')
            .Select(text => ulong.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) ? id : 0)
            .Where(id => id != 0));
        internal static string FormatIds(IEnumerable<ulong> ids) =>
            string.Join(",", ids.Distinct().OrderBy(id => id).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        // Installation requests: "<id>" waits for the next startup offer; "<id>:<transaction>"
        // was handed off to that updater transaction and is resolved by its outcome on the
        // following start. Malformed entries are ignored like malformed IDs.
        internal static Dictionary<ulong, string> ParseRequests(string value)
        {
            var result = new Dictionary<ulong, string>();
            foreach (string entry in (value ?? string.Empty).Split(',')) {
                string[] parts = entry.Trim().Split(':');
                if (parts.Length > 2 || !ulong.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out ulong id) || id == 0) continue;
                string transaction = parts.Length == 2 ? parts[1] : null;
                if (transaction != null && !System.Text.RegularExpressions.Regex.IsMatch(transaction, "^[a-f0-9]{32}$")) continue;
                // Duplicates are never written; if hand-edited, a pending entry wins (offer again).
                if (!result.ContainsKey(id) || transaction == null) result[id] = transaction;
            }
            return result;
        }
        internal static string FormatRequests(IReadOnlyDictionary<ulong, string> requests) =>
            string.Join(",", requests.OrderBy(pair => pair.Key).Select(pair => pair.Key.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                (pair.Value == null ? "" : ":" + pair.Value)));

        internal enum RequestOutcome { Fulfilled, Offer }
        // Outcome of a handed-off request from its transaction journal. A committed
        // installation fulfils it, and so does a committed complete repair that superseded
        // the interrupted transaction (it reinstalled the item from Steam; the original
        // journal never receives 'committed'). A rollback, cancellation, missing transaction
        // or an updater that never applied makes the request pending again, so the player is
        // asked again instead of losing the request silently. Other interrupted transactions
        // never reach this decision: the recovery dialog runs before installation offers.
        internal static RequestOutcome ResolveRequest(string[] journal, bool superseded = false) =>
            superseded || journal != null && journal.Contains("committed") ? RequestOutcome.Fulfilled : RequestOutcome.Offer;

        internal static IReadOnlyList<string> ReceiptRoots(WorkshopReceipt receipt) =>
            receipt.Files.Select(file => string.Join("/", file.Path.Split('/').Take(3)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
}

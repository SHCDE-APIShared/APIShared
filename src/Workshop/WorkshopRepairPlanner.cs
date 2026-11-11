using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Complete repair is a clean reinstallation from the Steam download. It is the
    // fallback that always exists when rollback is unavailable:
    // - every affected subscribed package is rewritten from its current inventory;
    // - stale assemblies, files of earlier releases and files touched by the
    //   interrupted transaction are removed from the affected plugin folders;
    // - affected packages that are no longer subscribed are removed the same way.
    // Settings (config/LobbyModSettings) and other unknown data files are kept, and
    // the updater backs up every file it replaces or deletes before the first change.
    // This class only plans file operations; Steam state and consent are the caller's.
    internal static class WorkshopRepairPlanner
    {
        internal sealed class Scope
        {
            internal readonly HashSet<ulong> Items = new HashSet<ulong>();
            // Interrupted payload targets per plugin root ("BepInEx/plugins/<folder>").
            internal readonly Dictionary<string, HashSet<string>> RootTargets =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        }

        internal static string Root(string relative)
        {
            string[] parts = relative.Split('/');
            if (parts.Length < 4) throw new InvalidDataException("Expected a plugin file: " + relative);
            return string.Join("/", parts.Take(3));
        }

        internal static Scope Collect(string game, IEnumerable<WorkshopInterruptedTransaction> readable)
        {
            var scope = new Scope();
            foreach (var entry in readable) {
                foreach (var row in WorkshopRecovery.ReadOperations(game, entry.Directory)) {
                    if (row[0].StartsWith("receipt-", StringComparison.Ordinal)) {
                        scope.Items.Add(ulong.Parse(Path.GetFileNameWithoutExtension(row[1]), CultureInfo.InvariantCulture));
                        continue;
                    }
                    string root = Root(row[1]);
                    if (!scope.RootTargets.TryGetValue(root, out var targets))
                        scope.RootTargets.Add(root, targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    targets.Add(row[1]);
                }
            }
            return scope;
        }

        // Plugin roots that a set of receipt or package files occupies.
        internal static IEnumerable<string> Roots(IEnumerable<WorkshopFile> files) =>
            files.Select(f => Root(f.Path)).Distinct(StringComparer.OrdinalIgnoreCase);

        // Files to remove from one plugin root so that only 'keep' (the current inventory,
        // empty for a removal) remains besides settings and unknown data files: every
        // assembly outside 'keep', existing files of earlier releases, and every interrupted
        // target. Protected settings paths and names the updater cannot address are never
        // returned.
        internal static IEnumerable<string> Cleanup(string game, string root, ISet<string> keep,
            IEnumerable<string> earlier, IEnumerable<string> interrupted)
        {
            var result = new SortedSet<string>(StringComparer.Ordinal);
            string directory = WorkshopPaths.Inside(game, root);
            string full = Path.GetFullPath(game).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (Directory.Exists(directory))
                foreach (string file in WorkshopPaths.Enumerate(directory))
                    if (Path.GetExtension(file).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                        Consider(file.Substring(full.Length).Replace('\\', '/'), false);
            foreach (string file in earlier ?? Enumerable.Empty<string>()) Consider(file, false);
            // An interrupted target may already be absent; its delete row still covers the
            // root, which the updater requires before it supersedes the old transaction.
            foreach (string file in interrupted ?? Enumerable.Empty<string>()) Consider(file, true);
            return result;

            void Consider(string relative, bool evenIfMissing)
            {
                if (!relative.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) || keep.Contains(relative) || !Removable(relative)) return;
                if (evenIfMissing || File.Exists(WorkshopPaths.Inside(game, relative))) result.Add(relative);
            }
        }

        internal static bool Removable(string relative)
        {
            try { WorkshopPaths.DeliveredPath(relative); return true; }
            catch (InvalidDataException) { return false; }
        }

        // Adds hash-guarded deletions to a plan. Paths the plan already writes or removes
        // are skipped; the current hash (or "missing") is the updater's precondition.
        internal static void AddRemovals(WorkshopInstallPlan plan, IEnumerable<string> files)
        {
            var written = new HashSet<string>(plan.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var removed = new List<string>(plan.RemovedFiles);
            var known = new HashSet<string>(removed, StringComparer.OrdinalIgnoreCase);
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in plan.TargetHashes) targets[pair.Key] = pair.Value;
            foreach (string file in files) {
                if (written.Contains(file) || !known.Add(file)) continue;
                // Keep a hash observed at planning time (e.g. an obsolete file) as precondition.
                if (!targets.ContainsKey(file)) targets.Add(file, WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(plan.GameRoot, file)));
                removed.Add(file);
            }
            plan.RemovedFiles = removed.AsReadOnly();
            plan.TargetHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(targets);
        }

        // Removal of an affected package that is no longer subscribed. A valid receipt uses
        // the ordinary removal plan; a missing or unreadable receipt still yields a plan whose
        // receipt-delete row covers the interrupted receipt (before and after may be "missing").
        internal static WorkshopInstallPlan Removal(string owner, string game, ulong id)
        {
            try { return WorkshopInstallation.Prepare(owner, game, id, null, WorkshopAction.Remove); }
            catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException) { }
            game = Path.GetFullPath(game);
            return new WorkshopInstallPlan {
                Owner = owner, GameRoot = game, OperationId = Guid.NewGuid().ToString("N"), WorkshopId = id,
                Action = WorkshopAction.Remove, PackageId = "Workshop " + id.ToString(CultureInfo.InvariantCulture), Version = "",
                ReceiptHash = WorkshopInstallation.CurrentHash(WorkshopInstallation.ReceiptPath(game, id)),
                Diagnostics = new string[0], Files = new WorkshopFile[0], RemovedFiles = new string[0],
                TargetHashes = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            };
        }

        // Completes a batch of reinstall plans (subscribed) and removal plans (unsubscribed):
        // each plan's plugin roots are cleaned, and interrupted roots that no plan owns
        // (a release moved its folder, or an unsubscribed package without receipt) are
        // cleaned completely by the first removal plan, else by the first plan.
        internal static void Complete(string game, IReadOnlyList<WorkshopInstallPlan> plans, Scope scope)
        {
            if (plans.Count == 0) return;
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans) {
                var keep = new HashSet<string>(plan.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
                var earlier = (plan.Previous?.Files ?? new WorkshopFile[0]).Select(f => f.Path)
                    .Concat(plan.Package?.LegacyReleases.SelectMany(r => r.Files).Select(f => f.Path) ?? Enumerable.Empty<string>()).ToArray();
                var roots = (plan.Package?.Roots ?? Enumerable.Empty<string>()).Concat(Roots(plan.Previous?.Files ?? new WorkshopFile[0]))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string root in roots) {
                    owned.Add(root);
                    scope.RootTargets.TryGetValue(root, out var interrupted);
                    AddRemovals(plan, Cleanup(game, root, keep, earlier, interrupted));
                }
            }
            var carrier = plans.FirstOrDefault(p => p.Action == WorkshopAction.Remove) ?? plans[0];
            foreach (var pair in scope.RootTargets.Where(p => !owned.Contains(p.Key)))
                AddRemovals(carrier, Cleanup(game, pair.Key, empty, null, pair.Value));
        }

        // Mirrors the updater's replacement check: every interrupted receipt is rewritten or
        // deleted by the batch, and every interrupted plugin root receives at least one row.
        internal static void AssertCoverage(string game, IReadOnlyList<WorkshopInterruptedTransaction> entries, IReadOnlyList<WorkshopInstallPlan> plans)
        {
            var receipts = new HashSet<string>(plans.Select(p => "_APIShared/Workshop/Receipts/" + p.WorkshopId.ToString(CultureInfo.InvariantCulture) + ".json"), StringComparer.OrdinalIgnoreCase);
            var targets = plans.SelectMany(p => p.Files.Select(f => f.Path).Concat(p.RemovedFiles)).ToArray();
            foreach (var entry in entries)
                foreach (var row in WorkshopRecovery.ReadOperations(game, entry.Directory)) {
                    if (row[0].StartsWith("receipt-", StringComparison.Ordinal)) {
                        if (!receipts.Contains(row[1])) throw new InvalidOperationException("The repair does not cover interrupted receipt: " + row[1]);
                    } else {
                        string prefix = Root(row[1]) + "/";
                        if (!targets.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("The repair does not cover interrupted plugin folder: " + Root(row[1]));
                    }
                }
        }
    }
}

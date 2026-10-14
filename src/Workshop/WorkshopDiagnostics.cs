using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Diagnostic metadata never changes the public exception type or update policy.
    internal enum WorkshopDiagnosticLevel { Info, Warning, Error }
    internal sealed class WorkshopOwnershipConflict
    {
        internal string Guid, Kind, Path, AllowedRoots;
        internal string Message => "An installed " + Kind + " outside this payload already owns GUID: " + Guid;
        public override string ToString() => "guid=" + Guid + "; kind=" + Kind + "; existingPath=" + Path + "; allowedRoots=" + AllowedRoots;
    }
    internal static class WorkshopDiagnostics
    {
        private const string BlockKey = "APIShared.Workshop.Blocked";
        private const string ConflictKey = "APIShared.Workshop.Conflicts";
        internal static void MarkBlocked(Exception exception) => exception.Data[BlockKey] = true;
        internal static InvalidOperationException Blocked(string message)
        {
            var exception = new InvalidOperationException(message);
            MarkBlocked(exception);
            return exception;
        }
        internal static WorkshopDiagnosticLevel Level(Exception exception) => exception.Data.Contains(BlockKey) ? WorkshopDiagnosticLevel.Warning : WorkshopDiagnosticLevel.Error;
        // Startup shows a modal notice only for error-level failures. Blocked conditions
        // (pending downloads, Extender staging, active mission, external management)
        // are routine, logged as warnings and retried on the next start.
        internal static bool UserVisible(Exception exception) => Level(exception) == WorkshopDiagnosticLevel.Error;
        internal static void Failure(Action<WorkshopDiagnosticLevel, string> write, string context, Exception exception)
        {
            if (exception.Data[ConflictKey] is IReadOnlyList<WorkshopOwnershipConflict> conflicts) {
                foreach (var conflict in conflicts) write(WorkshopDiagnosticLevel.Warning, context + "; " + conflict);
            } else write(Level(exception), context + "; " + (Level(exception) == WorkshopDiagnosticLevel.Error ? exception.ToString() : exception.Message));
        }
        internal static WorkshopOwnershipConflict Conflict(string game, IReadOnlyList<string> roots, string guid, string path, bool asset)
        {
            path = Path.GetFullPath(path);
            if (asset) path = path.TrimEnd(Path.DirectorySeparatorChar);
            var allowed = roots.Select(root => WorkshopPaths.Inside(game, root)).ToArray();
            if (allowed.Any(root => asset && path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return null;
            return new WorkshopOwnershipConflict { Guid = guid, Kind = asset ? "asset mod" : "plugin", Path = path, AllowedRoots = string.Join(" | ", allowed) };
        }
        internal static void RejectConflicts(IReadOnlyList<WorkshopOwnershipConflict> conflicts)
        {
            if (conflicts.Count == 0) return;
            var exception = Blocked(conflicts[0].Message);
            exception.Data[ConflictKey] = conflicts;
            throw exception;
        }
        internal static string Context(ulong id, string package, string installed, string available, string step) =>
            "Workshop step=" + step + "; item=" + id + "; package=" + (package ?? "unknown") + "; installed=" + (installed ?? "none") + "; available=" + (available ?? "unknown");
        internal static WorkshopDiagnosticLevel? Catalog(Action<WorkshopDiagnosticLevel, string> write, WorkshopCatalogItem item)
        {
            string context = Context(item.WorkshopId, item.Title, item.InstalledVersion, item.Package?.Version, "catalog");
            if (item.DiagnosticError != null) {
                Failure(write, context, item.DiagnosticError);
                return Level(item.DiagnosticError);
            }
            if (item.IsSubscribed && (item.Package == null || !item.CanInstall)) {
                write(WorkshopDiagnosticLevel.Warning, context + "; reason=" + item.Status + "; source=" + item.ContentRoot);
                return WorkshopDiagnosticLevel.Warning;
            }
            return null;
        }
        internal static string NoChangesResult(bool blocked, int failures) => failures > 0 ? "failed" : blocked ? "blocked" : "current";
        internal static void Outcome(Action<WorkshopDiagnosticLevel, string> write, string result, bool blocked, int failures) =>
            write(failures > 0 ? WorkshopDiagnosticLevel.Error : blocked ? WorkshopDiagnosticLevel.Warning : WorkshopDiagnosticLevel.Info,
                "Workshop startup result=" + result + "; blocked=" + blocked + "; technicalErrors=" + failures + ".");
    }
}

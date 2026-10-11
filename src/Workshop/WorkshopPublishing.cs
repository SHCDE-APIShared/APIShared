using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using APIShared.Internal;

namespace APIShared.Workshop
{
    internal static class WorkshopPublishing
    {
        internal static void VerifySnapshot(WorkshopUploadPlan plan)
        {
            if (plan.MapOnly) WorkshopMapPackage.Verify(plan.Package, plan.Snapshot);
            else { WorkshopPaths.Verify(plan.Package, plan.Snapshot); WorkshopBootstrapValidation.Verify(plan.Package, plan.Snapshot); }
        }
        internal static WorkshopUploadPlan Prepare(string owner, string state, WorkshopUploadOptions input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var options = new WorkshopUploadOptions {
                SourceDirectory = Path.GetFullPath(input.SourceDirectory), WorkshopId = input.WorkshopId, Title = input.Title,
                Description = input.Description, PreviewFile = Path.GetFullPath(input.PreviewFile), Visibility = input.Visibility,
                ChangeNote = input.ChangeNote ?? "", Tags = (input.Tags ?? new string[0]).ToArray(), MetadataOnly = input.MetadataOnly, Draft = input.Draft
            };
            if (string.IsNullOrWhiteSpace(options.Title) || string.IsNullOrWhiteSpace(options.Description)) throw new ArgumentException("Title and description are required.");
            if (!new[] { "Public", "FriendsOnly", "Private", "Unlisted" }.Contains(options.Visibility)) throw new ArgumentException("Invalid visibility.");
            if (options.MetadataOnly && options.WorkshopId == 0) throw new ArgumentException("Metadata-only updates require an existing item.");
            WorkshopPaths.CheckLinks(options.SourceDirectory); WorkshopPaths.CheckLinks(options.PreviewFile);
            string json = options.Draft == null ? File.ReadAllText(WorkshopPaths.Inside(options.SourceDirectory, WorkshopPackage.ManifestFileName)) : WorkshopPackageBuilder.Manifest(options.SourceDirectory, options.Draft);
            var package = WorkshopPackageCodec.Read(json); WorkshopPaths.Verify(package, options.SourceDirectory);
            string id = Guid.NewGuid().ToString("N");
            string snapshot = WorkshopPaths.Inside(state, "Uploads/" + id + "/Content");
            Directory.CreateDirectory(snapshot);
            foreach (var file in package.Files)
            {
                string target = WorkshopPaths.Inside(snapshot, "Payload/" + file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(WorkshopPaths.Inside(options.SourceDirectory, "Payload/" + file.Path), target);
            }
            File.WriteAllText(WorkshopPaths.Inside(snapshot, WorkshopPackage.ManifestFileName), json, new UTF8Encoding(false));
            if (package.Bootstrap != null) File.Copy(WorkshopPaths.Inside(options.SourceDirectory, package.Bootstrap.MapFile), WorkshopPaths.Inside(snapshot, package.Bootstrap.MapFile));
            string preview = WorkshopPaths.Inside(Path.GetDirectoryName(snapshot), "preview" + Path.GetExtension(options.PreviewFile));
            File.Copy(options.PreviewFile, preview); options.PreviewFile = preview;
            WorkshopPaths.Verify(package, snapshot);
            var files = WorkshopPaths.Enumerate(snapshot).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            string fingerprint = Fingerprint(snapshot);
            // An interrupted first upload can resume using the ID saved as soon as CreateItem succeeds.
            string history = HistoryPath(state, package.PackageId);
            if (File.Exists(history))
            {
                var previous = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history)));
                ulong previousId = WorkshopPackageCodec.Id(WorkshopPackageCodec.Text(previous, "WorkshopId"));
                if (options.WorkshopId == 0) options.WorkshopId = previousId;
                if (!options.MetadataOnly && options.WorkshopId == previousId && WorkshopPackageCodec.Text(previous, "Version", true).Length > 0 &&
                    WorkshopPackageCodec.Text(previous, "Fingerprint", true) != fingerprint &&
                    WorkshopPackageCodec.Version(package.Version) <= WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(previous, "Version")))
                    throw new InvalidOperationException("Changed content requires a higher published package version.");
            }
            return new WorkshopUploadPlan { Owner = owner, Snapshot = snapshot, Fingerprint = fingerprint, PreviewHash = WorkshopInventory.Hash(preview), Options = options,
                Package = package, OperationId = id, Files = files.Select(f => f.Substring(snapshot.Length + 1).Replace('\\', '/')).ToList().AsReadOnly() };
        }
        internal static string Fingerprint(string root)
        {
            string rows = string.Join("\n", WorkshopPaths.Enumerate(root).OrderBy(x => x, StringComparer.Ordinal).Select(f => f.Substring(root.Length + 1).Replace('\\', '/') + ":" + WorkshopInventory.Hash(f)));
            using (var sha = System.Security.Cryptography.SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(rows))).Replace("-", "").ToLowerInvariant();
        }
        internal static string HistoryPath(string state, string packageId)
        {
            WorkshopInventory.CheckGuid(packageId); return WorkshopPaths.Inside(state, "Publishing/" + packageId + ".json");
        }
        internal static void Save(string state, WorkshopUploadPlan plan, ulong id, bool completed)
        {
            string path = HistoryPath(state, plan.PackageId); Directory.CreateDirectory(Path.GetDirectoryName(path));
            // Keep the last successful version when persisting a newly allocated item ID.
            var row = File.Exists(path) ? WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(path))) : new Dictionary<string, object>();
            if (row.TryGetValue("WorkshopId", out var previousId) && previousId as string != id.ToString(CultureInfo.InvariantCulture)) row.Clear();
            row["WorkshopId"] = id.ToString(CultureInfo.InvariantCulture);
            if (!completed) row["CreatedByPublisher"] = true;
            if (completed) { row["Version"] = plan.Version; row["Fingerprint"] = plan.Fingerprint; }
            string temporary = path + ".tmp"; File.WriteAllText(temporary, WorkshopPackageCodec.Write(row), new UTF8Encoding(false));
            AtomicFileReplacement.Replace(temporary, path);
        }
    }
}

using APIShared.Workshop.Install;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Turns a confirmed plan into a staged container and request for APIShared's preloader
    // (src/Workshop/Install). Runs on a worker; no BepInEx, Unity or Steam types, so the tests
    // exercise exactly this code.
    internal static class WorkshopStaging
    {
        // request: false leaves publishing to the caller (InstallStaging.Publish), see StagePlansAsync.
        internal static void StagePlan(InstallPaths paths, WorkshopInstallPlan plan, Action<string> log, bool request = true)
        {
            if (plan.Action == WorkshopAction.Remove) {
                var targets = plan.Previous.Files.Select(file => PluginRoot(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                InstallStaging.StageRemove(paths, plan.WorkshopId, plan.PackageId, targets, log, request);
                return;
            }
            Validate(plan);
            var package = plan.Package;
            var roots = new List<InstallRoot>();
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in package.Roots) {
                string source = WorkshopPaths.Inside(WorkshopPaths.Inside(plan.Source, "Payload"), root);
                var files = package.Files.Where(file => file.Path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    .Select(file => new InstallFile(file.Path.Substring(root.Length + 1), file.Size, file.Sha256)).ToList();
                roots.Add(new InstallRoot(root, source, files));
                // Files covered by the package's keep list belong to the player after installation;
                // the receipt omits them so local status checks do not report them as modified.
                var entries = InstallKeep.Read(source);
                foreach (var file in files.Where(file => InstallKeep.Covers(entries, file.Path))) kept.Add(root + "/" + file.Path);
            }
            string receipt = WorkshopPackageCodec.Write(new WorkshopReceipt {
                WorkshopId = plan.WorkshopId.ToString(CultureInfo.InvariantCulture), PackageId = plan.PackageId, Version = plan.Version,
                Files = package.Files.Where(file => !kept.Contains(file.Path)).ToList(), ModGuids = package.ModGuids, Dependencies = package.Dependencies,
                ManifestHash = plan.ManifestHash, TransactionId = plan.OperationId
            });
            InstallStaging.StageInstall(paths, plan.WorkshopId, plan.PackageId, roots, receipt, log, DroppedRoots(plan).ToList(), request);
            // Steam may have replaced the item meanwhile; the copies were verified against the
            // manifest, and a changed manifest withdraws the staged change again.
            try { ValidateManifest(plan); }
            catch { InstallStaging.Withdraw(paths, plan.WorkshopId); throw; }
        }

        // Roots of the installed version that the new package no longer ships; the swap removes
        // them together with the update (the receipt lists every installed root).
        internal static IReadOnlyList<string> DroppedRoots(WorkshopInstallPlan plan)
        {
            if (plan.Previous == null || plan.Package == null) return new string[0];
            var shipped = new HashSet<string>(plan.Package.Roots, StringComparer.OrdinalIgnoreCase);
            return plan.Previous.Files.Select(file => PluginRoot(file.Path)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(root => !shipped.Contains(root)).OrderBy(root => root, StringComparer.Ordinal).ToList();
        }

        // Game-relative plugin root (BepInEx/plugins/<folder>) of a delivered file.
        internal static string PluginRoot(string path)
        {
            string[] parts = path.Split('/');
            if (parts.Length < 4 || parts[0] != "BepInEx" || parts[1] != "plugins") throw new InvalidDataException("Delivered file outside BepInEx/plugins: " + path);
            return "BepInEx/plugins/" + parts[2];
        }

        // Plan revalidation right before staging.
        internal static void Validate(WorkshopInstallPlan plan)
        {
            if (WorkshopInstallation.CurrentHash(WorkshopInstallation.ReceiptPath(plan.GameRoot, plan.WorkshopId)) != plan.ReceiptHash)
                throw new InvalidOperationException("Installation receipt changed; prepare a new plan.");
            if (plan.Package == null) return;
            ValidateManifest(plan);
            WorkshopPaths.Verify(plan.Package, plan.Source);
            WorkshopBootstrapValidation.Verify(plan.Package, plan.Source);
            WorkshopInstallation.AssertOwnership(plan.GameRoot, plan.WorkshopId, plan.Package);
        }

        internal static void ValidateManifest(WorkshopInstallPlan plan)
        {
            if (WorkshopInstallation.CurrentHash(WorkshopPaths.Inside(plan.Source, WorkshopPackage.ManifestFileName)) != plan.ManifestHash)
                throw WorkshopDiagnostics.Blocked("Workshop package changed; prepare and confirm a new plan.");
        }
    }
}

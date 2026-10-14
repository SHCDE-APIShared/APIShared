using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.Workshop
{
    // Pure prospective-state checks, shared by startup preparation and the final
    // handoff. A rejected dependency update cannot authorize dependent changes.
    internal static class WorkshopBatch
    {
        internal static Dictionary<string, Version> EndState(IEnumerable<WorkshopInstallPlan> plans, IReadOnlyDictionary<string, Version> installed)
        {
            var state = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in installed) state.Add(pair.Key, pair.Value);
            var delivered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var plan in plans) {
                foreach (string guid in plan.Previous?.ModGuids ?? new string[0]) state.Remove(guid);
                if (plan.Package == null) continue;
                if (!packages.Add(plan.PackageId)) throw WorkshopDiagnostics.Blocked("Duplicate batch package: " + plan.PackageId);
                foreach (string root in plan.Package.Roots)
                    if (!roots.Add(root)) throw WorkshopDiagnostics.Blocked("Overlapping batch plugin folder: " + root);
                foreach (var pair in plan.Package.ModVersions) {
                    if (!delivered.Add(pair.Key)) throw WorkshopDiagnostics.Blocked("Duplicate batch GUID: " + pair.Key);
                }
            }
            foreach (var plan in plans.Where(p => p.Package != null))
                foreach (var pair in plan.Package.ModVersions) state[pair.Key] = pair.Value;
            return state;
        }
        internal static void Dependencies(WorkshopPackage package, IReadOnlyDictionary<string, Version> state)
        {
            if (package == null) return;
            DependencyList(package.Dependencies, state);
        }
        internal static void Retained(WorkshopPackage package, WorkshopReceipt receipt, string manifestHash, IReadOnlyDictionary<string, Version> state, bool changesInstalledVersions)
        {
            if (receipt == null) return; // A deferred first installation owns no dependencies yet.
            var dependencies = receipt.Dependencies;
            if (dependencies == null && receipt.ManifestHash == manifestHash && receipt.PackageId == package.PackageId && receipt.Version == package.Version)
                dependencies = package.Dependencies;
            if (dependencies == null) {
                if (changesInstalledVersions) throw WorkshopDiagnostics.Blocked("Installed dependency evidence unavailable for " + receipt.PackageId + "; include its update before changing installed GUID versions.");
                return; // Adding unrelated GUIDs cannot break unknown old requirements.
            }
            DependencyList(dependencies, state);
        }
        private static void DependencyList(IEnumerable<WorkshopDependency> dependencies, IReadOnlyDictionary<string, Version> state)
        {
            foreach (var dependency in dependencies) {
                if (!state.TryGetValue(dependency.Guid, out var version)) throw WorkshopDiagnostics.Blocked("Missing dependency: " + dependency.Guid);
                if (version == null && (dependency.MinimumVersion.Length > 0 || dependency.MaximumVersion.Length > 0))
                    throw WorkshopDiagnostics.Blocked("Dependency has no comparable numeric version: " + dependency.Guid);
                if (dependency.MinimumVersion.Length > 0 && version < WorkshopPackageCodec.Version(dependency.MinimumVersion) ||
                    dependency.MaximumVersion.Length > 0 && version > WorkshopPackageCodec.Version(dependency.MaximumVersion))
                    throw WorkshopDiagnostics.Blocked("Dependency version mismatch: " + dependency.Guid);
            }
        }
    }
}

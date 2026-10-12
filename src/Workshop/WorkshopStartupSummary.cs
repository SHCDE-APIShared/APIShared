using APIShared.Internal;
using System;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Display evidence never substitutes for the receipt or authorizes removal.
    internal static class WorkshopStartupSummary
    {
        internal static string Describe(WorkshopInstallPlan plan, bool german)
        {
            string name = plan.PackageId, oldVersion = null;
            bool existing = plan.Package != null && plan.Package.Roots.Any(root => Directory.Exists(WorkshopPaths.Inside(plan.GameRoot, root)));
            bool uncertain = false;
            if (plan.Package != null && plan.Package.Roots.Count == 1) {
                string root = plan.Package.Roots[0];
                try {
                    string relative = root + "/info.json";
                    var delivered = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(WorkshopPaths.Inside(plan.Source, "Payload/" + relative))));
                    string guid = WorkshopPackageCodec.Text(delivered, "GUID");
                    if (!plan.Package.ModVersions.ContainsKey(guid)) throw new InvalidDataException("Host identity missing from payload.");
                    if (delivered.TryGetValue("Name", out var display) && display is string title && !string.IsNullOrWhiteSpace(title)) name = title;
                    if (existing) {
                        string installed = WorkshopPaths.Inside(plan.GameRoot, relative);
                        WorkshopPaths.CheckLinks(installed);
                        var row = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(installed)));
                        if (!guid.Equals(WorkshopPackageCodec.Text(row, "GUID"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Host identity mismatch.");
                        oldVersion = WorkshopPackageCodec.Text(row, "Version");
                        WorkshopPackageCodec.Version(oldVersion);
                    }
                } catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is FormatException) {
                    oldVersion = null; uncertain = existing;
                }
            } else if (existing) uncertain = true;

            string text;
            if (plan.Action == WorkshopAction.Install && existing) {
                text = german ? "bestehende Installation" : "existing installation";
                if (oldVersion != null) text += " " + oldVersion;
                text += german ? " auf " + plan.Version + " umstellen (mit Sicherung)." : " to " + plan.Version + " — migrate with backup.";
                if (uncertain) text += german ? " Bisherige Version/Identität nicht eindeutig nachweisbar." : " Previous version/identity could not be verified.";
                if (plan.Package.Bootstrap != null) text += german ? " Der Bootstrap ist bereits bereit; die Modpackdateien müssen noch übernommen werden." : " The bootstrap is ready; the package files still need to be applied.";
            } else if (plan.Action == WorkshopAction.Install) text = german ? "erstmals installieren: " + plan.Version : "first installation: " + plan.Version;
            else if (plan.Action == WorkshopAction.Update) text = german ? "aktualisieren: " + plan.Previous.Version + " auf " + plan.Version : "update: " + plan.Previous.Version + " to " + plan.Version;
            else text = german ? "Paketdateien entfernen: " + plan.Version : "remove package files: " + plan.Version;
            return name + ": " + text;
        }
    }
}

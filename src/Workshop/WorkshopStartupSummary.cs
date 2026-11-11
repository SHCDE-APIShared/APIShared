using APIShared.Internal;
using System;
using System.IO;
using System.Linq;

namespace APIShared.Workshop
{
    // Display evidence never substitutes for the receipt or authorizes removal.
    internal static class WorkshopStartupSummary
    {
        internal static string Describe(WorkshopInstallPlan plan, bool german) => Describe(plan, german ? "de-DE" : "en-US");
        internal static string Describe(WorkshopInstallPlan plan, string locale)
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
                text = Localization.UiText.ForLocale(locale, "Workshop.Summary.Existing");
                if (oldVersion != null) text += " " + oldVersion;
                text += Localization.UiText.ForLocale(locale, "Workshop.Summary.Migrate", "Version", plan.Version);
                if (uncertain) text += Localization.UiText.ForLocale(locale, "Workshop.Summary.Uncertain");
                if (plan.Package.Bootstrap != null) text += Localization.UiText.ForLocale(locale, "Workshop.Summary.BootstrapReady");
            } else if (plan.Action == WorkshopAction.Install) text = Localization.UiText.ForLocale(locale, "Workshop.Summary.Install", "Version", plan.Version);
            else if (plan.Action == WorkshopAction.Update) text = Localization.UiText.ForLocale(locale, "Workshop.Summary.Update", "Previous", plan.Previous.Version, "Version", plan.Version);
            else if (plan.Action == WorkshopAction.Reinstall) text = Localization.UiText.ForLocale(locale, "Workshop.Summary.Reinstall", "Version", plan.Version);
            else text = Localization.UiText.ForLocale(locale, "Workshop.Summary.Remove", "Version", plan.Version);
            return name + ": " + text;
        }
    }
}

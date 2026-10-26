using APIShared.Internal;
using APIShared.Workshop;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

internal static partial class Program
{
    private static string Input(string label, string previous = "", bool required = true)
    {
        while (true) {
            Console.Write(label + (previous.Length == 0 ? "" : " [" + previous.Replace("\n", " ") + "]") + ": ");
            string line = Console.ReadLine();
            if (line == null || line.Trim() == "/cancel") throw new OperationCanceledException("Cancelled.");
            line = line.Trim();
            if (line.Length == 0) line = previous;
            if (line.Length > 0 || !required) return line;
        }
    }
    private static string AuthorPath(string value)
    {
        value = value.Trim('"');
        // Proton exposes host paths through Z:. Accept normal Linux paths in
        // this Windows helper, without invoking a shell or altering arguments.
        if (value.StartsWith("/", StringComparison.Ordinal) && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STEAM_COMPAT_DATA_PATH")))
            value = "Z:" + value.Replace('/', '\\');
        return Path.GetFullPath(value);
    }
    private static string PathInput(string label, string previous = "") => AuthorPath(Input(label, previous));
    private static string ConfigText(Dictionary<string, object> config, string name, string fallback = "") => config.TryGetValue(name, out var value) && value is string text ? text : fallback;
    private static void SaveJson(string path, object value)
    {
        WorkshopPaths.CheckLinks(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + ".tmp";
        WorkshopPaths.CheckLinks(temporary);
        File.WriteAllText(temporary, DependencyFreeJson.Serialize(value), new UTF8Encoding(false));
        AtomicFileReplacement.Replace(temporary, path);
    }
    private static string SourceKey(string source)
    {
        using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(source.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant();
    }
    private static int Interactive(string[] args)
    {
        bool test = false, validate = false;
        foreach (string arg in args) {
            switch (arg.ToLowerInvariant()) {
                case "/test": test = true; break;
                case "/validate": validate = true; break;
                case "/nopause": break; // The launcher owns the closing pause.
                default: throw new ArgumentException("Unknown publisher argument: " + arg);
            }
        }
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.APISharedWorkshopPublisherCancelAbortsAny"));
        Console.WriteLine(APIShared.Localization.UiText.Get(test ? "Publisher.TestMode" : "Publisher.ProductionMode"));
        try {
            string account = "validation";
            if (!validate) {
                InitializeSteam();
                account = SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
                Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.SteamAccount") + SteamFriends.GetPersonaName() + " (" + account + ")");
            }
            // Account/mode scoped publishing IDs never leak into another account
            // or a test run. Mutable author data stays outside shipped files.
            string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APIShared", "WorkshopPublisher", "3024040", account, test ? "Test" : "Production");
            WorkshopPaths.CheckLinks(state);
            string last = Path.Combine(state, "last-source.txt");
            string source = PathInput(APIShared.Localization.UiText.Get("Publisher.PreparedPackageFolder"), File.Exists(last) ? File.ReadAllText(last) : "");
            WorkshopPaths.CheckLinks(source);
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            string settings = Path.Combine(state, "Settings", SourceKey(source) + ".json");
            var config = File.Exists(settings) ? WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(settings))) : new Dictionary<string, object>();
            string manifest = Path.Combine(source, WorkshopPackage.ManifestFileName);
            WorkshopPackageDraft draft = null;
            WorkshopPackage package;
            if (File.Exists(manifest)) package = WorkshopPackageCodec.Read(File.ReadAllText(manifest));
            else {
                string rememberedJson = ConfigText(config, "PreparedManifest");
                var remembered = rememberedJson.Length == 0 ? null : WorkshopPackageCodec.Read(rememberedJson);
                string packageId = Input(APIShared.Localization.UiText.Get("Publisher.Input.PackageId"), remembered?.PackageId ?? ""), version = Input(APIShared.Localization.UiText.Get("Publisher.PackageVersion"), remembered?.Version ?? "");
                string minimumApi = Input(APIShared.Localization.UiText.Get("Publisher.Input.MinimumAPISharedVersion"), remembered?.MinimumAPISharedVersion ?? "0.7.1");
                string minimumExtender = Input(APIShared.Localization.UiText.Get("Publisher.Input.MinimumScriptExtenderVersion"), remembered?.MinimumScriptExtenderVersion ?? "2.14.0");
                string maximumExtender = Input(APIShared.Localization.UiText.Get("Publisher.Input.MaximumScriptExtenderVersion"), remembered?.MaximumScriptExtenderVersion ?? "", false);
                var dependencies = new List<WorkshopDependency>();
                bool reuseDependencies = remembered != null && remembered.Dependencies.Count > 0 &&
                    Input(APIShared.Localization.UiText.Get("Publisher.ReuseSavedDependenciesYN"), "Y").Equals("Y", StringComparison.OrdinalIgnoreCase);
                if (reuseDependencies) dependencies.AddRange(remembered.Dependencies);
                else {
                    while (true) {
                        string guid = Input(APIShared.Localization.UiText.Get("Publisher.RequiredModGUIDEmptyDone"), "", false);
                        if (guid.Length == 0) break;
                        dependencies.Add(new WorkshopDependency(guid, Input(APIShared.Localization.UiText.Get("Publisher.Input.MinimumVersion"), "", false), Input(APIShared.Localization.UiText.Get("Publisher.Input.MaximumVersion"), "", false), Input(APIShared.Localization.UiText.Get("Publisher.Input.WorkshopId"), "", false)));
                    }
                }
                draft = new WorkshopPackageDraft(packageId, version, minimumApi, minimumExtender, maximumExtender, dependencies: dependencies);
                Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.NoBootstrapIsGeneratedByThis"));
                string generated = WorkshopPackageBuilder.Manifest(source, draft);
                package = WorkshopPackageCodec.Read(generated);
            }
            string id = ConfigText(config, "WorkshopId");
            string history = WorkshopPublishing.HistoryPath(state, package.PackageId);
            if (File.Exists(history)) {
                string remembered = WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId");
                if (id.Length > 0 && id != remembered) throw new InvalidDataException("Settings and saved publishing ID disagree.");
                id = remembered;
            }
            if (!validate) {
                var items = Pump(WorkshopSteamUpload.OwnedItems());
                Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.YourSHCDEWorkshopItems"));
                foreach (var item in items) Console.WriteLine(item.WorkshopId + " — " + item.Title);
            }
            id = Input(APIShared.Localization.UiText.Get("Publisher.WorkshopID0NewItem"), id.Length == 0 ? "0" : id);
            ulong selected = id == "0" ? 0 : WorkshopPackageCodec.Id(id);
            if (File.Exists(history) && WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId") != id)
                throw new InvalidOperationException(APIShared.Localization.UiText.Get("Publisher.ThisPackageAlreadyHasASaved"));
            if (test && selected != 0 && (!File.Exists(history) || WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId") != id))
                throw new InvalidOperationException("Test updates require an ID created in this account's Test publishing state.");
            string title = Input(APIShared.Localization.UiText.Get("Publisher.Title"), ConfigText(config, "Title", package.PackageId));
            string description = Input(APIShared.Localization.UiText.Get("Publisher.DescriptionOrTextFile"), ConfigText(config, "Description", title));
            if (description.StartsWith("@", StringComparison.Ordinal)) description = File.ReadAllText(AuthorPath(description.Substring(1)));
            string preview = PathInput(APIShared.Localization.UiText.Get("Publisher.PreviewImage"), ConfigText(config, "PreviewFile", Path.Combine(source, "preview.png")));
            string visibility = test ? "Private" : Input(APIShared.Localization.UiText.Get("Publisher.Input.Private"), ConfigText(config, "Visibility", "Private"));
            string note = Input(APIShared.Localization.UiText.Get("Publisher.ChangeNote"), "", false);
            var options = new WorkshopUploadOptions { SourceDirectory = source, WorkshopId = selected, Title = title, Description = description, PreviewFile = preview, Visibility = visibility, ChangeNote = note, Draft = draft };
            var plan = WorkshopPublishing.Prepare("offline-publisher", state, options);
            activePlan = plan;
            WorkshopPublishing.VerifySnapshot(plan);
            resultPath = Path.Combine(state, "Results", plan.OperationId + ".json");
            WorkshopPublisherContext.StateRoot = state;
            if (!validate) InspectPlan(plan);
            Console.WriteLine(package.PackageId + " " + package.Version + " — Workshop " + plan.WorkshopId);
            Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.Requirements", "Api", package.MinimumAPISharedVersion, "Extender", package.MinimumScriptExtenderVersion));
            foreach (string file in plan.Files) Console.WriteLine("  " + file);
            foreach (var dependency in package.Dependencies) Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.Dependency", "Guid", dependency.Guid, "Minimum", dependency.MinimumVersion, "Maximum", dependency.MaximumVersion, "Workshop", dependency.WorkshopId));
            Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.Fingerprint" , "Value", plan.Fingerprint));
            if (package.Bootstrap == null) Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.AWarningBootstrapIsRecommendedFor"));
            if (validate) { Result("Validated", "Package", "No Steam initialization or publication.", plan); return 0; }
            Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.Terms"));
            if (Input(APIShared.Localization.UiText.Get("Publisher.TypeUPLOADToPublish"), "", false) != "UPLOAD") return 2;
            SaveJson(settings, new { WorkshopId = plan.WorkshopId == 0 ? "" : plan.WorkshopId.ToString(), Title = title, Description = description, PreviewFile = preview, Visibility = visibility,
                PreparedManifest = File.ReadAllText(Path.Combine(plan.Snapshot, WorkshopPackage.ManifestFileName)) });
            Directory.CreateDirectory(state); WorkshopPaths.CheckLinks(last); File.WriteAllText(last, source, new UTF8Encoding(false));
            return PublishPlan(plan);
        } catch (OperationCanceledException) { Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.CancelledNothingSubmitted")); return 2; }
    }
    private sealed class ConsoleSteamBackend : WorkshopSteamBackend
    {
        internal override void ActivateGameOverlayToWebPage(string url, EActivateGameOverlayToWebPageMode mode = default(EActivateGameOverlayToWebPageMode))
        {
            Console.WriteLine(APIShared.Localization.UiText.Get("Publisher.OpenThisSteamPageIfYour") + url);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        }
    }
}

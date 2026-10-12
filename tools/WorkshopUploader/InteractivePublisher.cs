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
    private static bool German => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "de";
    private static string Local(string en, string de) => German ? de : en;
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
        Console.WriteLine(Local("APIShared Workshop Publisher — /cancel aborts any prompt", "APIShared Workshop Publisher — /cancel bricht jede Eingabe ab"));
        Console.WriteLine(test ? "TEST — PRIVATE / SEPARATE IDS" : "PRODUCTION");
        try {
            string account = "validation";
            if (!validate) {
                InitializeSteam();
                account = SteamUser.GetSteamID().m_SteamID.ToString(CultureInfo.InvariantCulture);
                Console.WriteLine(Local("Steam account: ", "Steam-Konto: ") + SteamFriends.GetPersonaName() + " (" + account + ")");
            }
            // Account/mode scoped publishing IDs never leak into another account
            // or a test run. Mutable author data stays outside shipped files.
            string state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "APIShared", "WorkshopPublisher", "3024040", account, test ? "Test" : "Production");
            WorkshopPaths.CheckLinks(state);
            string last = Path.Combine(state, "last-source.txt");
            string source = PathInput(Local("Prepared package folder", "Vorbereiteter Paketordner"), File.Exists(last) ? File.ReadAllText(last) : "");
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
                string packageId = Input("PackageId", remembered?.PackageId ?? ""), version = Input(Local("Package version", "Paketversion"), remembered?.Version ?? "");
                string minimumApi = Input("MinimumAPISharedVersion", remembered?.MinimumAPISharedVersion ?? "0.7.1");
                string minimumExtender = Input("MinimumScriptExtenderVersion", remembered?.MinimumScriptExtenderVersion ?? "2.14.0");
                string maximumExtender = Input("MaximumScriptExtenderVersion", remembered?.MaximumScriptExtenderVersion ?? "", false);
                var dependencies = new List<WorkshopDependency>();
                bool reuseDependencies = remembered != null && remembered.Dependencies.Count > 0 &&
                    Input(Local("Reuse saved dependencies? Y/N", "Gespeicherte Abhängigkeiten übernehmen? Y/N"), "Y").Equals("Y", StringComparison.OrdinalIgnoreCase);
                if (reuseDependencies) dependencies.AddRange(remembered.Dependencies);
                else {
                    while (true) {
                        string guid = Input(Local("Required mod GUID (empty = done)", "Benötigte Mod-GUID (leer = fertig)"), "", false);
                        if (guid.Length == 0) break;
                        dependencies.Add(new WorkshopDependency(guid, Input("MinimumVersion", "", false), Input("MaximumVersion", "", false), Input("WorkshopId", "", false)));
                    }
                }
                draft = new WorkshopPackageDraft(packageId, version, minimumApi, minimumExtender, maximumExtender, dependencies: dependencies);
                Console.WriteLine(Local("No bootstrap is generated by this assistant; use the supplied bootstrap template when needed.", "Dieser Assistent erzeugt keinen Bootstrap; bei Bedarf die mitgelieferte Bootstrap-Vorlage verwenden."));
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
                Console.WriteLine(Local("Your SHCDE Workshop items:", "Deine SHCDE-Workshop-Items:"));
                foreach (var item in items) Console.WriteLine(item.WorkshopId + " — " + item.Title);
            }
            id = Input(Local("Workshop ID (0 = new item)", "Workshop-ID (0 = neues Item)"), id.Length == 0 ? "0" : id);
            ulong selected = id == "0" ? 0 : WorkshopPackageCodec.Id(id);
            if (File.Exists(history) && WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId") != id)
                throw new InvalidOperationException(Local("This package already has a saved item ID. Use that ID to update or resume; use a distinct PackageId for a new package.",
                    "Für dieses Paket ist bereits eine Item-ID gespeichert. Zum Aktualisieren oder Fortsetzen diese ID verwenden; für ein neues Paket eine eigene PackageId verwenden."));
            if (test && selected != 0 && (!File.Exists(history) || WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId") != id))
                throw new InvalidOperationException("Test updates require an ID created in this account's Test publishing state.");
            string title = Input(Local("Title", "Titel"), ConfigText(config, "Title", package.PackageId));
            string description = Input(Local("Description (or @text-file)", "Beschreibung (oder @Textdatei)"), ConfigText(config, "Description", title));
            if (description.StartsWith("@", StringComparison.Ordinal)) description = File.ReadAllText(AuthorPath(description.Substring(1)));
            string preview = PathInput(Local("Preview image", "Vorschaubild"), ConfigText(config, "PreviewFile", Path.Combine(source, "preview.png")));
            string visibility = test ? "Private" : Input("Private / FriendsOnly / Unlisted / Public", ConfigText(config, "Visibility", "Private"));
            string note = Input(Local("Change note", "Änderungsnotiz"), "", false);
            var options = new WorkshopUploadOptions { SourceDirectory = source, WorkshopId = selected, Title = title, Description = description, PreviewFile = preview, Visibility = visibility, ChangeNote = note, Draft = draft };
            var plan = WorkshopPublishing.Prepare("offline-publisher", state, options);
            activePlan = plan;
            WorkshopPublishing.VerifySnapshot(plan);
            resultPath = Path.Combine(state, "Results", plan.OperationId + ".json");
            WorkshopPublisherContext.StateRoot = state;
            if (!validate) InspectPlan(plan);
            Console.WriteLine(package.PackageId + " " + package.Version + " — Workshop " + plan.WorkshopId);
            Console.WriteLine("APIShared >= " + package.MinimumAPISharedVersion + "; Extender >= " + package.MinimumScriptExtenderVersion);
            foreach (string file in plan.Files) Console.WriteLine("  " + file);
            foreach (var dependency in package.Dependencies) Console.WriteLine("Dependency: " + dependency.Guid + " " + dependency.MinimumVersion + " .. " + dependency.MaximumVersion + " / Workshop " + dependency.WorkshopId);
            Console.WriteLine("Fingerprint: " + plan.Fingerprint);
            if (package.Bootstrap == null) Console.WriteLine(Local("A warning bootstrap is recommended for users without APIShared.", "Ein Warn-Bootstrap wird für Nutzer ohne APIShared empfohlen."));
            if (validate) { Result("Validated", "Package", "No Steam initialization or publication.", plan); return 0; }
            Console.WriteLine("Steam Workshop terms: https://steamcommunity.com/sharedfiles/workshoplegalagreement");
            if (Input(Local("Type UPLOAD to publish", "UPLOAD eingeben zum Veröffentlichen"), "", false) != "UPLOAD") return 2;
            SaveJson(settings, new { WorkshopId = plan.WorkshopId == 0 ? "" : plan.WorkshopId.ToString(), Title = title, Description = description, PreviewFile = preview, Visibility = visibility,
                PreparedManifest = File.ReadAllText(Path.Combine(plan.Snapshot, WorkshopPackage.ManifestFileName)) });
            Directory.CreateDirectory(state); WorkshopPaths.CheckLinks(last); File.WriteAllText(last, source, new UTF8Encoding(false));
            return PublishPlan(plan);
        } catch (OperationCanceledException) { Console.WriteLine(Local("Cancelled; nothing submitted.", "Abgebrochen; nichts eingereicht.")); return 2; }
    }
    private sealed class ConsoleSteamBackend : WorkshopSteamBackend
    {
        internal override void ActivateGameOverlayToWebPage(string url, EActivateGameOverlayToWebPageMode mode = default(EActivateGameOverlayToWebPageMode))
        {
            Console.WriteLine(Local("Open this Steam page if your browser does not start: ", "Diese Steam-Seite öffnen, falls der Browser nicht startet: ") + url);
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
        }
    }
}

using APIShared.Internal;
using APIShared.Workshop;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

internal static partial class Program
{
    private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();
    private static readonly List<object> Queries = new List<object>();
    private static Dictionary<string, object> request;
    private static string resultPath;
    private static bool steamInitialized;
    private static WorkshopUploadPlan activePlan;
    private static ulong allocatedId;
    private static readonly string InvocationId = Guid.NewGuid().ToString("N");
    private static string Text(string key, bool optional = false) => WorkshopPackageCodec.Text(request, key, optional);
    private static int Main(string[] args)
    {
        try {
            if (args.Length > 0 && args[0] == "--interactive") return Interactive(args.Skip(1).ToArray());
            if (args.Length != 3 || !new[] { "--validate", "--inspect", "--publish" }.Contains(args[0])) throw new ArgumentException("Usage: WorkshopUploader --validate|--inspect|--publish request.json result.json");
            resultPath = Path.GetFullPath(args[2]);
            request = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(args[1])));
            WorkshopPackageCodec.Schema(request);
            if (Text("Mode") != "Test" && Text("Mode") != "Production") throw new InvalidDataException("Invalid publication mode.");
            var plan = Prepare();
            activePlan = plan;
            WorkshopPublishing.VerifySnapshot(plan);
            if (Text("Mode") == "Test") {
                if (plan.Options.Visibility != "Private" || plan.PreservePresentation) throw new InvalidDataException("Test uploads must explicitly set private visibility.");
                foreach (object raw in (System.Collections.IList)request["ForbiddenItemIds"])
                    if (WorkshopPackageCodec.Id(raw as string) == plan.WorkshopId) throw new InvalidDataException("Production item ID is forbidden in tests.");
            }
            if (args[0] == "--validate") { Result("Validated", "Package", "Complete package snapshot checked.", plan); return 0; }
            InitializeSteam();
            WorkshopPublisherContext.StateRoot = Text("StateRoot");
            InspectPlan(plan);
            if (args[0] == "--inspect") { Result("Inspected", "SteamOwnership", "Item and prerequisites checked.", plan); return 0; }
            return PublishPlan(plan);
        } catch (Exception ex) {
            if (resultPath != null) {
                if (activePlan != null) Result("Failed", "Publisher", ex.Message, activePlan, allocatedId);
                else WriteResult(new { SchemaVersion = 1, Status = "Failed", Step = "Package", Diagnostic = ex.Message, OperationId = InvocationId, WorkshopId = "" });
            }
            Console.Error.WriteLine(ex); return 1;
        } finally { if (steamInitialized) SteamAPI.Shutdown(); }
    }
    private static void InitializeSteam()
    {
        if (steamInitialized) return;
            Environment.SetEnvironmentVariable("SteamAppId", "3024040");
            if (!SteamAPI.Init()) throw new IOException("SteamAPI.Init failed; start Steam and sign in.");
            steamInitialized = true;
            int thread = Thread.CurrentThread.ManagedThreadId;
            WorkshopPublisherContext.RequireSteam = () => { if (Thread.CurrentThread.ManagedThreadId != thread) throw new InvalidOperationException("Steam call escaped its owning thread."); };
            WorkshopPublisherContext.Enqueue = action => { Queue.Enqueue(action); return true; };
            WorkshopPublisherContext.Log = message => Console.Error.WriteLine(message);
        if (!SteamUser.BLoggedOn()) throw new IOException("Steam is offline; connect the signed-in account first.");
        WorkshopSteamUpload.Backend = new ConsoleSteamBackend();
    }
    private static void InspectPlan(WorkshopUploadPlan plan)
    {
            if (plan.WorkshopId != 0) {
                var details = Pump(Inspect(plan.WorkshopId));
                if (details.Owner != SteamUser.GetSteamID().m_SteamID) throw new InvalidOperationException("The selected Workshop item belongs to another Steam account.");
                if (details.Metadata.Length > 0) {
                    var metadata = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(details.Metadata));
                    if (WorkshopPackageCodec.Text(metadata, "PackageId") != plan.PackageId) throw new InvalidDataException("Workshop item belongs to a different package.");
                    if (WorkshopPackageCodec.Text(metadata, "Fingerprint") != plan.Fingerprint && WorkshopPackageCodec.Version(plan.Version) <= WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(metadata, "Version"))) throw new InvalidOperationException("Changed content requires a higher package version.");
                }
            }
            if (plan.PackageId == "SerpsMods_Serp") {
                var dependency = plan.Package.Dependencies.Single(d => d.Guid == "APIShared_Serp");
                var api = Pump(Inspect(WorkshopPackageCodec.Id(dependency.WorkshopId)));
                var metadata = WorkshopPackageCodec.Object(DependencyFreeJson.Parse(api.Metadata));
                if (WorkshopPackageCodec.Text(metadata, "PackageId") != "APIShared_Serp" || WorkshopPackageCodec.Version(WorkshopPackageCodec.Text(metadata, "Version")) < WorkshopPackageCodec.Version(dependency.MinimumVersion)) throw new InvalidOperationException("Upload the required APIShared version first.");
            }
    }
    private static int PublishPlan(WorkshopUploadPlan plan)
    {
            var operation = WorkshopSteamUpload.Publish("offline-publisher", plan);
            operation.Changed += current => { allocatedId = current.WorkshopId; Result(current.State.ToString(), current.Step, current.Diagnostic, plan, current.WorkshopId); };
            DateTime started = DateTime.UtcNow;
            DateTime nextProgress = started.AddSeconds(2);
            while (operation.State != WorkshopOperationState.Succeeded && operation.State != WorkshopOperationState.Failed) {
                Tick(); Thread.Sleep(25);
                if (DateTime.UtcNow >= nextProgress) { WorkshopSteamUpload.RefreshProgress(); nextProgress = DateTime.UtcNow.AddSeconds(2); }
                if (DateTime.UtcNow - started > TimeSpan.FromMinutes(30)) throw new TimeoutException("Upload completion timed out. Inspect saved item ID and remote state before retrying.");
            }
            Result(operation.State.ToString(), operation.Step, operation.Diagnostic, plan, operation.WorkshopId);
            return operation.State == WorkshopOperationState.Succeeded ? 0 : 1;
    }
    private static WorkshopUploadPlan Prepare()
    {
        string source = Path.GetFullPath(Text("SourceDirectory")), state = Path.GetFullPath(Text("StateRoot"));
        var options = new WorkshopUploadOptions { SourceDirectory = source, PreviewFile = Text("PreviewFile"), Title = Text("Title"), Description = Text("Description"),
            ChangeNote = Text("ChangeNote"), Visibility = Text("Visibility"), WorkshopId = Text("WorkshopId", true).Length == 0 ? 0 : WorkshopPackageCodec.Id(Text("WorkshopId")) };
        WorkshopUploadPlan plan;
        if (Text("PackageId") == "APIShared_Serp") {
            string id = Guid.NewGuid().ToString("N"), content = WorkshopPaths.Inside(state, "Uploads/" + id + "/Content"); Directory.CreateDirectory(content);
            File.Copy(WorkshopPaths.Inside(source, "APIShared.map"), Path.Combine(content, "APIShared.map"));
            string preview = WorkshopPaths.Inside(state, "Uploads/" + id + "/preview.png"); WorkshopPaths.CheckLinks(options.PreviewFile); File.Copy(options.PreviewFile, preview); options.PreviewFile = preview;
            plan = new WorkshopUploadPlan { Owner = "offline-publisher", MapOnly = true, OperationId = id, Options = options, Snapshot = content,
                Package = new WorkshopPackage { PackageId = "APIShared_Serp", Version = Text("Version"), Dependencies = new WorkshopDependency[0] },
                Files = new[] { "APIShared.map" }, Fingerprint = WorkshopPublishing.Fingerprint(content), PreviewHash = WorkshopInventory.Hash(preview) };
            string history = WorkshopPublishing.HistoryPath(state, plan.PackageId);
            if (options.WorkshopId == 0 && File.Exists(history)) options.WorkshopId = WorkshopPackageCodec.Id(WorkshopPackageCodec.Text(WorkshopPackageCodec.Object(DependencyFreeJson.Parse(File.ReadAllText(history))), "WorkshopId"));
        } else plan = WorkshopPublishing.Prepare("offline-publisher", state, options);
        if (plan.PackageId != Text("PackageId") || plan.Version != Text("Version")) throw new InvalidDataException("Preparation proof does not match the package.");
        if (request.TryGetValue("ExpectedFiles", out var expectedFiles)) {
            var files = (System.Collections.IList)expectedFiles;
            if (files.Count != plan.Files.Count + 1) throw new InvalidDataException("Preparation proof file count differs from the snapshot.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object raw in files) {
                var row = WorkshopPackageCodec.Object(raw); string relative = WorkshopPackageCodec.Text(row, "Path");
                if (!seen.Add(relative)) throw new InvalidDataException("Duplicate preparation proof path.");
                string path = relative == "preview.png" ? options.PreviewFile : WorkshopPaths.Inside(plan.Snapshot, relative);
                if (new FileInfo(path).Length != Convert.ToInt64(row["Size"], CultureInfo.InvariantCulture) || WorkshopInventory.Hash(path) != WorkshopPackageCodec.Text(row, "Sha256")) throw new InvalidDataException("Snapshot changed since preparation: " + relative);
            }
        }
        plan.PreservePresentation = Text("Mode") != "Test" && request.TryGetValue("PreservePresentation", out var preserve) && preserve is bool flag && flag;
        if (request.TryGetValue("Fingerprint", out var fingerprint) && fingerprint as string != plan.Fingerprint) throw new InvalidDataException("Prepared content fingerprint changed.");
        return plan;
    }
    private sealed class Item { internal ulong Owner; internal string Metadata; }
    private static Task<Item> Inspect(ulong id)
    {
        var completion = new TaskCompletionSource<Item>();
        var query = SteamUGC.CreateQueryUGCDetailsRequest(new[] { new PublishedFileId_t(id) }, 1);
        if (query.m_UGCQueryHandle == ulong.MaxValue) throw new IOException("Cannot allocate item query.");
        if (query.m_UGCQueryHandle == ulong.MaxValue) throw new IOException("Cannot allocate item query.");
        CallResult<SteamUGCQueryCompleted_t> callback = null;
        try {
            if (!SteamUGC.SetReturnMetadata(query, true)) throw new IOException("Cannot request item metadata.");
            callback = CallResult<SteamUGCQueryCompleted_t>.Create((result, io) => {
                try {
                    if (io || result.m_eResult != EResult.k_EResultOK || result.m_unNumResultsReturned != 1 || !SteamUGC.GetQueryUGCResult(query, 0, out var details) || details.m_eResult != EResult.k_EResultOK || details.m_nPublishedFileId.m_PublishedFileId != id || details.m_nConsumerAppID.m_AppId != 3024040) throw new IOException("Cannot verify Workshop item " + id + ".");
                    string metadata = "";
                    if (!SteamUGC.GetQueryUGCMetadata(query, 0, out metadata, 8192)) metadata = "";
                    if (!SteamUGC.ReleaseQueryUGCRequest(query)) throw new IOException("Cannot release item query.");
                    completion.TrySetResult(new Item { Owner = details.m_ulSteamIDOwner, Metadata = metadata ?? "" });
                } catch (Exception ex) { SteamUGC.ReleaseQueryUGCRequest(query); completion.TrySetException(ex); }
                finally { Queries.Remove(callback); }
            });
            Queries.Add(callback); var call = SteamUGC.SendQueryUGCRequest(query);
            if (call.m_SteamAPICall == 0) throw new IOException("Cannot start item query."); callback.Set(call); return completion.Task;
        } catch { SteamUGC.ReleaseQueryUGCRequest(query); if (callback != null) Queries.Remove(callback); throw; }
    }
    private static T Pump<T>(Task<T> task)
    {
        DateTime started = DateTime.UtcNow;
        while (!task.IsCompleted) { Tick(); Thread.Sleep(25); if (DateTime.UtcNow - started > TimeSpan.FromSeconds(60)) throw new TimeoutException("Steam query timed out."); }
        return task.GetAwaiter().GetResult();
    }
    private static void Tick() { SteamAPI.RunCallbacks(); while (Queue.TryDequeue(out Action action)) action(); }
    private static void Result(string status, string step, string diagnostic, WorkshopUploadPlan plan, ulong id = 0) => WriteResult(new {
        SchemaVersion = 1, Status = status, Step = step, Diagnostic = diagnostic, OperationId = plan.OperationId,
        WorkshopId = (id == 0 ? plan.WorkshopId : id).ToString(CultureInfo.InvariantCulture), PackageId = plan.PackageId, Version = plan.Version, Fingerprint = plan.Fingerprint });
    private static void WriteResult(object result)
    {
        string temporary = resultPath + ".tmp"; Directory.CreateDirectory(Path.GetDirectoryName(resultPath));
        File.WriteAllText(temporary, DependencyFreeJson.Serialize(result), new UTF8Encoding(false)); AtomicFileReplacement.Replace(temporary, resultPath);
        Console.WriteLine(File.ReadAllText(resultPath));
    }
}

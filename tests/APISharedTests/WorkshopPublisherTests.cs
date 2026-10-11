using APIShared.Workshop;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Steamworks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class WorkshopPublisherTests
    {
        [TestMethod]
        public void EveryPublishingStepReportsFailureAndRetainsAllocatedIdentity()
        {
            foreach (string step in new[] { "CreateItem", "CreateItem.callback", "StartItemUpdate", "title", "description", "tags", "preview", "visibility", "content", "metadata", "SubmitItemUpdate", "SubmitItemUpdate.callback", "AddDependency", "AddDependency.callback" })
                Run(step, false);
            foreach (string step in new[] { "query", "query metadata", "query children", "SendQuery", "SendQuery.callback", "details", "children", "release", "RemoveDependency", "RemoveDependency.callback" })
                Run(step, true);
        }
        [TestMethod] public void SuccessfulUploadRemovesOnlyPreviouslyManagedDependencies() { Run(null, true); }
        [TestMethod] public void EmptyCreatedItemResumesWithoutLegacySubscription() { Run(null, true, true); }
        private static void Run(string failure, bool existing, bool pending = false)
        {
            string root = Path.Combine(Path.GetTempPath(), "WorkshopPublisher-" + Guid.NewGuid().ToString("N"));
            var queue = new ConcurrentQueue<Action>(); var fake = new Fake(queue, failure);
            fake.EmptyMetadata = pending;
            var previous = WorkshopSteamUpload.Backend;
            var require = WorkshopPublisherContext.RequireSteam; var enqueue = WorkshopPublisherContext.Enqueue;
            var log = WorkshopPublisherContext.Log; string state = WorkshopPublisherContext.StateRoot;
            try {
                string source = Path.Combine(root, "source"), plugin = Path.Combine(source, "Payload/BepInEx/plugins/PublisherExample"); Directory.CreateDirectory(plugin);
                File.WriteAllText(Path.Combine(plugin, "info.json"), "{\"GUID\":\"PublisherExample\",\"Version\":\"1.0.0\",\"Manifest\":1}");
                File.WriteAllText(Path.Combine(source, "preview.png"), "preview");
                var draft = new WorkshopPackageDraft("PublisherExample", "1.0.0", "0.7.1", "2.14.1", dependencies: new[] { new WorkshopDependency("Other", "1.0.0", workshopId: "200") });
                var plan = WorkshopPublishing.Prepare("tests", root, new WorkshopUploadOptions { SourceDirectory = source, PreviewFile = Path.Combine(source, "preview.png"), Title = "Title", Description = "Description", Draft = draft, WorkshopId = existing ? 123UL : 0 });
                if (pending) WorkshopPublishing.Save(root, plan, 123, false);
                int thread = Thread.CurrentThread.ManagedThreadId;
                WorkshopSteamUpload.Backend = fake; WorkshopPublisherContext.StateRoot = root; WorkshopPublisherContext.Log = _ => { };
                WorkshopPublisherContext.RequireSteam = () => Assert.AreEqual(thread, Thread.CurrentThread.ManagedThreadId);
                WorkshopPublisherContext.Enqueue = action => { queue.Enqueue(action); return true; };
                var operation = WorkshopSteamUpload.Publish("tests", plan);
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (operation.State != WorkshopOperationState.Failed && operation.State != WorkshopOperationState.Succeeded && DateTime.UtcNow < deadline) {
                    while (queue.TryDequeue(out Action action)) action(); Thread.Sleep(5);
                }
                Assert.AreEqual(failure == null ? WorkshopOperationState.Succeeded : WorkshopOperationState.Failed, operation.State, failure + ": " + operation.Diagnostic);
                if (!existing && failure != "CreateItem" && failure != "CreateItem.callback") {
                    Assert.AreEqual(123UL, operation.WorkshopId, failure);
                    Assert.IsTrue(File.ReadAllText(WorkshopPublishing.HistoryPath(root, "PublisherExample")).Contains("123"));
                }
                if (failure == null) CollectionAssert.AreEqual(pending ? new ulong[0] : new[] { 100UL }, fake.Removed.ToArray());
            } finally {
                WorkshopSteamUpload.Backend = previous; WorkshopPublisherContext.RequireSteam = require; WorkshopPublisherContext.Enqueue = enqueue;
                WorkshopPublisherContext.Log = log; WorkshopPublisherContext.StateRoot = state;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }
        private sealed class Fake : WorkshopSteamBackend
        {
            private readonly ConcurrentQueue<Action> queue; private readonly string failure;
            private readonly Dictionary<ulong, string> calls = new Dictionary<ulong, string>(); private ulong next;
            internal readonly List<ulong> Removed = new List<ulong>();
            internal bool EmptyMetadata;
            internal Fake(ConcurrentQueue<Action> queue, string failure) { this.queue = queue; this.failure = failure; }
            private bool Accepted(string step) => failure != step;
            private SteamAPICall_t Call(string step) { if (!Accepted(step)) return new SteamAPICall_t(0); calls[++next] = step; return new SteamAPICall_t(next); }
            internal override WorkshopSteamResult<T> CreateResult<T>(Action<T, bool> action) => new Result<T>(this, action);
            private sealed class Result<T> : WorkshopSteamResult<T> where T : struct
            {
                private readonly Fake fake; private readonly Action<T, bool> action;
                internal Result(Fake fake, Action<T, bool> action) { this.fake = fake; this.action = action; }
                internal override void Set(SteamAPICall_t call) {
                    string step = fake.calls[call.m_SteamAPICall];
                    EResult status = fake.Accepted(step + ".callback") ? EResult.k_EResultOK : EResult.k_EResultFail;
                    object result = typeof(T) == typeof(CreateItemResult_t) ? (object)new CreateItemResult_t { m_eResult = status, m_nPublishedFileId = new PublishedFileId_t(123) } :
                        typeof(T) == typeof(SubmitItemUpdateResult_t) ? (object)new SubmitItemUpdateResult_t { m_eResult = status } :
                        typeof(T) == typeof(AddUGCDependencyResult_t) ? (object)new AddUGCDependencyResult_t { m_eResult = status } :
                        typeof(T) == typeof(RemoveUGCDependencyResult_t) ? (object)new RemoveUGCDependencyResult_t { m_eResult = status } :
                        (object)new SteamUGCQueryCompleted_t { m_eResult = status, m_unNumResultsReturned = 1 };
                    fake.queue.Enqueue(() => action((T)result, false));
                }
            }
            internal override SteamAPICall_t CreateItem(AppId_t app, EWorkshopFileType type) => Call("CreateItem");
            internal override UGCUpdateHandle_t StartItemUpdate(AppId_t app, PublishedFileId_t id) => new UGCUpdateHandle_t(Accepted("StartItemUpdate") ? 1UL : ulong.MaxValue);
            internal override bool SetItemTitle(UGCUpdateHandle_t h, string s) => Accepted("title");
            internal override bool SetItemDescription(UGCUpdateHandle_t h, string s) => Accepted("description");
            internal override bool SetItemTags(UGCUpdateHandle_t h, IList<string> s, bool admin = false) => Accepted("tags");
            internal override bool SetItemPreview(UGCUpdateHandle_t h, string s) => Accepted("preview");
            internal override bool SetItemVisibility(UGCUpdateHandle_t h, ERemoteStoragePublishedFileVisibility s) => Accepted("visibility");
            internal override bool SetItemContent(UGCUpdateHandle_t h, string s) => Accepted("content");
            internal override bool SetItemMetadata(UGCUpdateHandle_t h, string s) => Accepted("metadata");
            internal override SteamAPICall_t SubmitItemUpdate(UGCUpdateHandle_t h, string note) => Call("SubmitItemUpdate");
            internal override SteamAPICall_t AddDependency(PublishedFileId_t parent, PublishedFileId_t child) => Call("AddDependency");
            internal override SteamAPICall_t RemoveDependency(PublishedFileId_t parent, PublishedFileId_t child) { Removed.Add(child.m_PublishedFileId); return Call("RemoveDependency"); }
            internal override void ActivateGameOverlayToWebPage(string url, EActivateGameOverlayToWebPageMode mode = default(EActivateGameOverlayToWebPageMode)) { }
            internal override UGCQueryHandle_t CreateQueryUGCDetailsRequest(PublishedFileId_t[] ids, uint count) => new UGCQueryHandle_t(Accepted("query") ? 1UL : ulong.MaxValue);
            internal override bool SetReturnMetadata(UGCQueryHandle_t h, bool b) => Accepted("query metadata");
            internal override bool SetReturnChildren(UGCQueryHandle_t h, bool b) => Accepted("query children");
            internal override SteamAPICall_t SendQueryUGCRequest(UGCQueryHandle_t h) => Call("SendQuery");
            internal override CSteamID GetSteamID() => new CSteamID(456);
            internal override bool GetQueryUGCResult(UGCQueryHandle_t h, uint index, out SteamUGCDetails_t details) { details = new SteamUGCDetails_t { m_eResult = EResult.k_EResultOK, m_nPublishedFileId = new PublishedFileId_t(123), m_nConsumerAppID = new AppId_t(3024040), m_ulSteamIDOwner = 456, m_unNumChildren = 2 }; return Accepted("details"); }
            internal override bool GetQueryUGCChildren(UGCQueryHandle_t h, uint i, PublishedFileId_t[] ids, uint count) { ids[0] = new PublishedFileId_t(100); ids[1] = new PublishedFileId_t(777); return Accepted("children"); }
            internal override bool ReleaseQueryUGCRequest(UGCQueryHandle_t h) => Accepted("release");
            internal override bool GetQueryUGCMetadata(UGCQueryHandle_t h, uint index, out string value, uint size) { value = EmptyMetadata ? "" : "{\"PackageId\":\"PublisherExample\",\"Version\":\"0.9.0\",\"Fingerprint\":\"old\",\"ManagedDependencies\":[\"100\"]}"; return !EmptyMetadata; }
            internal override bool GetItemInstallInfo(PublishedFileId_t id, out ulong size, out string folder, uint capacity, out uint stamp) { size=0;folder="";stamp=0;throw new InvalidOperationException("Pending publisher-created items must not use legacy installation verification."); }
        }
    }
}

using APIShared.Internal;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.API.Components.ModManager;
using SHCDESE.EventAPI;
using Steamworks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Version = System.Version;

namespace APIShared.Workshop
{
    internal static partial class WorkshopRuntime
    {
        private static IDisposable steamSubscription;
        private static ManualLogSource logger;
        private static bool startupDownloadPending;
        private static string startupPendingItems;
        private static int steamFailureReported;
        private static ConfigEntry<string> disabledOffers, installRequests;
        private static readonly WorkshopApplyCoordinator ApplyCoordinator = new WorkshopApplyCoordinator();
        internal static string StateRoot => WorkshopInstallation.StateRoot(Paths.GameRootPath);
        internal static bool IsExternallyManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId"));
        internal static void Initialize(BaseUnityPlugin host, ManualLogSource log)
        {
            logger = log;
            disabledOffers = host.Config.Bind("Workshop", "DisabledStartupOffers", string.Empty,
                "Workshop item IDs whose startup update offers are disabled. Managed by APIShared Mod Options.");
            installRequests = host.Config.Bind("Workshop", "StartupInstallRequests", string.Empty,
                "Workshop item IDs to offer for installation at the next game start, even when not installed. Managed by APIShared Mod Options; removed once the confirmed installation committed.");
            WorkshopPublisherContext.RequireSteam = RequireSteam;
            WorkshopPublisherContext.Enqueue = UnityMainThreadDispatch.TryEnqueue;
            WorkshopPublisherContext.Log = Log;
            WorkshopPublisherContext.StateRoot = StateRoot;
            steamSubscription = SteamworksR3EventHooks.OnSteamworksInitialized.Observable.Subscribe(e => {
                if (e.Phase != EventHookPhase.Post) return;
                try {
                    var pending = InitialSubscriptions().Where(x => (x.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0).ToArray();
                    startupDownloadPending = pending.Length != 0;
                    startupPendingItems = string.Join(", ", pending.Select(x => x.Id + " (" + x.State + ")"));
                    Log("Workshop startup gate active on Steam initialization; external management=" + IsExternallyManaged + ".");
                    WorkshopStartup.Run();
                    UnityMainThreadDispatch.TryEnqueue(() => Log("Workshop runtime active after startup cleanup; external management=" + IsExternallyManaged + "."));
                } catch (Exception ex) {
                    if (System.Threading.Interlocked.Exchange(ref steamFailureReported, 1) != 0) return;
                    WorkshopDiagnostics.Failure(WriteDiagnostic, "Workshop step=Steam initialization", ex);
                    WorkshopDiagnostics.Outcome(WriteDiagnostic, "failed", false, 1);
                }
            });
        }
        internal static void Log(string text) => NativeApiLog.Info(logger, text);
        internal static bool OffersEnabled(ulong item) => !WorkshopLocalState.ParseIds(disabledOffers.Value).Contains(item);
        internal static void SetOffersEnabled(ulong item, bool enabled) => SetId(disabledOffers, item, !enabled);

        // A request is the player's choice in Mod Options to have the next game start offer
        // this item like an update. The startup dialog still asks for consent. A confirmed
        // handoff records its transaction; the following start keeps the request only if that
        // transaction did not commit (see WorkshopLocalState.ResolveRequest).
        internal static bool InstallRequested(ulong item) => WorkshopLocalState.ParseRequests(installRequests.Value).ContainsKey(item);
        internal static HashSet<ulong> PendingInstallRequests() =>
            new HashSet<ulong>(WorkshopLocalState.ParseRequests(installRequests.Value).Where(pair => pair.Value == null).Select(pair => pair.Key));
        internal static void SetInstallRequested(ulong item, bool requested)
        {
            var requests = WorkshopLocalState.ParseRequests(installRequests.Value);
            if (requested ? requests.ContainsKey(item) : !requests.Remove(item)) return;
            if (requested) requests[item] = null;
            installRequests.Value = WorkshopLocalState.FormatRequests(requests);
        }
        internal static void RemoveInstallRequests(IEnumerable<ulong> items)
        {
            var requests = WorkshopLocalState.ParseRequests(installRequests.Value);
            bool changed = false;
            foreach (ulong item in items) changed |= requests.Remove(item);
            if (changed) installRequests.Value = WorkshopLocalState.FormatRequests(requests);
        }
        internal static void MarkInstallRequestsHandedOff(IEnumerable<ulong> items, string transaction)
        {
            var requests = WorkshopLocalState.ParseRequests(installRequests.Value);
            bool changed = false;
            foreach (ulong item in items.Where(requests.ContainsKey).ToArray()) { requests[item] = transaction; changed = true; }
            if (changed) installRequests.Value = WorkshopLocalState.FormatRequests(requests);
        }
        // Runs before installation offers on each start (after recovery inspection).
        internal static void ResolveHandedOffInstallRequests(string game)
        {
            var requests = WorkshopLocalState.ParseRequests(installRequests.Value);
            bool changed = false;
            foreach (var pair in requests.Where(pair => pair.Value != null).ToArray()) {
                string directory; string[] journal; bool superseded;
                try {
                    directory = WorkshopInstallation.FindTransaction(game, pair.Value);
                    journal = directory == null ? null : WorkshopRecovery.Journal(directory);
                    superseded = directory != null && WorkshopRecovery.SupersededByCommit(game, directory);
                } catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) {
                    // Undecidable now: keep the handed-off entry; other offers continue.
                    WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop installation request outcome unreadable; item=" + pair.Key + "; transaction=" + pair.Value + "; " + ex.Message);
                    continue;
                }
                if (WorkshopLocalState.ResolveRequest(journal, superseded) == WorkshopLocalState.RequestOutcome.Fulfilled) {
                    requests.Remove(pair.Key);
                    Log("Workshop installation request fulfilled; item=" + pair.Key + "; transaction=" + pair.Value + (superseded ? " (by complete repair)" : "") + ".");
                } else {
                    requests[pair.Key] = null;
                    string failure = journal?.LastOrDefault(line => line.StartsWith("failure ", StringComparison.Ordinal));
                    WriteDiagnostic(WorkshopDiagnosticLevel.Warning, "Workshop requested installation was not committed and is offered again; item=" + pair.Key +
                        "; transaction=" + pair.Value + "; " + (directory == null ? "transaction not found" : failure ?? "no recorded failure"));
                }
                changed = true;
            }
            if (changed) installRequests.Value = WorkshopLocalState.FormatRequests(requests);
        }
        private static void SetId(ConfigEntry<string> entry, ulong item, bool present)
        {
            var ids = WorkshopLocalState.ParseIds(entry.Value);
            if (present ? ids.Add(item) : ids.Remove(item)) entry.Value = WorkshopLocalState.FormatIds(ids);
        }
        internal static void WriteDiagnostic(WorkshopDiagnosticLevel level, string text)
        {
            // Failure reports are stamped by RuntimeHealth; an own unbracketed stamp would
            // make every identical repeat a new cause and defeat its repeat throttling.
            if (level == WorkshopDiagnosticLevel.Error) NativeApiLog.Error(logger, text);
            else if (level == WorkshopDiagnosticLevel.Warning) NativeApiLog.Warning(logger, text);
            else NativeApiLog.Info(logger, text);
        }
    }
}

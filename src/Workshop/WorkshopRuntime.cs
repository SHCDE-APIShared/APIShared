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
        private static ConfigEntry<string> disabledOffers;
        private static readonly WorkshopApplyCoordinator ApplyCoordinator = new WorkshopApplyCoordinator();
        private static readonly WorkshopShutdownHandoff SettingsHandoff = new WorkshopShutdownHandoff();
        internal static string StateRoot => WorkshopInstallation.StateRoot(Paths.GameRootPath);
        internal static bool IsExternallyManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId"));
        internal static void Initialize(BaseUnityPlugin host, ManualLogSource log)
        {
            logger = log;
            disabledOffers = host.Config.Bind("Workshop", "DisabledStartupOffers", string.Empty,
                "Workshop item IDs whose startup update offers are disabled. Managed by APIShared Mod Options.");
            WorkshopPublisherContext.RequireSteam = RequireSteam;
            WorkshopPublisherContext.Enqueue = UnityMainThreadDispatch.TryEnqueue;
            WorkshopPublisherContext.Log = Log;
            WorkshopPublisherContext.StateRoot = StateRoot;
            UnityEngine.Application.quitting += SettingsHandoff.Run;
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
        internal static bool OffersEnabled(ulong item) => !DisabledOfferIds().Contains(item);
        internal static void SetOffersEnabled(ulong item, bool enabled)
        {
            var ids = DisabledOfferIds();
            if (enabled ? ids.Remove(item) : ids.Add(item))
                disabledOffers.Value = string.Join(",", ids.OrderBy(id => id).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        private static HashSet<ulong> DisabledOfferIds() => new HashSet<ulong>(
            disabledOffers.Value.Split(',').Select(value => ulong.TryParse(value, out ulong id) ? id : 0).Where(id => id != 0));
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

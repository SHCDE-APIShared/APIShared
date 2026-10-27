using APIShared.Internal;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    internal static class LobbyMismatchRuntime
    {
        private static ILHook hook;
        private static ManualLogSource logger;
        private static int attempted;

        internal static void Initialize(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            logger = log;
            ILHook candidate = null;
            try
            {
                var target = typeof(GameNetworkAPI).GetMethod("RejectLobby", BindingFlags.NonPublic | BindingFlags.Static);
                LobbyMismatchIl.ValidateTarget(target);
                candidate = new ILHook(target, il => LobbyMismatchIl.ReplacePopup(il, ShowPopup),
                    new ILHookConfig { ManualApply = true, ID = "APIShared.ScriptExtenderFixes.LobbyMismatch" });
                candidate.Apply();
                hook = candidate; // Process lifetime; never tied to the early Unity component's teardown.
                DebugLogHelper.LogInfo(logger, "SE_LOBBY_MISMATCH: detailed rejection popup installed; SE join decisions are retained.");
            }
            catch (Exception error)
            {
                if (hook == null) candidate?.Dispose(); // Only an unpublished installation candidate.
                DebugLogHelper.LogWarning(logger, "SE_LOBBY_MISMATCH: not installed; original SE behavior retained. " + error);
            }
        }

        private static void ShowPopup(string title, Action accept, bool multiplayer, bool sands, CSteamID lobby, string reason)
        {
            // Steam's lobby-result callback is a Unity callback; do not touch Steam/Noesis on a foreign thread.
            if (!UnityMainThreadDispatch.IsMainThread)
                throw new InvalidOperationException("Lobby mismatch presentation requires the Unity thread.");
            LobbyMismatchReport report;
            try
            {
                var assets = new List<LobbyMismatchMod>();
                foreach (var item in GameAssetModManager.Instance.GetRegisteredAssetDirectories())
                    assets.Add(new LobbyMismatchMod(item.Key.GUID, item.Key.Name, item.Key.Version,
                        item.Key.NetworkMode == ModNetworkMode.Clientside));
                var plugins = new List<LobbyMismatchMod>();
                foreach (var item in Chainloader.PluginInfos.Values)
                    plugins.Add(new LobbyMismatchMod(item.Metadata.GUID, item.Metadata.Name, item.Metadata.Version.ToString(), false));
                report = LobbyMismatchReport.Create(SteamMatchmaking.GetLobbyData(lobby, LobbyMismatchReport.MetadataToken), assets, plugins);
            }
            catch (Exception error)
            {
                report = new LobbyMismatchReport(new List<LobbyMismatchDifference>(), "Could not capture complete mod inventories: " + error);
            }
            WriteReport(logger, lobby.m_SteamID, reason, report);
            string locale = Localization.GameLocale.Current();
            HUD_ConfirmationPopup.ShowOK(Localization.UiText.Get("LobbyMismatch.Title"), accept, multiplayer, sands);
            var model = MainViewModel.Instance;
            // ShowOK preserves the MP overlay and callback; Vanilla resets these dimensions for the next dialog.
            model.ConfirmationMessage = report.PopupText(reason, locale);
            model.ConfirmationPanelWidth = "650";
            model.ConfirmationPanelWidth2 = "620";
            model.ConfirmationPanelHeight = "480";
            model.ConfirmationPanelWidthView = "702";
            model.ConfirmationPanelHeightView = "585";
            HUD_ConfirmationPopup.ConfirmationWidth = 650;
            HUD_ConfirmationPopup.ConfirmationHeight = 480;
            model.FrontEndMenu.UpdateFrontMenuPopupScale();
        }

        internal static void WriteReport(ManualLogSource log, ulong lobby, string reason, LobbyMismatchReport report)
        {
            // One complete report per rejection; not conditional on development diagnostics or a rate limiter.
            DebugLogHelper.LogInfo(log, $"SE_LOBBY_MISMATCH: lobby={lobby}; SE reason={reason}; differences={report.Differences.Count}.");
            if (report.MetadataIssue != null) DebugLogHelper.LogInfo(log, "SE_LOBBY_MISMATCH: " + report.MetadataIssue);
            foreach (var difference in report.Differences)
                DebugLogHelper.LogInfo(log, "SE_LOBBY_MISMATCH: " + difference.LogText);
        }
    }
}

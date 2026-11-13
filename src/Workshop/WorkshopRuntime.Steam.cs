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
        internal static void RequireSteam()
        {
            if (!UnityMainThreadDispatch.IsMainThread) throw new InvalidOperationException("Workshop Steam calls require the Unity thread.");
            var type = typeof(SteamManager);
            object instance = type.GetField("s_instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            if (instance == null || !(type.GetField("m_bInitialized", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(instance) is bool ready) || !ready)
                throw new InvalidOperationException("Steamworks is not initialized.");
        }
        internal sealed class Subscription { internal ulong Id; internal string Root; internal EItemState State; internal uint InstalledAt; }
        // Startup runs once per session: a list that changes while Steam is still
        // enumerating it must not disable startup Workshop handling for the session.
        private static Subscription[] InitialSubscriptions()
        {
            for (int attempt = 1; ; attempt++) {
                try { return Subscriptions(); }
                catch (IOException) when (attempt < 3) { }
            }
        }
        internal static Subscription[] Subscriptions()
        {
            RequireSteam(); uint count = SteamUGC.GetNumSubscribedItems();
            var ids = new PublishedFileId_t[count];
            if (SteamUGC.GetSubscribedItems(ids, count) != count || SteamUGC.GetNumSubscribedItems() != count) throw new IOException("Steam subscription list changed; retry.");
            return ids.Select(id => {
                var state = (EItemState)SteamUGC.GetItemState(id);
                string folder = null;
                uint installedAt = 0;
                if ((state & EItemState.k_EItemStateInstalled) != 0 && SteamUGC.GetItemInstallInfo(id, out var _, out string root, 4096, out uint timestamp)) {
                    folder = root;
                    installedAt = timestamp;
                }
                return new Subscription { Id = id.m_PublishedFileId, Root = folder, State = state, InstalledAt = installedAt };
            }).ToArray();
        }
        internal static bool Stable(Subscription item) => item.Root != null && Directory.Exists(item.Root) &&
            (item.State & (EItemState.k_EItemStateNeedsUpdate | EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateDisabledLocally)) == 0;
    }
}

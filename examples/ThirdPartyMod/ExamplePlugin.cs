using System;
using System.Threading;
using APIShared;
using BepInEx;
using BepInEx.Logging;
using SHCDESE.API;

namespace ThirdPartyMod
{
    [BepInPlugin(Guid, "APIShared Example", "1.0.0")]
    [BepInDependency("000shcdese", "2.14.0")]
    [BepInDependency("APIShared_Serp", "0.5.0")]
    public sealed class ExamplePlugin : BaseUnityPlugin
    {
        public const string Guid = "Example.Author.APISharedDemo";
        private static readonly ModApiClient Api = ApiShared.ForMod(Guid);
        private static readonly ExampleSettings Settings = new ExampleSettings();
        private static ManualLogSource log;
        private static SynchronizationContext unityContext;

        private void Awake()
        {
            log = Logger;
            // Awake is a Unity-thread entry point. Retain its persistent context, not
            // a dispatcher MonoBehaviour that SHCDE destroys during startup cleanup.
            unityContext = SynchronizationContext.Current
                ?? throw new InvalidOperationException("A Unity synchronization context is required.");
            MissionExample.Register(Api, Log);
            APIShared.ModSettings.LobbyModSettingsPresetRegistration.Register(
                this, log, "APIShared Example", Settings, "ScriptExtenderUI/APISharedExample.xaml");
            HudExample.RegisterSideButtons(Api, Log);
            Api.WhenReady(OnReady);
        }

        private static void OnReady(ModApiClient client)
        {
            // Readiness does not dispatch to Unity. This also handles late registration
            // from a worker thread; the queued delegate and static runtime retain state.
            unityContext.Post(_ =>
            {
                // WhenReady isolates its callback, not work posted by that callback.
                try
                {
                    if (client.TryGetUnitHudPresentation(out var hud, out var diagnostic))
                        HudExample.Register(hud, Log);
                    else Log(diagnostic.Reason);
                }
                catch (Exception error) { Log("HUD example registration failed: " + error); }
            }, null);
        }

        internal static void Log(string message) =>
            log?.LogInfo($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
    }
}

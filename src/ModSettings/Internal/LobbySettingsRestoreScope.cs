using System;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SHCDESE.API;
using SHCDESE.API.Components.ModManager;

namespace APIShared.ModSettings
{
    // The Extender owns the origin stack. Do not impersonate an authenticated network
    // receive: internal projection only suppresses its outbound/persistence observer.
    internal static class LobbySettingsRestoreScope
    {
        private static readonly MethodInfo Run = typeof(GameXAMLManagerAPI).GetMethod(
            "RunWithChangeOrigin", BindingFlags.Instance | BindingFlags.NonPublic,
            null, new[] { typeof(LobbyModSettingsChangeOrigin), typeof(Action) }, null);

        internal static void Execute(Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            if (Run == null || Run.ReturnType != typeof(void) || Run.IsStatic)
                throw new MissingMethodException("Extender restore-origin scope is unavailable.");
            ExceptionDispatchInfo failure = null;
            Action guarded = () =>
            {
                try { action(); }
                catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            };
            Run.Invoke(GameXAMLManagerAPI.Instance, new object[] { LobbyModSettingsChangeOrigin.InitialLoad, guarded });
            // RunWithChangeOrigin logs and swallows callback exceptions. Snapshot owners
            // must instead keep failed cleanup reachable and must not publish success.
            failure?.Throw();
        }
    }
}

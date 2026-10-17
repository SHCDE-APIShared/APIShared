using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Mono.Cecil;

namespace APIShared.DependencyBootstrap
{
    // Preloader contract only. No Unity/MonoMod types, Chainloader access or eager hook setup.
    public static class DependencyBootstrapPatcher
    {
        public static IEnumerable<string> TargetDLLs => new string[0];
        private static ILogListener listener;
        private static string mode;
        public static void Patch(AssemblyDefinition assembly) { }
        public static void Finish()
        {
            try
            {
                if (listener != null) return;
                ProbeTrace.Initialize();
                mode = File.Exists(Path.Combine(BepInEx.Paths.ConfigPath, "APIShared.DependencyBootstrap.disabled")) ? "Disabled" : "Active";
                listener = new ReadinessListener(new ReadyGate(OnReady, () => { }, (marker, detail) => ProbeTrace.Error(detail)));
                Logger.Listeners.Add(listener);
            }
            catch (Exception ex) { ProbeTrace.Error("Finish " + ex); }
        }
        private static void OnReady()
        {
            // A retained companion is inert after plugin removal. Keep this gate
            // before runtime resolution, pair reads and any hook preparation.
            if (mode == "Active" && PairContract.HasRuntimeDirectory(BepInEx.Paths.GameRootPath))
                InvokeHookRuntime("Install");
        }
        // String-based dispatch deliberately avoids a runtime type token on the early path.
        private static object InvokeHookRuntime(string method)
        {
            var type = typeof(DependencyBootstrapPatcher).Assembly.GetType("APIShared.DependencyBootstrap.HookRuntime", true);
            return type.GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
        }
        private sealed class ReadinessListener : ILogListener
        {
            private readonly ReadyGate gate;
            internal ReadinessListener(ReadyGate gate) { this.gate = gate; }
            public void LogEvent(object sender, LogEventArgs args)
            {
                try { gate.Process(args?.Source?.SourceName, args?.Data as string); }
                catch (Exception ex) { ProbeTrace.Error("listener " + ex); }
            }
            public void Dispose() { /* Process lifetime, no hook teardown. */ }
        }
    }
}

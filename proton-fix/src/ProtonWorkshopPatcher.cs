using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Workshop.Proton;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;
using MonoMod.RuntimeDetour;

namespace APIShared.ProtonWorkshopFix
{
    /// <summary>Independent Linux-only preloader correction for Script Extender Workshop deployment.</summary>
    public static class ProtonWorkshopPatcher
    {
        /// <summary>No managed game assembly is rewritten on disk.</summary>
        public static IEnumerable<string> TargetDLLs => Array.Empty<string>();
        /// <summary>BepInEx preloader contract; this patcher acts only after Chainloader readiness.</summary>
        public static void Patch(AssemblyDefinition assembly) { }
        private static ILogListener listener;

        /// <summary>Attaches the process-lifetime readiness observer under Wine; native Windows is inert.</summary>
        public static void Finish()
        {
            try
            {
                if (listener != null || !WinePathInterop.Instance.IsWine || ExternallyManaged) return;
                listener = new ReadinessListener();
                Logger.Listeners.Add(listener);
            }
            catch (Exception error) { PatcherLog.Warning("Preloader readiness observer unavailable: " + error); }
        }

        internal static bool ExternallyManaged => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SCDEModManagerLaunchId"));

        // Keep MonoMod type resolution/JIT away from Finish and the preloader's early assembly phase.
        private static void StartObserver()
        {
            var runtime = typeof(ProtonWorkshopPatcher).Assembly.GetType("APIShared.ProtonWorkshopFix.PatcherRuntime", true);
            runtime.GetMethod("Observe", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        }

        private sealed class ReadinessListener : ILogListener
        {
            private int ready;
            public void LogEvent(object sender, LogEventArgs args)
            {
                if (args?.Source?.SourceName != "BepInEx" || !(args.Data is string message) ||
                    message != "Chainloader ready" || Interlocked.Exchange(ref ready, 1) != 0) return;
                try { StartObserver(); }
                catch (Exception error) { PatcherLog.Warning("Linux Workshop correction was not activated: " + error); }
            }
            public void Dispose() { /* The BepInEx logger roots this observer until process exit. */ }
        }
    }

    internal static class PatcherRuntime
    {
        private static ILHook unixHook, counterHook;
        private static AssemblyObservation assemblies;
        private static int active, observer, attempted;
        internal static ProtonExecutionContext Context { get; private set; }
        internal static bool IsActive() => Volatile.Read(ref active) != 0 && unixHook != null && counterHook != null;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Observe()
        {
            if (Interlocked.Exchange(ref observer, 1) != 0 || ProtonWorkshopPatcher.ExternallyManaged || !WinePathInterop.Instance.IsWine) return;
            Context = ProtonExecutionContext.Create(Paths.GameRootPath, ProtonConfiguration.Load(Paths.ConfigPath), TimeSpan.FromSeconds(2));
            VerifyOwner();
            assemblies = new AssemblyObservation("SHCDESE", TryInstall, PatcherLog.Warning);
            assemblies.Start();
        }

        internal static void TryInstall(Assembly assembly)
        {
            if (assembly == null || assembly.GetName().Name != "SHCDESE" || Interlocked.CompareExchange(ref attempted, 1, 0) != 0) return;
            try
            {
                VerifyOwner();
                var contract = ExtenderContract.Validate(assembly);
                // Native Unity Mono JIT validation is separate from offline desktop delegate generation.
                bool mono = Type.GetType("Mono.Runtime") != null;
                var preparedUnix = contract.PrepareUnix(mono);
                var preparedCounter = contract.PrepareCounter(mono);
                GC.KeepAlive(preparedUnix); GC.KeepAlive(preparedCounter);
                RetryGate.Initialize(contract.AttemptLimit, ExtenderDeployment.PreflightRetry,
                    () => PatcherFiles.RetryAvailable(Paths.ConfigPath), () => PatcherFiles.ClaimRetry(Paths.ConfigPath), IsActive);
                // Each published hook is immediately rooted and never disposed, even if the second installation fails.
                unixHook = new ILHook(contract.UnixMethod, il => ExtenderIl.ModifyUnix(il, contract));
                counterHook = new ILHook(contract.CounterMethod, il => ExtenderIl.ModifyCounter(il, contract.AttemptLimit, contract.ManagerTypeName));
                Volatile.Write(ref active, 1);
                PatcherLog.Info("Linux Workshop deployment correction active for " + assembly.FullName + "; process-lifetime hooks, independent of APIShared runtime.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref active, 0);
                PatcherLog.Warning("Extender correction unavailable; original deployment remains active. Assembly=" + assembly.FullName +
                    "; path=" + assembly.Location + "; reason=" + error);
            }
        }

        private static void VerifyOwner()
        {
            var own = typeof(ProtonWorkshopPatcher).Assembly;
            if (AppDomain.CurrentDomain.GetAssemblies().Count(a => a.GetName().Name == own.GetName().Name) != 1)
                throw new InvalidOperationException("Multiple loaded Proton Workshop patchers.");
            int matches = 0;
            var pending = new Stack<string>();
            PatcherFiles.CheckLinks(Paths.PatcherPluginPath); pending.Push(Paths.PatcherPluginPath);
            while (pending.Count != 0)
            {
                string directory = pending.Pop();
                foreach (string file in Directory.EnumerateFiles(directory, "*.dll"))
                {
                    PatcherFiles.CheckLinks(file);
                    if (PatcherFiles.ManagedAssemblyName(file) != own.GetName().Name) continue;
                    if (!PatcherFiles.SamePath(file, own.Location)) throw new InvalidOperationException("Renamed or competing Proton Workshop patcher: " + file);
                    matches++;
                }
                foreach (string child in Directory.EnumerateDirectories(directory)) { PatcherFiles.CheckLinks(child); pending.Push(child); }
            }
            if (matches != 1) throw new InvalidOperationException("The loaded Proton Workshop patcher does not have one unique installed owner.");
        }
    }

    // Subscribe before snapshotting: an assembly loaded at readiness cannot fall between both paths.
    internal sealed class AssemblyObservation
    {
        private readonly string name;
        private readonly Action<Assembly> found;
        private readonly Action<string> warning;
        private readonly HashSet<Assembly> seen = new HashSet<Assembly>();
        private int started;
        internal AssemblyObservation(string name, Action<Assembly> found, Action<string> warning)
        { this.name = name; this.found = found; this.warning = warning; }
        internal void Start()
        {
            if (Interlocked.Exchange(ref started, 1) != 0) return;
            AppDomain.CurrentDomain.AssemblyLoad += Loaded;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()) Check(assembly);
        }
        private void Loaded(object sender, AssemblyLoadEventArgs args) => Check(args.LoadedAssembly);
        private void Check(Assembly assembly)
        {
            try
            {
                if (assembly.GetName().Name != name) return;
                lock (seen) { if (!seen.Add(assembly)) return; }
                found(assembly);
            }
            catch (Exception error) { warning("Extender assembly observation failed: " + error); }
        }
    }

    internal static class PatcherLog
    {
        private static ManualLogSource source;
        internal static void Info(string text) => Write(LogLevel.Info, text);
        internal static void Warning(string text) => Write(LogLevel.Warning, text);
        internal static void Error(string text) => Write(LogLevel.Error, text);
        private static void Write(LogLevel level, string text)
        {
            try
            {
                if (source == null) source = Logger.CreateLogSource("APIShared.ProtonWorkshopFix");
                source.Log(level, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + " " + text);
            }
            catch { /* Logging cannot change loader behavior. */ }
        }
    }
}

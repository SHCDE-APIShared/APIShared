using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API.LowLevel;

namespace APIShared.Economy
{
    internal static class FearPopularityRuntime
    {
        private static ILHook hook;
        private static ManualLogSource log;
        private static int overflowReported, appliedReported;
        internal static bool Available => hook != null;
        internal static string Diagnostic { get; private set; } = "Native initialization has not completed.";
        internal static void Initialize(string nativeHash, ManualLogSource logger)
        {
            if (Available) return;
            log = logger;
            if (nativeHash != "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2")
                throw new InvalidOperationException(Diagnostic = "Unsupported native popularity contract.");
            try
            {
                var assembly = typeof(CrusaderLibrary).Assembly;
                Internal.InstalledAssemblyIdentity.Verify(assembly, assembly.Location,
                    "577F9AAB5DE504BE0BFA2193C7DEB734B26076ECBBAF0A408FC9ABC4CC5E7175");
                MethodInfo target = FindTarget(assembly);
                // Validate against the installed original before publishing a permanent hook.
                using (var definition = new DynamicMethodDefinition(target))
                using (var context = new ILContext(definition.Definition)) FearPopularityIl.Sites(context);
                hook = new ILHook(target, FearPopularityIl.Apply);
            }
            catch (Exception ex) { Diagnostic = ex.Message; throw; }
            Diagnostic = null;
            log.LogInfo(Stamp("FEAR_POPULARITY_READY signed SE branches retained; rounding=away-from-zero; units=points*25"));
        }
        internal static MethodInfo FindTarget(Assembly assembly)
        {
            var type = assembly.GetType("SHCDESE.Detours.BulkPlayerDetours", true);
            MethodInfo found = null;
            foreach (var candidate in new[] { type }.Concat(type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)))
            foreach (var method in candidate.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.GetMethodBody() == null) continue;
                using (var definition = new DynamicMethodDefinition(method))
                {
                    if (!definition.Definition.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference call &&
                        call.Name == "get_GoodBadThingPopModifierMultiplier")) continue;
                    if (found != null) throw new InvalidOperationException("Ambiguous fear calculation callback.");
                    found = method;
                }
            }
            return found ?? throw new MissingMethodException("SE fear calculation callback unavailable.");
        }
        internal static void ReportApplied(int level, int units)
        {
            if (Volatile.Read(ref appliedReported) == 0 && Interlocked.Exchange(ref appliedReported, 1) == 0)
                log?.LogInfo(Stamp("FEAR_POPULARITY_APPLIED signedLevel=" + level + "; roundedPoints=" + (units / 25)));
        }
        internal static void ReportOverflow()
        {
            if (Interlocked.Exchange(ref overflowReported, 1) == 0)
                log?.LogError(Stamp("FEAR_POPULARITY_OVERFLOW original SE contribution preserved."));
        }
        private static string Stamp(string text) => "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + text;
    }
}

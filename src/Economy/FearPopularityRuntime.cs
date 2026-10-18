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
                Internal.InstalledAssemblyIdentity.VerifyModule(assembly, assembly.Location);
                MethodInfo target = FindTarget(assembly);
                // Validate against the installed original before publishing a permanent hook.
                using (var definition = new DynamicMethodDefinition(target))
                using (var context = new ILContext(definition.Definition))
                    context.Invoke(il => FearPopularityIl.Sites(il));
                hook = new ILHook(target, FearPopularityIl.Apply);
            }
            catch (Exception ex) { Diagnostic = ex.Message; throw; }
            Diagnostic = null;
            // API_SHARED_DIAGNOSTICS: startup and first-use proof markers, silent unless opted in.
            NativeApiLog.Info(log, "FEAR_POPULARITY_READY validated SE coefficient/resource flow retained; rounding=away-from-zero; units=points*25");
        }
        internal static MethodInfo FindTarget(Assembly assembly)
        {
            using (var stream = System.IO.File.Open(assembly.Location, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
            using (var image = Mono.Cecil.AssemblyDefinition.ReadAssembly(stream))
            {
                if (image.Name.FullName != assembly.FullName || image.MainModule.Mvid != assembly.ManifestModule.ModuleVersionId)
                    throw new InvalidOperationException("Loaded/disk SE module identity differs.");
                var target = FindTargetDefinition(image.MainModule);
                return assembly.ManifestModule.ResolveMethod(target.MetadataToken.ToInt32()) as MethodInfo
                    ?? throw new MissingMethodException("SE fear calculation is not a managed method.");
            }
        }
        internal static Mono.Cecil.MethodDefinition FindTargetDefinition(Mono.Cecil.ModuleDefinition module)
        {
            var candidates = module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody &&
                m.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference call && FearPopularityIl.IsCoefficientGetter(call)) &&
                m.Body.Instructions.Any(i => (i.OpCode == Mono.Cecil.Cil.OpCodes.Stfld || i.OpCode == Mono.Cecil.Cil.OpCodes.Ldflda) &&
                    i.Operand is Mono.Cecil.FieldReference field && FearPopularityIl.IsContributionField(field))).ToArray();
            if (candidates.Length != 1) throw new InvalidOperationException("Missing or ambiguous fear coefficient/contribution calculation.");
            return candidates[0];
        }
        // Hot path: after the first call only one volatile read remains.
        internal static void ReportApplied(int level, int units)
        {
            if (Volatile.Read(ref appliedReported) == 0 && Interlocked.Exchange(ref appliedReported, 1) == 0)
                try { NativeApiLog.Info(log, "FEAR_POPULARITY_APPLIED signedLevel=" + level + "; roundedPoints=" + (units / 25)); } catch { }
        }
        internal static void ReportOverflow()
        {
            if (Interlocked.Exchange(ref overflowReported, 1) == 0)
                try { Internal.RuntimeHealth.Failure(log, "fear-calculation", true, "FEAR_POPULARITY_OVERFLOW original SE contribution preserved."); } catch { }
        }
    }
}

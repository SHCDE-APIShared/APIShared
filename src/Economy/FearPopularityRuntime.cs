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
                // Startup cost: discovery reads SHCDESE.dll and scans the IL of every method (~0.7 s).
                // The result is remembered per loaded module build (MVID). A remembered target is
                // used only after the same IL-site validation below succeeds on the loaded method;
                // otherwise discovery runs exactly as before.
                MethodInfo target = TryResolveRememberedTarget(assembly);
                if (target != null && !TryValidateSites(target)) target = null;
                if (target == null)
                {
                    Internal.InstalledAssemblyIdentity.VerifyModule(assembly, assembly.Location);
                    target = FindTarget(assembly);
                    // Validate against the installed original before publishing a permanent hook.
                    ValidateSites(target);
                    RememberTarget(assembly, target);
                }
                hook = new ILHook(target, FearPopularityIl.Apply);
            }
            catch (Exception ex) { Diagnostic = ex.Message; throw; }
            Diagnostic = null;
            // One-time startup marker; the first-use proof (ReportApplied) is Debug only.
            NativeApiLog.Info(log, "FEAR_POPULARITY_READY validated SE coefficient/resource flow retained; rounding=away-from-zero; units=points*25");
        }
        private static void ValidateSites(MethodInfo target)
        {
            using (var definition = new DynamicMethodDefinition(target))
            using (var context = new ILContext(definition.Definition))
                context.Invoke(il => FearPopularityIl.Sites(il));
        }

        private static bool TryValidateSites(MethodInfo target)
        {
            try { ValidateSites(target); return true; }
            catch { return false; }
        }

        private static string RememberedTargetPath =>
            System.IO.Path.Combine(BepInEx.Paths.CachePath, "APIShared_FearPopularityTarget.txt");

        // Cache format: "<module MVID> <method metadata token as hex>", one line.
        internal static string FormatRememberedTarget(Guid mvid, int token) =>
            mvid.ToString("D") + " " + token.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);

        internal static bool TryParseRememberedTarget(string text, Guid mvid, out int token)
        {
            token = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string[] parts = text.Trim().Split(' ');
            return parts.Length == 2 && Guid.TryParse(parts[0], out Guid stored) && stored == mvid &&
                int.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out token) && token != 0;
        }

        private static MethodInfo TryResolveRememberedTarget(Assembly assembly)
        {
            try
            {
                string path = RememberedTargetPath;
                if (!System.IO.File.Exists(path) ||
                    !TryParseRememberedTarget(System.IO.File.ReadAllText(path), assembly.ManifestModule.ModuleVersionId, out int token))
                    return null;
                return assembly.ManifestModule.ResolveMethod(token) as MethodInfo;
            }
            catch { return null; }
        }

        private static void RememberTarget(Assembly assembly, MethodInfo target)
        {
            try
            {
                System.IO.Directory.CreateDirectory(BepInEx.Paths.CachePath);
                System.IO.File.WriteAllText(RememberedTargetPath,
                    FormatRememberedTarget(assembly.ManifestModule.ModuleVersionId, target.MetadataToken));
            }
            catch { /* A missing cache only repeats discovery on the next start. */ }
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
                try { NativeApiLog.Debug(log, "FEAR_POPULARITY_APPLIED signedLevel=" + level + "; roundedPoints=" + (units / 25)); } catch { }
        }
        internal static void ReportOverflow()
        {
            if (Interlocked.Exchange(ref overflowReported, 1) == 0)
                try { Internal.RuntimeHealth.Failure(log, "fear-calculation", true, "FEAR_POPULARITY_OVERFLOW original SE contribution preserved."); } catch { }
        }
    }
}

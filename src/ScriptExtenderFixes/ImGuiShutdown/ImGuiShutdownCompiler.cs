using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace APIShared.ScriptExtenderFixes
{
    // Unity Mono's DynamicMethod signature-token API cannot emit the audited calli
    // bodies. Route only their copies (including MonoMod's original backups) through
    // the installed MethodBuilder backend. Never change process-wide DMD settings.
    internal sealed class ImGuiShutdownCompiler
    {
        private delegate MethodInfo OriginalGenerate(DynamicMethodDefinition definition, object context);
        private readonly HashSet<MethodBase> targets;
        // Install-time evidence for ImGuiShutdownProbe; Route runs only while hooks are generated.
        private readonly Dictionary<MethodBase, int> routed = new Dictionary<MethodBase, int>();
        internal readonly Hook Hook;

        internal ImGuiShutdownCompiler(IEnumerable<MethodInfo> methods)
        {
            targets = new HashSet<MethodBase>(methods);
            foreach (var target in targets) routed[target] = 0;
            var generate = typeof(DynamicMethodDefinition).GetMethod("Generate", new[] { typeof(object) });
            if (generate == null || generate.ReturnType != typeof(MethodInfo) || generate.IsStatic)
                throw new MissingMethodException("DynamicMethodDefinition.Generate(object)");
            Hook = new Hook(generate,
                (Func<OriginalGenerate, DynamicMethodDefinition, object, MethodInfo>)Route,
                new HookConfig { ManualApply = true, ID = "APIShared.ScriptExtenderFixes.ImGuiShutdown.Compiler" });
        }

        private MethodInfo Route(OriginalGenerate original, DynamicMethodDefinition definition, object context)
        {
            if (!targets.Contains(definition.OriginalMethod)) return original(definition, context);
            lock (routed) routed[definition.OriginalMethod]++;
            return GenerateAudited(definition, (MethodInfo)definition.OriginalMethod, context);
        }

        internal int RoutedCount(MethodBase method)
        {
            lock (routed) return routed.TryGetValue(method, out int count) ? count : 0;
        }

        internal static MethodInfo GenerateAudited(DynamicMethodDefinition definition, MethodInfo source, object context = null)
        {
            using (var il = new ILContext(definition.Definition))
                ImGuiShutdownIl.NormalizeNativePointerLocals(il, source);
            return DMDGenerator<DMDEmitMethodBuilderGenerator>.Generate(definition, context);
        }
    }
}

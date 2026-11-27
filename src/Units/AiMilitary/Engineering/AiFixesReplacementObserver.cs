using System;
using System.Linq;
using System.Reflection;
using BepInEx.Bootstrap;
using Mono.Cecil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary.Engineering
{
    // Optional Fixes integration. Scope the verified replacement operation itself;
    // a coincidentally matching crew on an unrelated creation is never provenance.
    internal static class AiFixesReplacementObserver
    {
        private delegate void Replacement(GameUnitManagerAPI api, in GameUnit unit, int id, UnmanagedVector2<ushort> position);
        private static readonly Replacement Callback = Invoke;
        private static Replacement original;
        private static Hook hook;
        private static bool attempted;
        internal static void Initialize()
        {
            if (attempted) return;
            attempted = true;
            if (!Chainloader.PluginInfos.TryGetValue("fixes", out var plugin)) return;
            Assembly assembly = plugin.Instance?.GetType().Assembly ?? throw new InvalidOperationException("Fixes did not finish initialization.");
            Internal.InstalledAssemblyIdentity.VerifyModule(assembly, assembly.Location);
            Type type = assembly.GetType("Fixes.Events.FixesAIEvents", true);
            MethodInfo method = type.GetMethod("MoveSiegeUnit", BindingFlags.Static | BindingFlags.NonPublic);
            Validate(method);
            Hook candidate = null; bool publishing = false;
            try
            {
                candidate = new Hook(method, Callback, new HookConfig { ManualApply = true, ID = "APIShared.AiMilitary.FixesReplacement" });
                original = candidate.GenerateTrampoline<Replacement>();
                // Root before the first executable write, including a failed Apply.
                hook = candidate; publishing = true; candidate.Apply();
            }
            catch { if (!publishing) { candidate?.Dispose(); original = null; } throw; }
        }
        internal static void Validate(MethodInfo method)
        {
            if (method == null || method.DeclaringType?.FullName != "Fixes.Events.FixesAIEvents" ||
                method.Name != "MoveSiegeUnit" || !method.IsStatic || !method.IsPrivate || method.ReturnType != typeof(void))
                throw new InvalidOperationException("Fixes replacement method identity differs.");
            Type[] expected = { typeof(GameUnitManagerAPI), typeof(GameUnit).MakeByRefType(), typeof(int), typeof(UnmanagedVector2<ushort>) };
            var parameters = method.GetParameters();
            if (parameters.Length != expected.Length || parameters.Where((p, i) => p.ParameterType != expected[i]).Any() || !parameters[1].IsIn)
                throw new InvalidOperationException("Fixes replacement method signature differs.");
            using (var definition = new DynamicMethodDefinition(method))
            {
                var instructions = definition.Definition.Body.Instructions;
                var deletes = instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(GameUnitManagerAPI).FullName && m.Name == nameof(GameUnitManagerAPI.DeleteUnitSafe)).ToArray();
                var creates = instructions.Where(i => i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(GameUnitManagerAPI).FullName && m.Name == nameof(GameUnitManagerAPI.CreateUnitLocal)).ToArray();
                if (deletes.Length != 1 || creates.Length != 1 || deletes[0].Offset >= creates[0].Offset)
                    throw new InvalidOperationException("Fixes replacement delete/create operation differs.");
            }
        }
        private static void Invoke(GameUnitManagerAPI api, in GameUnit unit, int id, UnmanagedVector2<ushort> position)
        {
            AiMilitaryEngineeringRuntime.ReplacementScope scope = null;
            try { scope = AiMilitaryEngineeringRuntime.BeginReplacement(id, unit.r_GlobalId); }
            catch (Exception error) { AiMilitaryEngineeringRuntime.ReportIntegration(error); }
            bool completed = false;
            try { original(api, in unit, id, position); completed = true; }
            finally
            {
                try { AiMilitaryEngineeringRuntime.EndReplacement(scope, completed); }
                catch (Exception error) { AiMilitaryEngineeringRuntime.ReportIntegration(error); }
            }
        }
    }
}

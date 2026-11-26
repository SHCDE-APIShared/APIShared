using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;

namespace APIShared.Buildings
{
    internal static unsafe partial class HumanConstructionRuntime
    {
        private static FieldInfo refundContextId, refundContextPercentage;

        private static void InitializeRefundInterceptor(Assembly assembly)
        {
            Type owner = assembly.GetType("SHCDESE.Detours.BulkBuildingDetours", true);
            refundContextId = RequireRefundField(owner, "RefundContextBuildingId");
            refundContextPercentage = RequireRefundField(owner, "RefundContextPercentage");
            MethodInfo callback = FindRefundInterceptor(assembly);
            if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "Construction.RefundContextIdentity",
                callback, TransformRefundInterceptor, log, out var hook, out string reason))
                throw new InvalidOperationException(reason);
            hooks.Add(hook);
        }

        private static FieldInfo RequireRefundField(Type owner, string name)
        {
            FieldInfo field = owner.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null || field.FieldType != typeof(int)) throw new MissingFieldException(owner.FullName, name);
            return field;
        }

        internal static MethodInfo FindRefundInterceptor(Assembly assembly)
        {
            Type owner = assembly.GetType("SHCDESE.Detours.BulkBuildingDetours", true);
            MethodInfo found = null;
            foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            foreach (var method in nested.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (method.GetMethodBody() == null) continue;
                using (var definition = new DynamicMethodDefinition(method))
                {
                    if (!definition.Definition.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldsfld &&
                        i.Operand is FieldReference field && field.DeclaringType.FullName == owner.FullName && field.Name == "RefundContextBuildingId")) continue;
                }
                if (found != null) throw new InvalidOperationException("Multiple Script Extender refund identity callbacks.");
                found = method;
            }
            return found ?? throw new MissingMethodException("Script Extender refund identity callback unavailable.");
        }

        // SE's +29*8 stack probe addresses its caller's frame, which changes when a managed
        // Original wrapper is added. Its entry detour already supplies the authoritative ID.
        // Replace only that initial read; retain every material multiplier/register write.
        internal static void TransformRefundInterceptor(ILContext il)
        {
            var code = il.Body.Instructions;
            FieldReference context = code.Where(i => i.OpCode == OpCodes.Ldsfld && i.Operand is FieldReference f &&
                f.DeclaringType.FullName == "SHCDESE.Detours.BulkBuildingDetours" && f.Name == "RefundContextBuildingId")
                .Select(i => (FieldReference)i.Operand).Single();
            if (code.Count < 8 || code[0].OpCode != OpCodes.Ldarg_1 || code[1].OpCode != OpCodes.Ldfld ||
                !(code[2].Operand is MethodReference size) || size.DeclaringType.FullName != "System.IntPtr" || size.Name != "get_Size" ||
                !code[3].MatchLdcI4(29) || code[4].OpCode != OpCodes.Mul ||
                !(code[5].Operand is GenericInstanceMethod probe) || probe.Name != "GetStackPtr" ||
                probe.GenericArguments.Count != 1 || probe.GenericArguments[0].MetadataType != MetadataType.Int32 ||
                code[6].OpCode != OpCodes.Ldind_I4 || code[7].OpCode != OpCodes.Stloc_0)
                throw new InvalidOperationException("Script Extender refund stack-read contract changed.");
            code[0].OpCode = OpCodes.Ldsfld; code[0].Operand = context;
            for (int index = 1; index <= 6; index++) { code[index].OpCode = OpCodes.Nop; code[index].Operand = null; }
        }

        // Generic policies run after SE's Pre event and may change the final percentage.
        // Keep the material interceptor's context aligned and restore nested calls safely.
        private static object InvokeRefundOriginal(Delegate original, object[] args)
        {
            int priorId = (int)refundContextId.GetValue(null);
            int priorPercentage = (int)refundContextPercentage.GetValue(null);
            refundContextId.SetValue(null, (int)args[1]);
            refundContextPercentage.SetValue(null, (int)args[3]);
            try { return Invoke(original, args); }
            finally
            {
                refundContextId.SetValue(null, priorId);
                refundContextPercentage.SetValue(null, priorPercentage);
            }
        }
    }
}

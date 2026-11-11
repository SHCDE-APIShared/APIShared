using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.Interop.Enums;
using System;
using System.Reflection;

namespace APIShared.ScriptExtenderFixes
{
    internal static class WorldPopularityIl
    {
        internal static MethodInfo FindTarget()
        {
            var parameters = new Type[14];
            for (int i = 0; i < parameters.Length; i++) parameters[i] = i == 5 ? typeof(float) : typeof(int);
            var target = typeof(GameMap).GetMethod("addUpdateFly", BindingFlags.Public | BindingFlags.Instance,
                null, parameters, null);
            if (target == null || target.ReturnType != typeof(void))
                throw new MissingMethodException("Public GameMap.addUpdateFly signature differs from the popup audit.");
            using (var definition = new DynamicMethodDefinition(target))
            using (var context = new ILContext(definition.Definition))
            {
                context.Invoke(Apply);
            }
            return target;
        }

        private static void ValidateField(ILContext context, string name, int argument)
        {
            int matches = 0;
            foreach (var instruction in context.Body.Instructions)
                if (instruction.OpCode == OpCodes.Stfld && instruction.Operand is FieldReference field &&
                    field.DeclaringType.FullName == "Fly" && field.Name == name)
                {
                    if (field.FieldType.FullName != "System.Int32" || instruction.Previous == null ||
                        !instruction.Previous.MatchLdarg(argument))
                        throw new InvalidOperationException("World popup field/argument mapping changed: " + name);
                    matches++;
                }
            if (matches != 1) throw new InvalidOperationException("World popup field assignment changed: " + name);
        }

        internal static void Validate(ILContext context)
        {
            var method = context.Method;
            int first = method.HasThis ? 0 : 1;
            // DMD represents the instance as an explicit first parameter. IL argument
            // numbers stay identical in both representations.
            if (method.ReturnType.FullName != "System.Void" || method.Parameters.Count != 14 + first ||
                (first == 1 && method.Parameters[0].ParameterType.MetadataType != MetadataType.Class) ||
                context.Body.Instructions.Count == 0)
                throw new InvalidOperationException("World popup IL signature changed.");
            for (int i = 0; i < 14; i++)
                if (method.Parameters[i + first].ParameterType.FullName != (i == 5 ? "System.Single" : "System.Int32"))
                    throw new InvalidOperationException("World popup IL parameter type changed.");
            ValidateField(context, "gameObjectType", 12);
            ValidateField(context, "infoType", 13);
            ValidateField(context, "infoData", 14);
        }

        internal static void Apply(ILContext context)
        {
            Validate(context);
            int first = context.Method.HasThis ? 0 : 1;
            // Insert before the existing entry without retargeting its internal branches.
            // Only infoType changes; Vanilla still owns the complete Fly cache and digits.
            var cursor = new ILCursor(context);
            cursor.Emit(OpCodes.Ldarg, context.Method.Parameters[11 + first]);
            cursor.Emit(OpCodes.Ldarg, context.Method.Parameters[12 + first]);
            cursor.Emit(OpCodes.Ldarg, context.Method.Parameters[13 + first]);
            cursor.Emit(OpCodes.Call, typeof(WorldPopularityIl).GetMethod(nameof(Normalize), BindingFlags.Static | BindingFlags.NonPublic));
            cursor.Emit(OpCodes.Starg, context.Method.Parameters[12 + first]);
        }

        internal static int Normalize(int projectileType, int originalMask, int displayedPopularity) =>
            WorldPopularityPolicy.Normalize(projectileType == (int)ProjectileType.EconomyIndicator2, originalMask, displayedPopularity);
    }
}

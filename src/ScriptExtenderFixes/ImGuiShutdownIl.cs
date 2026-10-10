using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.ScriptExtenderFixes
{
    // All edits target managed dispatch. Existing unmanaged calli sites retain their ABI.
    internal static class ImGuiShutdownIl
    {
        internal static void NormalizeNativePointerLocals(ILContext il, MethodInfo source)
        {
            // Loader-patched game assemblies may exist only in memory. Their ordinary
            // local types need no correction and must never be treated as disk paths.
            if (!il.Body.Variables.Any(local => local.VariableType is FunctionPointerType ||
                local.VariableType.FullName == "System.MonoFNPtrFakeClass")) return;
            // Mono's reflected function-pointer Type may lose its Cecil identity.
            // Use the hash-validated original metadata to identify precisely those slots.
            using (var module = ModuleDefinition.ReadModule(source.Module.Assembly.Location))
            {
                var original = (MethodDefinition)module.LookupToken(source.MetadataToken);
                if (original.Body.Variables.Count > il.Body.Variables.Count)
                    throw new InvalidOperationException("Native-pointer local contract changed: " + source.Name);
                for (int index = 0; index < original.Body.Variables.Count; index++)
                    if (original.Body.Variables[index].VariableType is FunctionPointerType)
                        il.Body.Variables[index].VariableType = il.Method.Module.TypeSystem.IntPtr;
            }
        }

        internal static void GuardRender(ILContext il, Func<bool> bypass, string trampoline, int argumentCount)
        {
            // Unity Mono cannot resolve function-pointer local types for SRE DeclareLocal.
            // These locals only store native addresses; IntPtr has the same CLI stack/storage
            // representation. Keep every calli signature and field signature unchanged.
            foreach (var local in il.Body.Variables)
                if (local.VariableType is FunctionPointerType)
                    local.VariableType = il.Method.Module.TypeSystem.IntPtr;

            var pointer = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldsfld &&
                i.Operand is FieldReference f && f.Name == trampoline);
            int start = il.Body.Instructions.IndexOf(pointer);
            var call = il.Body.Instructions.Skip(start).First(i => i.OpCode == OpCodes.Calli);
            var site = (CallSite)call.Operand;
            if (site.Parameters.Count != argumentCount || site.ReturnType.MetadataType != MetadataType.Int32 ||
                site.CallingConvention != MethodCallingConvention.ThisCall)
                throw new InvalidOperationException("Unexpected native trampoline ABI: " + trampoline);

            // Emit a separate native-only return, outside the original exception regions.
            var first = il.Body.Instructions[0];
            var cursor = new ILCursor(il);
            cursor.EmitDelegate(bypass);
            cursor.Emit(OpCodes.Brfalse, first);
            for (int index = 0; index < argumentCount; index++) cursor.Emit(OpCodes.Ldarg, il.Method.Parameters[index]);
            cursor.Emit(OpCodes.Ldsfld, (FieldReference)pointer.Operand);
            cursor.Emit(OpCodes.Calli, site);
            cursor.Emit(OpCodes.Ret);
        }

        internal static void GuardWindow(ILContext il, Func<uint, bool> forward,
            Func<IntPtr, uint, IntPtr, IntPtr, IntPtr> dispatch)
        {
            var first = il.Body.Instructions[0];
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate(forward);
            cursor.Emit(OpCodes.Brfalse, first);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.Emit(OpCodes.Ldarg_2);
            cursor.Emit(OpCodes.Ldarg_3);
            cursor.EmitDelegate(dispatch);
            cursor.Emit(OpCodes.Ret);
        }

        internal static void GuardSetter(ILContext il, Func<IntPtr, IntPtr> remember)
        {
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_1);
            cursor.EmitDelegate(remember);
            cursor.Emit(OpCodes.Starg, il.Method.Parameters[il.Method.HasThis ? 0 : 1]);
        }

        internal static void GuardUnhook(ILContext il, Func<bool, bool> bypass)
        {
            var first = il.Body.Instructions[0];
            var cursor = new ILCursor(il);
            cursor.Emit(OpCodes.Ldarg_0);
            cursor.EmitDelegate(bypass);
            cursor.Emit(OpCodes.Brfalse, first);
            cursor.Emit(OpCodes.Ret);
        }

        internal static void GuardDisable(ILContext il, Func<bool> retain)
        {
            // Preserve the entire managed OnDestroyed/null-assignment prefix.
            var clear = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                i.Operand is FieldReference f && f.Name == "currentContext");
            var cursor = new ILCursor(il) { Index = il.Body.Instructions.IndexOf(clear) + 1 };
            var originalTail = cursor.Next;
            cursor.EmitDelegate(retain);
            cursor.Emit(OpCodes.Brfalse, originalTail);
            cursor.Emit(OpCodes.Ret);
        }

        internal static void Begin(ILContext il, Action begin)
        {
            new ILCursor(il).EmitDelegate(begin);
        }
    }
}

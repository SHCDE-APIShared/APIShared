using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.Interop;
using CilInstruction = Mono.Cecil.Cil.Instruction;
using CilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace APIShared.Economy
{
    // SE already owns native B6100. Adapt only its managed Original.Invoke after mutable
    // SE Pre, leaving the existing Post inputs, exception path and opaque Int64 return intact.
    internal static unsafe class NativeStorageSeContract
    {
        internal const string TypeName = "SHCDESE.Detours.BulkBuildingDetours";
        internal const string MethodName = "c_game_add_good_to_goodsyard_hook_impl";
        internal const string DelegateName = "c_game_add_good_to_goodsyard_delegate";
        internal const string HandleName = "c_game_add_good_to_goodsyard_hook";
        private const string ArgsName = "SHCDESE.EventAPI.Buildings.AddGoodToGoodsyardEventArgs";
        private const string EventsName = "SHCDESE.EventAPI.BuildingR3EventHooks";
        private const string HandleTypeName = "RedBird.Abstractions.Hooks.Transaction.DetourHandle`1";
        private static readonly Type ManagerType = typeof(NativePointer<GameBuildingManager>);
        private static readonly Type[] Parameters = { ManagerType, typeof(int), typeof(int), typeof(eGoods), typeof(int), typeof(int), typeof(int) };

        internal static MethodInfo Resolve()
        {
            Type type = typeof(GameBuildingManagerAPI).Assembly.GetType(TypeName, true);
            MethodInfo method = type.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static, null, Parameters, null);
            if (method == null || method.ReturnType != typeof(long) || !method.IsStatic || !method.IsPublic)
                throw new MissingMethodException("The installed SE native-storage wrapper signature changed.");
            return method;
        }

        internal static Type OriginalDelegateType(MethodInfo method)
        {
            if (method == null || method.Name != MethodName || method.DeclaringType?.FullName != TypeName || method.ReturnType != typeof(long) ||
                !method.IsStatic || !method.IsPublic || !method.GetParameters().Select(p => p.ParameterType).SequenceEqual(Parameters))
                throw Invalid();
            Type type = method.DeclaringType.GetNestedType(DelegateName, BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo invoke = type?.GetMethod("Invoke");
            if (type == null || !typeof(Delegate).IsAssignableFrom(type) || invoke == null || invoke.ReturnType != typeof(long) ||
                !invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(Parameters)) throw Invalid();
            return type;
        }

        internal static Func<Delegate, IntPtr, int, int, int, int, int, int, long> CreateForwarder(Type delegateType)
        {
            MethodInfo invoke = delegateType?.GetMethod("Invoke");
            if (delegateType == null || !typeof(Delegate).IsAssignableFrom(delegateType) || invoke == null ||
                invoke.ReturnType != typeof(long) || !invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(Parameters)) throw Invalid();
            ConstructorInfo manager = ManagerType.GetConstructor(new[] { typeof(IntPtr) });
            if (manager == null) throw Invalid();
            // Bind one exact checked thunk at startup; no reflection, DynamicInvoke or
            // delegate allocation occurs during forwarding.
            var method = new DynamicMethod("APIShared.NativeStorage.ForwardSEOriginal", typeof(long),
                new[] { typeof(Delegate), typeof(IntPtr), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
                typeof(NativeStorageSeContract), true);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, delegateType);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Newobj, manager);
            for (short index = 2; index <= 7; index++) il.Emit(OpCodes.Ldarg, index);
            il.Emit(OpCodes.Callvirt, invoke); il.Emit(OpCodes.Ret);
            return (Func<Delegate, IntPtr, int, int, int, int, int, int, long>)method.CreateDelegate(
                typeof(Func<Delegate, IntPtr, int, int, int, int, int, int, long>));
        }

        internal static void Validate(MethodInfo method)
        {
            OriginalDelegateType(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition)) Find(il);
        }

        internal static CilInstruction Find(ILContext il)
        {
            var method = il.Method;
            // Cecil uses its generic-instance notation rather than reflection's assembly-qualified form.
            const string manager = "RedBird.Core.Memory.NativePointer`1<SHCDESE.Interop.GameBuildingManager>";
            string[] parameters = { manager, "System.Int32", "System.Int32", "SHCDESE.Interop.eGoods", "System.Int32", "System.Int32", "System.Int32" };
            if (method.HasThis || method.ReturnType.MetadataType != MetadataType.Int64 ||
                !method.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters) || il.Body.ExceptionHandlers.Count != 0) throw Invalid();
            IList<CilInstruction> body = il.Body.Instructions;
            // This deliberately binds the installed straight-line wrapper. Additional branches,
            // callbacks or return normalization require an explicit upstream-contract re-audit.
            if (body.Count != 47) throw Invalid();
            int at = ReadEventBlock(body, 0, 0, out int pre);
            if (!LocalIsArgs(il, pre) || !Field(body[at++], TypeName, HandleName) ||
                !Original(body[at++]) || !body[at++].MatchLdarg(0)) throw Invalid();
            string[] getters = { "get_BuildingId", "get_BuildingGlobalId", "get_Good", "get_AddAmount", "get_Capacity", "get_Add" };
            string[] returns = { "System.Int32", "System.Int32", "SHCDESE.Interop.eGoods", "System.Int32", "System.Int32", "System.Boolean" };
            for (int index = 0; index < getters.Length; index++)
                if (!body[at++].MatchLdloc(pre) || !Call(body[at++], ArgsName, getters[index], returns[index])) throw Invalid();
            if (!body[at++].MatchLdcI4(0) || body[at++].OpCode != CilOpCodes.Cgt_Un) throw Invalid();
            CilInstruction call = body[at++];
            if (!(call.Operand is MethodReference invoke) || call.OpCode != CilOpCodes.Callvirt || invoke.Name != "Invoke" ||
                invoke.DeclaringType.FullName != TypeName + "/" + DelegateName || invoke.ReturnType.MetadataType != MetadataType.Int64 ||
                !invoke.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(parameters)) throw Invalid();
            at = ReadEventBlock(body, at, 1, out int post);
            if (!LocalIsArgs(il, post) || at != body.Count - 1 || body[at].OpCode != CilOpCodes.Ret) throw Invalid();
            return call;
        }

        private static int ReadEventBlock(IList<CilInstruction> body, int at, int phase, out int local)
        {
            if (at < 0 || at > body.Count - 14 || !body[at++].MatchLdcI4(phase)) throw Invalid();
            for (int argument = 1; argument <= 6; argument++) if (!body[at++].MatchLdarg(argument)) throw Invalid();
            if (!body[at++].MatchLdcI4(1) || body[at++].OpCode != CilOpCodes.Ceq) throw Invalid();
            CilInstruction create = body[at++];
            string[] constructor = { "SHCDESE.EventAPI.EventHookPhase", "System.Int32", "System.Int32", "SHCDESE.Interop.eGoods", "System.Int32", "System.Int32", "System.Boolean" };
            if (create.OpCode != CilOpCodes.Newobj || !(create.Operand is MethodReference ctor) || ctor.Name != ".ctor" ||
                ctor.DeclaringType.FullName != ArgsName || !ctor.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(constructor) ||
                !body[at++].MatchStloc(out local) || !Field(body[at++], EventsName, "OnGoodsyardAddGood") || !body[at++].MatchLdloc(local)) throw Invalid();
            CilInstruction raise = body[at++];
            if (raise.OpCode != CilOpCodes.Callvirt || !(raise.Operand is MethodReference method) || method.Name != "Raise" ||
                method.ReturnType.MetadataType != MetadataType.Void || method.Parameters.Count != 1 ||
                !(method.DeclaringType is GenericInstanceType hook) || hook.ElementType.FullName != "SHCDESE.EventAPI.R3EventHook`1" ||
                hook.GenericArguments.Count != 1 || hook.GenericArguments[0].FullName != ArgsName) throw Invalid();
            return at;
        }

        private static bool LocalIsArgs(ILContext il, int local) => local >= 0 && local < il.Body.Variables.Count &&
            il.Body.Variables[local].VariableType.FullName == ArgsName;
        private static bool Field(CilInstruction instruction, string owner, string name) => instruction.OpCode == CilOpCodes.Ldsfld &&
            instruction.Operand is FieldReference field && field.DeclaringType.FullName == owner && field.Name == name;
        private static bool Call(CilInstruction instruction, string owner, string name, string returns) => instruction.OpCode == CilOpCodes.Callvirt &&
            instruction.Operand is MethodReference method && method.DeclaringType.FullName == owner && method.Name == name &&
            method.ReturnType.FullName == returns && method.Parameters.Count == 0;
        private static bool Original(CilInstruction instruction) => instruction.OpCode == CilOpCodes.Callvirt &&
            instruction.Operand is MethodReference method && method.Name == "get_Original" && method.Parameters.Count == 0 &&
            method.DeclaringType is GenericInstanceType handle && handle.ElementType.FullName == HandleTypeName &&
            handle.GenericArguments.Count == 1 && handle.GenericArguments[0].FullName == TypeName + "/" + DelegateName;

        internal static void Apply(ILContext il, Func<Delegate, IntPtr, int, int, int, int, int, int, long> adapter)
        {
            if (adapter == null) throw new ArgumentNullException(nameof(adapter));
            CilInstruction call = Find(il);
            var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before); cursor.Remove();
            // Construct this typed thunk once while preparing the IL. Its exact stack signature
            // consumes SE's NativePointer and enum without changing any Pre/Post instructions.
            cursor.EmitDelegate<Func<Delegate, NativePointer<GameBuildingManager>, int, int, eGoods, int, int, int, long>>(
                (original, manager, building, globalId, good, amount, capacity, add) =>
                    adapter(original, (IntPtr)manager.Pointer, building, globalId, (int)good, amount, capacity, add));
        }

        internal static void Apply(ILContext il, Func<Delegate, IntPtr, int, int, int, int, int, int, long> adapter,
            Action enter, Action leave)
        {
            if (enter == null) throw new ArgumentNullException(nameof(enter));
            if (leave == null) throw new ArgumentNullException(nameof(leave));
            Apply(il, adapter);
            var returns = il.Body.Instructions.Where(i => i.OpCode == CilOpCodes.Ret).ToArray();
            if (returns.Length != 1) throw Invalid();
            var cursor = new ILCursor(il);
            CilInstruction first = il.Body.Instructions[0];
            cursor.Goto(first, MoveType.Before); cursor.EmitDelegate(enter);
            // Depth starts before SE Pre and covers nested entries from Pre, Original and Post.
            // Keep the unmodified opaque result in an Int64 local while finally closes depth,
            // including exits caused by the adapter or an upstream SE callback exception.
            var result = new Mono.Cecil.Cil.VariableDefinition(il.Import(typeof(long))); il.Body.Variables.Add(result);
            CilInstruction ret = returns[0];
            ret.OpCode = CilOpCodes.Stloc; ret.Operand = result;
            var after = CilInstruction.Create(CilOpCodes.Ldloc, result);
            cursor.Goto(ret, MoveType.After); cursor.Emit(CilOpCodes.Leave, after);
            cursor.Emit(CilOpCodes.Nop); CilInstruction finallyStart = cursor.Prev;
            cursor.EmitDelegate(leave); cursor.Emit(CilOpCodes.Endfinally);
            il.Body.Instructions.Add(after); il.Body.Instructions.Add(CilInstruction.Create(CilOpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new Mono.Cecil.Cil.ExceptionHandler(Mono.Cecil.Cil.ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
        }

        internal static void ValidateNativeOwner(MethodInfo method, IntPtr expectedTarget)
        {
            OriginalDelegateType(method);
            FieldInfo field = method.DeclaringType.GetField(HandleName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object handle = field?.GetValue(null);
            object hook = handle?.GetType().GetProperty("Hook")?.GetValue(handle, null);
            object target = hook?.GetType().GetProperty("TargetAddress")?.GetValue(hook, null);
            object installed = hook?.GetType().GetProperty("IsInstalled")?.GetValue(hook, null);
            if (!(target is ulong address) || address != unchecked((ulong)expectedTarget.ToInt64()) || !Equals(installed, true))
                throw new InvalidOperationException("The SE native-storage owner is not installed at the audited entry.");
        }

        private static InvalidOperationException Invalid() => new InvalidOperationException("The installed SE native-storage Pre/Original/Post IL contract changed.");
    }
}

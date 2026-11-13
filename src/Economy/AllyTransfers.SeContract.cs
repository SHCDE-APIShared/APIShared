using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.Interop;
using CilInstruction = Mono.Cecil.Cil.Instruction;
using CilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace APIShared.Economy
{
    internal static class AllyTransfersSeContract
    {
        internal const string TypeName = "SHCDESE.Detours.BulkPlayerDetours";
        internal const string MethodName = "c_game_player_ai_send_good_hook_impl";
        internal const string DelegateName = "c_game_player_ai_send_good_delegate";
        internal const string HandleName = "c_game_player_ai_send_good_hook";
        private const string ArgsName = "SHCDESE.EventAPI.Player.PlayerAIRequestReceivedGoodsEventArg";
        internal static MethodInfo Resolve()
        {
            Type type = typeof(GamePlayerManagerAPI).Assembly.GetType(TypeName, true);
            MethodInfo method = type.GetMethod(MethodName, BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(int), typeof(eGoods), typeof(int), typeof(int) }, null);
            if (method == null || method.ReturnType != typeof(int) || !method.IsStatic || !method.IsPublic)
                throw new MissingMethodException("The installed SE ally-transfer wrapper signature changed.");
            return method;
        }
        internal static Type OriginalDelegateType(MethodInfo method)
        {
            Type type = method.DeclaringType.GetNestedType(DelegateName, BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo invoke = type?.GetMethod("Invoke");
            if (type == null || !typeof(Delegate).IsAssignableFrom(type) || invoke == null || invoke.ReturnType != typeof(int) ||
                !invoke.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { typeof(int), typeof(eGoods), typeof(int), typeof(int) }))
                throw new InvalidOperationException("The installed SE ally-transfer delegate signature changed.");
            return type;
        }
        internal static AllyTransferSeForward CreateForwarder(Type delegateType)
        {
            // The enclosing SE class is internal. Bind one exact typed forwarding thunk after
            // validating its real loaded signature; never cast unrelated delegate classes.
            var method = new DynamicMethod("APIShared.AllyTransfers.ForwardSEOriginal", typeof(int),
                new[] { typeof(Delegate), typeof(int), typeof(eGoods), typeof(int), typeof(int) }, typeof(AllyTransfersSeContract), true);
            var il = method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0); il.Emit(System.Reflection.Emit.OpCodes.Castclass, delegateType);
            for (short index = 1; index <= 4; index++) il.Emit(System.Reflection.Emit.OpCodes.Ldarg, index);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt, delegateType.GetMethod("Invoke"));
            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (AllyTransferSeForward)method.CreateDelegate(typeof(AllyTransferSeForward));
        }
        internal static void Validate(MethodInfo method)
        {
            OriginalDelegateType(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition)) Find(il);
        }
        internal static CilInstruction Find(ILContext il)
        {
            var body = il.Body.Instructions;
            var calls = body.Where(i => i.OpCode == CilOpCodes.Callvirt && i.Operand is MethodReference m &&
                m.Name == "Invoke" && m.DeclaringType.FullName == TypeName + "/" + DelegateName &&
                m.ReturnType.MetadataType == MetadataType.Int32 && m.Parameters.Count == 4).ToArray();
            if (calls.Length != 1 || il.Body.ExceptionHandlers.Count != 0) throw Invalid();
            CilInstruction call = calls[0]; int at = body.IndexOf(call);
            if (at < 13 || !body[at - 8].MatchLdloc(out int local)) throw Invalid();
            string[] getters = { "get_TargetPlayerId", "get_Good", "get_Amount", "get_SourcePlayerId" };
            for (int index = 0; index < getters.Length; index++)
                if (!body[at - 8 + index * 2].MatchLdloc(local) ||
                    !(body[at - 7 + index * 2].Operand is MethodReference getter) ||
                    getter.DeclaringType.FullName != ArgsName || getter.Name != getters[index]) throw Invalid();
            if (!(body[at - 10].Operand is FieldReference field) || field.Name != HandleName ||
                field.DeclaringType.FullName != TypeName ||
                !(body[at - 9].Operand is MethodReference original) || original.Name != "get_Original" ||
                !body[at - 13].MatchLdloc(local) || !(body[at - 12].Operand is MethodReference skip) ||
                skip.Name != "get_SkipOriginalFunction" ||
                (body[at - 11].OpCode != CilOpCodes.Brtrue && body[at - 11].OpCode != CilOpCodes.Brtrue_S) ||
                Target(body[at - 11].Operand) == null || body.IndexOf(Target(body[at - 11].Operand)) <= at) throw Invalid();
            CilInstruction skipTarget = Target(body[at - 11].Operand);
            var raises = body.Where(i => i.Operand is MethodReference m && m.Name == "Raise" &&
                m.DeclaringType is GenericInstanceType generic && generic.GenericArguments.Count == 1 &&
                generic.GenericArguments[0].FullName == ArgsName).ToArray();
            if (raises.Length != 2 || body.IndexOf(raises[0]) >= at - 13 || body.IndexOf(raises[1]) <= at ||
                body.IndexOf(skipTarget) <= body.IndexOf(raises[1])) throw Invalid();
            // Incoming branches/exception boundaries cannot enter the argument-building slice.
            var slice = new HashSet<CilInstruction>(body.Skip(at - 10).Take(11));
            foreach (var instruction in body)
                if (Target(instruction.Operand) is CilInstruction target && slice.Contains(target) ||
                    instruction.Operand is CilInstruction[] targets && targets.Any(slice.Contains) ||
                    instruction.Operand is ILLabel[] labels && labels.Any(label => slice.Contains(label.Target))) throw Invalid();
            return call;
        }
        private static CilInstruction Target(object operand) => operand as CilInstruction ?? (operand as ILLabel)?.Target;
        internal static void Apply(ILContext il, Func<Delegate, int, eGoods, int, int, int> adapter, Action enter, Action leave)
        {
            CilInstruction call = Find(il);
            var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before); cursor.Remove();
            cursor.EmitDelegate(adapter);
            // Whole-wrapper depth distinguishes the service root from gifts nested in SE
            // Pre, Original or Post. Finally also closes it when a subscriber faults.
            var returns = il.Body.Instructions.Where(i => i.OpCode == CilOpCodes.Ret).ToArray();
            if (returns.Length != 1) throw Invalid();
            CilInstruction first = il.Body.Instructions[0];
            cursor.Goto(first, MoveType.Before); cursor.EmitDelegate(enter);
            var result = new VariableDefinition(il.Import(typeof(int))); il.Body.Variables.Add(result);
            CilInstruction ret = returns[0];
            ret.OpCode = CilOpCodes.Stloc; ret.Operand = result;
            var after = CilInstruction.Create(CilOpCodes.Ldloc, result);
            cursor.Goto(ret, MoveType.After); cursor.Emit(CilOpCodes.Leave, after);
            cursor.Emit(CilOpCodes.Nop); CilInstruction finallyStart = cursor.Prev;
            cursor.EmitDelegate(leave); cursor.Emit(CilOpCodes.Endfinally);
            il.Body.Instructions.Add(after); il.Body.Instructions.Add(CilInstruction.Create(CilOpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new Mono.Cecil.Cil.ExceptionHandler(ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
        }
        internal static void ValidateNativeOwner(MethodInfo method, IntPtr expectedTarget)
        {
            FieldInfo field = method.DeclaringType.GetField(HandleName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object handle = field?.GetValue(null);
            object hook = handle?.GetType().GetProperty("Hook")?.GetValue(handle, null);
            object target = hook?.GetType().GetProperty("TargetAddress")?.GetValue(hook, null);
            object installed = hook?.GetType().GetProperty("IsInstalled")?.GetValue(hook, null);
            if (!(target is ulong address) || address != unchecked((ulong)expectedTarget.ToInt64()) || !Equals(installed, true))
                throw new InvalidOperationException("The SE ally-transfer native owner is not installed at the audited entry.");
        }
        private static InvalidOperationException Invalid() => new InvalidOperationException("The installed SE ally-transfer Pre/Original/Post IL contract changed.");
    }
}

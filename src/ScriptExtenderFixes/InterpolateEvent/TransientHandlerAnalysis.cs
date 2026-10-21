using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace APIShared.ScriptExtenderFixes
{
    // Conservative IL proof that an event handler cannot retain or forward its event-args object.
    // The handler qualifies only when every load of the args parameter is consumed directly by an
    // allowed parameterless getter on the args object, a null test (brtrue/brfalse) or pop. Anything
    // else (storing, passing, boxing, closures, address-taking, dup, setters, unknown calls) refuses.
    // A refusal is never an error: the caller then keeps the original per-call allocation.
    internal static class TransientHandlerAnalysis
    {
        private static readonly Dictionary<short, OpCode> OpCodesByValue = BuildOpCodeTable();

        internal static bool IsTransient(MethodInfo method, Type argsType, Func<MethodBase, bool> isAllowedGetter)
        {
            if (method == null || argsType == null || isAllowedGetter == null) return false;
            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 1 || !parameters[0].ParameterType.IsAssignableFrom(argsType)) return false;
            byte[] il;
            try { il = method.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception) { return false; }
            if (il == null || il.Length == 0) return false;
            int argsIndex = method.IsStatic ? 0 : 1;
            Type[] typeArguments = method.DeclaringType != null && method.DeclaringType.IsGenericType
                ? method.DeclaringType.GetGenericArguments() : Type.EmptyTypes;
            Type[] methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes;
            var instructions = new List<Instruction>();
            if (!TryDecode(il, instructions)) return false;
            for (int i = 0; i < instructions.Count; i++)
            {
                Instruction instruction = instructions[i];
                if (instruction.OpCode == OpCodes.Jmp) return false;
                int? address = AddressedArgument(instruction);
                if (address == argsIndex) return false;
                if (LoadedArgument(instruction) != argsIndex) continue;
                if (i + 1 >= instructions.Count) return false;
                Instruction consumer = instructions[i + 1];
                if (IsNullTest(consumer.OpCode) || consumer.OpCode == OpCodes.Pop) continue;
                if (consumer.OpCode != OpCodes.Call && consumer.OpCode != OpCodes.Callvirt) return false;
                MethodBase called;
                try { called = method.Module.ResolveMethod(consumer.Token, typeArguments, methodArguments); }
                catch (Exception) { return false; }
                if (called == null || called.IsStatic || called.GetParameters().Length != 0 || !isAllowedGetter(called))
                    return false;
            }
            return true;
        }

        private struct Instruction
        {
            public OpCode OpCode;
            public int Operand;
            public int Token => Operand;
        }

        private static bool IsNullTest(OpCode code) =>
            code == OpCodes.Brtrue || code == OpCodes.Brtrue_S || code == OpCodes.Brfalse || code == OpCodes.Brfalse_S;

        private static int? LoadedArgument(Instruction instruction)
        {
            OpCode code = instruction.OpCode;
            if (code == OpCodes.Ldarg_0) return 0;
            if (code == OpCodes.Ldarg_1) return 1;
            if (code == OpCodes.Ldarg_2) return 2;
            if (code == OpCodes.Ldarg_3) return 3;
            if (code == OpCodes.Ldarg_S || code == OpCodes.Ldarg) return instruction.Operand;
            return null;
        }

        // Address-taking and stores make later reads untraceable; refuse them for the args parameter.
        private static int? AddressedArgument(Instruction instruction)
        {
            OpCode code = instruction.OpCode;
            if (code == OpCodes.Ldarga_S || code == OpCodes.Ldarga || code == OpCodes.Starg_S || code == OpCodes.Starg)
                return instruction.Operand;
            return null;
        }

        private static bool TryDecode(byte[] il, List<Instruction> output)
        {
            int position = 0;
            while (position < il.Length)
            {
                short value = il[position++];
                if (value == 0xFE)
                {
                    if (position >= il.Length) return false;
                    value = unchecked((short)(0xFE00 | il[position++]));
                }
                if (!OpCodesByValue.TryGetValue(value, out OpCode code)) return false;
                int operand = 0;
                switch (code.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        if (position + 1 > il.Length) return false;
                        operand = il[position];
                        position += 1;
                        break;
                    case OperandType.InlineVar:
                        if (position + 2 > il.Length) return false;
                        operand = BitConverter.ToUInt16(il, position);
                        position += 2;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        if (position + 8 > il.Length) return false;
                        position += 8;
                        break;
                    case OperandType.InlineSwitch:
                        if (position + 4 > il.Length) return false;
                        int count = BitConverter.ToInt32(il, position);
                        if (count < 0 || count > (il.Length - position - 4) / 4) return false;
                        position += 4 + count * 4;
                        break;
                    default:
                        // InlineBrTarget, InlineField, InlineI, InlineMethod, InlineSig, InlineString,
                        // InlineTok, InlineType and ShortInlineR all use four bytes.
                        if (position + 4 > il.Length) return false;
                        operand = BitConverter.ToInt32(il, position);
                        position += 4;
                        break;
                }
                output.Add(new Instruction { OpCode = code, Operand = operand });
            }
            return position == il.Length;
        }

        private static Dictionary<short, OpCode> BuildOpCodeTable()
        {
            var table = new Dictionary<short, OpCode>();
            foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
                if (field.GetValue(null) is OpCode code) table[code.Value] = code;
            return table;
        }
    }
}

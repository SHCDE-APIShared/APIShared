using System;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace APIShared.Networking
{
    // Validate the installed loaded method, not an extender version/hash label.
    // The native blob must remain a two-byte packet ID followed by an unmodified
    // body, delivered exactly once with no Steam sender. The scope itself then
    // binds that blob to the native slot before any typed deserializer runs.
    internal static class NativeChoreDispatchContract
    {
        internal static void Validate(MethodInfo method)
        {
            using (var definition = new DynamicMethodDefinition(method)) ValidateBody(definition.Definition);
        }

        internal static void ValidateBody(MethodDefinition method)
        {
            int decoded = 0, copied = 0, delivered = 0, allocated = 0, initialized = 0;
            int idLocal = -1, bytesLocal = -1, senderLocal = -1;
            foreach (var instruction in method.Body.Instructions)
            {
                var op = instruction.OpCode;
                string name = op.Name;
                if (op == OpCodes.Stfld || op == OpCodes.Stsfld || op == OpCodes.Starg || op == OpCodes.Starg_S ||
                    name.StartsWith("stind", StringComparison.Ordinal) || name.StartsWith("stelem", StringComparison.Ordinal) ||
                    op == OpCodes.Stobj || op == OpCodes.Cpblk || op == OpCodes.Initblk || op == OpCodes.Cpobj)
                    throw Invalid();
                if (op == OpCodes.Initobj)
                {
                    if (!(instruction.Operand is TypeReference type) || !IsSender(type) ||
                        Local(Previous(instruction), true) < 0) throw Invalid();
                    senderLocal = Local(Previous(instruction), true); initialized++;
                }
                if (op == OpCodes.Newarr)
                {
                    var sub = Previous(instruction); var count = Previous(sub); var convert = Previous(count);
                    var length = Previous(convert); var argument = Previous(length);
                    if (!(instruction.Operand is TypeReference type) || type.FullName != "System.Byte" ||
                        sub?.OpCode != OpCodes.Sub || !Constant(count, 2) || convert?.OpCode != OpCodes.Conv_I4 ||
                        length?.OpCode != OpCodes.Ldlen || argument?.OpCode != OpCodes.Ldarg_0) throw Invalid();
                    bytesLocal = StoreLocal(Next(instruction));
                    if (bytesLocal < 0) throw Invalid();
                    allocated++;
                }
                if (op.FlowControl != FlowControl.Call) continue;
                if ((op != OpCodes.Call && op != OpCodes.Callvirt) || !(instruction.Operand is MethodReference called)) throw Invalid();
                string typeName = called.DeclaringType.FullName;
                if (typeName == "System.BitConverter" && called.Name == "ToInt16" && called.ReturnType.FullName == "System.Int16")
                {
                    if (!Constant(Previous(instruction), 0) || Previous(Previous(instruction))?.OpCode != OpCodes.Ldarg_0) throw Invalid();
                    idLocal = StoreLocal(Next(instruction)); if (idLocal < 0) throw Invalid(); decoded++; continue;
                }
                if (typeName == "System.Buffer" && called.Name == "BlockCopy")
                {
                    var convert = Previous(instruction); var length = Previous(convert); var bodyLength = Previous(length);
                    var offset = Previous(bodyLength); var body = Previous(offset); var prefix = Previous(body); var source = Previous(prefix);
                    if (bytesLocal < 0 || convert?.OpCode != OpCodes.Conv_I4 || length?.OpCode != OpCodes.Ldlen ||
                        Local(bodyLength) != bytesLocal || !Constant(offset, 0) || Local(body) != bytesLocal ||
                        !Constant(prefix, 2) || source?.OpCode != OpCodes.Ldarg_0) throw Invalid();
                    copied++; continue;
                }
                if (typeName == "SHCDESE.API.GameNetworkAPI" && called.Name == "HandleRawPacket")
                {
                    var sender = Previous(instruction); var init = Previous(sender); var address = Previous(init);
                    var body = Previous(address); var id = Previous(body); var instance = Previous(id);
                    if (called.ReturnType.FullName != "System.Boolean" || called.Parameters.Count != 3 ||
                        called.Parameters[0].ParameterType.FullName != "System.Int16" ||
                        called.Parameters[1].ParameterType.FullName != "System.Byte[]" || !IsSender(called.Parameters[2].ParameterType) ||
                        senderLocal < 0 || Local(sender) != senderLocal || init?.OpCode != OpCodes.Initobj ||
                        Local(address, true) != senderLocal || Local(body) != bytesLocal || Local(id) != idLocal ||
                        instance?.OpCode != OpCodes.Call || !(instance.Operand is MethodReference getter) ||
                        getter.DeclaringType.FullName != typeName || getter.Name != "get_Instance") throw Invalid();
                    delivered++; continue;
                }
                if (typeName == "SHCDESE.API.GameNetworkAPI" && called.Name == "get_Instance" ||
                    typeName == "SHCDESE.Logging.LogHelper" && (called.Name == "Warning" || called.Name == "Debug") ||
                    typeName == "System.String" && (called.Name == "Format" || called.Name == "Concat")) continue;
                throw Invalid();
            }
            if (decoded != 1 || copied != 1 || delivered != 1 || allocated != 1 || initialized != 1) throw Invalid();
            int idStores = 0, bodyStores = 0;
            foreach (var instruction in method.Body.Instructions)
            {
                int local = StoreLocal(instruction);
                if (local == idLocal) idStores++;
                if (local == bytesLocal) bodyStores++;
            }
            if (idLocal == bytesLocal || idLocal == senderLocal || bytesLocal == senderLocal || idStores != 1 || bodyStores != 1) throw Invalid();
        }

        private static bool IsSender(TypeReference type) => type.FullName == "System.Nullable`1<Steamworks.CSteamID>";
        private static InvalidOperationException Invalid() => new InvalidOperationException("Native Chore dispatcher blob/ID/body/sender contract changed.");
        private static Instruction Previous(Instruction instruction)
        { var value = instruction?.Previous; while (value?.OpCode == OpCodes.Nop) value = value.Previous; return value; }
        private static Instruction Next(Instruction instruction)
        { var value = instruction?.Next; while (value?.OpCode == OpCodes.Nop) value = value.Next; return value; }
        private static bool Constant(Instruction instruction, int value) => instruction != null &&
            (instruction.OpCode == OpCodes.Ldc_I4_0 && value == 0 || instruction.OpCode == OpCodes.Ldc_I4_2 && value == 2 ||
             (instruction.OpCode == OpCodes.Ldc_I4 || instruction.OpCode == OpCodes.Ldc_I4_S) && Convert.ToInt32(instruction.Operand) == value);
        private static int Local(Instruction instruction, bool address = false)
        {
            if (instruction == null) return -1;
            if (address) return (instruction.OpCode == OpCodes.Ldloca || instruction.OpCode == OpCodes.Ldloca_S) &&
                instruction.Operand is VariableDefinition target ? target.Index : -1;
            if (instruction.OpCode == OpCodes.Ldloc_0) return 0;
            if (instruction.OpCode == OpCodes.Ldloc_1) return 1;
            if (instruction.OpCode == OpCodes.Ldloc_2) return 2;
            if (instruction.OpCode == OpCodes.Ldloc_3) return 3;
            return (instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S) &&
                instruction.Operand is VariableDefinition variable ? variable.Index : -1;
        }
        private static int StoreLocal(Instruction instruction)
        {
            if (instruction == null) return -1;
            if (instruction.OpCode == OpCodes.Stloc_0) return 0;
            if (instruction.OpCode == OpCodes.Stloc_1) return 1;
            if (instruction.OpCode == OpCodes.Stloc_2) return 2;
            if (instruction.OpCode == OpCodes.Stloc_3) return 3;
            return (instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S) &&
                instruction.Operand is VariableDefinition variable ? variable.Index : -1;
        }
    }
}

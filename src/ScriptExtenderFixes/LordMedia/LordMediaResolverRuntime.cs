using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.Interop.Enums;
using System;
using System.Linq;
using System.Reflection;
using System.Threading;

namespace APIShared.ScriptExtenderFixes
{
    // Optional custom media must not turn an absent Lord identity into an error for
    // each native enqueue. Returning false retains the caller's original media paths.
    internal static class LordMediaResolverRuntime
    {
        internal delegate bool Resolver(GameAIManagerAPI self, string name, AILordMessageType type,
            out string video, out string audio);
        private static Hook hook;
        private static Resolver original;
        private static ManualLogSource logger;
        private static int attempted;

        internal static void Initialize(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            logger = log;
            Hook candidate = null;
            string stage = "resolve-method";
            try
            {
                MethodInfo target = typeof(GameAIManagerAPI).GetMethod("TryGetVideoAndAudio",
                    BindingFlags.NonPublic | BindingFlags.Instance, null,
                    new[] { typeof(string), typeof(AILordMessageType), typeof(string).MakeByRefType(), typeof(string).MakeByRefType() }, null);
                stage = "validate-resolver-contract";
                ValidateTarget(target);
                stage = "create-hook-and-trampoline";
                candidate = new Hook(target, (Resolver)OnResolve,
                    new HookConfig { ManualApply = true, ID = "APIShared.ScriptExtenderFixes.LordMedia" });
                original = candidate.GenerateTrampoline<Resolver>();
                stage = "publish-hook";
                candidate.Apply();
                hook = candidate; // Process lifetime; logical mission end never removes this hook.
                Write(false, "installed; Lords without registered Script Extender metadata quietly retain original game media paths.");
            }
            catch (Exception error)
            {
                // Only a failed, unpublished installation candidate can be rolled back.
                try { candidate?.Undo(); candidate?.Dispose(); } catch { }
                Write(true, "Lord media protection could not start because the Script Extender resolver contract could not be validated or hooked. " +
                    "Original Script Extender behavior remains active, including possible misleading Lord lookup errors. " +
                    "Check that APIShared and Script Extender versions are compatible. " +
                    $"stage={stage}, extender={typeof(GameAIManagerAPI).Assembly.FullName}, " +
                    $"errorType={error.GetType().FullName}, hresult=0x{error.HResult:X8}; {error}");
            }
        }

        internal static void ValidateTarget(MethodInfo target)
        {
            if (target == null) throw new MissingMethodException("GameAIManagerAPI.TryGetVideoAndAudio");
            ParameterInfo[] parameters = target.GetParameters();
            if (target.IsStatic || target.IsPublic || target.ContainsGenericParameters || target.ReturnType != typeof(bool) ||
                parameters.Length != 4 || parameters[0].ParameterType != typeof(string) ||
                parameters[1].ParameterType != typeof(AILordMessageType) ||
                parameters[2].ParameterType != typeof(string).MakeByRefType() || !parameters[2].IsOut ||
                parameters[3].ParameterType != typeof(string).MakeByRefType() || !parameters[3].IsOut || target.GetMethodBody() == null)
                throw new InvalidOperationException("Incompatible Lord media resolver signature/body: " + target);
            using (var definition = new DynamicMethodDefinition(target))
                ValidateBody(definition.Definition);
        }

        internal static void ValidateBody(MethodDefinition definition)
        {
            Instruction[] entry = definition.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).Take(25).ToArray();
            bool hasThis = definition.HasThis;
            // Audit the complete skipped path: output initialization, name normalization,
            // dictionary lookup, and the logging-only miss branch. Registered entries still
            // execute the original method, including its random choice and subtitle cache.
            if (entry.Length != 25 || !LoadsArgument(entry[0], 3, hasThis) || !LoadsEmpty(entry[1]) || entry[2].OpCode != OpCodes.Stind_Ref ||
                !LoadsArgument(entry[3], 4, hasThis) || !LoadsEmpty(entry[4]) || entry[5].OpCode != OpCodes.Stind_Ref ||
                !LoadsArgument(entry[6], 1, hasThis) || entry[7].OpCode != OpCodes.Callvirt ||
                !(entry[7].Operand is MethodReference lower) || lower.DeclaringType.FullName != "System.String" ||
                lower.Name != "ToLower" || lower.Parameters.Count != 0 ||
                !Local(entry[8], 0, false, true) || !LoadsArgument(entry[9], 0, hasThis) ||
                entry[10].OpCode != OpCodes.Call || !(entry[10].Operand is MethodReference getter) ||
                getter.DeclaringType.FullName != typeof(GameAIManagerAPI).FullName || getter.Name != "get_LordDataDict" ||
                getter.Parameters.Count != 0 || !Local(entry[11], 0) || !Local(entry[12], 1, true) ||
                entry[13].OpCode != OpCodes.Callvirt || !(entry[13].Operand is MethodReference lookup) ||
                lookup.Name != "TryGetValue" || lookup.ReturnType.FullName != "System.Boolean" || lookup.Parameters.Count != 2 ||
                !(lookup.DeclaringType is GenericInstanceType dictionary) ||
                dictionary.ElementType.FullName != "System.Collections.Generic.Dictionary`2" ||
                dictionary.GenericArguments.Count != 2 || dictionary.GenericArguments[0].FullName != "System.String" ||
                dictionary.GenericArguments[1].FullName != "SHCDESE.API.Components.AI.CustomLordEntry" ||
                getter.ReturnType.FullName != dictionary.FullName ||
                (entry[14].OpCode != OpCodes.Brtrue && entry[14].OpCode != OpCodes.Brtrue_S) || !ReferenceEquals(entry[14].Operand, entry[24]) ||
                !Literal(entry[15], "lord not found [") || !LoadsArgument(entry[16], 1, hasThis) || !Literal(entry[17], "]") ||
                entry[18].OpCode != OpCodes.Call || !(entry[18].Operand is MethodReference concat) ||
                concat.DeclaringType.FullName != "System.String" || concat.Name != "Concat" ||
                concat.Parameters.Count != 3 || concat.Parameters.Any(p => p.ParameterType.FullName != "System.String") ||
                !Literal(entry[19], "TryGetVideoAndAudio") || entry[20].OpCode != OpCodes.Ldstr ||
                entry[21].OpCode != OpCodes.Call || !(entry[21].Operand is MethodReference log) ||
                log.DeclaringType.FullName != "SHCDESE.Logging.LogHelper" || log.Name != "Error" ||
                log.ReturnType.FullName != "System.Void" || log.Parameters.Count != 3 ||
                log.Parameters.Any(p => p.ParameterType.FullName != "System.String") ||
                entry[22].OpCode != OpCodes.Ldc_I4_0 || entry[23].OpCode != OpCodes.Ret || !Local(entry[24], 1) ||
                definition.Body.ExceptionHandlers.Count != 0)
                throw new InvalidOperationException("Lord media resolver differs from the audited initialization/registration-miss contract; " +
                    "expected=initialize outputs, name.ToLower(), LordDataDict.TryGetValue, logging-only false return on a miss; observed=" +
                    string.Join(" | ", entry.Select(i => i.ToString())));
        }

        private static bool LoadsArgument(Instruction instruction, int index, bool hasThis) =>
            index == 0 && instruction.OpCode == OpCodes.Ldarg_0 || index == 1 && instruction.OpCode == OpCodes.Ldarg_1 ||
            index == 3 && instruction.OpCode == OpCodes.Ldarg_3 ||
            (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S) &&
            // DMD can turn an instance method into a static copy with an explicit self parameter.
            instruction.Operand is ParameterDefinition parameter && parameter.Index + (hasThis ? 1 : 0) == index;

        private static bool Literal(Instruction instruction, string value) => instruction.OpCode == OpCodes.Ldstr && Equals(instruction.Operand, value);

        private static bool Local(Instruction instruction, int index, bool address = false, bool store = false)
        {
            if (address) return (instruction.OpCode == OpCodes.Ldloca || instruction.OpCode == OpCodes.Ldloca_S) &&
                instruction.Operand is VariableDefinition addressed && addressed.Index == index;
            if (store && index == 0 && instruction.OpCode == OpCodes.Stloc_0 ||
                !store && index == 0 && instruction.OpCode == OpCodes.Ldloc_0 ||
                !store && index == 1 && instruction.OpCode == OpCodes.Ldloc_1) return true;
            return (store ? instruction.OpCode == OpCodes.Stloc || instruction.OpCode == OpCodes.Stloc_S :
                instruction.OpCode == OpCodes.Ldloc || instruction.OpCode == OpCodes.Ldloc_S) &&
                instruction.Operand is VariableDefinition local && local.Index == index;
        }

        private static bool LoadsEmpty(Instruction instruction) => instruction.OpCode == OpCodes.Ldsfld &&
            instruction.Operand is FieldReference field && field.DeclaringType.FullName == "System.String" && field.Name == "Empty";

        private static bool OnResolve(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio) =>
            Resolve(original, self, name, type, out video, out audio);

        internal static bool Resolve(Resolver next, GameAIManagerAPI self, string name, AILordMessageType type,
            out string video, out string audio)
        {
            // Use SE's culture-sensitive normalization. Absence is normal for both
            // extended Vanilla AICs and Vanilla-style custom packages without lordmeta.json.
            if (!string.IsNullOrEmpty(name) && self.LordDataDict.ContainsKey(name.ToLower()))
                return next(self, name, type, out video, out audio);
            video = string.Empty;
            audio = string.Empty;
            return false;
        }

        private static void Write(bool warning, string message)
        {
            try
            {
                string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] SE_LORD_MEDIA: " + message;
                if (warning) logger?.LogWarning(line); else logger?.LogInfo(line);
            }
            catch { /* Diagnostics must never prevent the original native enqueue. */ }
        }
    }
}

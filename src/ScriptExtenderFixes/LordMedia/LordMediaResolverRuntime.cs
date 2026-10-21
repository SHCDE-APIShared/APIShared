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
        private static int attempted, warned;

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
                Write(false, "installed; empty custom Lord identities retain original media paths.");
            }
            catch (Exception error)
            {
                // Only a failed, unpublished installation candidate can be rolled back.
                try { candidate?.Undo(); candidate?.Dispose(); } catch { }
                Write(true, $"not installed: stage={stage}, extender={typeof(GameAIManagerAPI).Assembly.FullName}, " +
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
            {
                Instruction[] entry = definition.Definition.Body.Instructions.Where(i => i.OpCode != OpCodes.Nop).Take(8).ToArray();
                bool hasThis = definition.Definition.HasThis;
                // The audited entry only initializes both outputs, then dereferences name.
                // Refuse changed prologues rather than skipping newly introduced side effects.
                if (entry.Length != 8 || !LoadsArgument(entry[0], 3, hasThis) || !LoadsEmpty(entry[1]) || entry[2].OpCode != OpCodes.Stind_Ref ||
                    !LoadsArgument(entry[3], 4, hasThis) || !LoadsEmpty(entry[4]) || entry[5].OpCode != OpCodes.Stind_Ref ||
                    !LoadsArgument(entry[6], 1, hasThis) || entry[7].OpCode != OpCodes.Callvirt ||
                    !(entry[7].Operand is MethodReference lower) || lower.DeclaringType.FullName != "System.String" ||
                    lower.Name != "ToLower" || lower.Parameters.Count != 0)
                    throw new InvalidOperationException("Lord media resolver entry differs from the audited output-initialization/name-lookup contract; " +
                        "expected=initialize video/audio to String.Empty then name.ToLower(); observed=" + string.Join(" | ", entry.Select(i => i.ToString())));
            }
        }

        private static bool LoadsArgument(Instruction instruction, int index, bool hasThis) =>
            index == 1 && instruction.OpCode == OpCodes.Ldarg_1 || index == 3 && instruction.OpCode == OpCodes.Ldarg_3 ||
            (instruction.OpCode == OpCodes.Ldarg || instruction.OpCode == OpCodes.Ldarg_S) &&
            // DMD can turn an instance method into a static copy with an explicit self parameter.
            instruction.Operand is ParameterDefinition parameter && parameter.Index + (hasThis ? 1 : 0) == index;

        private static bool LoadsEmpty(Instruction instruction) => instruction.OpCode == OpCodes.Ldsfld &&
            instruction.Operand is FieldReference field && field.DeclaringType.FullName == "System.String" && field.Name == "Empty";

        private static bool OnResolve(GameAIManagerAPI self, string name, AILordMessageType type, out string video, out string audio) =>
            Resolve(original, self, name, type, out video, out audio);

        internal static bool Resolve(Resolver next, GameAIManagerAPI self, string name, AILordMessageType type,
            out string video, out string audio)
        {
            if (!string.IsNullOrEmpty(name)) return next(self, name, type, out video, out audio);
            video = string.Empty;
            audio = string.Empty;
            if (Volatile.Read(ref warned) == 0 && Interlocked.CompareExchange(ref warned, 1, 0) == 0)
                Write(true, DescribeMissingIdentity(name, type));
            return false;
        }

        internal static string DescribeMissingIdentity(string name, AILordMessageType type) =>
            $"custom Lord identity is missing: input={(name == null ? "null" : "empty")}, messageType={type} ({(int)type}), " +
            "resolver=GameAIManagerAPI.TryGetVideoAndAudio; lookup skipped, original native media paths retained. " +
            "Custom media requires the missing Lord metadata; this does not indicate a failed native enqueue. " +
            "This warning is emitted once per process.";

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

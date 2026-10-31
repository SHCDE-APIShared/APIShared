using System;
using System.Linq;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.Interop.Enums;

namespace APIShared.ScriptExtenderFixes
{
    // Enrich the existing error branch only. No observer, lookup, retry or native hook.
    internal static class MoveCommandFailureRuntime
    {
        private static ILHook hook;
        internal static void Initialize(ManualLogSource log)
        {
            if (hook != null) return;
            var method = typeof(GameTribeManagerAPI).GetMethod("IssueMoveHereCommand", new[] {
                typeof(int), typeof(int), typeof(int), typeof(bool), typeof(int), typeof(TribeMoveType) });
            try
            {
                if (method == null || method.ReturnType != typeof(bool) || method.IsStatic)
                    throw new MissingMethodException("GameTribeManagerAPI.IssueMoveHereCommand signature");
                using (var definition = new DynamicMethodDefinition(method))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(Transform); definition.Generate();
                }
                hook = new ILHook(method, Transform, new ILHookConfig { ManualApply = true,
                    ID = "APIShared.ScriptExtenderFixes.MoveFailureContext" });
                hook.Apply(); // Root before publication, never undo a published candidate.
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "SE_MOVE_FAILURE_CONTEXT: log enrichment unavailable; original command behavior retained; error=" + error);
            }
        }

        internal static void Transform(ILContext il)
        {
            var errors = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Call &&
                i.Operand is MethodReference m && m.DeclaringType.FullName == "SHCDESE.Logging.LogHelper" &&
                m.Name == "Error" && m.Parameters.Count == 3 &&
                m.Parameters.All(p => p.ParameterType.FullName == "System.String")).ToArray();
            if (errors.Length != 2 || il.Body.Instructions.Count(i => i.Operand is MethodReference m &&
                m.Name == "c_game_tribe_issueorder_movehere_hook_impl") != 1)
                throw new InvalidOperationException("Move wrapper no longer has two audited error branches and one original dispatch.");
            foreach (var error in errors)
            {
                var cursor = new ILCursor(il);
                cursor.Goto(error, MoveType.Before);
                cursor.Emit(OpCodes.Ldarg, 4);
                cursor.Emit(OpCodes.Ldarg, 5);
                cursor.Emit(OpCodes.Ldarg, 6);
                cursor.Remove();
                cursor.EmitDelegate<Action<string, string, string, bool, int, TribeMoveType>>(Report);
            }
        }

        private static void Report(string message, string member, string file, bool patrol, int newOrder, TribeMoveType moveType) =>
            SHCDESE.Logging.LogHelper.Error("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message +
                "; patrol=" + patrol + ",newOrder=" + newOrder + ",moveType=" + moveType +
                "; phase=API-wrapper-failure; native execution/rejection cause unavailable (including possible Pre veto); action=return-false", member, file);
    }
}

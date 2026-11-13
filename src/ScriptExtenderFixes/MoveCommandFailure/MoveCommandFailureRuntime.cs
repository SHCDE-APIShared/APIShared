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
    // Checked, not an SE bug: "Result: 0" is native 0x11B520 rejecting an invalid or unreachable
    // target before any member is ordered, and the wrapper correctly returns false. Only the Error
    // level is SE's choice for this ordinary outcome.
    internal static class MoveCommandFailureRuntime
    {
        private static Patching.SafeIlHook hook;
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
                // Never undo a published hook; a failed installation leaves nothing behind.
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.ScriptExtenderFixes.MoveFailureContext", method, Transform, log,
                    out var installed, out string failure))
                    throw new InvalidOperationException(failure);
                hook = installed;
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
            int dispatch = il.Body.Instructions.IndexOf(il.Body.Instructions.Single(i => i.Operand is MethodReference m &&
                m.Name == "c_game_tribe_issueorder_movehere_hook_impl"));
            // The tribe-lookup branch precedes the dispatch and never reaches native code. Decided
            // before editing, because the edits shift instruction indexes.
            bool[] afterDispatch = errors.Select(e => il.Body.Instructions.IndexOf(e) > dispatch).ToArray();
            if (afterDispatch.Count(x => x) != 1)
                throw new InvalidOperationException("Move wrapper no longer has exactly one error branch after the original dispatch.");
            for (int index = 0; index < errors.Length; index++)
            {
                var error = errors[index];
                var cursor = new ILCursor(il);
                cursor.Goto(error, MoveType.Before);
                cursor.Emit(OpCodes.Ldarg, 4);
                cursor.Emit(OpCodes.Ldarg, 5);
                cursor.Emit(OpCodes.Ldarg, 6);
                cursor.Emit(afterDispatch[index] ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                cursor.Remove();
                cursor.EmitDelegate<Action<string, string, string, bool, int, TribeMoveType, bool>>(Report);
            }
        }

        private static void Report(string message, string member, string file, bool patrol, int newOrder, TribeMoveType moveType,
            bool afterDispatch) =>
            SHCDESE.Logging.LogHelper.Error("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message +
                "; patrol=" + patrol + ",newOrder=" + newOrder + ",moveType=" + moveType + (afterDispatch
                    ? "; phase=API-wrapper-failure; native execution/rejection cause unavailable (including possible Pre veto); action=return-false"
                    : "; phase=tribe-lookup; no native call; action=return-false"), member, file);
    }
}

using System;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.Detours;

namespace APIShared.Units
{
    internal static class TroopCommandInputHooks
    {
        private static Patching.SafeIlHook selectionHook;
        private static Patching.SafeIlHook runHook;
        // Gameplay readiness stays fixed for the process, as before: a rollback in a later rebuild is reported by SafeIlHook and must not flip a feature mid-mission.
        internal static bool IsReady { get; private set; }

        internal static void Initialize(ManualLogSource log)
        {
            if (!MarkedUnitSelectionAPI.HasRunBoundary || selectionHook != null || runHook != null) return;
            try
            {
                var selection = typeof(BulkUnitDetours).GetMethod(
                    nameof(BulkUnitDetours.c_game_dll_troopselection_hook_impl), BindingFlags.Public | BindingFlags.Static);
                var run = typeof(EngineInterface).GetMethod(nameof(EngineInterface.run),
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                Validate(selection, TransformSelection);
                Validate(run, TransformRun);
                // Retain both hooks from the first Apply. A partial failure stays dormant.
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.TroopCommandInput.Selection", selection, TransformSelection, log,
                    out var installedSelection, out string failure))
                    throw new InvalidOperationException(failure);
                selectionHook = installedSelection;
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.TroopCommandInput.Run", run, TransformRun, log,
                    out var installedRun, out failure))
                    throw new InvalidOperationException(failure);
                runHook = installedRun;
                IsReady = true;
            }
            catch (Exception error)
            {
                NativeApiLog.Error(log, "Shared troop command input unavailable: " + error);
            }
        }

        private static void Validate(MethodInfo target, ILContext.Manipulator transform)
        {
            if (target == null || target.GetMethodBody() == null)
                throw new MissingMethodException("The installed troop-command input method is unavailable.");
            using (var definition = new DynamicMethodDefinition(target))
            using (var il = new ILContext(definition.Definition))
            {
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                il.Invoke(transform);
                definition.Generate();
            }
        }

        internal static void TransformSelection(ILContext il)
        {
            var invoke = FindCall(il, typeof(BulkUnitDetours.c_game_dll_troopselection_delegate).FullName, "Invoke");
            var parameters = il.Method.Parameters;
            var expected = new[] { typeof(int), typeof(byte), typeof(byte), typeof(uint), typeof(ulong),
                typeof(byte), typeof(byte), typeof(uint), typeof(ulong), typeof(int), typeof(int),
                typeof(byte), typeof(uint), typeof(ulong) };
            if (!parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(expected.Select(type => type.FullName)))
                throw new InvalidOperationException("The installed Extender selection signature changed.");

            var argument = invoke.Previous;
            for (int index = parameters.Count - 1; index >= 0; index--)
            {
                if (argument == null || !argument.MatchLdarg(out int loaded) || loaded != index)
                    throw new InvalidOperationException("The Extender no longer forwards the original selection arguments.");
                argument = argument.Previous;
            }
            if (!(argument?.Operand is MethodReference original) || original.Name != "get_Original")
                throw new InvalidOperationException("The Extender selection call no longer uses its original delegate.");

            // The existing Extender hook has already filtered the pinned unit buffer. The
            // original call and all its arguments stay on the stack while we observe them.
            var cursor = new ILCursor(il);
            cursor.Goto(invoke, MoveType.Before);
            foreach (int index in new[] { 0, 2, 5, 6, 7, 8, 9, 10 })
                cursor.Emit(OpCodes.Ldarg, parameters[index]);
            cursor.EmitDelegate<Action<int, byte, byte, byte, uint, ulong, int, int>>(TroopCommandInputEvents.Capture);
        }

        internal static void TransformRun(ILContext il)
        {
            var prepare = FindCall(il, typeof(EditorDirector).FullName, nameof(EditorDirector.preDLLCallActions));
            var native = FindCall(il, typeof(EngineInterface).FullName, "DLL_RunTick");
            var enter = FindCall(il, typeof(System.Threading.Monitor).FullName, "Enter");
            var exit = FindCall(il, typeof(System.Threading.Monitor).FullName, "Exit");
            var code = il.Body.Instructions;
            var x = TileLocal(prepare.Previous?.Previous);
            var y = TileLocal(prepare.Previous);
            if (x == null || y == null || x == y ||
                code.IndexOf(enter) >= code.IndexOf(prepare) ||
                code.IndexOf(prepare) >= code.IndexOf(native) ||
                code.IndexOf(native) >= code.IndexOf(exit) ||
                !(prepare.Operand is MethodReference method) || method.Parameters.Count != 2 ||
                method.ReturnType.FullName != typeof(void).FullName)
                throw new InvalidOperationException("The EngineInterface cursor-tile boundary changed.");
            for (var next = prepare.Next; next != native; next = next.Next)
            {
                if (TileLocal(next) == x || TileLocal(next) == y ||
                    (next.MatchStloc(out int index) && (index == x.Index || index == y.Index)))
                    throw new InvalidOperationException("The engine changes the cursor tile after preparing input.");
            }

            // This call has returned through all preDLLCallActions patches. The engine still
            // owns its existing monitor and has not yet called DLL_RunTick.
            var cursor = new ILCursor(il);
            cursor.Goto(prepare, MoveType.After);
            cursor.Emit(OpCodes.Ldloc, x);
            cursor.Emit(OpCodes.Ldloc, y);
            cursor.EmitDelegate<Action<int, int>>(TroopCommandInputEvents.Publish);
        }

        private static VariableDefinition TileLocal(Instruction instruction) =>
            (instruction?.OpCode == OpCodes.Ldloca || instruction?.OpCode == OpCodes.Ldloca_S) &&
            instruction.Operand is VariableDefinition local && local.VariableType.FullName == typeof(int).FullName
                ? local : null;

        private static Instruction FindCall(ILContext il, string declaringType, string name)
        {
            var calls = il.Body.Instructions.Where(instruction =>
                (instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
                instruction.Operand is MethodReference method && method.Name == name &&
                method.DeclaringType.FullName.Replace('/', '+') == declaringType).ToArray();
            if (calls.Length != 1)
                throw new InvalidOperationException("Expected one " + name + " call in " + il.Method.FullName);
            return calls[0];
        }
    }
}

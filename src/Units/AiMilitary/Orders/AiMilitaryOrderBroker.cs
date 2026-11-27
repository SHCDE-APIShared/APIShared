using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.Detours;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units.AiMilitary.Orders
{
    // Adds a boundary around the actual Original delegate inside SE's existing native hook owners.
    // Mutable Pre runs first; legacy Post timing/arguments/result and the native hook chain stay intact.
    internal static class AiMilitaryOrderBroker
    {
        private static readonly List<Patching.SafeIlHook> Hooks = new List<Patching.SafeIlHook>();
        private static ManualLogSource log;
        private static Action<int, int, int, short, bool, int, long> moved;
        private static Action<int, int, int, int, int, long> targeted;
        private static Action<int> individualMove;
        private static Action<int> created;
        private static Func<int, int, bool> assignmentAllowed;
        private static Func<long, long, long> createdReturn;
        [ThreadStatic] private static int groupDispatch;
        // Gameplay readiness stays fixed for the process, as before: a rollback in a later rebuild is reported by SafeIlHook and must not flip a feature mid-mission.
        internal static bool IsReady => System.Threading.Volatile.Read(ref ready) != 0;
        private static int ready;
        internal static void EnterGroupDispatch() => groupDispatch++;
        internal static void ExitGroupDispatch() => groupDispatch--;

        internal static void Initialize(ManualLogSource logger,
            Action<int, int, int, short, bool, int, long> onMove,
            Action<int, int, int, int, int, long> onTarget, Action<int> onIndividualMove, Action<int> onCreated,
            Func<int, int, bool> onAssignmentAllowed = null, Func<long, long, long> onCreatedReturn = null)
        {
            if (IsReady) return;
            log = logger; moved = onMove; targeted = onTarget; individualMove = onIndividualMove;
            created = onCreated;
            assignmentAllowed = onAssignmentAllowed;
            createdReturn = onCreatedReturn;
            var edits = new[] {
                Tuple.Create(typeof(BulkTribeDetours).GetMethod("c_game_tribe_issueorder_movehere_hook_impl"), (ILContext.Manipulator)TransformMove),
                Tuple.Create(typeof(BulkTribeDetours).GetMethod("c_game_tribe_issueorder_withtarget_hook_impl"), (ILContext.Manipulator)TransformTarget),
                Tuple.Create(typeof(BulkUnitDetours).GetMethod("c_game_unit_issueorder_movehere_hook_impl"), (ILContext.Manipulator)TransformUnitMove),
                Tuple.Create(typeof(BulkUnitDetours).GetMethod("c_game_unit_spawn_ex_hook_impl"), (ILContext.Manipulator)TransformCreate),
                Tuple.Create(typeof(BulkTribeDetours).GetMethod("c_game_unit_assign_tribe_hook_impl"), (ILContext.Manipulator)TransformAssign),
                Tuple.Create(typeof(BulkTribeDetours).GetMethod("c_game_create_new_tribe_hook_impl"), (ILContext.Manipulator)TransformTribeCreate)
            };
            foreach (var edit in edits)
            {
                if (edit.Item1 == null || edit.Item1.ReturnType != typeof(long) || !edit.Item1.IsStatic)
                    throw new MissingMethodException("The installed SE effective-order wrapper differs.");
                using (var definition = new DynamicMethodDefinition(edit.Item1))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(edit.Item2); definition.Generate();
                }
            }
            // All bodies are validated before publication. Installed hooks stay rooted even when a later one fails.
            foreach (var edit in edits)
            {
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.AiMilitary.Effective." + edit.Item1.Name, edit.Item1, edit.Item2, log,
                    out var hook, out string failure))
                    throw new InvalidOperationException("AI effective-order observation unavailable: " + failure);
                lock (Hooks) Hooks.Add(hook);
            }
            System.Threading.Volatile.Write(ref ready, 1);
        }

        private static Instruction FindOriginalInvoke(ILContext il, Type delegateType)
        {
            var calls = il.Body.Instructions.Where(i => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call) &&
                i.Operand is MethodReference m && m.Name == "Invoke" && m.DeclaringType.Name == delegateType.Name).ToArray();
            if (calls.Length != 1 || !il.Body.Instructions.Any(i => i.Operand is MethodReference m && m.Name == "get_Original"))
                throw new InvalidOperationException("SE wrapper no longer has one validated Original delegate invocation: " + il.Method.FullName);
            return calls[0];
        }
        internal static void TransformMove(ILContext il)
        {
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkTribeDetours.c_game_tribe_issueorder_movehere_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkTribeDetours.c_game_tribe_issueorder_movehere_delegate,
                NativePointer<GameTribeManager>, int, int, int, short, int, TribeMoveType, long>>(InvokeMove);
            GuardReturns(il);
        }
        internal static void TransformTarget(ILContext il)
        {
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkTribeDetours.c_game_tribe_issueorder_withtarget_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkTribeDetours.c_game_tribe_issueorder_withtarget_delegate,
                NativePointer<GameTribeManager>, int, TribeAICommand, int, int, int, long>>(InvokeTarget);
            GuardReturns(il);
        }
        internal static void TransformUnitMove(ILContext il)
        {
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkUnitDetours.c_game_unit_issueorder_movehere_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkUnitDetours.c_game_unit_issueorder_movehere_delegate,
                NativePointer<GameUnitManager>, int, int, int, int, long>>(InvokeUnitMove);
            GuardReturns(il);
        }
        internal static void TransformCreate(ILContext il)
        {
            var rawResult = new VariableDefinition(il.Method.Module.ImportReference(typeof(long)));
            il.Body.Variables.Add(rawResult); il.Body.InitLocals = true;
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkUnitDetours.c_game_unit_spawn_ex_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkUnitDetours.c_game_unit_spawn_ex_delegate,
                NativePointer<GameUnitManager>, int, int, int, int, int, eChimps, long>>(InvokeCreate);
            cursor.Emit(OpCodes.Dup); cursor.Emit(OpCodes.Stloc, rawResult);
            foreach (var instruction in il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                instruction.OpCode = OpCodes.Nop; instruction.Operand = null;
                var returns = new ILCursor(il); returns.Goto(instruction, MoveType.After);
                returns.Emit(OpCodes.Ldloc, rawResult);
                returns.EmitDelegate<Func<long, long, long>>(FilterCreateReturn);
                returns.Emit(OpCodes.Ret);
            }
        }
        private static long FilterCreateReturn(long wrapperResult, long rawResult)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return 0;
            try { return createdReturn == null ? wrapperResult : createdReturn(rawResult, wrapperResult); }
            catch (Exception error) { Report(error); return 0; }
        }
        internal static void TransformAssign(ILContext il)
        {
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkTribeDetours.c_game_unit_assign_tribe_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkTribeDetours.c_game_unit_assign_tribe_delegate, NativePointer<GameTribeManager>, int, int, long>>(InvokeAssign);
            GuardReturns(il);
        }
        internal static void TransformTribeCreate(ILContext il)
        {
            var rawResult = new VariableDefinition(il.Method.Module.ImportReference(typeof(long)));
            il.Body.Variables.Add(rawResult); il.Body.InitLocals = true;
            var cursor = new ILCursor(il);
            cursor.Goto(FindOriginalInvoke(il, typeof(BulkTribeDetours.c_game_create_new_tribe_delegate)), MoveType.Before);
            cursor.Remove();
            cursor.EmitDelegate<Func<BulkTribeDetours.c_game_create_new_tribe_delegate, NativePointer<GameTribeManager>, int, int, long>>(InvokeTribeCreate);
            cursor.Emit(OpCodes.Dup); cursor.Emit(OpCodes.Stloc, rawResult);
            foreach (var instruction in il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                instruction.OpCode = OpCodes.Nop; instruction.Operand = null;
                var returns = new ILCursor(il); returns.Goto(instruction, MoveType.After);
                returns.Emit(OpCodes.Ldloc, rawResult);
                returns.EmitDelegate<Func<long, long, long>>(AiRecruitMutationReceipts.FilterTribeCreateReturn);
                returns.Emit(OpCodes.Ret);
            }
        }
        private static void GuardReturns(ILContext il)
        {
            foreach (var instruction in il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
            {
                // Keep the old instruction as an anchor: incoming branches to RET
                // must also execute the guard instead of jumping over inserted IL.
                instruction.OpCode = OpCodes.Nop; instruction.Operand = null;
                var cursor = new ILCursor(il); cursor.Goto(instruction, MoveType.After);
                cursor.EmitDelegate<Func<long, long>>(AiRecruitMutationReceipts.FilterReturn);
                cursor.Emit(OpCodes.Ret);
            }
        }
        private static long InvokeAssign(BulkTribeDetours.c_game_unit_assign_tribe_delegate original,
            NativePointer<GameTribeManager> manager, int unit, int tribe)
        {
            if (!AiRecruitMutationReceipts.BeforeAssign(unit, tribe)) return 0;
            try { if (assignmentAllowed != null && !assignmentAllowed(unit, tribe)) return 0; }
            catch (Exception error) { Report(error); return 0; }
            long result = original(manager, unit, tribe); AiRecruitMutationReceipts.AfterAssign(); return result;
        }
        private static long InvokeTribeCreate(BulkTribeDetours.c_game_create_new_tribe_delegate original,
            NativePointer<GameTribeManager> manager, int owner, int unknown)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return 0;
            return original(manager, owner, unknown);
        }
        private static long InvokeMove(BulkTribeDetours.c_game_tribe_issueorder_movehere_delegate original,
            NativePointer<GameTribeManager> manager, int tribe, int x, int y, short patrol, int newOrder, TribeMoveType moveType)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return 0;
            long result;
            EnterGroupDispatch();
            try { result = original(manager, tribe, x, y, patrol, newOrder, moveType); }
            finally { ExitGroupDispatch(); }
            try { if (IsReady) moved?.Invoke(tribe, x, y, patrol, newOrder != 0, (int)moveType, result); }
            catch (Exception error) { Report(error); }
            return result;
        }
        private static long InvokeTarget(BulkTribeDetours.c_game_tribe_issueorder_withtarget_delegate original,
            NativePointer<GameTribeManager> manager, int tribe, TribeAICommand command, int arg1, int arg2, int arg3)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return 0;
            long result;
            EnterGroupDispatch();
            try { result = original(manager, tribe, command, arg1, arg2, arg3); }
            finally { ExitGroupDispatch(); }
            try { if (IsReady) targeted?.Invoke(tribe, (int)command, arg1, arg2, arg3, result); }
            catch (Exception error) { Report(error); }
            return result;
        }
        private static long InvokeUnitMove(BulkUnitDetours.c_game_unit_issueorder_movehere_delegate original,
            NativePointer<GameUnitManager> manager, int unit, int x, int y, int extra)
        {
            if (!AiUnitMoveReceipts.Before(unit, x, y, extra)) return 0;
            long result = original(manager, unit, x, y, extra);
            AiUnitMoveReceipts.After(result);
            if (IsReady && groupDispatch == 0 && !AiUnitMoveReceipts.IsActive)
                try { individualMove?.Invoke(unit); } catch (Exception error) { Report(error); }
            return result;
        }
        private static long InvokeCreate(BulkUnitDetours.c_game_unit_spawn_ex_delegate original,
            NativePointer<GameUnitManager> manager, int owner, int color, int x, int y, int height, eChimps type)
        {
            if (!AiRecruitMutationReceipts.IsCurrentWorld) return 0;
            long result = original(manager, owner, color, x, y, height, type);
            // Capture before mutable legacy Post callbacks can replace the result with an unrelated ID.
            if (IsReady && result > 0 && result < AiMilitaryService.UnitIdLimit)
                try { created?.Invoke((int)result); } catch (Exception error) { Report(error); }
            return result;
        }
        private static void Report(Exception error)
        {
            try { Internal.RuntimeHealth.Failure(log, "ai-military-order-broker", true, "AI order observation failed: " + error); }
            catch { }
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using SHCDESE.API;

namespace APIShared.Economy
{
    // The SE Pre event may redirect UnitId, but the caller still puts the returned cargo on
    // its original worker (or ox). Keep that physical owner across the complete SE wrapper.
    // Production is serialized by the engine monitor; nested synchronous events save/restore
    // this process-owned slot. No extra managed call, allocation or native game hook is added.
    internal static class WorkerProductionYieldContext
    {
        private static Patching.SafeIlHook hook;
        private static IntPtr originalUnit;
        private static int attempted;
        private static int ready;
        private static string diagnostic = "Original production owner context has not initialized.";
        // Gameplay readiness stays fixed for the process, as before: a rollback in a later rebuild is reported by SafeIlHook and must not flip a feature mid-mission.
        internal static bool IsAvailable => Volatile.Read(ref ready) != 0;
        internal static string Diagnostic => diagnostic;
        internal static ulong OriginalUnitAddress => unchecked((ulong)originalUnit.ToInt64());

        internal static void InitializeManaged(ManualLogSource log)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) return;
            bool publishing = false;
            try
            {
                MethodInfo target = Resolve();
                originalUnit = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(originalUnit, 0);
                ulong address = OriginalUnitAddress;
                ILContext.Manipulator transform = il => Apply(il, address);
                using (var definition = new DynamicMethodDefinition(target))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(transform);
                    definition.Generate();
                }
                // A false result leaves nothing installed, so the context memory can still be freed.
                if (!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid, "APIShared.WorkerProduction.OriginalOwner", target, transform, log,
                    out var installed, out string failure))
                    throw new InvalidOperationException(failure);
                hook = installed;
                publishing = true;
                diagnostic = string.Empty;
                Volatile.Write(ref ready, 1);
                NativeApiLog.Info(log, "Worker production original-owner context published for process lifetime.");
            }
            catch (Exception error)
            {
                if (!publishing)
                {
                    if (originalUnit != IntPtr.Zero) Marshal.FreeHGlobal(originalUnit);
                    originalUnit = IntPtr.Zero;
                }
                diagnostic = error.Message;
                NativeApiLog.Error(log, "Worker production original-owner context unavailable: " + error);
            }
        }

        internal static MethodInfo Resolve()
        {
            Type type = typeof(GameUnitManagerAPI).Assembly.GetType("SHCDESE.Detours.BulkUnitDetours", true);
            MethodInfo target = type.GetMethod("c_game_unit_calculate_worker_good_yield_hook_impl",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int), typeof(int) }, null);
            if (target == null || target.ReturnType != typeof(long) || target.ContainsGenericParameters)
                throw new MissingMethodException("The installed SE production wrapper signature changed.");
            return target;
        }

        internal static void Apply(ILContext il, ulong address)
        {
            if (address == 0 || il.Method.HasThis || il.Method.ReturnType.MetadataType != MetadataType.Int64 ||
                il.Method.Parameters.Count != 3 || il.Method.Parameters.Any(p => p.ParameterType.MetadataType != MetadataType.Int32) ||
                il.Body.ExceptionHandlers.Count != 0 || il.Body.Instructions.Count == 0 ||
                il.Body.Instructions.Any(i => i.OpCode == OpCodes.Jmp || i.OpCode == OpCodes.Tail))
                throw new InvalidOperationException("SE production wrapper no longer has the audited static, non-tail, unprotected body.");
            Instruction[] returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (returns.Length == 0) throw new InvalidOperationException("SE production wrapper has no result return.");
            // Adding a leave after each return may extend an existing short branch's distance.
            foreach (Instruction instruction in il.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = LongBranch(instruction.OpCode);

            var previous = new VariableDefinition(il.Import(typeof(int)));
            var result = new VariableDefinition(il.Import(typeof(long)));
            il.Body.Variables.Add(previous); il.Body.Variables.Add(result);
            Instruction first = il.Body.Instructions[0];
            var cursor = new ILCursor(il);
            cursor.Goto(first, MoveType.Before);
            Pointer(cursor, address); cursor.Emit(OpCodes.Ldind_I4); cursor.Emit(OpCodes.Stloc, previous);
            Pointer(cursor, address); cursor.Emit(OpCodes.Ldarg_0); cursor.Emit(OpCodes.Stind_I4);

            Instruction after = Instruction.Create(OpCodes.Ldloc, result);
            foreach (Instruction ret in returns)
            {
                // Keep the original instruction object: incoming branches may target this RET.
                ret.OpCode = OpCodes.Stloc; ret.Operand = result;
                cursor.Goto(ret, MoveType.After); cursor.Emit(OpCodes.Leave, after);
            }
            cursor.Goto(il.Body.Instructions.Last(), MoveType.After);
            cursor.Emit(OpCodes.Nop); Instruction finallyStart = cursor.Prev;
            Pointer(cursor, address); cursor.Emit(OpCodes.Ldloc, previous); cursor.Emit(OpCodes.Stind_I4);
            cursor.Emit(OpCodes.Endfinally);
            il.Body.Instructions.Add(after); il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
        }

        private static void Pointer(ILCursor cursor, ulong address)
        { cursor.Emit(OpCodes.Ldc_I8, unchecked((long)address)); cursor.Emit(OpCodes.Conv_I); }

        private static OpCode LongBranch(OpCode code)
        {
            switch (code.Code)
            {
                case Code.Br_S: return OpCodes.Br;
                case Code.Brfalse_S: return OpCodes.Brfalse;
                case Code.Brtrue_S: return OpCodes.Brtrue;
                case Code.Beq_S: return OpCodes.Beq;
                case Code.Bge_S: return OpCodes.Bge;
                case Code.Bgt_S: return OpCodes.Bgt;
                case Code.Ble_S: return OpCodes.Ble;
                case Code.Blt_S: return OpCodes.Blt;
                case Code.Bne_Un_S: return OpCodes.Bne_Un;
                case Code.Bge_Un_S: return OpCodes.Bge_Un;
                case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
                case Code.Ble_Un_S: return OpCodes.Ble_Un;
                case Code.Blt_Un_S: return OpCodes.Blt_Un;
                case Code.Leave_S: return OpCodes.Leave;
                default: throw new InvalidOperationException("Unexpected short branch in SE production wrapper.");
            }
        }
    }
}

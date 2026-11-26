using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Buildings
{
    internal static unsafe partial class HumanConstructionRuntime
    {
        [ThreadStatic] private static int buildingSpawnDepth, unitSpawnDepth, decorationSpawnDepth, resourceDepth;
        private static bool IsPrimaryBuildingSpawn(BuildFrame frame) => frame!=null && buildingSpawnDepth==frame.ExpectedSpawnDepth && !frame.Identity.IsValid;

        private static int EnterUnitScope(eChimps type) => unitSpawnDepth==0 &&
            type!=eChimps.CHIMP_TYPE_BALLISTA && type!=eChimps.CHIMP_TYPE_MANGONEL ? -1 : EnterMethodScope(Operation.SpawnUnit);
        private static int EnterDecorationScope(ProjectileType type) => decorationSpawnDepth==0 && type!=ProjectileType.Brazier ? -1 : EnterMethodScope(Operation.SpawnDecoration);

        // Includes SE Pre and Post, not just the final native Original. A raw spawn/resource gift
        // made by a subscriber cannot claim the surrounding construction/refund's identity.
        private static int EnterMethodScope(Operation operation)
        {
            if (!ready) return -1;
            if (operation == Operation.AddResource)
            {
                if (refund == null && goldCall.Frame == null) return -1;
                return resourceDepth++;
            }
            if (operation == Operation.SpawnBuilding && building == null) return -1;
            if (building == null && !HumanConstructionCosts.HasDemand) return -1;
            switch (operation)
            {
                case Operation.SpawnBuilding: return buildingSpawnDepth++;
                case Operation.SpawnUnit: return unitSpawnDepth++;
                case Operation.SpawnDecoration: return decorationSpawnDepth++;
                default: throw new InvalidOperationException("Unexpected construction scope.");
            }
        }
        private static void LeaveMethodScope(Operation operation, int previous)
        {
            if (previous < 0) return;
            switch (operation)
            {
                case Operation.SpawnBuilding: buildingSpawnDepth = previous; break;
                case Operation.SpawnUnit: unitSpawnDepth = previous; break;
                case Operation.SpawnDecoration: decorationSpawnDepth = previous; break;
                case Operation.AddResource: resourceDepth = previous; break;
            }
        }

        internal static void ApplyMethodScope(ILContext il, Func<int> enter, Action<int> leave)
            => ApplyMethodScope(il,c=>c.EmitDelegate(enter),leave);
        private static void ApplyMethodScope(ILContext il, Action<ILCursor> emitEnter, Action<int> leave)
        {
            if (il.Body.ExceptionHandlers.Count != 0 || il.Body.Instructions.Count == 0 ||
                il.Body.Instructions.Any(i => i.OpCode == OpCodes.Tail || i.OpCode == OpCodes.Jmp) ||
                il.Method.ReturnType.MetadataType != MetadataType.Int64)
                throw new InvalidOperationException("SE construction wrapper scope contract changed.");
            var returns = il.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
            if (returns.Length == 0) throw new InvalidOperationException("Missing SE construction return.");
            foreach (var instruction in il.Body.Instructions)
                if (instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget)
                    instruction.OpCode = LongScopeBranch(instruction.OpCode);
            var previous = new VariableDefinition(il.Import(typeof(int)));
            var result = new VariableDefinition(il.Import(typeof(long)));
            il.Body.Variables.Add(previous); il.Body.Variables.Add(result);
            var first = il.Body.Instructions[0]; var cursor = new ILCursor(il);
            cursor.Goto(first, MoveType.Before); emitEnter(cursor); cursor.Emit(OpCodes.Stloc, previous);
            var after = Instruction.Create(OpCodes.Ldloc, result);
            foreach (var ret in returns)
            {
                ret.OpCode = OpCodes.Stloc; ret.Operand = result;
                cursor.Goto(ret, MoveType.After); cursor.Emit(OpCodes.Leave, after);
            }
            cursor.Goto(il.Body.Instructions.Last(), MoveType.After);
            cursor.Emit(OpCodes.Nop); var finallyStart = cursor.Prev;
            cursor.Emit(OpCodes.Ldloc, previous); cursor.EmitDelegate(leave); cursor.Emit(OpCodes.Endfinally);
            il.Body.Instructions.Add(after); il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally) {
                TryStart = first, TryEnd = finallyStart, HandlerStart = finallyStart, HandlerEnd = after });
        }
        private static OpCode LongScopeBranch(OpCode op)
        {
            switch (op.Code)
            {
                case Code.Br_S: return OpCodes.Br; case Code.Brfalse_S: return OpCodes.Brfalse;
                case Code.Brtrue_S: return OpCodes.Brtrue; case Code.Beq_S: return OpCodes.Beq;
                case Code.Bge_S: return OpCodes.Bge; case Code.Bge_Un_S: return OpCodes.Bge_Un;
                case Code.Bgt_S: return OpCodes.Bgt; case Code.Bgt_Un_S: return OpCodes.Bgt_Un;
                case Code.Ble_S: return OpCodes.Ble; case Code.Ble_Un_S: return OpCodes.Ble_Un;
                case Code.Blt_S: return OpCodes.Blt; case Code.Blt_Un_S: return OpCodes.Blt_Un;
                case Code.Bne_Un_S: return OpCodes.Bne_Un; case Code.Leave_S: return OpCodes.Leave;
                default: throw new InvalidOperationException("Unknown SE short branch.");
            }
        }
    }
}

using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Units
{
    internal static class UnitMoveWrapper
    {
        // Part of the existing owner's single IL publication. The outer scope starts before
        // public Pre and exits after Post, including exceptions and incoming RET branches.
        internal static void AddScope(ILContext il)
        {
            if(il.Method.ReturnType.MetadataType!=MetadataType.Int64 || il.Body.Instructions.Count==0 ||
                il.Body.Instructions.Any(i=>i.OpCode==OpCodes.Tail||i.OpCode==OpCodes.Jmp))
                throw new InvalidOperationException("Unit-move outer wrapper contract changed.");
            var returns=il.Body.Instructions.Where(i=>i.OpCode==OpCodes.Ret).ToArray();
            if(returns.Length==0)throw new InvalidOperationException("Unit-move wrapper has no return.");
            foreach(var instruction in il.Body.Instructions)
                if(instruction.OpCode.OperandType==OperandType.ShortInlineBrTarget)instruction.OpCode=LongBranch(instruction.OpCode);
            var frame=new VariableDefinition(il.Import(typeof(UnitMoveBroker.Frame)));
            var value=new VariableDefinition(il.Import(typeof(long)));
            il.Body.Variables.Add(frame);il.Body.Variables.Add(value);il.Body.InitLocals=true;
            var first=il.Body.Instructions[0];var cursor=new ILCursor(il);cursor.Goto(first,MoveType.Before);
            cursor.EmitDelegate<Func<UnitMoveBroker.Frame>>(UnitMoveRuntime.Enter);cursor.Emit(OpCodes.Stloc,frame);
            var after=Instruction.Create(OpCodes.Ldloc,value);
            foreach(var ret in returns){ret.OpCode=OpCodes.Stloc;ret.Operand=value;cursor.Goto(ret,MoveType.After);cursor.Emit(OpCodes.Leave,after);}
            cursor.Goto(il.Body.Instructions.Last(),MoveType.After);cursor.Emit(OpCodes.Nop);var cleanup=cursor.Prev;
            cursor.Emit(OpCodes.Ldloc,frame);cursor.EmitDelegate<Action<UnitMoveBroker.Frame>>(UnitMoveRuntime.Exit);cursor.Emit(OpCodes.Endfinally);
            il.Body.Instructions.Add(after);il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Finally){TryStart=first,TryEnd=cleanup,HandlerStart=cleanup,HandlerEnd=after});
        }
        private static OpCode LongBranch(OpCode code)
        {
            switch(code.Code)
            {
                case Code.Br_S:return OpCodes.Br;case Code.Brfalse_S:return OpCodes.Brfalse;case Code.Brtrue_S:return OpCodes.Brtrue;
                case Code.Beq_S:return OpCodes.Beq;case Code.Bge_S:return OpCodes.Bge;case Code.Bge_Un_S:return OpCodes.Bge_Un;
                case Code.Bgt_S:return OpCodes.Bgt;case Code.Bgt_Un_S:return OpCodes.Bgt_Un;case Code.Ble_S:return OpCodes.Ble;
                case Code.Ble_Un_S:return OpCodes.Ble_Un;case Code.Blt_S:return OpCodes.Blt;case Code.Blt_Un_S:return OpCodes.Blt_Un;
                case Code.Bne_Un_S:return OpCodes.Bne_Un;case Code.Leave_S:return OpCodes.Leave;
                default:throw new InvalidOperationException("Unknown short unit-move wrapper branch.");
            }
        }
    }
}

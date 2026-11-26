using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.Interop;
using Instruction=Mono.Cecil.Cil.Instruction;
using OpCodes=Mono.Cecil.Cil.OpCodes;

namespace APIShared.Economy
{
    // SE remains the sole native B6100 entry owner. Observe its one actual Original
    // invocation after mutable Pre, retaining its untouched legacy Post and return.
    internal static class ProductionGoodsReceivedSeContract
    {
        private const string TypeName="SHCDESE.Detours.BulkBuildingDetours";
        private const string MethodName="c_game_add_good_to_goodsyard_hook_impl";
        private const string DelegateName="c_game_add_good_to_goodsyard_delegate";
        private const string ArgsName="SHCDESE.EventAPI.Buildings.AddGoodToGoodsyardEventArgs";
        internal static MethodInfo Resolve()
        {
            Type type=typeof(GameBuildingManagerAPI).Assembly.GetType(TypeName,true);
            var method=type.GetMethod(MethodName,BindingFlags.Public|BindingFlags.Static,null,
                new[]{typeof(NativePointer<GameBuildingManager>),typeof(int),typeof(int),typeof(eGoods),typeof(int),typeof(int),typeof(int)},null);
            if(method==null||method.ReturnType!=typeof(long))throw Invalid();
            return method;
        }
        internal static Type OriginalDelegateType(MethodInfo method)
        {
            var type=method.DeclaringType.GetNestedType(DelegateName,BindingFlags.Public|BindingFlags.NonPublic);
            var invoke=type?.GetMethod("Invoke");
            if(type==null||!typeof(Delegate).IsAssignableFrom(type)||invoke==null||invoke.ReturnType!=typeof(long)||
                !invoke.GetParameters().Select(p=>p.ParameterType).SequenceEqual(method.GetParameters().Select(p=>p.ParameterType)))throw Invalid();
            return type;
        }
        internal static ProductionGoodsForward CreateForwarder(Type delegateType)
        {
            var parameters=new[]{typeof(Delegate)}.Concat(delegateType.GetMethod("Invoke").GetParameters().Select(p=>p.ParameterType)).ToArray();
            var method=new DynamicMethod("APIShared.ProductionGoods.ForwardSEOriginal",typeof(long),parameters,typeof(ProductionGoodsReceivedSeContract),true);
            var il=method.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);il.Emit(System.Reflection.Emit.OpCodes.Castclass,delegateType);
            for(short i=1;i<parameters.Length;i++)il.Emit(System.Reflection.Emit.OpCodes.Ldarg,i);
            il.Emit(System.Reflection.Emit.OpCodes.Callvirt,delegateType.GetMethod("Invoke"));il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (ProductionGoodsForward)method.CreateDelegate(typeof(ProductionGoodsForward));
        }
        internal static void Validate(MethodInfo method)
        {OriginalDelegateType(method);using(var definition=new DynamicMethodDefinition(method))using(var il=new ILContext(definition.Definition))Find(il);}
        private static Instruction Find(ILContext il)
        {
            var body=il.Body.Instructions;
            var calls=body.Where(i=>i.OpCode==OpCodes.Callvirt&&i.Operand is MethodReference m&&m.Name=="Invoke"&&
                m.DeclaringType.FullName==TypeName+"/"+DelegateName&&m.ReturnType.MetadataType==MetadataType.Int64&&m.Parameters.Count==7).ToArray();
            var raises=body.Where(i=>i.Operand is MethodReference m&&m.Name=="Raise"&&m.DeclaringType is GenericInstanceType g&&
                g.GenericArguments.Count==1&&g.GenericArguments[0].FullName==ArgsName).ToArray();
            if(calls.Length!=1||raises.Length!=2||il.Body.ExceptionHandlers.Count!=0||body.Count(i=>i.OpCode==OpCodes.Ret)!=1)throw Invalid();
            int at=body.IndexOf(calls[0]),pre=body.IndexOf(raises[0]),post=body.IndexOf(raises[1]);
            if(pre>=at||post<=at)throw Invalid();
            var inputs=body.Skip(pre+1).Take(at-pre-1).ToArray();
            foreach(string getter in new[]{"get_BuildingId","get_BuildingGlobalId","get_Good","get_AddAmount","get_Capacity","get_Add"})
                if(inputs.Count(i=>i.Operand is MethodReference m&&m.DeclaringType.FullName==ArgsName&&m.Name==getter)!=1)throw Invalid();
            if(inputs.Count(i=>i.Operand is MethodReference m&&m.Name=="get_Original")!=1||
                inputs.Count(i=>i.Operand is FieldReference f&&f.Name=="c_game_add_good_to_goodsyard_hook"&&f.DeclaringType.FullName==TypeName)!=1)throw Invalid();
            return calls[0];
        }
        internal static void Apply(ILContext il,ProductionGoodsForward adapter,Action enter,Action leave)
        {
            Instruction call=Find(il);var cursor=new ILCursor(il);cursor.Goto(call,MoveType.Before);cursor.Remove();cursor.EmitDelegate(adapter);
            // Expand short branches before introducing a whole-wrapper finally.
            foreach(var i in il.Body.Instructions)
            {
                if(i.OpCode==OpCodes.Br_S)i.OpCode=OpCodes.Br;
                else if(i.OpCode==OpCodes.Brtrue_S)i.OpCode=OpCodes.Brtrue;
                else if(i.OpCode==OpCodes.Brfalse_S)i.OpCode=OpCodes.Brfalse;
            }
            var ret=il.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ret);
            var first=il.Body.Instructions[0];cursor.Goto(first,MoveType.Before);cursor.EmitDelegate(enter);
            var result=new VariableDefinition(il.Import(typeof(long)));il.Body.Variables.Add(result);
            ret.OpCode=OpCodes.Stloc;ret.Operand=result;
            var after=Instruction.Create(OpCodes.Ldloc,result);cursor.Goto(ret,MoveType.After);cursor.Emit(OpCodes.Leave,after);
            cursor.Emit(OpCodes.Nop);var finallyStart=cursor.Prev;cursor.EmitDelegate(leave);cursor.Emit(OpCodes.Endfinally);
            il.Body.Instructions.Add(after);il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            il.Body.ExceptionHandlers.Add(new Mono.Cecil.Cil.ExceptionHandler(ExceptionHandlerType.Finally){TryStart=first,TryEnd=finallyStart,HandlerStart=finallyStart,HandlerEnd=after});
        }
        internal static void ValidateNativeOwner(MethodInfo method,IntPtr target)
        {
            object handle=method.DeclaringType.GetField("c_game_add_good_to_goodsyard_hook",BindingFlags.Static|BindingFlags.NonPublic|BindingFlags.Public)?.GetValue(null);
            object hook=handle?.GetType().GetProperty("Hook")?.GetValue(handle,null);
            object address=hook?.GetType().GetProperty("TargetAddress")?.GetValue(hook,null);
            object installed=hook?.GetType().GetProperty("IsInstalled")?.GetValue(hook,null);
            if(!(address is ulong actual)||actual!=unchecked((ulong)target.ToInt64())||!Equals(installed,true))
                throw new InvalidOperationException("The SE native stockpile owner is not installed at the audited entry.");
        }
        private static InvalidOperationException Invalid()=>new InvalidOperationException("The installed SE stockpile Pre/Original/Post contract changed.");
    }
}

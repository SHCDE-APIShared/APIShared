using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Economy
{
    internal static class PopularityPlayerIl
    {
        internal sealed class Contract
        {
            internal Instruction Resolve;
            internal VariableDefinition Player,Resources;
            internal readonly Dictionary<Instruction,PopularitySource> Reads=new Dictionary<Instruction,PopularitySource>();
        }
        private static MethodReference Method(ILContext il,Type type,string name) => il.Method.Module.ImportReference(type.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic));
        private static void Redirect(ILContext il,Instruction old,Instruction replacement)
        {
            foreach(var i in il.Body.Instructions) {
                if(ReferenceEquals(i.Operand,old))i.Operand=replacement;
                if(i.Operand is Instruction[] targets)for(int n=0;n<targets.Length;n++)if(ReferenceEquals(targets[n],old))targets[n]=replacement;
                if(i.Operand is ILLabel label&&ReferenceEquals(label.Target,old))label.Target=replacement;
                if(i.Operand is ILLabel[] labels)foreach(var l in labels)if(ReferenceEquals(l.Target,old))l.Target=replacement;
            }
        }
        internal static bool Apply(ILContext il,Action<string> unavailable=null)
        {
            Contract plan;
            try {plan=FearPopularityIl.PlayerSites(il);}
            catch(InvalidOperationException ex){unavailable?.Invoke(ex.Message);FearPopularityIl.Apply(il);return false;}
            var fearSites=FearPopularityIl.Sites(il);
            FearPopularityIl.Apply(il);
            var snapshot=new VariableDefinition(il.Method.Module.ImportReference(typeof(PopularitySelection)));
            il.Body.Variables.Add(snapshot);
            var processor=il.Body.GetILProcessor();
            var next=plan.Resolve.Next;
            // Keep the original bool on the stack. Failed resolution must not read
            // game state and follows the unchanged original failure branch.
            var capture=new[]{Instruction.Create(OpCodes.Dup),Instruction.Create(OpCodes.Brfalse,next),
                Instruction.Create(OpCodes.Ldloc,plan.Player),Instruction.Create(OpCodes.Ldloc,plan.Resources),
                Instruction.Create(OpCodes.Conv_I),Instruction.Create(OpCodes.Call,Method(il,typeof(PopularitySelection),"Capture")),
                Instruction.Create(OpCodes.Stloc,snapshot)};
            var cursor=plan.Resolve;foreach(var added in capture){processor.InsertAfter(cursor,added);cursor=added;}
            foreach(var pair in plan.Reads) {
                cursor=pair.Key;
                foreach(var added in new[]{Instruction.Create(OpCodes.Ldloc,snapshot),Instruction.Create(OpCodes.Ldc_I4,(int)pair.Value),
                    Instruction.Create(OpCodes.Call,Method(il,typeof(PopularitySelection),"Resolve"))}){processor.InsertAfter(cursor,added);cursor=added;}
            }
            foreach(var site in fearSites) {
                var first=Instruction.Create(OpCodes.Ldloc,snapshot);
                Redirect(il,site,first);processor.InsertBefore(site,first);
                var old=(MethodReference)site.Operand;
                site.Operand=Method(il,typeof(FearPopularity),old.Name=="MultiplySwapped"?"MultiplySwappedTargeted":"MultiplyTargeted");
            }
            return true;
        }
    }
}

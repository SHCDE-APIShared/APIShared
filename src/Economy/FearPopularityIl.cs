using System;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;

namespace APIShared.Economy
{
    // A binary-bound SE adapter, not a dependency on generated lambda names or local numbers.
    // Preserve the mul instruction identity: branches/exception boundaries keep their exact targets.
    internal static class FearPopularityIl
    {
        internal static List<Instruction> Sites(ILContext context)
        {
            var sites = new List<Instruction>();
            var code = context.Body.Instructions;
            for (int i = 0; i < code.Count; i++)
            {
                if (!(code[i].Operand is MethodReference getter) || getter.Name != "get_GoodBadThingPopModifierMultiplier" ||
                    getter.DeclaringType.FullName != "SHCDESE.GameGlobals.GameGlobalsManager") continue;
                if (i + 3 >= code.Count || !(code[i+1].Operand is MethodReference read) || read.Name != "GetValue" ||
                    read.DeclaringType.FullName != "RedBird.Core.Memory.Managed.ManagedValue`1<System.Int32>" ||
                    (code[i+2].OpCode.Code != Code.Ldloc_S && code[i+2].OpCode.Code != Code.Ldloc) ||
                    code[i+3].OpCode != OpCodes.Mul)
                    throw new InvalidOperationException("Unsupported fear multiplication data flow.");
                sites.Add(code[i+3]);
            }
            // Both signed branches in the pinned SE binary must be covered; this is an
            // audited patch-completeness contract, not a general source-layout assumption.
            if (sites.Count != 2) throw new InvalidOperationException("Expected both audited signed fear multiplication branches.");
            return sites;
        }
        internal static void Apply(ILContext context)
        {
            var sites = Sites(context); // Validate the entire edit before mutation.
            var replacement = context.Method.Module.ImportReference(typeof(FearPopularity).GetMethod("Multiply",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic));
            foreach (var site in sites) { site.OpCode = OpCodes.Call; site.Operand = replacement; }
        }
    }
}

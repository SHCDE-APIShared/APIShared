using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using Mono.Cecil.Cil;
using SHCDESE.API.LowLevel;
using System;
using System.Linq;

[TestClass]
public sealed class FearPopularityIlTests
{
    [TestMethod]
    public void InstalledSignedBranchesAreRewrittenWithoutChangingTheirConsumers()
    {
        var method = FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            var sites = FearPopularityIl.Sites(context);
            var instructions = context.Body.Instructions.ToArray();
            var unchanged = instructions.Where(i => !sites.Contains(i)).Select(i => (i,i.OpCode,i.Operand)).ToArray();
            FearPopularityIl.Apply(context);
            CollectionAssert.AreEqual(instructions,context.Body.Instructions.ToArray(), "Branch/exception instruction identities retained");
            foreach (var item in unchanged) { Assert.AreEqual(item.Item2,item.Item1.OpCode); Assert.AreSame(item.Item3,item.Item1.Operand); }
            foreach (var site in sites) Assert.AreEqual(OpCodes.Call,site.OpCode);
            // Generate the real edited callback. This verifies stack/signatures and both
            // downstream consumers; it does not execute native game memory or prove Mono gameplay.
            var generated = definition.Generate();
            var signature = (method.IsStatic ? Enumerable.Empty<Type>() : new[] {method.DeclaringType})
                .Concat(method.GetParameters().Select(p => p.ParameterType)).Concat(new[] {method.ReturnType}).ToArray();
            var callback = generated.CreateDelegate(System.Linq.Expressions.Expression.GetDelegateType(signature));
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(callback);
            Assert.IsNotNull(callback, "Edited real callback has a JIT-ready delegate");
            Assert.Throws<InvalidOperationException>(() => FearPopularityIl.Sites(context), "Double installation rejected");
        }
    }
    [TestMethod]
    public void MalformedOneBranchFailsBeforeEitherBranchIsModified()
    {
        var method = FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            var sites = FearPopularityIl.Sites(context);
            sites[1].OpCode = OpCodes.Add;
            Assert.Throws<InvalidOperationException>(() => FearPopularityIl.Apply(context));
            Assert.AreEqual(OpCodes.Mul,sites[0].OpCode,"First branch untouched on a later validation failure");
        }
    }
}

using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using Mono.Cecil.Cil;
using SHCDESE.API.LowLevel;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;
using System.Reflection;
using Mono.Cecil;
using System;
using System.Linq;

[TestClass]
public sealed class FearPopularityIlTests
{
    [TestMethod]
    public void InstalledPlayerContextAndAllSourceReadsCompile()
    {
        var method=FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using(var definition=new DynamicMethodDefinition(method))
        using(var context=new ILContext(definition.Definition)) {
            context.Invoke(il=>{
                var plan=FearPopularityIl.PlayerSites(il);
                CollectionAssert.AreEquivalent(Enum.GetValues(typeof(PopularitySource)),plan.Reads.Values.Distinct().ToArray());
                Assert.IsTrue(PopularityPlayerIl.Apply(il));
            });
            var generated=definition.Generate();
            var signature=(method.IsStatic?Enumerable.Empty<Type>():new[]{method.DeclaringType})
                .Concat(method.GetParameters().Select(p=>p.ParameterType)).Concat(new[]{method.ReturnType}).ToArray();
            var callback=generated.CreateDelegate(System.Linq.Expressions.Expression.GetDelegateType(signature));
            System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(callback);
        }
    }
    [TestMethod]
    public void UnknownPlayerContextDisablesOnlyTargetedPoliciesBeforeEditing()
    {
        var method=FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using(var definition=new DynamicMethodDefinition(method))
        using(var context=new ILContext(definition.Definition)) {
            context.Invoke(il=>{
                var original=il.Body.Instructions.First(i=>i.Operand is FieldReference f && f.Name=="R15");
                var field=(FieldReference)original.Operand;
                original.Operand=new FieldReference("R14",field.FieldType,field.DeclaringType);
                var before=il.Body.Instructions.ToArray();
                var sites=FearPopularityIl.Sites(il);
                Assert.Throws<InvalidOperationException>(()=>FearPopularityIl.PlayerSites(il));
                string diagnostic=null;
                Assert.IsFalse(PopularityPlayerIl.Apply(il,message=>diagnostic=message));
                Assert.IsFalse(string.IsNullOrWhiteSpace(diagnostic));
                CollectionAssert.AreEqual(before,il.Body.Instructions.ToArray(),"Legacy fallback adds no player/source interception");
                foreach(var site in sites) Assert.AreEqual("Multiply",((MethodReference)site.Operand).Name);
            });
        }
    }
    [TestMethod]
    public void InstalledSignedBranchesAreRewrittenWithoutChangingTheirConsumers()
    {
        var method = FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            context.Invoke(il => {
            var sites = FearPopularityIl.Sites(context);
            var instructions = context.Body.Instructions.ToArray();
            var unchanged = instructions.Where(i => !sites.Contains(i)).Select(i => (i,i.OpCode,i.Operand)).ToArray();
            FearPopularityIl.Apply(context);
            CollectionAssert.AreEqual(instructions,context.Body.Instructions.ToArray(), "Branch/exception instruction identities retained");
            foreach (var item in unchanged) { Assert.AreEqual(item.Item2,item.Item1.OpCode); Assert.AreSame(item.Item3,item.Item1.Operand); }
            foreach (var site in sites) Assert.AreEqual(OpCodes.Call,site.OpCode);
            });
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
    public void ManipulatorResolvesLabelsAndSwitchesWithoutChangingThem()
    {
        using (var definition = new DynamicMethodDefinition(Fixture(nameof(ThreePaths))))
        using (var context = new ILContext(definition.Definition))
        {
            var first = context.Body.Instructions[0];
            context.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Ldc_I4_0));
            context.Body.GetILProcessor().InsertBefore(first, Instruction.Create(OpCodes.Switch, new[] { first, first }));
            context.Invoke(il => {
                Assert.IsTrue(il.Body.Instructions.Any(i => i.Operand is ILLabel));
                Assert.IsTrue(il.Body.Instructions.Any(i => i.Operand is ILLabel[]));
                var branches = il.Body.Instructions.Where(i => i.Operand is ILLabel || i.Operand is ILLabel[])
                    .Select(i => (i, i.Operand)).ToArray();
                FearPopularityIl.Apply(il);
                foreach (var branch in branches) Assert.AreSame(branch.Item2, branch.Item1.Operand);
            });
        }
    }
    [TestMethod]
    public void ForeignOrUnboundBranchTargetsAreRejectedBeforeEditing()
    {
        foreach (bool missing in new[] { false, true })
        using (var definition = new DynamicMethodDefinition(Fixture(nameof(ThreePaths))))
        using (var context = new ILContext(definition.Definition))
        {
            context.Invoke(il => {
                var label = il.Body.Instructions.Select(i => i.Operand).OfType<ILLabel>().First();
                var original = label.Target;
                var before = il.Body.Instructions.Select(i => i.OpCode).ToArray();
                try {
                    label.Target = missing ? null : Instruction.Create(OpCodes.Nop);
                    Assert.Throws<InvalidOperationException>(() => FearPopularityIl.Apply(il));
                    CollectionAssert.AreEqual(before, il.Body.Instructions.Select(i => i.OpCode).ToArray());
                } finally { label.Target = original; }
            });
        }
    }
    [TestMethod]
    public void MalformedProductFailsBeforeAnyEdit()
    {
        var method = FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            var sites = FearPopularityIl.Sites(context);
            sites.Last().OpCode = OpCodes.Add;
            var before=context.Body.Instructions.Select(i=>i.ToString()).ToArray();
            Assert.Throws<InvalidOperationException>(() => FearPopularityIl.Apply(context));
            CollectionAssert.AreEqual(before,context.Body.Instructions.Select(i=>i.ToString()).ToArray(),"No partial edit regardless of site count");
        }
    }

    private static unsafe int Unified(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int coefficient=globals.GoodBadThingPopModifierMultiplier.GetValue();
        int level=resources->r_GoodBadThingBoost;
        int contribution=coefficient*level;
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int Swapped(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int contribution=resources->r_GoodBadThingBoost*globals.GoodBadThingPopModifierMultiplier.GetValue();
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int ThreePaths(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int level=resources->r_GoodBadThingBoost;
        int coefficient=globals.GoodBadThingPopModifierMultiplier.GetValue();
        int contribution;
        if(level>0) contribution=coefficient*level;
        else if(level<0) contribution=level*coefficient;
        else contribution=coefficient*level;
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int WrongInput(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int contribution=globals.GoodBadThingPopModifierMultiplier.GetValue()*(int)resources->r_TotalPopulation;
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int WrongPlayer(GameGlobalsManager globals, GamePlayerResources* one, GamePlayerResources* two)
    {
        int contribution=globals.GoodBadThingPopModifierMultiplier.GetValue()*one->r_GoodBadThingBoost;
        two->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int ChangedMath(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int contribution=globals.GoodBadThingPopModifierMultiplier.GetValue()*(resources->r_GoodBadThingBoost+1);
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe int ClampedProduct(GameGlobalsManager globals, GamePlayerResources* resources)
    {
        int contribution=Math.Min(100,globals.GoodBadThingPopModifierMultiplier.GetValue()*resources->r_GoodBadThingBoost);
        resources->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static unsafe void ReplaceResources(ref GamePlayerResources* pointer, GamePlayerResources* next) { pointer=next; }
    private static unsafe int ReassignedPlayer(GameGlobalsManager globals, GamePlayerResources* one, GamePlayerResources* two)
    {
        var pointer=one;
        int level=pointer->r_GoodBadThingBoost;
        ReplaceResources(ref pointer,two);
        int contribution=globals.GoodBadThingPopModifierMultiplier.GetValue()*level;
        pointer->r_GoodBadThingsPopularityModifier=unchecked((uint)contribution);
        return contribution;
    }
    private static MethodInfo Fixture(string name) => typeof(FearPopularityIlTests).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static);
    [TestMethod]
    public void EquivalentLayoutsAcceptAnySiteCountAndRenamedCompilerCarriers()
    {
        foreach(string name in new[]{nameof(Unified),nameof(Swapped),nameof(ThreePaths)})
        using(var definition=new DynamicMethodDefinition(Fixture(name)))
        using(var context=new ILContext(definition.Definition))
        {
            var sites=FearPopularityIl.Sites(context);
            Assert.IsTrue(sites.Count>0);
            foreach(var instruction in context.Body.Instructions)
            {
                string nameOfOp=instruction.OpCode.Name;
                if(nameOfOp.StartsWith("ldloc.") || nameOfOp.StartsWith("stloc."))
                {
                    if(int.TryParse(nameOfOp.Substring(6),out int index))
                    {
                        instruction.OpCode=nameOfOp.StartsWith("ldloc")?OpCodes.Ldloc:OpCodes.Stloc;
                        instruction.Operand=context.Body.Variables[index];
                    }
                }
            }
            // Expand local opcodes and add harmless sequence points/NOPs. These
            // variations must not become a version or compiler-layout gate.
            foreach(var instruction in context.Body.Instructions.ToArray())
                context.Body.GetILProcessor().InsertBefore(instruction,Instruction.Create(OpCodes.Nop));
            Assert.AreEqual(sites.Count,FearPopularityIl.Sites(context).Count);
            FearPopularityIl.Apply(context);
            foreach(var site in sites) Assert.AreEqual(OpCodes.Call,site.OpCode);
        }
        foreach(var version in new[]{new Version(1,0),new Version(99,7)})
        using(var image=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition("FutureSE",version),"FutureSE",ModuleKind.Dll))
        using(var definition=new DynamicMethodDefinition(Fixture(nameof(Unified))))
        {
            var method=definition.Definition;
            method.DeclaringType.Methods.Remove(method);
            var carrier=new TypeDefinition("CompilerChanged","ArbitraryContainer",Mono.Cecil.TypeAttributes.Class,image.MainModule.TypeSystem.Object);
            image.MainModule.Types.Add(carrier); carrier.Methods.Add(method); method.Name="RenamedCalculation";
            Assert.AreSame(method,FearPopularityRuntime.FindTargetDefinition(image.MainModule),"Discovery ignores version/type/method names");
            using(var context=new ILContext(method)) Assert.IsTrue(FearPopularityIl.Sites(context).Count>0);
        }
    }
    [TestMethod]
    public void ChangedMeaningIsRejectedBeforeMutation()
    {
        foreach(string name in new[]{nameof(WrongInput),nameof(WrongPlayer),nameof(ChangedMath),nameof(ClampedProduct),nameof(ReassignedPlayer)})
        using(var definition=new DynamicMethodDefinition(Fixture(name)))
        using(var context=new ILContext(definition.Definition))
        {
            var before=context.Body.Instructions.Select(i=>i.ToString()).ToArray();
            Assert.Throws<InvalidOperationException>(()=>FearPopularityIl.Apply(context),name);
            CollectionAssert.AreEqual(before,context.Body.Instructions.Select(i=>i.ToString()).ToArray(),name+" leaves the entire body unchanged");
        }
    }
    [TestMethod]
    public void ChangedSignednessOrWidthIsRejected()
    {
        foreach(bool input in new[]{true,false})
        using(var definition=new DynamicMethodDefinition(Fixture(nameof(Unified))))
        using(var context=new ILContext(definition.Definition))
        {
            var instruction=context.Body.Instructions.First(i=>i.Operand is FieldReference f &&
                (input?f.Name=="r_GoodBadThingBoost":FearPopularityIl.IsContributionField(f)));
            var original=(FieldReference)instruction.Operand;
            instruction.Operand=new FieldReference(original.Name,input?context.Method.Module.TypeSystem.UInt32:context.Method.Module.TypeSystem.UInt64,original.DeclaringType);
            Assert.Throws<InvalidOperationException>(()=>FearPopularityIl.Apply(context));
        }
    }

    // A remembered discovery result is bound to one loaded SE build; anything else repeats discovery.
    [TestMethod]
    public void RememberedTargetIsBoundToTheModuleBuild()
    {
        var mvid = Guid.NewGuid();
        var method = FearPopularityRuntime.FindTarget(typeof(CrusaderLibrary).Assembly);
        string text = FearPopularityRuntime.FormatRememberedTarget(mvid, method.MetadataToken);
        Assert.IsTrue(FearPopularityRuntime.TryParseRememberedTarget(text, mvid, out int token));
        Assert.AreEqual(method.MetadataToken, token);
        Assert.IsTrue(FearPopularityRuntime.TryParseRememberedTarget(text + Environment.NewLine, mvid, out _), "trailing line break");
        Assert.IsFalse(FearPopularityRuntime.TryParseRememberedTarget(text, Guid.NewGuid(), out _), "other SE build");
        foreach (string broken in new[] { null, "", "   ", mvid.ToString("D"), mvid + " zz", mvid + " 0", "x " + token.ToString("X8"), text + " extra" })
            Assert.IsFalse(FearPopularityRuntime.TryParseRememberedTarget(broken, mvid, out _), "malformed: " + broken);
        // The remembered token resolves to exactly the discovered method in the loaded module.
        Assert.AreEqual(method, typeof(CrusaderLibrary).Assembly.ManifestModule.ResolveMethod(token));
    }}

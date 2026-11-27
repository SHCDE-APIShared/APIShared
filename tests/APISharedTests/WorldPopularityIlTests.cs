using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using System;
using System.Linq;
using System.Reflection;

[TestClass]
public sealed class WorldPopularityIlTests
{
    [TestMethod]
    public void ChangedPopupArgumentMappingFailsBeforeAnyEdit()
    {
        using (var definition = new DynamicMethodDefinition(WorldPopularityIl.FindTarget()))
        using (var context = new ILContext(definition.Definition))
        {
            context.Invoke(il => {
                var store = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld &&
                    i.Operand is FieldReference f && f.DeclaringType.FullName == "Fly" && f.Name == "infoType");
                store.Previous.OpCode = OpCodes.Ldarg;
                store.Previous.Operand = il.Method.Parameters[il.Method.HasThis ? 11 : 12];
                var before = il.Body.Instructions.Select(i => (i, i.OpCode, i.Operand)).ToArray();
                Assert.Throws<InvalidOperationException>(() => WorldPopularityIl.Apply(il));
                CollectionAssert.AreEqual(before.Select(i => i.i).ToArray(), il.Body.Instructions.ToArray());
                foreach (var item in before) { Assert.AreEqual(item.OpCode, item.i.OpCode); Assert.AreSame(item.Operand, item.i.Operand); }
            });
        }
    }

    [TestMethod]
    public void InstalledPublicFlyMethodTransformsAndEmitsWithoutEditingItsOriginalBody()
    {
        var method = WorldPopularityIl.FindTarget();
        Assert.IsTrue(method.IsPublic && !method.IsStatic);
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            context.Invoke(il => {
                var original = il.Body.Instructions.Select(i => (i, i.OpCode, i.Operand)).ToArray();
                WorldPopularityIl.Apply(il);
                CollectionAssert.AreEqual(original.Select(i => i.i).ToArray(), il.Body.Instructions.Skip(5).ToArray());
                foreach (var item in original) { Assert.AreEqual(item.OpCode, item.i.OpCode); Assert.AreSame(item.Operand, item.i.Operand); }
            });
            var signature = new[] { method.DeclaringType }.Concat(method.GetParameters().Select(p => p.ParameterType))
                .Concat(new[] { method.ReturnType }).ToArray();
            var callback = definition.Generate().CreateDelegate(System.Linq.Expressions.Expression.GetDelegateType(signature));
            Assert.IsNotNull(callback);
            // JIT-preparing the unchanged Unity body outside Unity fails on its ECalls.
            // The boundary test below executes the identical emitted argument prefix.
        }
    }

    [TestMethod]
    public void CorrectMaskReachesOriginalCacheOnFirstCallAndOtherArgumentsSurvive()
    {
        var method = typeof(FlyBoundary).GetMethod(nameof(FlyBoundary.Add));
        using (var definition = new DynamicMethodDefinition(method))
        using (var context = new ILContext(definition.Definition))
        {
            context.Invoke(WorldPopularityIl.Apply);
            var callback = (Action<FlyBoundary, int, int, int, int, int, float, int, int, int, int, int, int, int, int>)
                definition.Generate().CreateDelegate(typeof(Action<FlyBoundary, int, int, int, int, int, float, int, int, int, int, int, int, int, int>));
            var target = new FlyBoundary();
            callback(target, 7, 8, 9, 10, 11, 2.5f, 202, 1, 2, 3, 4, 41, 130, 98);
            Assert.AreEqual(128, target.Mask); Assert.AreEqual(98, target.Value); Assert.AreEqual(1, target.Calls);
            Assert.AreEqual(7, target.ObjectId); Assert.AreEqual(2.5f, target.Height);
            callback(target, 7, 8, 9, 10, 11, 2.5f, 202, 1, 2, 3, 4, 40, 171, 98);
            Assert.AreEqual(171, target.Mask); Assert.AreEqual(98, target.Value); Assert.AreEqual(2, target.Calls);
        }
    }

    public sealed class FlyBoundary
    {
        public int Mask, Value, Calls, ObjectId;
        public float Height;
        public void Add(int id, int x, int y, int tileX, int tileY, float height, int file, int image,
            int colour, int transparency, int layer, int type, int mask, int value)
        {
            var fly = new Fly { gameObjectType = type, infoType = mask, infoData = value };
            ObjectId = id; Height = height; Mask = fly.infoType; Value = fly.infoData; Calls++;
        }
    }
}

using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class ConstructionRefundInteropTests
    {
        [TestMethod]
        public void RefundArithmeticPreservesMaterialPrecisionAndNativeStationaryUnitPercentages()
        {
            // Preserve the installed SE expression, rather than assuming .NET's result
            // also holds for Unity Mono's wider intermediate floating-stack precision.
            float multiplier = .7f, percentageFactor = 100 / 100f;
            int seAmount = (int)(ulong)(10 * multiplier * percentageFactor);
            Assert.AreEqual(seAmount, HumanConstructionRefundMath.Materials(10, multiplier, 100));
            Assert.AreEqual(-seAmount, HumanConstructionRefundMath.Materials(-10, multiplier, 100));
            // Exercise the real frame adapter, not just the arithmetic helper: unit refunds
            // bypass SE's material multiplier and use native integer percentage division.
            Type runtime = typeof(HumanConstructionRuntime);
            Type frameType = runtime.GetNestedType("RefundFrame", BindingFlags.NonPublic);
            object frame = Activator.CreateInstance(frameType, true);
            frameType.GetField("Identity", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(frame,
                new ConstructionObjectIdentity(ConstructionObjectKind.Unit, 1, 1));
            frameType.GetField("Percentage", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(frame, 29);
            MethodInfo quote = runtime.GetMethod("QuoteFrameRefund", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.AreEqual(29, (int)quote.Invoke(null, new object[] { frame, SHCDESE.Interop.eGoods.STORED_GOLD, 100 }));
            Assert.AreEqual(-29, (int)quote.Invoke(null, new object[] { frame, SHCDESE.Interop.eGoods.STORED_GOLD, -100 }));
        }

        [TestMethod]
        public void InstalledRefundCallbackUsesEntryIdentityWithoutDereferencingCallerStack()
        {
            MethodInfo callback = HumanConstructionRuntime.FindRefundInterceptor(typeof(GameBuildingManagerAPI).Assembly);
            using (var full = new DynamicMethodDefinition(callback))
            using (var context = new ILContext(full.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
            {
                HumanConstructionRuntime.TransformRefundInterceptor(context);
                MethodInfo generated = full.Generate();
                Type signature = Expression.GetDelegateType(generated.GetParameters().Select(p => p.ParameterType)
                    .Concat(new[] { generated.ReturnType }).ToArray());
                // Generate alone defers IL verification. Prepare the complete production callback
                // without invoking its game-manager/native operations.
                RuntimeHelpers.PrepareDelegate(generated.CreateDelegate(signature));
            }
            Type owner = typeof(GameBuildingManagerAPI).Assembly.GetType("SHCDESE.Detours.BulkBuildingDetours", true);
            FieldInfo field = owner.GetField("RefundContextBuildingId", BindingFlags.Static | BindingFlags.NonPublic);
            int prior = (int)field.GetValue(null);
            try
            {
                using (var extracted = new DynamicMethodDefinition(callback))
                using (var context = new ILContext(extracted.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                using (var prefix = new DynamicMethodDefinition("RefundIdentityPrefix", typeof(int), Type.EmptyTypes))
                {
                    HumanConstructionRuntime.TransformRefundInterceptor(context);
                    // Execute the production identity-selection prefix with a null/default CPU-context
                    // pointer. Only the subsequent game-manager calls are removed from this copy.
                    // DMD copies retain OriginalMethod.ReturnType (void) during generation even
                    // if Definition.ReturnType is edited. Use an independent int-returning copy.
                    var code = prefix.Definition.Body.Instructions;
                    prefix.Definition.Body.Variables.Add(new VariableDefinition(prefix.Definition.Module.TypeSystem.Int32));
                    foreach (var instruction in extracted.Definition.Body.Instructions.Take(8))
                    {
                        var copy = Instruction.Create(OpCodes.Nop); copy.OpCode = instruction.OpCode;
                        // A Cecil reference copied from a DMD module can retain its assembly-less
                        // module scope. Import the real reflection field into this independent copy.
                        copy.Operand = instruction.Operand is FieldReference
                            ? prefix.Definition.Module.ImportReference(field) : instruction.Operand;
                        code.Add(copy);
                    }
                    code.Add(Instruction.Create(OpCodes.Ldloc_0)); code.Add(Instruction.Create(OpCodes.Ret));
                    var readIdentity = (Func<int>)prefix.Generate().CreateDelegate(typeof(Func<int>));
                    foreach (int identity in new[] { 52, -4, 0 })
                    {
                        field.SetValue(null, identity);
                        Assert.AreEqual(identity, readIdentity());
                    }
                }
            }
            finally { field.SetValue(null, prior); }
        }

        [TestMethod]
        public void FinalRefundArgumentsAlignMaterialContextAndNestedFailuresRestoreIt()
        {
            Type runtime = typeof(HumanConstructionRuntime);
            Type owner = typeof(GameBuildingManagerAPI).Assembly.GetType("SHCDESE.Detours.BulkBuildingDetours", true);
            var id = owner.GetField("RefundContextBuildingId", BindingFlags.Static | BindingFlags.NonPublic);
            var percentage = owner.GetField("RefundContextPercentage", BindingFlags.Static | BindingFlags.NonPublic);
            var cachedId = runtime.GetField("refundContextId", BindingFlags.Static | BindingFlags.NonPublic);
            var cachedPercentage = runtime.GetField("refundContextPercentage", BindingFlags.Static | BindingFlags.NonPublic);
            var invoke = runtime.GetMethod("InvokeRefundOriginal", BindingFlags.Static | BindingFlags.NonPublic);
            object priorCachedId = cachedId.GetValue(null), priorCachedPercentage = cachedPercentage.GetValue(null);
            int priorId = (int)id.GetValue(null), priorPercentage = (int)percentage.GetValue(null);
            try
            {
                cachedId.SetValue(null, id); cachedPercentage.SetValue(null, percentage);
                id.SetValue(null, 111); percentage.SetValue(null, 33);
                Action<int, int, int, int> nested = (manager, building, player, percent) => {
                    Assert.AreEqual(17, (int)id.GetValue(null)); Assert.AreEqual(25, (int)percentage.GetValue(null));
                    throw new InvalidOperationException("original failure");
                };
                Action<int, int, int, int> outer = (manager, building, player, percent) => {
                    Assert.AreEqual(52, (int)id.GetValue(null)); Assert.AreEqual(75, (int)percentage.GetValue(null));
                    Assert.ThrowsExactly<TargetInvocationException>(() => invoke.Invoke(null, new object[] { nested, new object[] { 0, 17, 1, 25 } }));
                    Assert.AreEqual(52, (int)id.GetValue(null)); Assert.AreEqual(75, (int)percentage.GetValue(null));
                };
                invoke.Invoke(null, new object[] { outer, new object[] { 0, 52, 1, 75 } });
                Assert.AreEqual(111, (int)id.GetValue(null)); Assert.AreEqual(33, (int)percentage.GetValue(null));
            }
            finally
            {
                id.SetValue(null, priorId); percentage.SetValue(null, priorPercentage);
                cachedId.SetValue(null, priorCachedId); cachedPercentage.SetValue(null, priorCachedPercentage);
            }
        }
    }
}

using System;
using System.Linq;
using System.Reflection;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.Detours;

namespace APISharedTests
{
    [TestClass]
    public sealed class TroopCommandInputTests
    {
        [TestMethod]
        public void ReleaseRequiresTheConfiguredButtonAndRejectsReplacementSelection()
        {
            Assert.IsTrue(TroopCommandInputEvents.IsCommandRelease(false, 0, true, false, false, 120, 250));
            Assert.IsTrue(TroopCommandInputEvents.IsCommandRelease(true, 3, false, false, false, 0, 0));
            Assert.IsFalse(TroopCommandInputEvents.IsCommandRelease(false, 3, false, false, false, 120, 250));
            Assert.IsFalse(TroopCommandInputEvents.IsCommandRelease(true, 0, true, false, false, 120, 250));
            Assert.IsFalse(TroopCommandInputEvents.IsCommandRelease(true, 3, false, false, false, -1, -1));
            Assert.IsFalse(TroopCommandInputEvents.IsCommandRelease(true, 3, false, true, false, 120, 250));
            Assert.IsFalse(TroopCommandInputEvents.IsCommandRelease(false, 0, true, false, true, 120, 250));
        }

        [TestMethod]
        public void FilteredNativeSlotsDoNotBecomePublicIds()
        {
            CollectionAssert.AreEqual(new[] { 84, 23, 90 },
                TroopCommandInputEvents.RemoveFilteredUnits(new[] { 0, 84, 0, 23, -1, 90, 0 }));
            Assert.AreEqual(0, TroopCommandInputEvents.RemoveFilteredUnits(new[] { 0, 0 }).Length);
            var units = new[] { 25, 3 };
            Assert.AreSame(units, TroopCommandInputEvents.RemoveFilteredUnits(units));
        }

        [TestMethod]
        public void BothBoundariesValidateAgainstTheInstalledMethods()
        {
            Verify(typeof(BulkUnitDetours).GetMethod(nameof(BulkUnitDetours.c_game_dll_troopselection_hook_impl)),
                TroopCommandInputHooks.TransformSelection);
            Verify(typeof(EngineInterface).GetMethod(nameof(EngineInterface.run), new[] { typeof(bool) }),
                TroopCommandInputHooks.TransformRun);
        }

        [TestMethod]
        public void ReplacedExtenderInvokeArgumentFailsClosed()
        {
            var method = typeof(BulkUnitDetours).GetMethod(nameof(BulkUnitDetours.c_game_dll_troopselection_hook_impl));
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                var invoke = context.Body.Instructions.Single(instruction =>
                    instruction.Operand is MethodReference call && call.Name == "Invoke");
                invoke.Previous.OpCode = OpCodes.Ldc_I8;
                invoke.Previous.Operand = 0L;
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    context.Invoke(TroopCommandInputHooks.TransformSelection));
            }
        }

        private static void Verify(MethodInfo method, ILContext.Manipulator transform)
        {
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(transform);
                Assert.IsNotNull(definition.Generate());
            }
        }
    }
}

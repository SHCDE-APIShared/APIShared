using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using APIShared.Patching;
using HarmonyLib;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Each test patches its own private fixture method; patches stay for the process, as in the game.
    [TestClass]
    [DoNotParallelize]
    public sealed class SafeTranspilerTests
    {
        private const string Owner = "APISharedTests";
        private static readonly Harmony harmony = new Harmony("APISharedTests.SafeTranspiler");
        private static int postfixRuns;

        // Branches and a try/finally: a transpiler result that loses labels or exception blocks cannot be generated.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Kept(int value) { int result; try { result = value > 0 ? 10 : 20; } finally { value++; } return result + value; }
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int Failing(int value) { int result; try { result = value > 0 ? 10 : 20; } finally { value++; } return result + value; }

        private static IEnumerable<CodeInstruction> AddOneBeforeReturn(IEnumerable<CodeInstruction> instructions)
        {
            var code = instructions.ToList();
            int at = code.FindLastIndex(instruction => instruction.opcode == OpCodes.Ret);
            // Insert after the labels of the return so that every branch to it also runs the edit.
            var add = new CodeInstruction(OpCodes.Ldc_I4_1).MoveLabelsFrom(code[at]);
            code.InsertRange(at, new[] { add, new CodeInstruction(OpCodes.Add) });
            return code;
        }

        private static IEnumerable<CodeInstruction> KeptTranspiler(IEnumerable<CodeInstruction> instructions) =>
            SafeTranspiler.Run(Owner, "APISharedTests.Kept", instructions, AddOneBeforeReturn, null);

        // Edits a copy, then fails: Harmony must still receive the untouched original.
        private static IEnumerable<CodeInstruction> FailingTranspiler(IEnumerable<CodeInstruction> instructions) =>
            SafeTranspiler.Run(Owner, "APISharedTests.Failing", instructions, code =>
            {
                var edited = AddOneBeforeReturn(code).ToList();
                foreach (var instruction in edited) instruction.labels.Clear();
                throw new InvalidOperationException("planned failure");
            }, null);

        private static void CountPostfix() => postfixRuns++;

        private static MethodInfo Fixture(string name) =>
            typeof(SafeTranspilerTests).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        [TestMethod]
        public void AnEditOfAMethodWithBranchesAndFinallyKeepsItsLabelsAndBlocks()
        {
            Assert.AreEqual(12, Kept(1)); Assert.AreEqual(21, Kept(0));
            harmony.Patch(Fixture(nameof(Kept)), transpiler: new HarmonyMethod(Fixture(nameof(KeptTranspiler))));
            Assert.AreEqual(13, Kept(1)); Assert.AreEqual(22, Kept(0));
        }

        [TestMethod]
        public void AFailingEditKeepsTheOriginalAndTheOtherPatchesOfTheMethod()
        {
            harmony.Patch(Fixture(nameof(Failing)), postfix: new HarmonyMethod(Fixture(nameof(CountPostfix))),
                transpiler: new HarmonyMethod(Fixture(nameof(FailingTranspiler))));
            int before = postfixRuns;
            Assert.AreEqual(12, Failing(1)); Assert.AreEqual(21, Failing(0));
            Assert.AreEqual(before + 2, postfixRuns, "The postfix of the same patch operation still runs.");
        }
    }
}

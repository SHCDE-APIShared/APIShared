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

        [TestMethod]
        public void SuccessfulEditsPreserveSharedMetadataAliasesWithoutChangingTheInput()
        {
            var input = MetadataInstructions(out Label originalTarget, out Label replacement);
            int outcomes = 0;
            var result = SafeTranspiler.Run(Owner, "metadata-success", input, code =>
            {
                var edited = code.ToList();
                ((Label[])edited[0].operand)[0] = replacement;
                edited[2].blocks[0].catchType = typeof(Exception);
                return edited;
            }, null, applied => { Assert.IsTrue(applied); outcomes++; }).ToList();

            Assert.AreEqual(originalTarget, ((Label[])input[0].operand)[0]);
            Assert.AreEqual(typeof(InvalidOperationException), input[2].blocks[0].catchType);
            Assert.AreNotSame(input[0].operand, result[0].operand);
            Assert.AreSame(result[0].operand, result[1].operand, "Two switches can deliberately share one target array.");
            Assert.AreEqual(replacement, ((Label[])result[1].operand)[0]);
            Assert.AreNotSame(input[2].blocks[0], result[2].blocks[0]);
            Assert.AreSame(result[2].blocks[0], result[3].blocks[0]);
            Assert.AreEqual(typeof(Exception), result[3].blocks[0].catchType);
            Assert.AreEqual(originalTarget, result[0].labels[0], "Copy the target array, never the label identities.");
            Assert.AreNotSame(input[0].labels, result[0].labels);
            Assert.AreEqual(1, outcomes);
        }

        [TestMethod]
        public void LazyFailureRestoresSharedSwitchAndExceptionMetadataAndReportsOnce()
        {
            var input = MetadataInstructions(out Label originalTarget, out Label replacement);
            int outcomes = 0; bool? appliedOutcome = null;
            IEnumerable<CodeInstruction> Fail(IEnumerable<CodeInstruction> code)
            {
                var edited = code.ToList();
                yield return edited[0]; // The failure must be caught during enumeration, too.
                ((Label[])edited[0].operand)[0] = replacement;
                edited[2].blocks[0].blockType = ExceptionBlockType.BeginFinallyBlock;
                edited[2].blocks[0].catchType = typeof(Exception);
                throw new InvalidOperationException("planned lazy failure");
            }
            var result = SafeTranspiler.Run(Owner, "metadata-failure", input, Fail, null,
                applied => { outcomes++; appliedOutcome = applied; throw new Exception("outcome callback failure"); }).ToList();

            for (int i = 0; i < input.Count; i++) Assert.AreSame(input[i], result[i]);
            Assert.AreEqual(originalTarget, ((Label[])result[0].operand)[0]);
            Assert.AreEqual(ExceptionBlockType.BeginCatchBlock, result[2].blocks[0].blockType);
            Assert.AreEqual(typeof(InvalidOperationException), result[2].blocks[0].catchType);
            Assert.AreSame(result[0].operand, result[1].operand);
            Assert.AreSame(result[2].blocks[0], result[3].blocks[0]);
            Assert.AreEqual(1, outcomes); Assert.AreEqual(false, appliedOutcome);
        }

        [TestMethod]
        public void ExactCatchMetadataAndSuccessfulResultSurviveAnOutcomeFailure()
        {
            var input = MetadataInstructions(out _, out _);
            input[2].blocks[0].catchType = null;
            int outcomes = 0;
            var result = SafeTranspiler.Run(Owner, "metadata-outcome", input, code => code, null,
                applied => { outcomes++; throw new Exception("outcome callback failure"); }).ToList();
            Assert.IsNull(result[2].blocks[0].catchType, "The Harmony constructor substitutes object for null; copying must retain the exact field.");
            Assert.AreNotSame(input[2].blocks[0], result[2].blocks[0]);
            Assert.AreEqual(1, outcomes);
        }

        private static List<CodeInstruction> MetadataInstructions(out Label first, out Label second)
        {
            var generator = new DynamicMethod("MetadataLabels", typeof(void), Type.EmptyTypes).GetILGenerator();
            first = generator.DefineLabel(); second = generator.DefineLabel();
            var targets = new[] { first };
            var block = new ExceptionBlock(ExceptionBlockType.BeginCatchBlock, typeof(InvalidOperationException));
            var instructions = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Switch, targets), new CodeInstruction(OpCodes.Switch, targets),
                new CodeInstruction(OpCodes.Nop), new CodeInstruction(OpCodes.Nop)
            };
            instructions[0].labels.Add(first);
            instructions[2].blocks.Add(block); instructions[3].blocks.Add(block);
            return instructions;
        }
    }
}

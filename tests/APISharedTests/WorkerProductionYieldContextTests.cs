using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class WorkerProductionYieldContextTests
    {
        private static IntPtr slot;
        private static Func<int,int,int,long> run;
        private static Action<UnitCalculateBonusYieldEventArgs> pre, post;
        private static Func<int,int,int,long> original;
        private static void Raise(UnitCalculateBonusYieldEventArgs args)
        { if (args.Phase == EventHookPhase.Pre) pre?.Invoke(args); else post?.Invoke(args); }
        private static long Native(int id,int amount,int bonus) => original(id,amount,bonus);

        // Execute the installed SE body, replacing only external publisher/native boundaries.
        // This preserves its actual Pre/Skip/Post/ReturnValue implementation, not a duplicate.
        private static Func<int,int,int,long> Compile(bool instrument)
        {
            using (var definition = new DynamicMethodDefinition(WorkerProductionYieldContext.Resolve()))
            using (var il = new ILContext(definition.Definition))
            {
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                int raised = 0, invoked = 0, handles = 0, publishers = 0;
                foreach (Instruction instruction in il.Body.Instructions)
                {
                    if (instruction.Operand is FieldReference field && field.Name == "c_game_unit_calculate_worker_good_yield_hook")
                    {
                        Assert.AreEqual(OpCodes.Ldsfld, instruction.OpCode);
                        Instruction getter = instruction.Next;
                        Assert.AreEqual("get_Original", ((MethodReference)getter.Operand).Name);
                        instruction.OpCode = OpCodes.Nop; instruction.Operand = null;
                        getter.OpCode = OpCodes.Nop; getter.Operand = null; handles++;
                    }
                    else if (instruction.Operand is FieldReference publisher && publisher.Name == "OnCalculateBonusYield")
                    {
                        Assert.AreEqual(OpCodes.Ldsfld, instruction.OpCode);
                        instruction.OpCode = OpCodes.Nop; instruction.Operand = null; publishers++;
                    }
                    else if (instruction.Operand is MethodReference method && method.Name == "Raise" &&
                        method.DeclaringType is GenericInstanceType generic && generic.GenericArguments.Count == 1 &&
                        generic.GenericArguments[0].FullName == typeof(UnitCalculateBonusYieldEventArgs).FullName)
                    {
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = il.Import(typeof(WorkerProductionYieldContextTests).GetMethod(nameof(Raise), BindingFlags.NonPublic|BindingFlags.Static));
                        raised++;
                    }
                    else if (instruction.Operand is MethodReference invoke && invoke.Name == "Invoke" &&
                        invoke.DeclaringType.Name == "c_game_unit_calculate_worker_good_yield_delegate")
                    {
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = il.Import(typeof(WorkerProductionYieldContextTests).GetMethod(nameof(Native), BindingFlags.NonPublic|BindingFlags.Static));
                        invoked++;
                    }
                }
                Assert.AreEqual(2, raised); Assert.AreEqual(2, publishers); Assert.AreEqual(1, invoked); Assert.AreEqual(1, handles);
                if (instrument)
                {
                    int calls = il.Body.Instructions.Count(i => i.OpCode.FlowControl == FlowControl.Call);
                    int allocations = il.Body.Instructions.Count(i => i.OpCode == OpCodes.Newobj);
                    WorkerProductionYieldContext.Apply(il, unchecked((ulong)slot.ToInt64()));
                    Assert.AreEqual(calls, il.Body.Instructions.Count(i => i.OpCode.FlowControl == FlowControl.Call), "No new managed hot-path callback");
                    Assert.AreEqual(allocations, il.Body.Instructions.Count(i => i.OpCode == OpCodes.Newobj), "No new hot-path allocation");
                }
                return (Func<int,int,int,long>)definition.Generate().CreateDelegate(typeof(Func<int,int,int,long>));
            }
        }

        private static void Fixture(Action body)
        {
            slot = Marshal.AllocHGlobal(4); Marshal.WriteInt32(slot, 77);
            try { run = Compile(true); body(); }
            finally { run = null; pre = null; post = null; original = null; Marshal.FreeHGlobal(slot); slot = IntPtr.Zero; }
        }

        [TestMethod]
        public void OriginalOwnerSurvivesRedirectAndNestedPreAndPostCalls()
        {
            Fixture(() => {
                int calls = 0;
                pre = args => {
                    Assert.AreEqual(args.UnitId, Marshal.ReadInt32(slot));
                    if (args.UnitId != 11) return;
                    args.UnitId = 99; args.GoodAmount = 5; args.b50PercentBonus = 0;
                    Assert.AreEqual(44L, run(22, 2, 1));
                    Assert.AreEqual(11, Marshal.ReadInt32(slot));
                };
                original = (id, amount, bonus) => {
                    calls++;
                    if (id == 99) { Assert.AreEqual(11, Marshal.ReadInt32(slot)); Assert.AreEqual(5, amount); Assert.AreEqual(0, bonus); }
                    else Assert.AreEqual(id, Marshal.ReadInt32(slot));
                    return id == 99 ? 120L : id * 2L;
                };
                post = args => {
                    Assert.AreEqual(args.UnitId, Marshal.ReadInt32(slot));
                    if (args.UnitId != 11) return;
                    Assert.AreEqual(66L, run(33, 3, 1)); Assert.AreEqual(11, Marshal.ReadInt32(slot));
                    args.ReturnValue += 7;
                };
                Assert.AreEqual(127L, run(11, 8, 1)); Assert.AreEqual(3, calls); Assert.AreEqual(77, Marshal.ReadInt32(slot));
            });
        }

        [TestMethod]
        public void SkipPreservesExistingResultWithoutOriginalOrPost()
        {
            Fixture(() => {
                pre = args => { Assert.AreEqual(11, Marshal.ReadInt32(slot)); args.SkipOriginalFunction = true; args.ReturnValue = 123; };
                original = (id,amount,bonus) => throw new AssertFailedException("Skipped original called");
                post = args => Assert.Fail("Skipped Post called");
                Assert.AreEqual(123L, run(11, 1, 1)); Assert.AreEqual(77, Marshal.ReadInt32(slot));
            });
        }

        [TestMethod]
        public void EveryExceptionPathRestoresTheEnclosingContext()
        {
            Fixture(() => {
                for (int stage = 0; stage != 3; stage++)
                {
                    int failingStage = stage;
                    pre = args => { if (failingStage == 0) throw new InvalidOperationException("Pre"); };
                    original = (id,amount,bonus) => { if (failingStage == 1) throw new InvalidOperationException("Original"); return 2; };
                    post = args => { if (failingStage == 2) throw new InvalidOperationException("Post"); };
                    Assert.Throws<InvalidOperationException>(() => run(11, 1, 1));
                    Assert.AreEqual(77, Marshal.ReadInt32(slot));
                }
                pre = args => { if (args.UnitId == 11) { Assert.Throws<InvalidOperationException>(() => run(22, 1, 1)); Assert.AreEqual(11, Marshal.ReadInt32(slot)); } else throw new InvalidOperationException("Nested Pre"); };
                original = (id,amount,bonus) => 9; post = null;
                Assert.AreEqual(9L, run(11, 1, 1)); Assert.AreEqual(77, Marshal.ReadInt32(slot));
            });
        }

        private static long MultipleReturns(int id,int amount,int bonus)
        { if (id < 0) return -7; if (amount == 0) return 0; return (long)id + amount + bonus; }

        [TestMethod]
        public void AllReturnsLeaveThroughFinally()
        {
            slot = Marshal.AllocHGlobal(4); Marshal.WriteInt32(slot, 77);
            try
            {
                using (var definition = new DynamicMethodDefinition(typeof(WorkerProductionYieldContextTests).GetMethod(nameof(MultipleReturns), BindingFlags.Static|BindingFlags.NonPublic)))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    // C# Debug builds may merge all returns. Build three real RETs and a
                    // branch directly to one RET to exercise instruction-identity preservation.
                    il.Body.Instructions.Clear(); il.Body.Variables.Clear();
                    var negative = Instruction.Create(OpCodes.Ldc_I8, -7L);
                    var zero = Instruction.Create(OpCodes.Ldc_I8, 0L);
                    var normalReturn = Instruction.Create(OpCodes.Ret);
                    var instructions = il.Body.Instructions;
                    instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    instructions.Add(Instruction.Create(OpCodes.Blt_S, negative));
                    instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); instructions.Add(Instruction.Create(OpCodes.Brfalse_S, zero));
                    instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); instructions.Add(Instruction.Create(OpCodes.Conv_I8));
                    instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); instructions.Add(Instruction.Create(OpCodes.Conv_I8));
                    instructions.Add(Instruction.Create(OpCodes.Add)); instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
                    instructions.Add(Instruction.Create(OpCodes.Conv_I8)); instructions.Add(Instruction.Create(OpCodes.Add));
                    instructions.Add(Instruction.Create(OpCodes.Br_S, normalReturn));
                    instructions.Add(negative); instructions.Add(Instruction.Create(OpCodes.Ret));
                    instructions.Add(zero); instructions.Add(Instruction.Create(OpCodes.Ret)); instructions.Add(normalReturn);
                    Assert.AreEqual(3, instructions.Count(i => i.OpCode == OpCodes.Ret));
                    WorkerProductionYieldContext.Apply(il, unchecked((ulong)slot.ToInt64()));
                    var call = (Func<int,int,int,long>)definition.Generate().CreateDelegate(typeof(Func<int,int,int,long>));
                    Assert.AreEqual(-7L, call(-1,1,1)); Assert.AreEqual(77, Marshal.ReadInt32(slot));
                    Assert.AreEqual(0L, call(1,0,1)); Assert.AreEqual(77, Marshal.ReadInt32(slot));
                    Assert.AreEqual(6L, call(1,2,3)); Assert.AreEqual(77, Marshal.ReadInt32(slot));
                }
            }
            finally { Marshal.FreeHGlobal(slot); slot = IntPtr.Zero; }
        }
    }
}

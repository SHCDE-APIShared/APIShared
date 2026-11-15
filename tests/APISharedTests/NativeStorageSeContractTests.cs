using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using MonoMod.Cil;
using MonoMod.Utils;
using R3;
using SHCDESE.EventAPI;
using SHCDESE.Interop;
using CilOpCodes = Mono.Cecil.Cil.OpCodes;

namespace APISharedTests
{
    // Execute a generated copy of the installed wrapper, with a managed original. No native
    // hooks, game managers or executable buffers are created. SE event subscribers are restored.
    [TestClass, DoNotParallelize]
    public sealed class NativeStorageSeContractTests
    {
        private const long OpaqueResult = unchecked((long)0xFEDCBA9876543210UL);
        private static int originalCalls;
        private static IntPtr observedManager;
        private static int[] observedArguments;

        [TestInitialize]
        public void ResetOriginalObservation()
        { originalCalls = 0; observedManager = IntPtr.Zero; observedArguments = null; }

        [TestMethod]
        public void InstalledWrapperHasOneExactFinalArgumentOriginalBetweenSePreAndPost()
        {
            MethodInfo method = NativeStorageSeContract.Resolve();
            NativeStorageSeContract.Validate(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition))
            {
                var original = NativeStorageSeContract.Find(il);
                il.Body.Instructions.Remove(original);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeStorageSeContract.Find(il));
            }
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition))
            {
                var capacity = il.Body.Instructions.First(i => i.Operand is MethodReference m && m.Name == "get_Capacity");
                capacity.Operand = il.Body.Instructions.First(i => i.Operand is MethodReference m && m.Name == "get_AddAmount").Operand;
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeStorageSeContract.Find(il));
            }
        }

        [TestMethod]
        public void PreparedForwarderPreservesManagerUnsignedIdentityBitsAndOpaqueInt64()
        {
            MethodInfo method = NativeStorageSeContract.Resolve();
            Type type = NativeStorageSeContract.OriginalDelegateType(method);
            var forward = NativeStorageSeContract.CreateForwarder(type);
            IntPtr manager = new IntPtr(unchecked((long)0x1234567887654321UL));
            long result = forward(ManagedOriginal(type), manager, 7, unchecked((int)0xF1234567u), -1, -4, int.MaxValue, 9);
            Assert.AreEqual(OpaqueResult, result);
            Assert.AreEqual(manager, observedManager);
            CollectionAssert.AreEqual(new[] { 7, unchecked((int)0xF1234567u), -1, -4, int.MaxValue, 9 }, observedArguments);
            Assert.AreEqual(1, originalCalls);
        }

        [TestMethod]
        public void AdapterSeesFinalSePreArgumentsClampsOnlyOriginalAndPreservesLegacyPost()
        {
            Type type = NativeStorageSeContract.OriginalDelegateType(NativeStorageSeContract.Resolve());
            var forward = NativeStorageSeContract.CreateForwarder(type);
            int[] effective = null, post = null; int posts = 0;
            using (var wrapper = new Wrapper((original, manager, building, globalId, good, amount, capacity, add) => {
                effective = new[] { building, globalId, good, amount, capacity, add };
                return forward(original, manager, building, globalId, good, amount, Math.Min(capacity, 50), add);
            }))
            using (BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(args => {
                if (args.Phase == EventHookPhase.Pre)
                {
                    args.BuildingId = 11; args.BuildingGlobalId = 21; args.Good = eGoods.STORED_STONE_BLOCKS;
                    args.AddAmount = 7; args.Capacity = 90; args.Add = true;
                }
                else
                {
                    posts++; post = new[] { args.BuildingId, args.BuildingGlobalId, (int)args.Good, args.AddAmount, args.Capacity, args.Add ? 1 : 0 };
                    Assert.AreEqual(0L, args.ReturnValue, "SE Post's legacy property does not acknowledge the original result.");
                }
            }))
            {
                IntPtr manager = new IntPtr(0x1234);
                long result = wrapper.Call(manager, 10, 20, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 0);
                Assert.AreEqual(OpaqueResult, result, "The opaque Int64 must survive SE Post without ==1 normalization.");
                Assert.AreEqual(manager, observedManager);
                CollectionAssert.AreEqual(new[] { 11, 21, (int)eGoods.STORED_STONE_BLOCKS, 7, 90, 1 }, effective);
                CollectionAssert.AreEqual(new[] { 11, 21, (int)eGoods.STORED_STONE_BLOCKS, 7, 50, 1 }, observedArguments);
                CollectionAssert.AreEqual(new[] { 10, 20, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 0 }, post);
                Assert.AreEqual(1, originalCalls); Assert.AreEqual(1, posts);
            }
        }

        [TestMethod]
        public void RefusingFinalInvalidOrFullCallSkipsOriginalAndStillRunsSePost()
        {
            int posts = 0, adapters = 0;
            using (var wrapper = new Wrapper((original, manager, building, globalId, good, amount, capacity, add) => {
                adapters++;
                Assert.IsTrue(amount <= 0 || capacity == 0);
                return 0;
            }))
            using (BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(args => {
                if (args.Phase == EventHookPhase.Pre)
                {
                    if (args.BuildingId == 10) args.AddAmount = -1;
                    else args.Capacity = 0;
                }
                else posts++;
            }))
            {
                Assert.AreEqual(0L, wrapper.Call(new IntPtr(0x1234), 10, 20, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 1));
                Assert.AreEqual(0L, wrapper.Call(new IntPtr(0x1234), 11, 21, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 1));
                Assert.AreEqual(2, adapters); Assert.AreEqual(2, posts); Assert.AreEqual(0, originalCalls);
            }
        }

        [TestMethod]
        public void WholeWrapperDepthCoversSePreOriginalPostAndNestedEntries()
        {
            int depth = 0, entered = 0, left = 0;
            var phases = new List<string>();
            Type type = NativeStorageSeContract.OriginalDelegateType(NativeStorageSeContract.Resolve());
            var forward = NativeStorageSeContract.CreateForwarder(type);
            Wrapper wrapper = null;
            using (wrapper = new Wrapper((original, manager, building, globalId, good, amount, capacity, add) => {
                phases.Add("original:" + depth);
                return forward(original, manager, building, globalId, good, amount, capacity, add);
            }, () => { entered++; depth++; }, () => { left++; depth--; }))
            using (BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(args => {
                phases.Add((args.Phase == EventHookPhase.Pre ? "pre:" : "post:") + depth);
                if (args.Phase == EventHookPhase.Pre && args.BuildingId == 10)
                    wrapper.Call(new IntPtr(0x1234), 11, 21, (int)eGoods.STORED_WOOD_PLANKS, 1, 100, 1);
            }))
            {
                Assert.AreEqual(OpaqueResult, wrapper.Call(new IntPtr(0x1234), 10, 20, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 1));
                CollectionAssert.AreEqual(new[] { "pre:1", "pre:2", "original:2", "post:2", "original:1", "post:1" }, phases);
                Assert.AreEqual(2, originalCalls); Assert.AreEqual(2, entered); Assert.AreEqual(2, left);
                Assert.AreEqual(0, depth);
            }
        }

        [TestMethod]
        public void WholeWrapperFinallyRestoresDepthWhenOriginalAdapterThrows()
        {
            int depth = 0, entered = 0, left = 0, preDepth = 0, originalDepth = 0, posts = 0;
            using (var wrapper = new Wrapper((original, manager, building, globalId, good, amount, capacity, add) => {
                originalDepth = depth;
                throw new InvalidOperationException("Managed original fixture failed.");
            }, () => { entered++; depth++; }, () => { left++; depth--; }))
            using (BuildingR3EventHooks.OnGoodsyardAddGood.Observable.Subscribe(args => {
                if (args.Phase == EventHookPhase.Pre) preDepth = depth;
                else posts++;
            }))
            {
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    wrapper.Call(new IntPtr(0x1234), 10, 20, (int)eGoods.STORED_WOOD_PLANKS, 5, 100, 1));
                Assert.AreEqual(1, preDepth); Assert.AreEqual(1, originalDepth); Assert.AreEqual(0, posts);
                Assert.AreEqual(1, entered); Assert.AreEqual(1, left); Assert.AreEqual(0, depth);
            }
        }

        private static long RecordOriginal(IntPtr manager, int building, int globalId, int good, int amount, int capacity, int add)
        {
            originalCalls++; observedManager = manager;
            observedArguments = new[] { building, globalId, good, amount, capacity, add };
            return OpaqueResult;
        }

        private static Delegate ManagedOriginal(Type type)
        {
            MethodInfo invoke = type.GetMethod("Invoke");
            Type[] parameters = invoke.GetParameters().Select(p => p.ParameterType).ToArray();
            var method = new DynamicMethod("NativeStorage.ManagedOriginalFixture", typeof(long), parameters, typeof(NativeStorageSeContractTests), true);
            ILGenerator il = method.GetILGenerator();
            // Extract the unmanaged address as bits; never dereference the fixture address.
            il.Emit(OpCodes.Ldarga, (short)0); il.Emit(OpCodes.Ldfld, parameters[0].GetField("Pointer")); il.Emit(OpCodes.Conv_I);
            for (short index = 1; index <= 6; index++) il.Emit(OpCodes.Ldarg, index);
            il.Emit(OpCodes.Call, typeof(NativeStorageSeContractTests).GetMethod(nameof(RecordOriginal), BindingFlags.Static | BindingFlags.NonPublic));
            il.Emit(OpCodes.Ret);
            return method.CreateDelegate(type);
        }

        private sealed class Wrapper : IDisposable
        {
            private readonly ILContext context;
            private readonly Delegate entry;
            private readonly Func<Delegate, IntPtr, int, int, int, int, int, int, long> forward;
            internal Wrapper(Func<Delegate, IntPtr, int, int, int, int, int, int, long> adapter, Action enter = null, Action leave = null)
            {
                MethodInfo method = NativeStorageSeContract.Resolve();
                Type type = NativeStorageSeContract.OriginalDelegateType(method);
                forward = NativeStorageSeContract.CreateForwarder(type);
                Delegate original = ManagedOriginal(type);
                using (var definition = new DynamicMethodDefinition(method))
                {
                    context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance };
                    context.Invoke(il => {
                        if (enter == null && leave == null) NativeStorageSeContract.Apply(il, adapter);
                        else NativeStorageSeContract.Apply(il, adapter, enter, leave);
                        // Supply the original only in this copy; the real SE handle stays untouched.
                        var load = il.Body.Instructions.Single(i => i.OpCode == CilOpCodes.Ldsfld &&
                            i.Operand is FieldReference field && field.Name == NativeStorageSeContract.HandleName);
                        var cursor = new ILCursor(il); cursor.Goto(load, MoveType.Before); cursor.RemoveRange(2);
                        cursor.EmitDelegate<Func<Delegate>>(() => original);
                    });
                    MethodInfo copy = definition.Generate();
                    entry = copy is DynamicMethod dynamic ? dynamic.CreateDelegate(type) : Delegate.CreateDelegate(type, copy);
                }
            }
            internal long Call(IntPtr manager, int building, int globalId, int good, int amount, int capacity, int add) =>
                forward(entry, manager, building, globalId, good, amount, capacity, add);
            public void Dispose() { context.Dispose(); GC.KeepAlive(entry); }
        }
    }
}

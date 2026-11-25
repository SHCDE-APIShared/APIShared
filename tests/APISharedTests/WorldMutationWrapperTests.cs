using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared;
using APIShared.WorldMutations;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class WorldMutationWrapperTests
    {
        private sealed class Observer : IWorldMutationObserver, IWorldMutationInvocation
        {
            internal readonly List<string> Events = new List<string>();
            internal WorldMutationCompletion Completion;
            internal WorldMutationBatchCompletion Batch;
            public IWorldMutationInvocation TryBegin(WorldMutationContext context)
            { Events.Add("begin:" + context.GetArgument(0)); return this; }
            public void Complete(WorldMutationCompletion completion) { Completion = completion; Events.Add("complete"); }
            public void OnBatchCompleted(WorldMutationBatchCompletion completion) { Batch = completion; Events.Add("batch"); }
        }
        private static long Wrapper(Func<long> child, Action pre, Action post, bool fail)
        {
            child(); // Mirror siblings precede the main wrapper's public Pre event.
            pre();
            try { if (fail) throw new InvalidOperationException("post-pre failure"); return child(); }
            finally { post(); }
        }

        [TestMethod]
        public void ProductionScopeEncompassesMirrorPreOriginalPostAndPreservesFailure()
        {
            var registry = WorldMutationRuntime.Registry; var available = registry.AvailableSources;
            bool enabled = true; var observer = new Observer();
            registry.SetAvailable(WorldMutationSource.All);
            Assert.IsTrue(registry.Register("tests.world.wrapper", Guid.NewGuid().ToString(), observer,
                WorldMutationSource.All, () => enabled, 0, out _));
            try
            {
                using (var definition = new DynamicMethodDefinition(typeof(WorldMutationWrapperTests).GetMethod(nameof(Wrapper), BindingFlags.Static | BindingFlags.NonPublic)))
                using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                {
                    WorldMutationRuntime.ApplyOuterScope(context, WorldMutationSource.BuildStructure);
                    var wrapper = (Func<Func<long>, Action, Action, bool, long>)definition.Generate().CreateDelegate(typeof(Func<Func<long>, Action, Action, bool, long>));
                    long calls = 0;
                    var original = new Func<long, long>(argument => { observer.Events.Add("native:" + argument); return ++calls; });
                    var observed = (Func<long, long>)WorldMutationRuntime.WrapOriginal(original, original.GetType(), WorldMutationSource.BuildStructure);
                    Assert.AreEqual(2L, wrapper(() => observed(42), () => observer.Events.Add("pre"), () => observer.Events.Add("post"), false));
                    CollectionAssert.AreEqual(new[] { "begin:42", "native:42", "complete", "pre", "begin:42", "native:42", "complete", "post", "batch" }, observer.Events);
                    Assert.IsTrue(observer.Batch.ReturnedNormally); Assert.AreEqual(2L, observer.Completion.ReturnValue);
                    observer.Events.Clear();
                    Assert.ThrowsExactly<InvalidOperationException>(() => wrapper(() => observed(43), () => observer.Events.Add("pre"), () => observer.Events.Add("post"), true));
                    CollectionAssert.AreEqual(new[] { "begin:43", "native:43", "complete", "pre", "post", "batch" }, observer.Events);
                    Assert.IsFalse(observer.Batch.ReturnedNormally); Assert.AreEqual(3L, calls);
                    Assert.IsFalse(observer.Batch.ContainsOriginalFailure, "A wrapper failure must not invent an original failure.");
                    var failure = new ArgumentException("original");
                    var throwing = new Func<long, long>(argument => throw failure);
                    var wrappedThrow = (Func<long, long>)WorldMutationRuntime.WrapOriginal(throwing, throwing.GetType(), WorldMutationSource.BuildStructure);
                    Assert.AreSame(failure, Assert.ThrowsExactly<ArgumentException>(() => wrappedThrow(2)));
                    Assert.AreSame(failure, observer.Completion.Failure);
                    Assert.IsTrue(observer.Completion.OriginalInvoked); Assert.IsFalse(observer.Completion.OriginalReturned);
                }
            }
            finally { enabled = false; registry.SetAvailable(available); }
        }

        [TestMethod]
        public void AivWholeFrameCompletesAfterChildrenAndCleanupWithoutReplacingOriginalOutcome()
        {
            var registry = WorldMutationRuntime.Registry; var available = registry.AvailableSources;
            bool enabled = true; var observer = new Observer();
            registry.SetAvailable(WorldMutationSource.All);
            Assert.IsTrue(registry.Register("tests.world.aiv", Guid.NewGuid().ToString(), observer,
                WorldMutationSource.All, () => enabled, 0, out _));
            try
            {
                var child = new Func<long, long>(argument => { observer.Events.Add("child"); return argument; });
                var observed = (Func<long, long>)WorldMutationRuntime.WrapOriginal(child, child.GetType(), WorldMutationSource.BuildStructure);
                var frame = new AivBuildStepContext(0x1234, 4, 9, 2, 1);
                Assert.AreEqual(7, WorldMutationRuntime.InvokeAiv(frame, original => {
                    observer.Events.Add("pre"); int result = original(); observer.Events.Add("post"); return result;
                }, () => { observed(42); observer.Events.Add("cleanup-tail"); return 7; }));
                CollectionAssert.AreEqual(new[] { "begin:4660", "pre", "begin:42", "child", "complete", "cleanup-tail", "post", "complete", "batch" }, observer.Events);
                Assert.AreEqual(9, observer.Completion.Context.FrameIndex);
                Assert.AreEqual(7L, observer.Completion.ReturnValue);
                var postError = new InvalidOperationException("foreign post");
                Assert.AreSame(postError, Assert.ThrowsExactly<InvalidOperationException>(() =>
                    WorldMutationRuntime.InvokeAiv(frame, original => { original(); throw postError; }, () => 8)));
                Assert.IsTrue(observer.Completion.OriginalInvoked); Assert.IsTrue(observer.Completion.OriginalReturned);
                Assert.IsNull(observer.Completion.Failure); Assert.AreEqual(8L, observer.Completion.ReturnValue);
                Assert.IsFalse(observer.Batch.ReturnedNormally); Assert.IsFalse(observer.Batch.ContainsOriginalFailure);
            }
            finally { enabled = false; registry.SetAvailable(available); }
        }

        [TestMethod]
        public void InstalledExtenderAllOuterWrappersAndMirrorOriginalsCompileAndJit()
        {
            string[][] groups = {
                new[] { "BulkBuildingDetours", "c_game_player_build_structure", "BuildStructure" },
                new[] { "BulkBuildingDetours", "c_game_build_wall", "HumanWall" },
                new[] { "BulkAIDetours", "c_game_ai_build_wall", "AiWall" },
                new[] { "BulkBuildingDetours", "c_game_building_bulldoze", "BuildingRemoval" },
                new[] { "BulkBuildingDetours", "c_game_bulldoze_wall", "WallRemoval" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_set_tiletype", "EditorTileType" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_set_tiletype_to_none", "EditorTileTypeClear" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_set_height_to_preset", "EditorHeightPreset" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_raise_tileheight", "EditorHeightRaise" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_set_height_to_number", "EditorHeightNumber" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_even_height", "EditorHeightEven" },
                new[] { "BulkMapEditorDetours", "c_game_editor_brush_delete", "EditorDelete" }
            };
            foreach (var group in groups)
            {
                Type owner = typeof(GameBuildingManagerAPI).Assembly.GetType("SHCDESE.Detours." + group[0], true);
                Type native = owner.GetNestedType(group[1] + "_delegate", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo method = owner.GetMethod(group[1] + "_hook_impl", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                var source = (WorldMutationSource)Enum.Parse(typeof(WorldMutationSource), group[2]);
                WorldMutationRuntime.ValidateDelegate(native, source);
                Assert.ThrowsExactly<InvalidOperationException>(() => WorldMutationRuntime.ValidateDelegate(typeof(Func<long, long>), source));
                bool costOwned = source == WorldMutationSource.BuildStructure || source == WorldMutationSource.BuildingRemoval;
                var targets = new List<MethodInfo> { method };
                if (!costOwned) targets.AddRange(owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                    .Where(candidate => candidate.Name.Contains("<" + method.Name + ">")));
                foreach (var target in targets)
                using (var definition = new DynamicMethodDefinition(target))
                using (var context = new ILContext(definition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                {
                    if (!costOwned)
                    {
                        Type adapter = typeof(WorldMutationRuntime).GetNestedType("OriginalSite", BindingFlags.NonPublic);
                        object site = Activator.CreateInstance(adapter, BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { source, native }, null);
                        var transform = adapter.GetMethod("Transform", BindingFlags.NonPublic | BindingFlags.Instance);
                        using (var changedDefinition = new DynamicMethodDefinition(target))
                        using (var changed = new ILContext(changedDefinition.Definition) { ReferenceBag = RuntimeILReferenceBag.Instance })
                        {
                            var getter = changed.Body.Instructions.Single(i => i.Operand is Mono.Cecil.MethodReference m &&
                                m.Name == "get_Original" && m.DeclaringType.FullName.Contains("DetourHandle"));
                            getter.OpCode = Mono.Cecil.Cil.OpCodes.Nop; getter.Operand = null;
                            var rejected = Assert.ThrowsExactly<TargetInvocationException>(() => transform.Invoke(site, new object[] { changed }));
                            Assert.IsInstanceOfType<InvalidOperationException>(rejected.InnerException);
                        }
                        transform.Invoke(site, new object[] { context });
                    }
                    if (target == method) WorldMutationRuntime.ApplyOuterScope(context, source);
                    MethodInfo generated = definition.Generate();
                    Type signature = Expression.GetDelegateType(generated.GetParameters().Select(p => p.ParameterType).Concat(new[] { generated.ReturnType }).ToArray());
                    RuntimeHelpers.PrepareDelegate(generated.CreateDelegate(signature));
                }
            }
        }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared;
using APIShared.Savegames;
using APIShared.ScriptExtenderFixes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;

namespace APISharedTests
{
    public sealed partial class SaveContextTests
    {
        private const BindingFlags NormalPrivate = BindingFlags.NonPublic | BindingFlags.Static;
        private static Func<IntPtr, int, int, int, int, int, byte, byte, byte, long> normalControlledOriginal;
        private static bool normalControlledEditor;
        private static object NormalInvoke(MethodInfo method, object target, object[] values)
        {
            try { return method.Invoke(target, values); }
            catch (TargetInvocationException error) when (error.InnerException != null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }

        private static void NormalValues(SaveContext value, bool save, bool editor, string path)
        {
            Assert.IsNotNull(value);
            Assert.AreEqual(save, value.IsSaveFile); Assert.AreEqual(editor, value.IsMapEditorSave);
            Assert.AreEqual(path, value.FilePath);
        }

        private static void NormalRead(NormalControlledMethods fixture, bool save, bool editor, string path)
        {
            Assert.IsTrue(fixture.Reader.TryReadSnapshot(out var value, out var source, out string reason), reason);
            Assert.AreEqual(PreCaptureSaveContextSource.FrameworkNormal, source);
            Assert.AreEqual(save, value.IsSaveFile); Assert.AreEqual(editor, value.IsMapEditorSave);
            Assert.AreEqual(path, value.FilePath);
        }

        private static void NormalUnavailable(NormalControlledMethods fixture)
        {
            Assert.IsFalse(fixture.Reader.TryReadSnapshot(out var value, out var source, out string reason));
            Assert.AreEqual(PreCaptureSaveContextSource.None, source);
            Assert.IsNull(value.FilePath); Assert.IsFalse(string.IsNullOrEmpty(reason));
        }

        // Execute official installed exporter/factory DMD copies. Only the native
        // Original, owner editor storage and unrelated factory tail are replaced.
        // The readiness field models successful startup publication; no real
        // process hook, writer, file replacement or game call is installed here.
        [TestMethod]
        [DataRow((byte)0)]
        [DataRow((byte)1)]
        [DataRow((byte)255)]
        public void NormalActualProducerBaselineLacksEarlyContextAndOptInPinsItsFirstFactory(byte map)
        {
            foreach (bool transformed in new[] { false, true })
            using (var fixture = new NormalControlledMethods(transformed))
            {
                fixture.Original = () => {
                    if (transformed) NormalRead(fixture, map == 0, map != 0, "Relative-\uD800\0inside.sav");
                    else NormalUnavailable(fixture); // Executed old-negative, not a compile/missing-method failure.
                    fixture.Legacy("redirected-native.tmp", map != 0);
                    if (transformed) NormalRead(fixture, map == 0, map != 0, "Relative-\uD800\0inside.sav");
                    fixture.Factory(map == 0);
                    NormalUnavailable(fixture);
                    fixture.Legacy("handler-reentrant.map", false);
                    fixture.Factory(true);
                    return 0x1234567812345678L;
                };
                Assert.AreEqual(0x1234567812345678L, fixture.Export("Relative-\uD800\0inside.sav\0\0", map));
                Assert.AreEqual(1, fixture.OriginalCalls);
                Assert.AreEqual(2, fixture.Delivered.Count);
                NormalValues(fixture.Delivered[0], transformed ? map == 0 : map != 0,
                    transformed ? map != 0 : map == 0, transformed ? "Relative-\uD800\0inside.sav" : "redirected-native.tmp");
                NormalValues(fixture.Delivered[1], false, true, "handler-reentrant.map");
                Assert.AreEqual("handler-reentrant.map", fixture.LegacyPath, "TLS cleanup must not restore globals.");
                CollectionAssert.AreEqual(new object[] { 11, 12, 13, 14, (byte)7, (byte)9, map }, fixture.Arguments[0].Skip(2).ToArray());
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalActualUnqueriedFactoriesPreserveNestedLegacyStateAndCloseBeforeConstruction()
        {
            foreach (bool transformed in new[] { false, true })
            using (var fixture = new NormalControlledMethods(transformed))
            {
                Func<long> outer = null;
                outer = () => {
                    fixture.Original = () => 17;
                    try { Assert.AreEqual(17L, fixture.Export("nested-editor.map", 1)); }
                    finally { fixture.Original = outer; }
                    fixture.ConstructorObserver = () => NormalUnavailable(fixture);
                    fixture.Factory(true);
                    fixture.Legacy("second-legacy.sav", true);
                    fixture.Factory(false);
                    NormalUnavailable(fixture);
                    return 18;
                };
                fixture.Original = outer;
                Assert.AreEqual(18L, fixture.Export("unqueried.sav", 0));
                Assert.AreEqual(2, fixture.OriginalCalls);
                NormalValues(fixture.Delivered[0], false, true, "nested-editor.map");
                NormalValues(fixture.Delivered[1], true, false, "second-legacy.sav");
            }
        }

        [TestMethod]
        public void NormalActualDecoderRetainsCodeUnitsOddLengthsAndInvalidMasks()
        {
            using (var fixture = new NormalControlledMethods())
            {
                foreach (var input in new[] {
                    Tuple.Create("MiXeD\\relative\0embedded\uD800\0\0", 0),
                    Tuple.Create("odd\0", 1), Tuple.Create("\0\0", 0), Tuple.Create("", 0) })
                {
                    string text = input.Item1;
                    int length = checked(text.Length * 2 + input.Item2);
                    IntPtr pointer = Marshal.StringToHGlobalUni(text);
                    string expected = text.TrimEnd('\0');
                    try
                    {
                        fixture.Original = () => {
                            if (expected.Length == 0) NormalUnavailable(fixture);
                            else NormalRead(fixture, true, false, expected);
                            return 21;
                        };
                        Assert.AreEqual(21L, fixture.ExportRaw(pointer, length, 0));
                    }
                    finally { Marshal.FreeHGlobal(pointer); }
                }
                foreach (int length in new[] { 0, -1, 7 })
                {
                    fixture.Original = () => { NormalUnavailable(fixture); return 22; };
                    Assert.AreEqual(22L, fixture.ExportRaw(IntPtr.Zero, length, 0));
                }
            }
        }

        [TestMethod]
        public void NormalActualNestedReturnFailureAndInvalidSetupRestoreOnlyCallerScope()
        {
            foreach (bool fail in new[] { false, true })
            using (var fixture = new NormalControlledMethods())
            {
                var marker = new InvalidOperationException("controlled inner Original");
                Func<long> outer = null;
                outer = () => {
                    NormalRead(fixture, true, false, "outer.sav");
                    fixture.Original = () => {
                        NormalRead(fixture, false, true, "inner.map");
                        fixture.Factory(false);
                        NormalUnavailable(fixture);
                        if (fail) throw marker;
                        return 31;
                    };
                    try { fixture.Export("inner.map", 1); Assert.IsFalse(fail); }
                    catch (Exception error) { Assert.IsTrue(fail); Assert.AreSame(marker, error); }
                    finally { fixture.Original = outer; }
                    NormalRead(fixture, true, false, "outer.sav");
                    fixture.Original = () => { NormalUnavailable(fixture); return 32; };
                    try { Assert.AreEqual(32L, fixture.Export(null, 0)); }
                    finally { fixture.Original = outer; }
                    NormalRead(fixture, true, false, "outer.sav");
                    fixture.Factory(true);
                    return 33;
                };
                fixture.Original = outer;
                Assert.AreEqual(33L, fixture.Export("outer.sav", 0));
                Assert.AreEqual(3, fixture.OriginalCalls);
                NormalValues(fixture.Delivered[0], false, true, "inner.map");
                NormalValues(fixture.Delivered[1], true, false, "outer.sav");
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalActualDecoderAndSetupReentryCannotBorrowOuterValues()
        {
            using (var fixture = new NormalControlledMethods())
            {
                bool inner = false;
                int decodeCalls = 0, setupCalls = 0;
                Exception decodeFailure = null, setupFailure = null;
                // These observers execute inside SE's real catch(Exception).
                // Retain any assertion failure so that catch cannot hide it.
                fixture.DecodeObserver = () => {
                    decodeCalls++;
                    try { NormalUnavailable(fixture); }
                    catch (Exception error) { decodeFailure = decodeFailure ?? error; }
                };
                fixture.SetupObserver = () => {
                    setupCalls++;
                    try { NormalUnavailable(fixture); }
                    catch (Exception error) { setupFailure = setupFailure ?? error; }
                    if (inner) throw new InvalidOperationException("controlled setup failure");
                };
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "outer-published.sav");
                    inner = true;
                    fixture.Original = () => { NormalUnavailable(fixture); return 41; };
                    Assert.AreEqual(41L, fixture.Export("inner-unpublished.sav", 0));
                    inner = false;
                    NormalRead(fixture, true, false, "outer-published.sav");
                    return 42;
                };
                Assert.AreEqual(42L, fixture.Export("outer-published.sav", 0));
                Assert.AreEqual(2, fixture.OriginalCalls, "The original inner catch still reaches each Original exactly once.");
                Assert.AreEqual(2, decodeCalls); Assert.AreEqual(2, setupCalls);
                Assert.IsNull(decodeFailure, "The actual decoder observer saw an outer context: " + decodeFailure);
                Assert.IsNull(setupFailure, "The actual setup observer saw an outer context: " + setupFailure);
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalAndTrailActualScopesUseNewestProducerAndClosedMasks()
        {
            using (var trailFixture = new TrailControlledMethods())
            using (var fixture = new NormalControlledMethods())
            {
                trailFixture.NativeAction = call => {
                    TrailAssertRead(trailFixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "trail-source.map");
                    fixture.Original = () => {
                        NormalRead(fixture, false, true, "inside-trail.map");
                        fixture.Factory(false);
                        NormalUnavailable(fixture); // Closed Normal cannot borrow the outer Trail.
                        return 51;
                    };
                    Assert.AreEqual(51L, fixture.Export("inside-trail.map", 1));
                    TrailAssertRead(trailFixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "trail-source.map");
                    trailFixture.DispatchFactory("trail-source.map", false, false);
                };
                trailFixture.Producer(null, "trail-source.map", "title", "output.trail", null);
                NormalValues(fixture.Delivered[0], false, true, "inside-trail.map");
                NormalValues(fixture.Delivered[1], false, false, "trail-source.map");
                fixture.Delivered.Clear();
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "outer.sav");
                    trailFixture.NativeAction = call => {
                        TrailAssertRead(trailFixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "inner-source.map");
                        trailFixture.DispatchFactory("inner-source.map", false, false);
                        NormalUnavailable(fixture);
                    };
                    trailFixture.Producer(null, "inner-source.map", "title", "inner.trail", null);
                    NormalRead(fixture, true, false, "outer.sav");
                    trailFixture.NativeAction = call => NormalUnavailable(fixture);
                    Assert.IsTrue(trailFixture.Wrapper("unidentified.trail", new byte[] { 1 }));
                    NormalRead(fixture, true, false, "outer.sav");
                    fixture.Factory(true);
                    return 52;
                };
                Assert.AreEqual(52L, fixture.Export("outer.sav", 0));
                NormalValues(fixture.Delivered[0], false, false, "inner-source.map");
                NormalValues(fixture.Delivered[1], true, false, "outer.sav");
                Assert.AreEqual(0, trailFixture.Provider.Calls);
            }
        }

        [TestMethod]
        public void NormalActualScopeCanShadowAwaitingTrailAndReturnToItsMask()
        {
            using (var fixture = new NormalControlledMethods())
            using (var definition = new DynamicMethodDefinition(typeof(SaveContextRuntime).GetMethod("CreateTrailMissionFromSource", NormalPrivate)))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(il => {
                    var call = il.Body.Instructions.Single(i => i.Operand is MethodReference m && m.DeclaringType.FullName == "EngineInterface" && m.Name == "CreateTrailMission");
                    var cursor = new ILCursor(il); cursor.Goto(call, MoveType.Before); cursor.Remove();
                    cursor.EmitDelegate<Func<string, byte[], bool>>((destination, restart) => {
                        NormalUnavailable(fixture);
                        fixture.Original = () => { NormalRead(fixture, false, true, "normal-inside-awaiting.map"); return 91; };
                        Assert.AreEqual(91L, fixture.Export("normal-inside-awaiting.map", 1));
                        NormalUnavailable(fixture);
                        return false;
                    });
                });
                var pendingTrail = (Func<string, byte[], string, bool>)definition.Generate().CreateDelegate(typeof(Func<string, byte[], string, bool>));
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "outer.sav");
                    Assert.IsFalse(pendingTrail("output.trail", new byte[] { 1 }, "awaiting-source.map"));
                    NormalRead(fixture, true, false, "outer.sav");
                    return 92;
                };
                Assert.AreEqual(92L, fixture.Export("outer.sav", 0));
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalActualThreadFailureAndSynchronizedPolicyKeepTheirSeparateContracts()
        {
            using (var fixture = new NormalControlledMethods())
            {
                var marker = new InvalidOperationException("Original failure identity");
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "thread.sav");
                    bool accepted = true; Exception threadError = null;
                    var thread = new Thread(() => {
                        try { accepted = fixture.Reader.TryReadSnapshot(out _, out _, out _); }
                        catch (Exception error) { threadError = error; }
                    }) { IsBackground = true };
                    thread.Start();
                    Assert.IsTrue(thread.Join(2000), "Bounded observer thread did not finish.");
                    Assert.IsNull(threadError); Assert.IsFalse(accepted);
                    throw marker;
                };
                Assert.AreSame(marker, Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Export("thread.sav", 0)));
                NormalUnavailable(fixture);
                fixture.Original = () => {
                    Assert.IsTrue(fixture.Reader.TryReadSnapshot(out var value, out var source, out _));
                    Assert.AreEqual(PreCaptureSaveContextSource.SynchronizedPolicy, source);
                    Assert.IsTrue(value.IsSaveFile); Assert.IsFalse(value.IsMapEditorSave); Assert.IsNull(value.FilePath);
                    fixture.Factory(true);
                    // The existing constant policy can still be read late; it is
                    // not proof of a reopened early producer or capture window.
                    Assert.IsTrue(fixture.Reader.TryReadSnapshot(out _, out source, out _));
                    Assert.AreEqual(PreCaptureSaveContextSource.SynchronizedPolicy, source);
                    return 61;
                };
                SaveContextRuntime.RunSynchronizedSave(() => Assert.AreEqual(61L, fixture.Export("editor.map", 1)));
                NormalValues(fixture.Delivered[0], true, false, null);
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalFactoryIncomingConstructorBranchStillClosesAndPinsBeforeConstruction()
        {
            using (var fixture = new NormalControlledMethods(branchToConstructor: true))
            {
                fixture.ConstructorObserver = () => NormalUnavailable(fixture);
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "branched-save.sav");
                    fixture.Legacy("wrong-editor.map", false);
                    fixture.Factory(true);
                    return 70;
                };
                Assert.AreEqual(70L, fixture.Export("branched-save.sav", 0));
                NormalValues(fixture.Delivered.Single(), true, false, "branched-save.sav");
            }
        }

        [TestMethod]
        public void NormalActualFactoryClosesBeforeItsOriginalConstructorAndRetainsOriginalFailure()
        {
            using (var fixture = new NormalControlledMethods())
            {
                var marker = new InvalidOperationException("constructor-stage observer failure");
                fixture.ConstructorObserver = () => { NormalUnavailable(fixture); throw marker; };
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "pinned.sav");
                    Assert.AreSame(marker, Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Factory(true)));
                    NormalUnavailable(fixture);
                    return 71;
                };
                Assert.AreEqual(71L, fixture.Export("pinned.sav", 0));
                Assert.AreEqual(0, fixture.Delivered.Count);
                NormalUnavailable(fixture);
            }
        }

        [TestMethod]
        public void NormalOptionalCohortFailureMasksContextAndNeverReactivatesOnLaterSuccessfulRebuild()
        {
            using (var fixture = new NormalControlledMethods())
            {
                bool fail = false;
                MethodInfo body = typeof(SaveContextTests).GetMethod(nameof(NormalFixtureBody), NormalPrivate);
                var hook = APIShared.Patching.SafeIlHook.Wrap("NormalFixture", "ControlledRebuild", body,
                    il => { if (fail) throw new InvalidOperationException("controlled coupled edit failure"); }, null);
                hook.Rebuilt += (Action<APIShared.Patching.SafeIlHook>)Delegate.CreateDelegate(typeof(Action<APIShared.Patching.SafeIlHook>),
                    typeof(SaveContextRuntime).GetMethod("NormalCohortRebuilt", NormalPrivate));
                void Rebuild()
                {
                    using (var definition = new DynamicMethodDefinition(body))
                    using (var context = new ILContext(definition.Definition)) context.Invoke(hook.Manipulator);
                }
                fixture.Original = () => {
                    NormalRead(fixture, true, false, "before-failure.sav");
                    Rebuild(); Assert.IsTrue(hook.IsApplied);
                    NormalRead(fixture, true, false, "before-failure.sav");
                    fail = true; Rebuild(); Assert.IsFalse(hook.IsApplied);
                    NormalUnavailable(fixture);
                    fail = false; Rebuild(); Assert.IsTrue(hook.IsApplied);
                    NormalUnavailable(fixture);
                    return 81;
                };
                Assert.AreEqual(81L, fixture.Export("before-failure.sav", 0));
                fixture.Original = () => { NormalUnavailable(fixture); return 82; };
                Assert.AreEqual(82L, fixture.Export("after-failure.sav", 0));
            }
        }

        private static void NormalFixtureBody() { }

        [TestMethod]
        [DataRow("signature")]
        [DataRow("getter")]
        [DataRow("forwarding")]
        [DataRow("flag")]
        [DataRow("decoder")]
        public void NormalActualOptionalShapeRefusalsDoNotPartiallyRewriteTheProducer(string changed)
        {
            Type owner = typeof(ModSaveDataAPI).Assembly.GetType("SHCDESE.Detours.BulkMapLoaderDetours", true);
            MethodInfo source = owner.GetMethod("c_game_dll_savesavegame_hook_impl", BindingFlags.Public | BindingFlags.Static);
            using (var definition = new DynamicMethodDefinition(source))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(il => {
                    if (changed == "signature") il.Method.ReturnType = il.Method.Module.TypeSystem.Int32;
                    else if (changed == "getter")
                        il.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "get_Original").OpCode = OpCodes.Call;
                    else if (changed == "forwarding")
                        il.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "Invoke").Previous.OpCode = OpCodes.Ldc_I4_0;
                    else if (changed == "flag")
                        il.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && i.Operand is FieldReference f && f.Name == "_isMapEditorSave").Previous.OpCode = OpCodes.Ceq;
                    else il.Body.Instructions.Single(i => i.OpCode == OpCodes.Div).OpCode = OpCodes.Mul;
                    Instruction[] instructions = il.Body.Instructions.ToArray();
                    var opcodes = instructions.Select(i => i.OpCode).ToArray();
                    var operands = instructions.Select(i => i.Operand).ToArray();
                    int locals = il.Body.Variables.Count, handlers = il.Body.ExceptionHandlers.Count;
                    Assert.ThrowsExactly<InvalidOperationException>(() => SaveContextRuntime.CorrectNormalProducer(il));
                    CollectionAssert.AreEqual(instructions, il.Body.Instructions.ToArray());
                    CollectionAssert.AreEqual(opcodes, il.Body.Instructions.Select(i => i.OpCode).ToArray());
                    CollectionAssert.AreEqual(operands, il.Body.Instructions.Select(i => i.Operand).ToArray());
                    Assert.AreEqual(locals, il.Body.Variables.Count); Assert.AreEqual(handlers, il.Body.ExceptionHandlers.Count);
                });
            }
        }

        [TestMethod]
        public void NormalUnqueriedProductionFramesAndFactorySelectionHaveNoWarmAllocation()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type type = typeof(SaveContextRuntime);
            var enter = (Func<SaveContextRuntime.NormalScope>)Delegate.CreateDelegate(typeof(Func<SaveContextRuntime.NormalScope>), type.GetMethod("EnterNormalProducer", flags));
            var publish = (Action<string, bool, bool>)Delegate.CreateDelegate(typeof(Action<string, bool, bool>), type.GetMethod("PublishNormalDecoded", flags));
            var select = (Func<bool, bool, string, SaveContextRuntime.SaveFactoryArguments>)Delegate.CreateDelegate(
                typeof(Func<bool, bool, string, SaveContextRuntime.SaveFactoryArguments>), type.GetMethod("SelectNormalFactoryContext", flags));
            var exit = (Action<SaveContextRuntime.NormalScope>)Delegate.CreateDelegate(typeof(Action<SaveContextRuntime.NormalScope>), type.GetMethod("ExitNormalProducer", flags));
            var ready = type.GetField("normalManagedState", flags);
            object previous = ready.GetValue(null);
            try
            {
                ready.SetValue(null, 1);
                for (int round = 0; round < 2; round++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int index = 0; index < 10000; index++)
                    {
                        var scope = enter();
                        publish("source.sav", true, false);
                        var selected = select(false, true, "legacy.map");
                        exit(scope);
                    }
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (round != 0) Assert.AreEqual(0L, allocated);
                }
            }
            finally { ready.SetValue(null, previous); }
        }

        private sealed class NormalControlledMethods : IDisposable
        {
            private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
            private readonly object manager = GameMapArchiveManagerAPI.Instance;
            private readonly FieldInfo archiveField, pathField, saveField;
            private readonly object previousArchive, previousPath, previousSave;
            private readonly IDictionary handlers;
            private readonly DictionaryEntry[] previousHandlers;
            private readonly Func<IntPtr, int, int, int, int, int, byte, byte, byte, long> previousOriginal = normalControlledOriginal;
            private readonly bool previousEditor = normalControlledEditor;
            private DynamicMethodDefinition exportDefinition, factoryDefinition;
            private ILContext exportContext, factoryContext;
            private MethodInfo export, factory;
            private bool disposed;
            private readonly bool transformed, branchToConstructor;
            private readonly FieldInfo readiness = typeof(SaveContextRuntime).GetField("normalManagedState", NormalPrivate);
            private readonly int previousReadiness;
            internal Action ConstructorObserver, DecodeObserver, SetupObserver;
            internal readonly List<object[]> Arguments = new List<object[]>();
            internal readonly PreCaptureSaveContextReader Reader = PreCaptureSaveContextReader.Create(out _);
            internal readonly List<SaveContext> Delivered = new List<SaveContext>();
            internal Func<long> Original;
            internal int OriginalCalls;

            internal NormalControlledMethods(bool transformed = true, bool branchToConstructor = false)
            {
                this.transformed = transformed;
                this.branchToConstructor = branchToConstructor;
                previousReadiness = (int)readiness.GetValue(null);
                readiness.SetValue(null, 1); // Controlled startup capability, no real hook publication.
                Type managerType = manager.GetType();
                archiveField = managerType.GetField("_mapArchive", Fields);
                pathField = managerType.GetField("_currentFilePath", Fields);
                saveField = managerType.GetField("_currentFileIsSaveFile", Fields);
                previousArchive = archiveField.GetValue(manager);
                previousPath = pathField.GetValue(manager);
                previousSave = saveField.GetValue(manager);
                handlers = (IDictionary)typeof(ModSaveDataAPI).GetField("_handlers", Fields).GetValue(ModSaveDataAPI.Instance);
                previousHandlers = SnapshotHandlers(handlers);
                try
                {
                    handlers.Clear();
                    if (!ModSaveDataAPI.Instance.RegisterModDataHandler("NormalActualContextFixture",
                        context => { Delivered.Add(context); return null; }, (bytes, context) => { }))
                        throw new InvalidOperationException("Controlled observer registration failed.");
                    CreateFactory();
                    CreateExport();
                    normalControlledOriginal = (data, length, sx, sy, rx, ry, locked, temporary, map) => {
                        Arguments.Add(new object[] { data, length, sx, sy, rx, ry, locked, temporary, map });
                        OriginalCalls++;
                        return Original();
                    };
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            private void CreateFactory()
            {
                MethodInfo source = manager.GetType().GetMethod("PrepareMapArchiveAndGetBytes", Fields);
                factoryDefinition = new DynamicMethodDefinition(source);
                // Instance observer delegates are owned by this ILContext's
                // reference bag. Retain it until every generated call has ended,
                // as the existing TrailControlledMethods fixture does.
                factoryContext = new ILContext(factoryDefinition.Definition);
                var il = factoryContext;
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                if (branchToConstructor)
                {
                    var target = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj &&
                        i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(SaveContext).FullName);
                    var branch = new ILCursor(il); branch.Goto(target, MoveType.Before);
                    var label = branch.DefineLabel(); label.Target = target;
                    branch.Emit(OpCodes.Br, label);
                }
                if (transformed) il.Invoke(SaveContextRuntime.CorrectSaveFactory);
                var constructor = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj &&
                    i.Operand is MethodReference m && m.DeclaringType.FullName == typeof(SaveContext).FullName);
                var observe = new ILCursor(il); observe.Goto(constructor, MoveType.Before);
                observe.EmitDelegate<Action>(() => ConstructorObserver?.Invoke());
                // Execute the actual SE value selection, one SaveContext ctor and
                // actual handler dispatch. As in the maintained fixture, omit only
                // the unrelated native metadata/timer/archive tail after dispatch.
                Instruction dispatch = il.Body.Instructions.Single(i => i.Operand is MethodReference m &&
                    m.Name == "WriteAllModDataToArchive");
                while (dispatch.Next != null) il.Body.Instructions.Remove(dispatch.Next);
                il.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                factory = factoryDefinition.Generate();
            }

            private void CreateExport()
            {
                Type owner = typeof(ModSaveDataAPI).Assembly.GetType("SHCDESE.Detours.BulkMapLoaderDetours", true);
                MethodInfo source = owner.GetMethod("c_game_dll_savesavegame_hook_impl",
                    BindingFlags.Public | BindingFlags.Static);
                exportDefinition = new DynamicMethodDefinition(source);
                // Instance observer delegates are owned by this ILContext's
                // reference bag. Retain it until every generated call has ended,
                // as the existing TrailControlledMethods fixture does.
                exportContext = new ILContext(exportDefinition.Definition);
                var il = exportContext;
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                if (transformed) il.Invoke(SaveContextRuntime.CorrectNormalProducer);
                var decoder = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj &&
                    i.Operand is MethodReference m && m.DeclaringType.FullName == "System.String");
                var setup = il.Body.Instructions.Single(i => i.Operand is MethodReference m && m.Name == "SetCurrentFileContext");
                var observe = new ILCursor(il); observe.Goto(decoder, MoveType.Before);
                observe.EmitDelegate<Action>(() => DecodeObserver?.Invoke());
                observe.Goto(setup, MoveType.Before);
                observe.EmitDelegate<Action>(() => SetupObserver?.Invoke());
                var module = exportDefinition.Definition.Module;
                FieldInfo editor = typeof(SaveContextTests).GetField(nameof(normalControlledEditor), NormalPrivate);
                FieldInfo original = typeof(SaveContextTests).GetField(nameof(normalControlledOriginal), NormalPrivate);
                MethodInfo invoke = typeof(Func<IntPtr, int, int, int, int, int, byte, byte, byte, long>).GetMethod("Invoke");
                int getters = 0, calls = 0, editorSlots = 0;
                foreach (Instruction instruction in il.Body.Instructions.ToArray())
                {
                    if (instruction.Operand is FieldReference field && field.DeclaringType.FullName == owner.FullName)
                    {
                        if (field.Name == "_isMapEditorSave")
                        {
                            instruction.Operand = module.ImportReference(editor);
                            editorSlots++;
                        }
                        else if (field.Name == "c_game_dll_savesavegame_hook" && instruction.OpCode == OpCodes.Ldsfld)
                        {
                            Instruction getter = instruction.Next;
                            if (!(getter?.Operand is MethodReference method) || method.Name != "get_Original")
                                throw new InvalidOperationException("Actual Original accessor shape changed.");
                            instruction.Operand = module.ImportReference(original);
                            getter.OpCode = OpCodes.Nop; getter.Operand = null;
                            getters++;
                        }
                        else throw new InvalidOperationException("Unexpected export-owner static field: " + field.Name);
                    }
                    if (instruction.Operand is MethodReference member && member.Name == "Invoke" &&
                        member.DeclaringType.Name == "c_game_dll_savesavegame_delegate")
                    {
                        instruction.Operand = module.ImportReference(invoke);
                        calls++;
                    }
                }
                if (getters != 1 || calls != 1 || editorSlots < 1)
                    throw new InvalidOperationException("Exact controlled Original/editor-storage substitution did not bind.");
                // The hook-owner fields are the only substituted storage. The
                // real decoder, catch, setup and Original forwarding run; only
                // production scope/factory edits and explicit observers are added.
                if (il.Body.Instructions.Any(i => i.Operand is FieldReference f && f.DeclaringType.FullName == owner.FullName))
                    throw new InvalidOperationException("Native hook-owner initialization would remain reachable.");
                export = exportDefinition.Generate();
            }

            internal void Legacy(string path, bool save)
            {
                pathField.SetValue(manager, path);
                saveField.SetValue(manager, save);
            }
            internal string LegacyPath => (string)pathField.GetValue(manager);
            internal void Factory(bool editor) => NormalInvoke(factory, null, new object[] { manager, editor });
            internal long Export(string path, byte map)
            {
                IntPtr chars = path == null ? IntPtr.Zero : Marshal.StringToHGlobalUni(path);
                try { return ExportRaw(chars, path == null ? 0 : checked((path.Length + 1) * 2), map); }
                finally { if (chars != IntPtr.Zero) Marshal.FreeHGlobal(chars); }
            }
            internal long ExportRaw(IntPtr path, int length, byte map) => (long)NormalInvoke(export, null,
                new object[] { path, length, 11, 12, 13, 14, (byte)7, (byte)9, map });

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                var failures = new List<Exception>();
                void Restore(Action action) { try { action(); } catch (Exception error) { failures.Add(error); } }
                Restore(() => readiness.SetValue(null, previousReadiness));
                Restore(() => exportContext?.Dispose());
                Restore(() => exportDefinition?.Dispose());
                Restore(() => factoryContext?.Dispose());
                Restore(() => factoryDefinition?.Dispose());
                Restore(() => { handlers.Clear(); foreach (var entry in previousHandlers) handlers.Add(entry.Key, entry.Value); });
                Restore(() => {
                    object current = archiveField.GetValue(manager);
                    if (!ReferenceEquals(current, previousArchive)) (current as IDisposable)?.Dispose();
                    archiveField.SetValue(manager, previousArchive);
                });
                Restore(() => pathField.SetValue(manager, previousPath));
                Restore(() => saveField.SetValue(manager, previousSave));
                normalControlledOriginal = previousOriginal;
                normalControlledEditor = previousEditor;
                if (failures.Count != 0) throw new AggregateException("Controlled SE fixture restoration failed.", failures);
            }
        }

    }
}

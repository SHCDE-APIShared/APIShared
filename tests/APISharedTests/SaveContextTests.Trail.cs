using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using APIShared;
using APIShared.Economy;
using APIShared.Internal;
using APIShared.PlayerControl;
using APIShared.Savegames;
using APIShared.ScriptExtenderFixes;
using BepInEx.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using TrailRestartInfo = CrusaderDE.HUD_IngameMenu.RestartSkirmishMapInfo;

namespace APISharedTests
{
    public sealed partial class SaveContextTests
    {
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TrailWithNoReadableSourceRequiresOnlyRecordsInMapScope(bool maps)
        {
            using (var fixture = new TrailControlledMethods())
            {
                const BindingFlags stateFlags = BindingFlags.Static | BindingFlags.NonPublic;
                var registryField = typeof(VersionedSaveDataRuntime).GetField("registry", stateFlags);
                var writerType = typeof(WorkerProductionCriticalSaveRuntime);
                var sequenceField = writerType.GetField("sequence", stateFlags);
                var failedField = writerType.GetField("lastFailed", stateFlags);
                var contextField = writerType.GetField("preCaptureContext", stateFlags);
                var logField = writerType.GetField("log", stateFlags);
                var monitorField = EngineMonitor.Field;
                var writerPublication = (NativeHookPublicationState)writerType.GetField("publication", stateFlags).GetValue(null);
                var scopePublication = (NativeHookPublicationState)typeof(PlayerViewSaveScope)
                    .GetField("publication", stateFlags).GetValue(null);
                var publicationState = typeof(NativeHookPublicationState).GetField("state",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                object previousRegistry = registryField.GetValue(null);
                object previousSequence = sequenceField.GetValue(null);
                object previousFailure = failedField.GetValue(null);
                object previousContext = contextField.GetValue(null);
                object previousLog = logField.GetValue(null);
                object previousMonitor = monitorField.GetValue(null);
                object previousWriter = publicationState.GetValue(writerPublication);
                object previousScope = publicationState.GetValue(scopePublication);
                var registry = new VersionedSaveDataRegistry((id, save, load) => true, _ => { }, _ => { });
                var record = new VersionedSaveDataRegistration("test.owner", "TrailScope", 1, 1,
                    new[] { "state" }, _ => throw new AssertFailedException("No capture is eligible here."), _ => { })
                {
                    RequireAtomicWrite = true,
                    Scope = maps ? VersionedSaveScope.SaveGamesAndMaps : VersionedSaveScope.SaveGames
                };
                Assert.IsTrue(registry.TryRegister(record, out _, out string reason), reason);
                var log = new ManualLogSource("AtomicTrailScopeFixture");
                string failure = null;
                log.LogEvent += (sender, args) => failure = args.Data.ToString();
                int originals = 0;
                ulong result = ulong.MaxValue;
                string destination = Path.GetTempFileName();
                byte[] previous = { 4, 1, 9 };
                byte[] replacement = { 7, 2, 3 };
                try
                {
                    File.WriteAllBytes(destination, previous);
                    registryField.SetValue(null, registry);
                    contextField.SetValue(null, fixture.Reader);
                    logField.SetValue(null, log);
                    monitorField.SetValue(null, fixture.MonitorObject);
                    // Model completed publication without installing native hooks. The real
                    // writer must decide and, if necessary, refuse before reading native memory.
                    writerPublication.Complete();
                    scopePublication.Complete();
                    fixture.NativeAction = call =>
                    {
                        Assert.IsTrue(call.MonitorHeld && WorkerProductionCriticalSaveRuntime.IsAvailable);
                        Assert.AreEqual(PreCaptureSaveRoute.Trail, SaveContextRuntime.CurrentPreCaptureRoute);
                        result = WorkerProductionCriticalSaveRuntime.RunWriter(IntPtr.Zero, IntPtr.Zero, 0,
                            (archive, sections, map) =>
                            {
                                originals++;
                                File.WriteAllBytes(destination, replacement);
                                return 1;
                            });
                    };
                    // The producer and wrapper match, but this source is empty after the
                    // Extender's trailing-NUL trim. The writer's zero map byte is deliberate.
                    Assert.IsTrue(fixture.Helper("output.trail", fixture.RestartBytes, "\0"));
                    Assert.AreEqual(maps ? 0UL : 1UL, result);
                    Assert.AreEqual(maps ? 0 : 1, originals);
                    CollectionAssert.AreEqual(maps ? previous : replacement, File.ReadAllBytes(destination));
                    if (maps) StringAssert.Contains(failure, "source-map context");
                    else Assert.IsNull(failure);
                    Assert.AreEqual(0, fixture.Provider.Calls);
                    Assert.AreEqual(PreCaptureSaveRoute.Unknown, SaveContextRuntime.CurrentPreCaptureRoute);
                }
                finally
                {
                    registryField.SetValue(null, previousRegistry);
                    sequenceField.SetValue(null, previousSequence);
                    failedField.SetValue(null, previousFailure);
                    contextField.SetValue(null, previousContext);
                    logField.SetValue(null, previousLog);
                    monitorField.SetValue(null, previousMonitor);
                    publicationState.SetValue(writerPublication, previousWriter);
                    publicationState.SetValue(scopePublication, previousScope);
                    File.Delete(destination);
                }
            }
        }

        [TestMethod]
        public void RequiredAtomicCaptureUsesTrailSourceContextForMapRecordsOnly()
        {
            using (var fixture = new TrailControlledMethods())
            {
                const string source = @"C:\Trails\loaded.map";
                const string destination = @"C:\Export\new.trail";
                fixture.NativeAction = call => AssertRequiredAtomicTransport(fixture.Reader, source, false, () =>
                {
                    fixture.DispatchFactory(source, false, false);
                    return fixture.Delivered.Single();
                });
                fixture.Producer(null, source, "display name", destination, null);
                Assert.AreEqual(1, fixture.RawCalls);
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes(destination), fixture.RawArguments.Single().PathBytes);
                Assert.AreEqual(0, fixture.Provider.Calls);
            }
        }

        // These are generated copies of the installed producer, production helper,
        // wrapper and SE archive factory. Only UI/load/encode boundaries and the
        // P/Invoke are controlled. They do not execute a native writer, establish
        // world identity or prove that a game save was committed.
        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TrailActualProducerQueriesArePureAndMatchingFactoryContextKeepsItsActualDispatch(bool queryBeforeFactory)
        {
            using (var fixture = new TrailControlledMethods())
            {
                const string source = "source-\uD800\0inside.map\0\0";
                const string destination = "different-output.trail";
                string canonical = Encoding.Unicode.GetString(Encoding.Unicode.GetBytes(source)).TrimEnd('\0');
                int captures = 0;
                var captureFailure = new InvalidOperationException("controlled Trail factory capture");
                TrailRead early = null, repeated = null, late = null;
                using (var frame = VersionedSaveFactoryFrame.Begin(context => {
                    captures++;
                    throw captureFailure;
                }))
                {
                    fixture.NativeAction = call => {
                        Assert.IsTrue(call.MonitorHeld, "The actual wrapper must own its monitor at the native boundary.");
                        if (queryBeforeFactory)
                        {
                            early = fixture.Read();
                            repeated = fixture.Read();
                            TrailAssertRead(early, PreCaptureSaveContextSource.FrameworkTrail, false, false, canonical);
                            TrailAssertRead(repeated, PreCaptureSaveContextSource.FrameworkTrail, false, false, canonical);
                            Assert.AreEqual(0, captures, "Snapshot reads must not invoke the critical factory owner.");
                            Assert.IsNull(frame.Failure);
                        }
                        if (queryBeforeFactory) fixture.DispatchFactory(canonical, false, false);
                        else fixture.DispatchFactory();
                        late = fixture.Read();
                    };
                    fixture.Producer(null, source, "display name", destination, null);
                    Assert.AreEqual(1, captures, "Only the actual transformed factory invokes its owner.");
                    Assert.AreSame(captureFailure, frame.Failure);
                }
                Assert.AreEqual(1, fixture.RawCalls);
                Assert.AreEqual(1, fixture.Loads);
                Assert.AreEqual(source, fixture.LoadedSource);
                Assert.IsTrue(fixture.LoadedAsEditor);
                Assert.AreEqual(1, fixture.Encodes);
                Assert.AreEqual(1, fixture.MapNames);
                Assert.AreEqual(1, fixture.ClearWaits);
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes(destination), fixture.RawArguments[0].PathBytes);
                CollectionAssert.AreEqual(fixture.RestartBytes, fixture.RawArguments[0].RestartBytes);
                Assert.AreEqual(1, fixture.Delivered.Count);
                SaveContext delivered = fixture.Delivered[0];
                Assert.AreEqual(!queryBeforeFactory, delivered.IsSaveFile);
                Assert.AreEqual(!queryBeforeFactory, delivered.IsMapEditorSave);
                Assert.AreEqual(queryBeforeFactory ? canonical : TrailControlledMethods.LegacyPath, delivered.FilePath);
                Assert.IsNotNull(late);
                Assert.IsFalse(late.Success, "A first or repeated query after actual factory entry must refuse.");
                Assert.AreEqual(PreCaptureSaveContextSource.None, late.Source);
                Assert.AreEqual(0, fixture.Provider.Calls, "A present Trail frame must never borrow the SE fallback.");
                if (queryBeforeFactory)
                {
                    // Returned values are detached snapshots; refusal of a later
                    // query does not mutate the value already held by a caller.
                    TrailAssertRead(early, PreCaptureSaveContextSource.FrameworkTrail, false, false, canonical);
                    StringAssert.Contains(early.Context.FilePath, "\0inside.map");
                }
                TrailAssertInactive();
                Assert.IsNull(VersionedSaveFactoryFrame.Current);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void TrailActualProducerRejectsWrongOrNullNormalizedFailureTargets(bool nullTarget)
        {
            MethodInfo method = typeof(EditorDirector).GetMethod("SaveCustomTrailMap", BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(string), typeof(string), typeof(string), typeof(TrailRestartInfo) }, null);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(il => {
                    var call = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference target &&
                        target.DeclaringType.FullName == "EngineInterface" && target.Name == "CreateTrailMission");
                    var load = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference target &&
                        target.DeclaringType.FullName == "EngineInterface" && target.Name == "LoadMapFile");
                    Instruction branch = load.Next.Next.Next;
                    Assert.IsTrue(branch.Operand is ILLabel, "Exercise the actual normalized manipulator body.");
                    var label = (ILLabel)branch.Operand;
                    Instruction previousTarget = label.Target;
                    Assert.AreSame(call.Next.Next, previousTarget, "The inspected original failure edge must skip the writer call.");
                    int previousCount = il.Body.Instructions.Count;
                    object previousCall = call.Operand;
                    try
                    {
                        // Isolate target identity: every other original producer
                        // instruction and successful-load condition stays intact.
                        label.Target = nullTarget ? null : call;
                        Assert.ThrowsExactly<InvalidOperationException>(() => SaveContextRuntime.CorrectTrailProducer(il));
                        Assert.AreEqual(previousCount, il.Body.Instructions.Count, "Refusal must not partially rewrite the producer.");
                        Assert.AreEqual(OpCodes.Call, call.OpCode);
                        Assert.AreSame(previousCall, call.Operand);
                    }
                    finally { label.Target = previousTarget; }
                });
            }
        }

        [TestMethod]
        public void TrailActualProducerDoesNotEnterWrapperWhenItsActualLoadSuccessBranchIsNotTaken()
        {
            using (var fixture = new TrailControlledMethods())
            {
                fixture.LoadSucceeds = false;
                fixture.NativeAction = call => Assert.Fail("The failed-load producer reached the native boundary.");
                fixture.Producer(null, "not-loaded.map", "display name", "output.trail", null);
                Assert.AreEqual(1, fixture.Loads);
                Assert.AreEqual(0, fixture.Encodes);
                Assert.AreEqual(0, fixture.MapNames);
                Assert.AreEqual(0, fixture.RawCalls);
                Assert.AreEqual(1, fixture.ClearWaits);
                TrailAssertInactive();
            }
        }

        [TestMethod]
        public void TrailActualMismatchedFirstFactoryFailsTheCriticalFrameWithoutRewritingItsLegacyTransport()
        {
            using (var fixture = new TrailControlledMethods())
            {
                var prepareFailure = new InvalidOperationException("prepare ran despite Trail provenance mismatch");
                TrailRead late = null;
                using (var frame = VersionedSaveFactoryFrame.Begin(context => { throw prepareFailure; }))
                {
                    fixture.NativeAction = call => {
                        TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "known-source.map");
                        fixture.DispatchFactory();
                        late = fixture.Read();
                    };
                    fixture.Producer(null, "known-source.map", "", "output.trail", null);
                    Assert.IsNotNull(frame.Failure, "A mismatching first factory must fail the active critical frame.");
                    Assert.AreNotSame(prepareFailure, frame.Failure,
                        "The mismatch must latch its own failure before the preparation callback could mask it.");
                    Assert.IsFalse(frame.TryGetWrittenCapture(out var ignored, out string reason));
                    Assert.IsFalse(string.IsNullOrEmpty(reason));
                }
                Assert.AreEqual(1, fixture.RawCalls);
                Assert.AreEqual(1, fixture.Delivered.Count);
                Assert.IsTrue(fixture.Delivered[0].IsSaveFile);
                Assert.IsTrue(fixture.Delivered[0].IsMapEditorSave);
                Assert.AreEqual(TrailControlledMethods.LegacyPath, fixture.Delivered[0].FilePath,
                    "Provenance failure must not relabel the actual legacy handler payload.");
                Assert.IsNotNull(late);
                Assert.IsFalse(late.Success);
                Assert.AreEqual(PreCaptureSaveContextSource.None, late.Source);
                Assert.AreEqual(0, fixture.Provider.Calls);
                TrailAssertInactive();
            }
        }

        [TestMethod]
        public void TrailActualHandlerReentryKeepsItsOwnNormalFactoryContextAfterTheTrailFactoryWasSelected()
        {
            using (var fixture = new TrailControlledMethods())
            {
                bool reentered = false;
                Exception reentryFailure = null;
                TrailRead afterReentry = null;
                fixture.SaveHandlerObserver = context => {
                    if (reentered) return;
                    reentered = true;
                    try
                    {
                        // SE invokes this observer inline in its actual handler.
                        // Reuse the live archive while temporarily supplying this
                        // second factory's own normal-save manager inputs.
                        fixture.DispatchFactory("nested-normal.sav", true, false);
                        afterReentry = fixture.Read();
                    }
                    catch (Exception error) { reentryFailure = error; }
                };
                fixture.NativeAction = call => {
                    TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "outer-source.map");
                    fixture.DispatchFactory("outer-source.map", false, false);
                };
                fixture.Producer(null, "outer-source.map", "", "output.trail", null);
                Assert.IsTrue(reentered);
                Assert.IsNull(reentryFailure, "SE isolates handler exceptions, so the fixture must inspect its recorded failure.");
                Assert.AreEqual(1, fixture.RawCalls);
                Assert.AreEqual(2, fixture.Delivered.Count);
                Assert.IsFalse(fixture.Delivered[0].IsSaveFile);
                Assert.IsFalse(fixture.Delivered[0].IsMapEditorSave);
                Assert.AreEqual("outer-source.map", fixture.Delivered[0].FilePath);
                Assert.IsTrue(fixture.Delivered[1].IsSaveFile);
                Assert.IsFalse(fixture.Delivered[1].IsMapEditorSave);
                Assert.AreEqual("nested-normal.sav", fixture.Delivered[1].FilePath,
                    "A pinned Trail context belongs only to its first actual factory, including during inline reentry.");
                Assert.IsNotNull(afterReentry);
                Assert.IsFalse(afterReentry.Success);
                Assert.AreEqual(PreCaptureSaveContextSource.None, afterReentry.Source);
                Assert.AreEqual(0, fixture.Provider.Calls);
                TrailAssertInactive();
            }
        }

        [TestMethod]
        [DataRow(1, true)]
        [DataRow(0, false)]
        [DataRow(-1, false)]
        public void TrailActualWrapperPreservesRawArgumentsAndPositiveResultAndMasksDirectCallFallback(int rawResult, bool result)
        {
            using (var fixture = new TrailControlledMethods())
            {
                fixture.RawResult = rawResult;
                fixture.NativeAction = call => {
                    Assert.IsTrue(call.MonitorHeld);
                    TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "source.map");
                };
                Assert.AreEqual(result, fixture.Helper("helper-output.trail", fixture.RestartBytes, "source.map"));
                TrailAssertInactive();
                fixture.NativeAction = call => {
                    Assert.IsTrue(call.MonitorHeld);
                    Assert.AreEqual(PreCaptureSaveRoute.Unknown, SaveContextRuntime.CurrentPreCaptureRoute);
                    Assert.IsFalse(fixture.Read().Success, "An unidentified direct wrapper cannot borrow an export provider.");
                    Assert.IsFalse(SaveContextRuntime.TryGetTrailSaveContext(out var ignored, out bool active));
                    Assert.IsTrue(active, "The direct wrapper must shadow outside/outer contexts while it executes.");
                };
                Assert.AreEqual(result, fixture.Wrapper("direct-output.trail", fixture.RestartBytes));
                Assert.AreEqual(2, fixture.RawCalls);
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("helper-output.trail"), fixture.RawArguments[0].PathBytes);
                CollectionAssert.AreEqual(Encoding.Unicode.GetBytes("direct-output.trail"), fixture.RawArguments[1].PathBytes);
                foreach (var raw in fixture.RawArguments)
                    CollectionAssert.AreEqual(fixture.RestartBytes, raw.RestartBytes);
                Assert.AreEqual(0, fixture.Provider.Calls);
                TrailAssertInactive();
            }
        }

        [TestMethod]
        public void TrailActualWrapperRestoresFramesAndMonitorAfterTheOriginalBoundaryThrows()
        {
            using (var fixture = new TrailControlledMethods())
            {
                var failure = new InvalidOperationException("controlled Trail native boundary failure");
                fixture.NativeAction = call => {
                    TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "source.map");
                    Assert.IsTrue(call.MonitorHeld);
                    throw failure;
                };
                try
                {
                    fixture.Helper("output.trail", fixture.RestartBytes, "source.map");
                    Assert.Fail("Original boundary exception was lost.");
                }
                catch (InvalidOperationException error) { Assert.AreSame(failure, error); }
                Assert.AreEqual(1, fixture.RawCalls);
                TrailAssertInactive();
                bool acquired = false;
                Exception workerFailure = null;
                var thread = new Thread(() => {
                    try
                    {
                        acquired = Monitor.TryEnter(fixture.MonitorObject, 500);
                        if (acquired) Monitor.Exit(fixture.MonitorObject);
                    }
                    catch (Exception error) { workerFailure = error; }
                });
                thread.Start();
                Assert.IsTrue(thread.Join(2000), "The monitor-release probe must finish without waiting for the fixture owner.");
                Assert.IsNull(workerFailure);
                Assert.IsTrue(acquired, "The actual wrapper finally left its monitor owned after failure.");
            }
        }

        [TestMethod]
        public void TrailActualNestedAndDirectWrappersRestoreTheirCallerAndDoNotLeakToAnotherThread()
        {
            using (var fixture = new TrailControlledMethods())
            {
                fixture.NativeAction = call => {
                    if (call.Depth == 1)
                    {
                        TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "outer.map");
                        TrailRead workerRead = null;
                        bool workerActive = true;
                        Exception workerFailure = null;
                        var thread = new Thread(() => {
                            try
                            {
                                Assert.IsFalse(SaveContextRuntime.TryGetTrailSaveContext(out var ignored, out workerActive));
                                workerRead = fixture.Read();
                            }
                            catch (Exception error) { workerFailure = error; }
                        });
                        thread.Start();
                        Assert.IsTrue(thread.Join(2000));
                        Assert.IsNull(workerFailure);
                        Assert.IsFalse(workerActive);
                        TrailAssertRead(workerRead, PreCaptureSaveContextSource.ExtenderExport, true, false, "fallback.sav");
                        Assert.IsTrue(fixture.Wrapper("unidentified.trail", fixture.RestartBytes));
                        TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "outer.map");
                        Assert.IsTrue(fixture.Helper("nested.trail", fixture.RestartBytes, "inner.map"));
                        TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "outer.map");
                    }
                    else if (call.Destination == "unidentified.trail")
                    {
                        Assert.IsFalse(fixture.Read().Success, "The direct nested wrapper must mask its caller.");
                        Assert.IsFalse(SaveContextRuntime.TryGetTrailSaveContext(out var ignored, out bool active));
                        Assert.IsTrue(active);
                    }
                    else TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.FrameworkTrail, false, false, "inner.map");
                };
                Assert.IsTrue(fixture.Helper("outer.trail", fixture.RestartBytes, "outer.map"));
                Assert.AreEqual(3, fixture.RawCalls);
                Assert.IsTrue(fixture.RawArguments.All(call => call.MonitorHeld));
                Assert.AreEqual(1, fixture.Provider.Calls, "Only the independent thread may use its own fallback provider.");
                TrailAssertInactive();
            }
        }

        [TestMethod]
        public void TrailActualFactoryRetainsSynchronizedPolicyPriority()
        {
            using (var fixture = new TrailControlledMethods())
            {
                fixture.NativeAction = call => {
                    Assert.AreEqual(PreCaptureSaveRoute.Synchronized, SaveContextRuntime.CurrentPreCaptureRoute);
                    TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.SynchronizedPolicy, true, false, null);
                    fixture.DispatchFactory();
                    // The existing Chore policy is a scope constant, including after
                    // factory entry. This does not make a late Trail query usable.
                    TrailAssertRead(fixture.Read(), PreCaptureSaveContextSource.SynchronizedPolicy, true, false, null);
                };
                SaveContextRuntime.RunSynchronizedSave(() =>
                    Assert.IsTrue(fixture.Helper("output.trail", fixture.RestartBytes, "source.map")));
                Assert.AreEqual(1, fixture.Delivered.Count);
                Assert.IsTrue(fixture.Delivered[0].IsSaveFile);
                Assert.IsFalse(fixture.Delivered[0].IsMapEditorSave);
                Assert.IsNull(fixture.Delivered[0].FilePath);
                Assert.IsFalse(SaveContextRuntime.TryGetSynchronizedSaveContext(out var ignored));
                TrailAssertInactive();
            }
        }

        private static void TrailAssertRead(TrailRead read, PreCaptureSaveContextSource source,
            bool save, bool editor, string path)
        {
            Assert.IsNotNull(read);
            Assert.IsTrue(read.Success, read.Reason);
            Assert.AreEqual(source, read.Source);
            Assert.AreEqual(save, read.Context.IsSaveFile);
            Assert.AreEqual(editor, read.Context.IsMapEditorSave);
            Assert.AreEqual(path, read.Context.FilePath);
        }

        private static void TrailAssertInactive()
        {
            Assert.IsFalse(SaveContextRuntime.TryGetTrailSaveContext(out var context, out bool active));
            Assert.IsNull(context);
            Assert.IsFalse(active, "A completed/failed wrapper leaked a caller-thread frame.");
        }

        private sealed class TrailRead
        {
            internal bool Success;
            internal VersionedSaveContext Context;
            internal PreCaptureSaveContextSource Source;
            internal string Reason;
        }

        private sealed class TrailFallbackProvider
        {
            internal int Calls;
            public bool TryGetActiveSaveContext(out SaveContext context)
            {
                Interlocked.Increment(ref Calls);
                context = new SaveContext(true, false, "fallback.sav");
                return true;
            }
        }

        private sealed class TrailRawArguments
        {
            internal string Destination;
            internal byte[] PathBytes, RestartBytes;
            internal bool MonitorHeld;
            internal int Depth;
        }

        private sealed unsafe class TrailControlledMethods : IDisposable
        {
            internal const string LegacyPath = "legacy-stale.sav";
            internal readonly object MonitorObject = new object();
            internal readonly byte[] RestartBytes = { 7, 9, 11 };
            internal readonly TrailFallbackProvider Provider = new TrailFallbackProvider();
            internal readonly List<TrailRawArguments> RawArguments = new List<TrailRawArguments>();
            internal readonly List<SaveContext> Delivered = new List<SaveContext>();
            internal readonly Action<EditorDirector, string, string, string, TrailRestartInfo> Producer;
            internal readonly Func<string, byte[], bool> Wrapper;
            internal readonly Func<string, byte[], string, bool> Helper;
            internal Action<TrailRawArguments> NativeAction;
            internal Action<SaveContext> SaveHandlerObserver;
            internal bool LoadSucceeds = true, LoadedAsEditor;
            internal string LoadedSource;
            internal int RawResult = 1, RawCalls, Loads, Encodes, MapNames, ClearWaits;
            private int depth, dispatchDepth;
            private readonly List<IDisposable> generated = new List<IDisposable>();
            private readonly GameMapArchiveManagerAPI manager = GameMapArchiveManagerAPI.Instance;
            private readonly FieldInfo archiveField, pathField, saveField;
            private readonly object previousArchive, previousPath, previousSave;
            private readonly IDictionary handlers;
            private readonly DictionaryEntry[] previousHandlers;
            private readonly Func<GameMapArchiveManagerAPI, bool, byte[]> factory;
            private readonly PreCaptureSaveContextReader reader;

            internal TrailControlledMethods()
            {
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                TrailAssertInactive();
                archiveField = manager.GetType().GetField("_mapArchive", flags);
                pathField = manager.GetType().GetField("_currentFilePath", flags);
                saveField = manager.GetType().GetField("_currentFileIsSaveFile", flags);
                previousArchive = archiveField.GetValue(manager);
                previousPath = pathField.GetValue(manager);
                previousSave = saveField.GetValue(manager);
                handlers = (IDictionary)typeof(ModSaveDataAPI).GetField("_handlers", flags).GetValue(ModSaveDataAPI.Instance);
                previousHandlers = SnapshotHandlers(handlers);
                try
                {
                    reader = PreCaptureSaveContextReader.CreateForProvider(typeof(TrailFallbackProvider), Provider, out string reason);
                    Assert.IsTrue(reader.HasExtenderExportProvider, reason);
                    handlers.Clear();
                    Assert.IsTrue(ModSaveDataAPI.Instance.RegisterModDataHandler("TrailActualContextFixture",
                        context => { Delivered.Add(context); SaveHandlerObserver?.Invoke(context); return new byte[] { 19 }; }, (bytes, context) => { }));
                    factory = Compile<Func<GameMapArchiveManagerAPI, bool, byte[]>>(
                        manager.GetType().GetMethod("PrepareMapArchiveAndGetBytes", flags), il => {
                            il.Invoke(SaveContextRuntime.CorrectSaveFactory);
                            var dispatch = il.Body.Instructions.Single(i => i.Operand is MethodReference method &&
                                method.Name == "WriteAllModDataToArchive");
                            // Keep the real SaveContext construction, owned correction
                            // and SE dispatch; only native metadata/timer tail is omitted.
                            while (dispatch.Next != null) il.Body.Instructions.Remove(dispatch.Next);
                            il.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                            il.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        });
                    Wrapper = Compile<Func<string, byte[], bool>>(typeof(EngineInterface).GetMethod("CreateTrailMission",
                        BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(byte[]) }, null), il => {
                            il.Invoke(SaveContextRuntime.CorrectTrailWrapper);
                            ReplaceField(il, "EngineInterface", "threadLock", (Func<object>)(() => MonitorObject));
                            ReplaceTrailNativeCall(il, (TrailNativeCall)RawBoundary);
                            Assert.AreEqual(2, il.Body.ExceptionHandlers.Count, "Keep the original monitor finally inside the real frame finally.");
                            Assert.AreEqual(1, il.Body.Instructions.Count(i => i.Operand is MethodReference method &&
                                method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name == "Enter"));
                            Assert.AreEqual(1, il.Body.Instructions.Count(i => i.Operand is MethodReference method &&
                                method.DeclaringType.FullName == "System.Threading.Monitor" && method.Name == "Exit"));
                        });
                    // Capture this fixture when forwarding generated delegates. EmitDelegate
                    // must bind a runtime instance method, not the generated method's MethodInfo.
                    Helper = Compile<Func<string, byte[], string, bool>>(typeof(SaveContextRuntime).GetMethod(
                        "CreateTrailMissionFromSource", BindingFlags.Static | BindingFlags.NonPublic), il =>
                            ReplaceCall(il, "EngineInterface", "CreateTrailMission", (Func<string, byte[], bool>)((path, restart) => this.Wrapper(path, restart))));
                    Producer = Compile<Action<EditorDirector, string, string, string, TrailRestartInfo>>(
                        typeof(EditorDirector).GetMethod("SaveCustomTrailMap", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(string), typeof(string), typeof(string), typeof(TrailRestartInfo) }, null), il => {
                            // Apply the production shape gate before replacing any of
                            // the inspected producer's controlled external boundaries.
                            il.Invoke(SaveContextRuntime.CorrectTrailProducer);
                            ReplaceCall(il, typeof(SaveContextRuntime).FullName, "CreateTrailMissionFromSource", (Func<string, byte[], string, bool>)((path, restart, source) => this.Helper(path, restart, source)));
                            ReplaceCall(il, "UnityEngine.Application", "get_streamingAssetsPath", (Func<string>)(() => "assets"));
                            ReplaceCall(il, "ConfigSettings", "GetMpAutoSavePath", (Func<string>)(() => "autosaves"));
                            ReplaceCall(il, "ConfigSettings", "GetSavesPath", (Func<string>)(() => "saves"));
                            ReplaceCall(il, "EngineInterface", "sendPath", (Action<string, string, string>)((a, b, c) => { }));
                            ReplaceCall(il, "EngineInterface", "LoadMapFile", (Func<string, bool, EngineInterface.LoadMapReturnData>)LoadBoundary);
                            ReplaceCall(il, "EngineInterface", "SetUTF8MapName", (Action<string>)(name => MapNames++));
                            ReplaceCall(il, "CrusaderDE.HUD_IngameMenu/RestartSkirmishMapInfo", "encode",
                                (Func<TrailRestartInfo, byte[]>)(info => { Encodes++; return RestartBytes; }));
                            ReplaceField(il, "Director", "instance", (Func<Director>)(() => null));
                            ReplaceCall(il, "Director", "ClearWaitCursor", (Action<Director>)(director => ClearWaits++));
                        });
                }
                catch { Dispose(); throw; }
            }

            internal TrailRead Read()
            {
                var read = new TrailRead();
                read.Success = reader.TryReadSnapshot(out read.Context, out read.Source, out read.Reason);
                return read;
            }

            internal PreCaptureSaveContextReader Reader => reader;

            internal void DispatchFactory(string path = LegacyPath, bool save = true, bool editor = true)
            {
                if (dispatchDepth == 0)
                {
                    if (!ReferenceEquals(archiveField.GetValue(manager), previousArchive))
                        (archiveField.GetValue(manager) as IDisposable)?.Dispose();
                    archiveField.SetValue(manager, null);
                }
                object priorPath = pathField.GetValue(manager), priorSave = saveField.GetValue(manager);
                pathField.SetValue(manager, path);
                saveField.SetValue(manager, save);
                dispatchDepth++;
                try { factory(manager, editor); }
                finally
                {
                    dispatchDepth--;
                    pathField.SetValue(manager, priorPath);
                    saveField.SetValue(manager, priorSave);
                }
            }

            private EngineInterface.LoadMapReturnData LoadBoundary(string source, bool editor)
            {
                Loads++;
                LoadedSource = source;
                LoadedAsEditor = editor;
                return new EngineInterface.LoadMapReturnData { errorCode = LoadSucceeds ? 1 : 0 };
            }

            private unsafe delegate int TrailNativeCall(byte* path, int pathLength, byte* restart, int restartLength);

            private unsafe int RawBoundary(byte* path, int pathLength, byte* restart, int restartLength)
            {
                RawCalls++;
                var call = new TrailRawArguments {
                    PathBytes = new byte[pathLength], RestartBytes = new byte[restartLength],
                    MonitorHeld = Monitor.IsEntered(MonitorObject), Depth = ++depth
                };
                if (pathLength != 0) Marshal.Copy((IntPtr)path, call.PathBytes, 0, pathLength);
                if (restartLength != 0) Marshal.Copy((IntPtr)restart, call.RestartBytes, 0, restartLength);
                call.Destination = Encoding.Unicode.GetString(call.PathBytes);
                RawArguments.Add(call);
                try { NativeAction?.Invoke(call); return RawResult; }
                finally { depth--; }
            }

            private T Compile<T>(MethodInfo method, ILContext.Manipulator edit) where T : Delegate
            {
                Assert.IsNotNull(method);
                var definition = new DynamicMethodDefinition(method);
                generated.Add(definition);
                var il = new ILContext(definition.Definition);
                generated.Add(il);
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                il.Invoke(edit);
                return (T)definition.Generate().CreateDelegate(typeof(T));
            }

            private static void ReplaceTrailNativeCall(ILContext il, TrailNativeCall replacement)
            {
                var call = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Call &&
                    i.Operand is MethodReference method && method.DeclaringType.FullName == "EngineInterface" && method.Name == "DLL_CreateTrailMission");
                var original = (MethodReference)call.Operand;
                Assert.IsFalse(original.HasThis);
                Assert.AreEqual(4, original.Parameters.Count);
                var arguments = original.Parameters.Select(parameter => new VariableDefinition(parameter.ParameterType)).ToArray();
                foreach (var argument in arguments) il.Body.Variables.Add(argument);
                var cursor = new ILCursor(il);
                cursor.Goto(call, MoveType.After);
                // Keep branch/EH targets on the original call, then preserve its
                // four exact argument values while putting the instance delegate first.
                // EmitDelegate's generic invoker cannot represent the byte* parameters.
                call.OpCode = OpCodes.Nop;
                call.Operand = null;
                for (int index = arguments.Length - 1; index >= 0; index--) cursor.Emit(OpCodes.Stloc, arguments[index]);
                cursor.EmitReference<TrailNativeCall>(replacement);
                foreach (var argument in arguments) cursor.Emit(OpCodes.Ldloc, argument);
                cursor.Emit(OpCodes.Callvirt, typeof(TrailNativeCall).GetMethod("Invoke"));
            }

            private static void ReplaceCall<T>(ILContext il, string type, string name, T replacement) where T : Delegate
            {
                var call = il.Body.Instructions.Single(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) &&
                    i.Operand is MethodReference method && method.DeclaringType.FullName == type && method.Name == name);
                var cursor = new ILCursor(il);
                cursor.Goto(call, MoveType.After);
                // Preserve incoming branch/EH targets on the original instruction.
                call.OpCode = OpCodes.Nop;
                call.Operand = null;
                cursor.EmitDelegate(replacement);
            }

            private static void ReplaceField<T>(ILContext il, string type, string name, Func<T> replacement)
            {
                var load = il.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldsfld &&
                    i.Operand is FieldReference field && field.DeclaringType.FullName == type && field.Name == name);
                var cursor = new ILCursor(il);
                cursor.Goto(load, MoveType.After);
                load.OpCode = OpCodes.Nop;
                load.Operand = null;
                cursor.EmitDelegate(replacement);
            }

            public void Dispose()
            {
                try
                {
                    if (archiveField != null)
                    {
                        if (!ReferenceEquals(archiveField.GetValue(manager), previousArchive))
                            (archiveField.GetValue(manager) as IDisposable)?.Dispose();
                        archiveField.SetValue(manager, previousArchive);
                        pathField.SetValue(manager, previousPath);
                        saveField.SetValue(manager, previousSave);
                    }
                    if (handlers != null)
                    {
                        handlers.Clear();
                        foreach (var entry in previousHandlers) handlers.Add(entry.Key, entry.Value);
                    }
                }
                finally
                {
                    for (int i = generated.Count - 1; i >= 0; i--) generated[i].Dispose();
                    generated.Clear();
                }
            }
        }
    }
}

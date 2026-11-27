using System;
using System.Reflection;
using System.IO;
using System.Linq;
using System.Collections;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.ScriptExtenderFixes;
using APIShared;
using APIShared.GameModes;
using APIShared.Savegames;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.API.Components.Archive;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed partial class SaveContextTests
    {
        private ResolveEventHandler resolve;
        [TestInitialize]
        public void ResolveInstalledExtenderDependencies()
        {
            // Like the existing archive/readiness fixtures, resolve SE's own dependency
            // versions before invoking its code. A failed bind is cached for later suites.
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            resolve = (sender, args) => {
                string file = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
        }
        [TestCleanup]
        public void ReleaseDependencyResolver() => AppDomain.CurrentDomain.AssemblyResolve -= resolve;

        // Snapshot the IDictionary contract directly. A generic dictionary can
        // expose KeyValuePair from IEnumerable.Current rather than DictionaryEntry.
        // Entry retains the exact key/value pairs and their original enumeration order.
        private static DictionaryEntry[] SnapshotHandlers(IDictionary handlers)
        {
            var snapshot = new List<DictionaryEntry>(handlers.Count);
            IDictionaryEnumerator enumerator = handlers.GetEnumerator();
            while (enumerator.MoveNext()) snapshot.Add(enumerator.Entry);
            return snapshot.ToArray();
        }

        [TestMethod]
        public void SynchronizedSavesCorrectStaleFlagsWithoutLeakingIntoMapsOrOtherThreads()
        {
            foreach (var source in new[] { new SaveContext(false, false, "source.map"),
                new SaveContext(true, false, "previous.sav"), new SaveContext(false, true, "editor.map") })
            {
                Assert.AreSame(source, SaveContextRuntime.NormalizeSave(source));
                int calls = 0;
                try
                {
                    SaveContextRuntime.RunSynchronizedSave(() => {
                        calls++;
                        var saved = SaveContextRuntime.NormalizeSave(source);
                        Assert.IsTrue(saved.IsSaveFile); Assert.IsFalse(saved.IsMapEditorSave);
                        Assert.IsNull(saved.FilePath, "The source map must not be advertised as the output save.");
                        SaveContextRuntime.RunSynchronizedSave(() => Assert.IsTrue(SaveContextRuntime.NormalizeSave(source).IsSaveFile));
                        Assert.IsTrue(SaveContextRuntime.NormalizeSave(source).IsSaveFile);
                        SaveContext other = null;
                        var thread = new Thread(() => other = SaveContextRuntime.NormalizeSave(source));
                        thread.Start(); thread.Join();
                        Assert.AreSame(source, other);
                        throw new InvalidOperationException("native save failed");
                    });
                    Assert.Fail("Original failure was lost.");
                }
                catch (InvalidOperationException error) { Assert.AreEqual("native save failed", error.Message); }
                Assert.AreEqual(1, calls);
                Assert.AreSame(source, SaveContextRuntime.NormalizeSave(source), "Failed/nested save leaked its scope.");
            }
        }

        [TestMethod]
        public void LoadedExtenderMethodsAcceptAndCompileTheProductionTransformations()
        {
            Check("PrepareMapArchiveAndGetBytes", typeof(bool), SaveContextRuntime.CorrectSaveFactory);
            Check("OnLoadMap", typeof(MapLoadEventArgs), il => SaveContextRuntime.CorrectLoadFlag(il, false));
            Check("OnLoadSave", typeof(LoadSaveGameEventArgs), il => SaveContextRuntime.CorrectLoadFlag(il, true));
            var move = typeof(GameTribeManagerAPI).GetMethod("IssueMoveHereCommand");
            using (var definition = new DynamicMethodDefinition(move))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(MoveCommandFailureRuntime.Transform);
                Assert.IsNotNull(definition.Generate());
            }
        }

        [TestMethod]
        [DataRow(true, true, false)]
        [DataRow(true, false, false)]
        [DataRow(false, true, false)]
        [DataRow(false, false, false)]
        [DataRow(true, true, true)]
        [DataRow(true, false, true)]
        [DataRow(false, true, true)]
        [DataRow(false, false, true)]
        public void ConfirmedMetadataOwnsArchiveCallbacksUntilLoadFinishesOrAborts(
            bool hostHasRecord, bool hostSide, bool abort)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type settings = typeof(SavegameModSettings), coordinator = typeof(SavegameLoadCoordinator);
            // Replace only the external readiness/admission boundary. The production
            // archive callback, mode reader and both terminal paths run unchanged.
            // No Steam roster is present: this also covers solo Coop's confirmed path.
            var previous = new Dictionary<FieldInfo, object>();
            foreach (Type type in new[] { settings, coordinator })
                foreach (FieldInfo field in type.GetFields(flags).Where(f => !f.IsInitOnly && !f.IsLiteral))
                    previous.Add(field, field.GetValue(null));
            foreach (string name in new[] { "preparedEndpoints", "preparedSnapshots" })
                Assert.AreEqual(0, ((IDictionary)settings.GetField(name, flags).GetValue(null)).Count,
                    "Fixture requires no active settings participants.");
            string path = Path.GetFullPath("confirmed-coop.msv");
            var host = hostHasRecord ? Metadata(GameModeKind.CoopTrail,
                GameModeLaunchVariant.RestoredCustomizedSave, hostSide) : null;
            var local = Metadata(GameModeKind.CustomGame, GameModeLaunchVariant.Standard, !hostSide);
            byte[] bytes = SavegameModSettings.SerializeWithinLimit(local);
            MethodInfo load = settings.GetMethod("Load", flags);
            try
            {
                coordinator.GetField("pending", flags).SetValue(null, null);
                coordinator.GetField("loading", flags).SetValue(null, true);
                settings.GetField("loaded", flags).SetValue(null, host);
                settings.GetField("preparedRecord", flags).SetValue(null, host);
                settings.GetField("preparedApplied", flags).SetValue(null, true);
                settings.GetField("preparedTransferred", flags).SetValue(null, false);
                settings.GetField("preparedApplicationContext", flags).SetValue(null, null);
                settings.GetField("chosenPath", flags).SetValue(null, hostSide ? path : null);
                settings.GetField("legacyChoice", flags).SetValue(null, !hostHasRecord);
                // SE may deliver the record in Pre and Post. The client may still
                // await managed initialization after the surrounding load returns.
                for (int callback = 0; callback < 3; callback++)
                {
                    load.Invoke(null, new object[] { bytes, new LoadContext(true, path) });
                    Assert.AreSame(host, settings.GetField("loaded", flags).GetValue(null));
                    Assert.AreSame(host, SavegameModSettings.PreparedRecordForStart);
                    Assert.AreEqual(hostHasRecord, SavegameModSettings.TryGetRestoredMode(out var kind, out var variant));
                    Assert.AreEqual(hostHasRecord ? GameModeKind.CoopTrail : GameModeKind.Unknown, kind);
                    Assert.AreEqual(hostHasRecord ? GameModeLaunchVariant.RestoredCustomizedSave : GameModeLaunchVariant.Standard, variant);
                }
                if (abort) coordinator.GetMethod("Abort", flags).Invoke(null,
                    new object[] { "fixture cancelled", false, false, "fixture", Type.Missing, Type.Missing, Type.Missing });
                else SavegameLoadCoordinator.FinishLoad();
                Assert.IsFalse(SavegameLoadCoordinator.IsLoadingMultiplayerSave);
                // A subsequent ordinary load retains the original file binding.
                settings.GetField("chosenPath", flags).SetValue(null, path);
                load.Invoke(null, new object[] { bytes, new LoadContext(true, path + ".other") });
                Assert.AreSame(host, settings.GetField("loaded", flags).GetValue(null));
                load.Invoke(null, new object[] { bytes, new LoadContext(false, path) });
                Assert.AreSame(host, settings.GetField("loaded", flags).GetValue(null));
                load.Invoke(null, new object[] { bytes, new LoadContext(true, path) });
                Assert.IsTrue(SavegameModSettings.TryGetRestoredMode(out var localKind, out var localVariant));
                Assert.AreEqual(GameModeKind.CustomGame, localKind);
                Assert.AreEqual(GameModeLaunchVariant.Standard, localVariant);
            }
            finally { foreach (var item in previous) item.Key.SetValue(null, item.Value); }
        }

        private static SavegameModSettingsRecord Metadata(GameModeKind kind, GameModeLaunchVariant variant, bool locked)
            => new SavegameModSettingsRecord { Version = 3, Kind = (int)kind, Variant = (int)variant,
                LockedByConflict = locked, Mods = new Dictionary<string, Dictionary<string, byte[]>>(),
                CreatorRules = new Dictionary<string, Dictionary<string, TrailCreatorRule>>() };

        private static void Check(string name, Type arg, ILContext.Manipulator edit)
        {
            var method = typeof(GameMapArchiveManagerAPI).GetMethod(name,
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic, null, new[] { arg }, null);
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.ReferenceBag = RuntimeILReferenceBag.Instance;
                context.Invoke(edit);
                Assert.IsNotNull(definition.Generate());
            }
        }

        [TestMethod]
        public void TransformedLoadFlagsDistinguishMultiplayerSavesAndEditorMaps()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
            var field = typeof(GameMapArchiveManagerAPI).GetField("_currentFileIsSaveFile", flags);
            object previous = field.GetValue(GameMapArchiveManagerAPI.Instance);
            try
            {
                foreach (bool editorCapable in new[] { false, true })
                {
                    var method = typeof(GameMapArchiveManagerAPI).GetMethod(editorCapable ? "OnLoadSave" : "OnLoadMap", flags);
                    using (var definition = new DynamicMethodDefinition(method))
                    using (var il = new ILContext(definition.Definition))
                    {
                        il.ReferenceBag = RuntimeILReferenceBag.Instance;
                        il.Invoke(context => SaveContextRuntime.CorrectLoadFlag(context, editorCapable));
                        var store = il.Body.Instructions.Single(i => i.OpCode == Mono.Cecil.Cil.OpCodes.Stfld &&
                            i.Operand is Mono.Cecil.FieldReference f && f.Name == field.Name);
                        // Keep the actual transformed classification; omit file loading itself.
                        while (store.Next != null) il.Body.Instructions.Remove(store.Next);
                        il.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                        var classify = definition.Generate();
                        foreach (bool value in new[] { false, true })
                        {
                            object args = editorCapable ? (object)new LoadSaveGameEventArgs(SHCDESE.EventAPI.EventHookPhase.Pre,
                                "map-or-save", IntPtr.Zero, value) : new MapLoadEventArgs(SHCDESE.EventAPI.EventHookPhase.Pre,
                                0, "map-or-save", IntPtr.Zero, "map", (byte)(value ? 1 : 0), 0, 0, 0);
                            classify.Invoke(null, new[] { args });
                            Assert.AreEqual(editorCapable ? !value : value, field.GetValue(GameMapArchiveManagerAPI.Instance));
                        }
                    }
                }
            }
            finally { field.SetValue(GameMapArchiveManagerAPI.Instance, previous); }
        }

        [TestMethod]
        public void CorrectedFactoryWritesSaveOnlyRecordsBeforeAndAfterManualSaveAndLeavesEditorMapsAlone()
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var manager = GameMapArchiveManagerAPI.Instance;
            var archiveField = manager.GetType().GetField("_mapArchive", flags);
            var pathField = manager.GetType().GetField("_currentFilePath", flags);
            var saveField = manager.GetType().GetField("_currentFileIsSaveFile", flags);
            var handlers = (IDictionary)typeof(ModSaveDataAPI).GetField("_handlers", flags).GetValue(ModSaveDataAPI.Instance);
            var previousHandlers = SnapshotHandlers(handlers);
            object previousArchive = archiveField.GetValue(manager), previousPath = pathField.GetValue(manager), previousSave = saveField.GetValue(manager);
            string file = Path.Combine(Path.GetTempPath(), "api-context-" + Guid.NewGuid().ToString("N") + ".msv");
            try
            {
                handlers.Clear();
                byte[] payload = MessagePack.MessagePackSerializer.Serialize(new APIShared.SavegameModSettingsRecord {
                    Version = 3, Kind = (int)APIShared.GameModes.GameModeKind.CoopTrail,
                    Mods = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, byte[]>>() });
                Assert.IsTrue(ModSaveDataAPI.Instance.RegisterModDataHandler("ContextFixture",
                    context => context.IsSaveFile && !context.IsMapEditorSave ? payload : null, (bytes, context) => { }));
                var method = manager.GetType().GetMethod("PrepareMapArchiveAndGetBytes", flags);
                using (var definition = new DynamicMethodDefinition(method))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(SaveContextRuntime.CorrectSaveFactory);
                    // Execute the actual factory and SE dispatch; cut only the unrelated native
                    // timer/metadata tail, which requires a running Unity game.
                    var dispatch = il.Body.Instructions.Single(i => i.Operand is Mono.Cecil.MethodReference m &&
                        m.Name == "WriteAllModDataToArchive");
                    while (dispatch.Next != null) il.Body.Instructions.Remove(dispatch.Next);
                    il.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                    il.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    MethodInfo factory = definition.Generate();
                    foreach (bool previousManualSave in new[] { false, true })
                    {
                        archiveField.SetValue(manager, null); pathField.SetValue(manager, file); saveField.SetValue(manager, previousManualSave);
                        SaveContextRuntime.RunSynchronizedSave(() => factory.Invoke(null, new object[] { manager, false }));
                        var archive = (SHCDESE.API.Components.Archive.MapArchive)archiveField.GetValue(manager);
                        Assert.IsNotNull(archive);
                        archive.CommitChanges();
                        CollectionAssert.AreEqual(payload, archive.TryReadBinaryFile("_SE_ModData_ContextFixture.msgpack", true));
                        File.WriteAllBytes(file, new byte[10000].Concat(archive.ArchiveStream.ToArray()).ToArray());
                        Assert.IsTrue(SHCDESE.API.Components.Archive.MapArchive.TryLoad(file, out var reloaded));
                        using (reloaded) CollectionAssert.AreEqual(payload, reloaded.TryReadBinaryFile("_SE_ModData_ContextFixture.msgpack", true));
                        archive.Dispose(); archiveField.SetValue(manager, null);
                    }
                    saveField.SetValue(manager, false);
                    factory.Invoke(null, new object[] { manager, true });
                    Assert.IsNull(archiveField.GetValue(manager), "Editor map unexpectedly received a save-only record.");
                }
            }
            finally
            {
                if (!ReferenceEquals(archiveField.GetValue(manager), previousArchive))
                    (archiveField.GetValue(manager) as IDisposable)?.Dispose();
                archiveField.SetValue(manager, previousArchive); pathField.SetValue(manager, previousPath); saveField.SetValue(manager, previousSave);
                handlers.Clear(); foreach (var entry in previousHandlers) handlers.Add(entry.Key, entry.Value);
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OwnedFactoryFreezesBeforeActualEnrolledTransportAndRetainsCaptureFailures(bool captureThrows)
        {
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            const string identifier = "CriticalFactoryFixture";
            Assert.IsNull(VersionedSaveFactoryFrame.Current, "Fixture requires no active factory frame.");
            var manager = GameMapArchiveManagerAPI.Instance;
            var archiveField = manager.GetType().GetField("_mapArchive", flags);
            var pathField = manager.GetType().GetField("_currentFilePath", flags);
            var saveField = manager.GetType().GetField("_currentFileIsSaveFile", flags);
            var handlers = (IDictionary)typeof(ModSaveDataAPI).GetField("_handlers", flags).GetValue(ModSaveDataAPI.Instance);
            var previousHandlers = SnapshotHandlers(handlers);
            object previousArchive = archiveField.GetValue(manager), previousPath = pathField.GetValue(manager), previousSave = saveField.GetValue(manager);
            string file = Path.Combine(Path.GetTempPath(), "api-critical-factory-" + Guid.NewGuid().ToString("N") + ".msv");
            try
            {
                handlers.Clear();
                archiveField.SetValue(manager, null); pathField.SetValue(manager, file); saveField.SetValue(manager, false);
                var events = new List<string>();
                int factoryCaptures = 0, recordCaptures = 0, transports = 0;
                VersionedSaveContext factoryContext = default, transportContext = default;
                VersionedSavePreparedSource prepared = null;
                VersionedSavePreparedOrigin origin = null;
                string originError = null;
                var adapterMethod = typeof(VersionedSaveDataRuntime).GetMethod("RegisterWithExtender", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.IsNotNull(adapterMethod);
                // Keep the production SaveContext converter and actual SE handler dispatch.
                // Only this local registry and its declared consumer are supplied by the fixture.
                var adapter = (VersionedSaveDataRegistry.Transport)Delegate.CreateDelegate(typeof(VersionedSaveDataRegistry.Transport), adapterMethod);
                var registry = new VersionedSaveDataRegistry((id, save, load) => adapter(id, context => {
                    transports++; events.Add("transport"); transportContext = context;
                    return save(context);
                }, load), ignored => { }, ignored => { });
                var registration = new VersionedSaveDataRegistration("owner", identifier, 3, 1, new[] { "value", "cutover" }, writer => {
                    recordCaptures++; events.Add("capture"); writer.Write("value", new byte[] { 23 }); return true;
                }, ignored => { });
                registration.Scope = VersionedSaveScope.SaveGamesAndMaps;
                registration.SimulationRelevant = true;
                registration.Prepared = load => {
                    if (!prepared.TryGetCurrent(load, out origin, out string reason)) originError = reason;
                };
                Assert.IsTrue(registry.TryRegister(registration, out var handle, out string detail), detail);
                Assert.IsTrue(registry.TryTrackPreparedSource("owner", handle, out prepared, out detail), detail);
                Assert.IsTrue(registry.TryEnrollCriticalSource(prepared, "cutover", 3, out var enrollment, out detail), detail);
                registry.BeginLoad(VersionedSaveLoadSource.SaveGame, "loaded-source.sav");
                registry.PrepareLoad(); registry.EndLoad();
                Assert.IsNull(originError);
                Assert.IsTrue(prepared.TryValidateCompleted(origin, out detail), detail);

                var method = manager.GetType().GetMethod("PrepareMapArchiveAndGetBytes", flags);
                using (var definition = new DynamicMethodDefinition(method))
                using (var il = new ILContext(definition.Definition))
                {
                    il.ReferenceBag = RuntimeILReferenceBag.Instance;
                    il.Invoke(SaveContextRuntime.CorrectSaveFactory);
                    // As in the existing factory fixture, retain the actual constructor,
                    // transformation and handler dispatch, cutting only the native tail.
                    var dispatch = il.Body.Instructions.Single(i => i.Operand is Mono.Cecil.MethodReference m &&
                        m.Name == "WriteAllModDataToArchive");
                    while (dispatch.Next != null) il.Body.Instructions.Remove(dispatch.Next);
                    il.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ldnull));
                    il.Body.Instructions.Add(Mono.Cecil.Cil.Instruction.Create(Mono.Cecil.Cil.OpCodes.Ret));
                    MethodInfo factory = definition.Generate();
                    var failure = new InvalidOperationException("controlled critical factory capture failure");
                    VersionedSaveCriticalCapture frozen = null;
                    using (var frame = VersionedSaveFactoryFrame.Begin(context => {
                        factoryCaptures++; events.Add("factory"); factoryContext = context;
                        if (captureThrows) throw failure;
                        if (!registry.TryFreezeCriticalRecords(new[] { new VersionedSaveCriticalRecord(enrollment, origin, new byte[] { 7 }) },
                            context, 4096, out frozen, out string reason)) throw new InvalidOperationException(reason);
                        return frozen;
                    }))
                    {
                        // Stale map/editor fields must become the same known synchronized
                        // null-path policy for the frame and the real transport converter.
                        SaveContextRuntime.RunSynchronizedSave(() => factory.Invoke(null, new object[] { manager, true }));
                        Assert.AreEqual(1, factoryCaptures); Assert.AreEqual(1, transports);
                        Assert.IsTrue(factoryContext.IsSaveFile); Assert.IsFalse(factoryContext.IsMapEditorSave); Assert.IsNull(factoryContext.FilePath);
                        Assert.IsTrue(transportContext.IsSaveFile); Assert.IsFalse(transportContext.IsMapEditorSave); Assert.IsNull(transportContext.FilePath);
                        CollectionAssert.AreEqual(captureThrows ? new[] { "factory", "transport", "capture" } :
                            new[] { "factory", "capture", "transport" }, events);
                        // Assertions remain outside SE callbacks, whose failures are isolated.
                        if (captureThrows)
                        {
                            Assert.AreSame(failure, frame.Failure);
                            Assert.IsFalse(frame.TryGetWrittenCapture(out _, out detail));
                            Assert.IsFalse(string.IsNullOrEmpty(detail));
                        }
                        else
                        {
                            Assert.IsNull(frame.Failure);
                            Assert.IsTrue(frame.TryGetWrittenCapture(out var written, out detail), detail);
                            Assert.AreSame(frozen, written);
                            Assert.AreEqual(1, recordCaptures);
                            Assert.AreEqual(VersionedSaveWriteStatus.Written, handle.LastWriteStatus);
                            var archive = (MapArchive)archiveField.GetValue(manager);
                            Assert.IsNotNull(archive); archive.CommitChanges();
                            byte[] bytes = archive.TryReadBinaryFile("_SE_ModData_" + identifier + ".msgpack", true);
                            Assert.IsTrue(written.MatchesRecord(enrollment, bytes, out detail), detail);
                            var decoded = VersionedSaveRecordCodec.Decode(bytes, 3);
                            Assert.AreEqual(3, decoded.MinimumReaderVersion);
                            Assert.IsTrue(decoded.Entries.TryGet("value", out var value)); CollectionAssert.AreEqual(new byte[] { 23 }, value);
                            Assert.IsTrue(decoded.Entries.TryGet("cutover", out var marker)); CollectionAssert.AreEqual(new byte[] { 7 }, marker);
                            Assert.AreEqual(VersionedSaveRecordCodec.DecodeStatus.TooNew, VersionedSaveRecordCodec.Decode(bytes, 2).Status);
                        }

                        SaveContextRuntime.RunSynchronizedSave(() => factory.Invoke(null, new object[] { manager, true }));
                        Assert.AreEqual(1, factoryCaptures, "A duplicate factory must never recapture its consumer.");
                        Assert.AreEqual(2, transports);
                        Assert.IsFalse(frame.TryGetWrittenCapture(out _, out detail), "Duplicate dispatch cannot authorize replacement.");
                        Assert.IsFalse(string.IsNullOrEmpty(detail));
                        if (captureThrows) Assert.AreSame(failure, frame.Failure, "Later failures must retain the first callback failure.");
                        else Assert.AreEqual(1, recordCaptures, "Duplicate transport must not recapture a frozen source.");
                    }
                    Assert.IsNull(VersionedSaveFactoryFrame.Current);
                }
            }
            finally
            {
                if (!ReferenceEquals(archiveField.GetValue(manager), previousArchive))
                    (archiveField.GetValue(manager) as IDisposable)?.Dispose();
                archiveField.SetValue(manager, previousArchive); pathField.SetValue(manager, previousPath); saveField.SetValue(manager, previousSave);
                handlers.Clear(); foreach (var entry in previousHandlers) handlers.Add(entry.Key, entry.Value);
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint allocation, uint protection);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint kind);

        [TestMethod]
        public void RealNativeBackendUsesSixBytePrologueAndExecutesOriginalExactlyOnce()
        {
            // Private executable fixture, never game memory or a published runtime hook.
            // Retain the actual 19-byte prefix and its chkstk call displacement. The helper
            // returns zero allocation; the continuation restores the two displaced registers.
            IntPtr copy = VirtualAlloc(IntPtr.Zero, (UIntPtr)0x200000, 0x3000, 0x40);
            Assert.AreNotEqual(IntPtr.Zero, copy);
            NativeDetour<SynchronizedSaveRuntime.SaveCall> probe = null;
            SynchronizedSaveRuntime.SaveCall callback = null;
            try
            {
                Marshal.Copy(SynchronizedSaveRuntime.Entry, 0, copy, SynchronizedSaveRuntime.Entry.Length);
                byte[] finish = { 0x5F, 0x48, 0x8B, 0x5C, 0x24, 0x08, 0xC3 };
                Marshal.Copy(finish, 0, copy + SynchronizedSaveRuntime.Entry.Length, finish.Length);
                byte[] helper = { 0x31, 0xC0, 0xC3 };
                Marshal.Copy(helper, 0, copy + (0x1A6990 - SynchronizedSaveRuntime.Rva), helper.Length);
                int callbacks = 0;
                callback = () => SaveContextRuntime.RunSynchronizedSave(() => {
                    callbacks++; Assert.IsTrue(SaveContextRuntime.NormalizeSave(new SaveContext(false, false)).IsSaveFile);
                    probe.Original();
                });
                var request = new DetourRequest<SynchronizedSaveRuntime.SaveCall> {
                    Name = "private synchronized save fixture", TargetAddress = unchecked((ulong)copy.ToInt64()), Callback = callback };
                probe = SynchronizedSaveRuntime.Backend.CreateDetour(in request) as NativeDetour<SynchronizedSaveRuntime.SaveCall>;
                SynchronizedSaveRuntime.Validate(probe, copy, false);
                probe.Enable();
                SynchronizedSaveRuntime.Validate(probe, copy, true);
                var call = (SynchronizedSaveRuntime.SaveCall)Marshal.GetDelegateForFunctionPointer(copy, typeof(SynchronizedSaveRuntime.SaveCall));
                call(); call();
                Assert.AreEqual(2, callbacks);
                Assert.IsFalse(SaveContextRuntime.NormalizeSave(new SaveContext(false, false)).IsSaveFile);
            }
            finally { probe?.Dispose(); GC.KeepAlive(callback); VirtualFree(copy, UIntPtr.Zero, 0x8000); }
        }
    }
}

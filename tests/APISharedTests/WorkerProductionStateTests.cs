using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using APIShared.Economy;
using APIShared.NativeSites;
using APIShared.Savegames;
using CrusaderDE;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed unsafe class WorkerProductionStateTests
    {
        private ResolveEventHandler resolve;
        [TestInitialize]
        public void ResolveInstalledExtenderDependencies()
        {
            string game = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt")).Trim().Trim('\uFEFF');
            string extender = Path.Combine(game, "BepInEx", "plugins", "000shcdese");
            resolve = (_, args) => {
                string file = Path.Combine(extender, new AssemblyName(args.Name).Name + ".dll");
                return File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            AppDomain.CurrentDomain.AssemblyResolve += resolve;
        }
        [TestCleanup]
        public void ReleaseDependencyResolver() => AppDomain.CurrentDomain.AssemblyResolve -= resolve;
        private static byte[] Payload()
        {
            var bytes = new byte[WorkerProductionStateRuntime.HeaderSize + WorkerProductionStateStore.RowCapacity * WorkerProductionStateStore.RowSize];
            bytes[0] = (byte)'W'; bytes[1] = (byte)'P'; bytes[2] = (byte)'S'; bytes[3] = (byte)'1';
            WorkerProductionStateRuntime.WriteU32(bytes, 4, 1); WorkerProductionStateRuntime.WriteU32(bytes, 8, 10000); WorkerProductionStateRuntime.WriteU32(bytes, 12, 32);
            SetRow(bytes, 16 + 32, WorkerProductionStateStore.Canonical(123, eChimps.CHIMP_TYPE_WOODCUTTER, WorkerProductionBatchFlags.EmptyBatch, 1, 3));
            return bytes;
        }
        private static void SetRow(byte[] bytes, int offset, WorkerProductionStateRow row)
        { fixed (byte* pointer = &bytes[offset]) *(WorkerProductionStateRow*)pointer = row; }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int BuildingTransferFixture();

        [TestMethod]
        public void BuildingRepairEntryUsesTheActualIndirectSixByteBackend()
        {
            byte[] prologue = WorkerProductionBonusNativeDefinition.Hex("48895C2408574883EC30");
            using (var target = ExecutableFunction.Create(a => {
                a.db(prologue); a.mov(eax, 17); a.add(rsp, 0x30); a.pop(rdi); a.ret();
            }))
            {
                NativeDetour<BuildingTransferFixture> hook = null;
                var callback = new BuildingTransferFixture(() => hook.Original() + 1);
                var request = new DetourRequest<BuildingTransferFixture> {
                    Name = "Private weapon workshop building transfer fixture", Callback = callback,
                    TargetAddress = unchecked((ulong)target.EntryPoint.ToInt64())
                };
                var backend = new NativeDetourBackend(new NativeDetourOptions {
                    AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, SuspendThreadsDuringPatch = false
                });
                try
                {
                    hook = (NativeDetour<BuildingTransferFixture>)backend.CreateDetour(in request);
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, prologue, 6, false);
                    var invoke = Marshal.GetDelegateForFunctionPointer<BuildingTransferFixture>(target.EntryPoint);
                    Assert.AreEqual(17, invoke());
                    hook.Enable();
                    NativeIndirectEntry.Validate(hook, target.EntryPoint, prologue, 6, true);
                    Assert.AreEqual(18, invoke(), "The relocated six-byte prologue must resume before SUB RSP and run Original once.");
                    GC.KeepAlive(callback);
                }
                finally { hook?.Dispose(); }
            }
        }

        [TestMethod]
        public void BuildingRepairFrameRequiresItsCompleteUnscheduledRecordAndTail()
        {
            var frame = new byte[WeaponProductionTransferNativeRuntime.ExtendedBodyBytes + 8];
            frame[0] = 57;
            WorkerProductionStateRuntime.WriteU32(frame, 8, 1);
            Assert.IsTrue(WeaponProductionTransferNativeRuntime.ValidateFrame(frame, out _),
                "The exact extended frame with a zero tail clears a stale receiver row.");
            Assert.IsFalse(WeaponProductionTransferNativeRuntime.ValidateFrame(frame.AsSpan(0, frame.Length - 1), out _));
            frame[1] = 1;
            Assert.IsFalse(WeaponProductionTransferNativeRuntime.ValidateFrame(frame, out _));
            frame[1] = 0;
            WorkerProductionStateRuntime.WriteU32(frame, 8, 4000);
            Assert.IsFalse(WeaponProductionTransferNativeRuntime.ValidateFrame(frame, out _));
            WorkerProductionStateRuntime.WriteU32(frame, 8, 1);
            frame[frame.Length - 1] = 1;
            Assert.IsFalse(WeaponProductionTransferNativeRuntime.ValidateFrame(frame, out _),
                "An arbitrary nonempty tail may not restore over an empty native identity.");
        }

        [TestMethod]
        public void MissingWorldSafetyRejectsStateBeforeAnyHookOrArchiveRegistration()
        {
            var safety = typeof(WorkerProductionWorldSafety).GetField("engineGate", BindingFlags.Static | BindingFlags.NonPublic);
            var initialized = typeof(WorkerProductionStateRuntime).GetField("initialized", BindingFlags.Static | BindingFlags.NonPublic);
            object oldGate = safety.GetValue(null), oldInitialized = initialized.GetValue(null);
            try
            {
                safety.SetValue(null, null); initialized.SetValue(null, false);
                // Null load context is deliberate: this prerequisite must reject before
                // touching any native image, permanent receive hook or archive registry.
                var state = Assert.ThrowsExactly<InvalidOperationException>(() => WorkerProductionStateRuntime.Initialize(
                    null, WorkerProductionBonusNativeDefinition.ReferenceHash, null));
                var wire = Assert.ThrowsExactly<InvalidOperationException>(() => WorkerProductionStateNativeRuntime.Initialize(
                    null, WorkerProductionBonusNativeDefinition.ReferenceHash, null));
                StringAssert.Contains(state.Message, "world-safety boundary");
                StringAssert.Contains(wire.Message, "world-safety boundary");
            }
            finally { safety.SetValue(null, oldGate); initialized.SetValue(null, oldInitialized); }
        }

        [TestMethod]
        public void WholePayloadValidationRejectsCorruptLateRowsAndNonemptySentinel()
        {
            var valid = Payload(); var requirement = WorkerProductionStateRuntime.ValidatePayload(valid);
            Assert.IsTrue(requirement.IsValid); Assert.IsTrue(requirement.RequiresExtended); Assert.AreEqual(64, requirement.Digest.Length);
            foreach (int offset in new[] { 0, 4, 8, 12, 16, valid.Length - 4 })
            {
                var bad = (byte[])valid.Clone(); bad[offset] ^= 1;
                Assert.IsFalse(WorkerProductionStateRuntime.ValidatePayload(bad).IsValid, "Corrupt byte " + offset);
            }
            Assert.IsFalse(WorkerProductionStateRuntime.ValidatePayload(valid.Take(valid.Length - 1).ToArray()).IsValid);
            var emptyHeader = Payload(); SetRow(emptyHeader, 16 + 32, default);
            var empty = WorkerProductionStateRuntime.ValidatePayload(emptyHeader);
            Assert.IsTrue(empty.IsValid); Assert.IsFalse(empty.RequiresExtended);
            Assert.AreEqual(64, empty.Digest.Length, "A valid empty record still participates in exact selected-file agreement.");
            SetRow(emptyHeader, 16 + 32, WorkerProductionStateStore.Canonical(123, eChimps.CHIMP_TYPE_WOODCUTTER,
                WorkerProductionBatchFlags.EmptyBatch, 0, 1));
            Assert.IsTrue(WorkerProductionStateRuntime.ValidatePayload(emptyHeader).RequiresExtended,
                "A provenance-only0/1 row remains required history even without a fraction.");
        }

        [TestMethod]
        public void TransferChecksIdentitySchemaAndAdmittedPrecisionBeforeAnyNativeCopy()
        {
            var body = new byte[WorkerProductionStateNativeRuntime.ExtendedPayloadBytes];
            WorkerProductionStateRuntime.WriteU32(body, 0, 1);
            WorkerProductionStateRuntime.WriteU32(body, 4 + 0x98, 123);
            body[4 + 0x8E] = (byte)eChimps.CHIMP_TYPE_WOODCUTTER;
            var row = WorkerProductionStateStore.Canonical(123, eChimps.CHIMP_TYPE_WOODCUTTER, WorkerProductionBatchFlags.EmptyBatch, 1, 3);
            SetRow(body, WorkerProductionStateNativeRuntime.NativePayloadBytes, row);
            Assert.IsTrue(WorkerProductionStateNativeRuntime.ValidateBody(body, 6, out _));
            Assert.IsFalse(WorkerProductionStateNativeRuntime.ValidateBody(body, 2, out _), "Foreign denominator cannot enter this admitted world.");
            // SE Post can replace the zero base with positive final cargo. Provenance
            // flags must survive that legitimate mutation without claiming cargo=0.
            body[4 + 0x388] = 99;
            Assert.IsTrue(WorkerProductionStateNativeRuntime.ValidateBody(body, 6, out _));
            row.GlobalId++; SetRow(body, WorkerProductionStateNativeRuntime.NativePayloadBytes, row);
            Assert.IsFalse(WorkerProductionStateNativeRuntime.ValidateBody(body, 6, out _));
            row.GlobalId--; row.Reserved = 1; SetRow(body, WorkerProductionStateNativeRuntime.NativePayloadBytes, row);
            Assert.IsFalse(WorkerProductionStateNativeRuntime.ValidateBody(body, 6, out _));
            Assert.IsFalse(WorkerProductionStateNativeRuntime.ValidateReceived(new byte[] { 125, 56 }, 2, out _));
            Assert.IsFalse(WorkerProductionStateNativeRuntime.ValidateReceived(new byte[] { 56 }, 1, out _));
            var unrelated = new byte[180001]; unrelated[0] = 126;
            Assert.IsTrue(WorkerProductionStateNativeRuntime.ValidateReceived(unrelated, unrelated.Length, out _),
                "The unit-sidecar guard leaves opaque non-unit control frames to their original native bounds and semantics.");
        }

        [TestMethod]
        public void OptionalPersistenceFailureDoesNotThrowWhileRootCompletesItsLifecycle()
        {
            var type = typeof(WorkerProductionStateRuntime);
            var initialized = type.GetField("initialized", BindingFlags.Static | BindingFlags.NonPublic);
            var connected = type.GetField("connected", BindingFlags.Static | BindingFlags.NonPublic);
            object previousInitialized = initialized.GetValue(null), previousConnected = connected.GetValue(null);
            try
            {
                initialized.SetValue(null, false); connected.SetValue(null, false);
                WorkerProductionStateRuntime.CompleteIntegration();
                Assert.IsFalse((bool)connected.GetValue(null), "An unavailable optional cohort cannot register a raw restore participant.");
                Assert.IsFalse(WorkerProductionStateRuntime.IsAvailable, "Base demand still requires the complete persistence cohort.");
            }
            finally { initialized.SetValue(null, previousInitialized); connected.SetValue(null, previousConnected); }
        }

        [TestMethod]
        public void UnknownModeCannotDeferArithmeticPastAnUnprovenInitialWorkerFold()
        {
            foreach (int scene in new[] { -1, 12, 14, 16, 35, 40 })
            {
                Assert.IsTrue(WorkerProductionRuntime.CanDeferUnknownRestore(false, false, scene), "Vanilla-only restoration needs no selected custom arithmetic.");
                Assert.IsFalse(WorkerProductionRuntime.CanDeferUnknownRestore(true, false, scene), "A map/MP-save entry cannot borrow the local-save scene proof.");
                if (scene != 40)
                    Assert.IsFalse(WorkerProductionRuntime.CanDeferUnknownRestore(true, true, scene), "Local-save route also requires the actual audited paused scene: " + scene);
            }
            Assert.IsTrue(WorkerProductionRuntime.CanDeferUnknownRestore(true, true, 40), "853F0 preserves scene40 until after71670/CDE60; NativeLoaded can select the real rule before engine.run.");
        }

        [TestMethod]
        public void NativeSaveIoFailuresRemainStickyAcrossSuccessfulLaterAppendAndClose()
        {
            var complete = new WorkerProductionSaveIoState();
            Assert.IsFalse(complete.IsComplete);
            complete.ObserveWrite(7, 4, 4);
            complete.ObserveWrite(19, 100, -1); // unrelated callback file
            complete.ObserveClose(19, -1);
            Assert.IsFalse(complete.IsComplete, "The destination must close successfully before replacement.");
            complete.ObserveWrite(7, 0, 0);
            complete.ObserveWrite(7, 20000, 20000); // SE archive append
            complete.ObserveClose(7, 0);
            Assert.IsTrue(complete.IsComplete);

            foreach (int written in new[] { -1, 0, 3, 5 })
            {
                var failed = new WorkerProductionSaveIoState();
                failed.ObserveWrite(7, 4, written);
                failed.ObserveWrite(7, 20000, 20000);
                failed.ObserveClose(7, 0);
                Assert.IsFalse(failed.IsComplete, "A valid ZIP tail cannot repair an incomplete native header: " + written);
            }
            var closeFailure = new WorkerProductionSaveIoState();
            closeFailure.ObserveWrite(7, 4, 4); closeFailure.ObserveClose(7, -1);
            Assert.IsFalse(closeFailure.IsComplete);
            var lateWrite = new WorkerProductionSaveIoState();
            lateWrite.ObserveWrite(7, 4, 4); lateWrite.ObserveClose(7, 0);
            lateWrite.ObserveWrite(7, 1, 1);
            Assert.IsFalse(lateWrite.IsComplete, "Writing after the owned close invalidates the transaction.");
        }

        [TestMethod]
        public void ShortNativeWriteCannotCommitEvenWhenLaterZipPayloadIsValid()
        {
            string destination = Path.Combine(Path.GetTempPath(), "api-production-" + Guid.NewGuid().ToString("N") + ".sav");
            string temporary = destination + ".tmp";
            byte[] old = { 1, 2, 3, 4 }, payload = Payload();
            try
            {
                File.WriteAllBytes(destination, old); WriteArchive(temporary, payload);
                WorkerProductionCriticalSaveRuntime.VerifySavedPayload(temporary, payload);
                foreach (int result in new[] { -1, 0, 3 })
                {
                    var io = new WorkerProductionSaveIoState();
                    io.ObserveWrite(7, 4, result); io.ObserveWrite(7, 20000, 20000); io.ObserveClose(7, 0);
                    Assert.ThrowsExactly<IOException>(() => WorkerProductionCriticalSaveRuntime.CommitSavedFile(temporary, destination, payload, in io));
                    CollectionAssert.AreEqual(old, File.ReadAllBytes(destination));
                    Assert.IsTrue(File.Exists(temporary), "Failed I/O must never enter atomic replacement.");
                }
                var failedClose = new WorkerProductionSaveIoState();
                failedClose.ObserveWrite(7, 4, 4); failedClose.ObserveClose(7, -1);
                Assert.ThrowsExactly<IOException>(() => WorkerProductionCriticalSaveRuntime.CommitSavedFile(temporary, destination, payload, in failedClose));
                CollectionAssert.AreEqual(old, File.ReadAllBytes(destination));
                var complete = new WorkerProductionSaveIoState();
                complete.ObserveWrite(7, 4, 4); complete.ObserveWrite(7, 20000, 20000); complete.ObserveClose(7, 0);
                WorkerProductionCriticalSaveRuntime.CommitSavedFile(temporary, destination, payload, in complete);
                Assert.IsFalse(File.Exists(temporary));
                WorkerProductionCriticalSaveRuntime.VerifySavedPayload(destination, payload);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); if (File.Exists(destination)) File.Delete(destination); }
        }

        [TestMethod]
        public void ActualSavedArchiveMustContainCurrentPayloadAndDenominatorsAreInspectedBeforeLoad()
        {
            string path = Path.Combine(Path.GetTempPath(), "api-production-" + Guid.NewGuid().ToString("N") + ".sav");
            var payload = Payload();
            try
            {
                WriteArchive(path, payload);
                WorkerProductionCriticalSaveRuntime.VerifySavedPayload(path, payload);
                var requirement = WorkerProductionStateRuntime.InspectSelectedSaveRequirement(path);
                Assert.IsTrue(requirement.IsValid); Assert.IsTrue(requirement.RequiresExtended);
                CollectionAssert.AreEqual(new ulong[] { 3 }, WorkerProductionStateRuntime.InspectSelectedSaveDenominators(path));
                var stale = (byte[])payload.Clone(); stale[16 + 32]++;
                Assert.ThrowsExactly<InvalidDataException>(() => WorkerProductionCriticalSaveRuntime.VerifySavedPayload(path, stale));
                WriteArchive(path, null);
                Assert.ThrowsExactly<InvalidDataException>(() => WorkerProductionCriticalSaveRuntime.VerifySavedPayload(path, payload));
                Assert.IsFalse(WorkerProductionStateRuntime.InspectSelectedSaveRequirement(path).RequiresExtended, "An intentionally empty retired record is legacy.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }
        private static void WriteArchive(string path, byte[] payload)
        {
            var entries = new List<KeyValuePair<string, byte[]>>();
            if (payload != null) entries.Add(new KeyValuePair<string, byte[]>(WorkerProductionStateRuntime.EntryName, payload));
            byte[] record = VersionedSaveRecordCodec.Encode(1, 1, entries);
            using (var file = File.Create(path))
            {
                // Real saves contain a native prefix; ZIP offsets remain relative to the
                // appended archive, exactly as the Extender's archive loader supports.
                file.Write(new byte[1024], 0, 1024);
                using (var zip = new ZipOutputStream(file) { IsStreamOwner = false })
                {
                    zip.PutNextEntry(new ZipEntry("_SE_ModData_" + WorkerProductionStateRuntime.RecordIdentifier + ".msgpack"));
                    zip.Write(record, 0, record.Length); zip.CloseEntry(); zip.Finish();
                }
            }
        }

        private static bool simulatedSaveFailure;
        private static string savedContinuationValue;
        private static void SaveStackFixture() { }
        private static void AcceptContinuationValue(string value) { savedContinuationValue=value; }
        private static void SimulateNativeSave(EditorDirector editor,string path,string name,bool locked,bool temporary,bool map)
        {
            // External native outcome boundary only. The real IL transform and
            // sequence/FailedSince implementation remain under this behavior test.
            var type=typeof(WorkerProductionCriticalSaveRuntime);
            type.GetField("sequence",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,WorkerProductionCriticalSaveRuntime.OutcomeToken+1);
            type.GetField("lastFailed",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,simulatedSaveFailure);
        }
        [TestMethod]
        public void WorkshopFailureDropsRetainedPathButSuccessKeepsItForFollowingPublication()
        {
            var outcome=typeof(WorkerProductionCriticalSaveRuntime);
            var sequence=outcome.GetField("sequence",BindingFlags.Static|BindingFlags.NonPublic);
            var failed=outcome.GetField("lastFailed",BindingFlags.Static|BindingFlags.NonPublic);
            object oldSequence=sequence.GetValue(null),oldFailed=failed.GetValue(null);
            bool failureReleased=false;
            try
            {
                using(var definition=new DynamicMethodDefinition(typeof(WorkerProductionStateTests).GetMethod(nameof(SaveStackFixture),BindingFlags.Static|BindingFlags.NonPublic)))
                using(var il=new ILContext(definition.Definition))
                {
                    il.ReferenceBag=RuntimeILReferenceBag.Instance;
                    il.Body.Instructions.Clear();
                    var cursor=new ILCursor(il);
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldstr,"retained workshop path");
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldnull);
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldstr,"output.map");cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldstr,"map");
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_0);cursor.Emit(Mono.Cecil.Cil.OpCodes.Ldc_I4_1);
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Callvirt,typeof(EditorDirector).GetMethod("SaveSaveGameOrMap"));
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Call,typeof(WorkerProductionStateTests).GetMethod(nameof(AcceptContinuationValue),BindingFlags.Static|BindingFlags.NonPublic));
                    cursor.Emit(Mono.Cecil.Cil.OpCodes.Ret);
                    il.Invoke(context=>WorkerProductionSaveUiRuntime.TransformWorkshopSave(context,()=>failureReleased=true));
                    var save=il.Body.Instructions.Single(i=>i.Operand is Mono.Cecil.MethodReference m && m.DeclaringType.FullName=="EditorDirector" && m.Name=="SaveSaveGameOrMap");
                    save.OpCode=Mono.Cecil.Cil.OpCodes.Call;
                    save.Operand=il.Method.Module.ImportReference(typeof(WorkerProductionStateTests).GetMethod(nameof(SimulateNativeSave),BindingFlags.Static|BindingFlags.NonPublic));
                    MethodInfo generated=WorkerProductionSaveUiRuntime.GenerateAndCompile(definition);
                    simulatedSaveFailure=false;savedContinuationValue=null;failureReleased=false;
                    generated.Invoke(null,null);
                    Assert.AreEqual("retained workshop path",savedContinuationValue,"Success must preserve the original stack value for the .data publication.");
                    Assert.IsFalse(failureReleased,"The UI failure callback must not run for a successful save.");
                    simulatedSaveFailure=true;savedContinuationValue=null;
                    generated.Invoke(null,null);
                    Assert.IsNull(savedContinuationValue,"A newly failed native transaction must abort before the success continuation.");
                    Assert.IsTrue(failureReleased,"Failure still invokes the workshop UI release boundary exactly on the aborted path.");
                }
            }
            finally
            { sequence.SetValue(null,oldSequence);failed.SetValue(null,oldFailed); }
        }

        [TestMethod]
        public void InstalledManagedBodiesAcceptAllConcreteSaveContinuationTransforms()
        {
            Probe(typeof(EditorDirector).GetMethod("SaveSaveGameOrMap"), WorkerProductionSaveUiRuntime.TransformEditorSave);
            Probe(typeof(HUD_LoadSaveRequester).GetMethod("RunOKAction", BindingFlags.Instance | BindingFlags.NonPublic), WorkerProductionSaveUiRuntime.TransformRequester);
            WorkerProductionManagedContracts.ValidateReceive(typeof(EngineInterface).GetMethod("ReceiveChore"));
            int workshop = 0;
            foreach (var type in Types(typeof(HUD_WorkshopUploader)))
                foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (method.GetMethodBody() == null) continue;
                    using (var definition = new DynamicMethodDefinition(method))
                        if (definition.Definition.Body.Instructions.Any(i => i.Operand is Mono.Cecil.MethodReference m && m.DeclaringType.FullName == "EditorDirector" && m.Name == "SaveSaveGameOrMap"))
                        { Probe(method, WorkerProductionSaveUiRuntime.TransformWorkshopSave); workshop++; }
                }
            Assert.AreEqual(2, workshop, "Both upload and successful-upload local publication paths are guarded.");
        }
        private static IEnumerable<Type> Types(Type type)
        { yield return type; foreach (var child in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)) foreach (var nested in Types(child)) yield return nested; }
        private static void Probe(MethodInfo method, ILContext.Manipulator transform)
        {
            Assert.IsNotNull(method);
            using (var definition = new DynamicMethodDefinition(method))
            using (var il = new ILContext(definition.Definition))
            {
                il.ReferenceBag = RuntimeILReferenceBag.Instance;
                // Production transforms perform their full CFG/stack validation. Generate
                // validates the real bodies structurally here; forcing desktop CLR JIT
                // would attempt Unity ECalls which exist only inside the game process.
                // The boundary fixture above separately forces JIT and executes both paths;
                // production initialization still forces the real bodies through Mono JIT.
                il.Invoke(transform); Assert.IsNotNull(definition.Generate());
            }
        }
    }
}

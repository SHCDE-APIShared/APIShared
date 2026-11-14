using Microsoft.VisualStudio.TestTools.UnitTesting;
using CrusaderDE;
using System.Linq;
namespace APISharedTests
{
    public partial class RuntimeTests
    {
        [TestMethod] public void PreparedEngineReadbacks() => EngineReadbackTests.Run(Assert);
        [TestMethod] public void SavegameSettingsRoundTrip() => SavegameModSettingsTests.Run(Assert);
        [TestMethod] public void SavegameSettingsReaderAcceptsTheCapturedPerModLimit() => SavegameModSettingsTests.RunPerModLimit(Assert);
        [TestMethod] public void SaveReadinessPacketRoundTrip()
        {
            var packet = new APIShared.SavegameLoadPacket { Operation = 0, Lobby = 123,
                Token = "01234567890123456789012345678901", SaveName = "coop-save", Fingerprint = new string('A', 64),
                Roster = "101,102", Checksum = 17, Time = 29, Metadata = new byte[] { 1, 2 } };
            var copy = MessagePack.MessagePackSerializer.Deserialize<APIShared.SavegameLoadPacket>(
                MessagePack.MessagePackSerializer.Serialize(packet));
            Assert(copy.Lobby == packet.Lobby && copy.Token == packet.Token && copy.SaveName == packet.SaveName &&
                copy.Fingerprint == packet.Fingerprint && copy.Roster == packet.Roster &&
                copy.Checksum == packet.Checksum && copy.Time == packet.Time && copy.Metadata[1] == 2,
                "Save readiness packet lost identity or provenance.");
        }
        [TestMethod] public void CompiledSavePreparationPrecedesTheRootVanillaLoad()
        {
            using (var assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly(typeof(APIShared.SavegameModSettings).Assembly.Location))
            {
                var lifecycle = assembly.MainModule.GetType("APIShared.MissionLifecycleService");
                // Follow the actual producer and compiler-generated delegate bodies. This
                // guard follows extraction/renaming; it does not require the old flat Run body.
                System.Func<Mono.Cecil.MethodDefinition, System.Collections.Generic.IEnumerable<Mono.Cecil.MethodDefinition>> targets = method =>
                    System.Linq.Enumerable.Where(System.Linq.Enumerable.Select(
                        System.Linq.Enumerable.Where(method.Body.Instructions, instruction =>
                            instruction.OpCode.Code == Mono.Cecil.Cil.Code.Call || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Callvirt ||
                            instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldftn || instruction.OpCode.Code == Mono.Cecil.Cil.Code.Ldvirtftn),
                        instruction => instruction.Operand as Mono.Cecil.MethodReference), called => called != null &&
                            (called.DeclaringType.FullName == lifecycle.FullName ||
                             called.DeclaringType.FullName.StartsWith(lifecycle.FullName + "/", System.StringComparison.Ordinal)))
                    .Select(called => called.Resolve()).Where(called => called != null && called.HasBody);
                var actionAdapter = System.Linq.Enumerable.Single(lifecycle.Methods, method =>
                    method.Parameters.Count == 2 && method.Parameters[1].ParameterType.FullName == typeof(System.Action).FullName);
                System.Func<Mono.Cecil.MethodDefinition, bool> isProducer = method =>
                    method.GenericParameters.Count == 1 && method.Parameters.Count == 2 &&
                    method.Parameters[0].ParameterType.FullName == actionAdapter.Parameters[0].ParameterType.FullName &&
                    method.Parameters[1].ParameterType is Mono.Cecil.GenericInstanceType callback &&
                    callback.ElementType.FullName == typeof(System.Func<>).FullName && callback.GenericArguments.Count == 1 &&
                    callback.GenericArguments[0] is Mono.Cecil.GenericParameter argument &&
                    argument.Type == Mono.Cecil.GenericParameterType.Method && argument.Position == 0 &&
                    method.ReturnType is Mono.Cecil.GenericParameter result &&
                    result.Type == Mono.Cecil.GenericParameterType.Method && result.Position == 0;
                var run = System.Linq.Enumerable.Single(targets(actionAdapter), isProducer);
                var reachable = new System.Collections.Generic.HashSet<Mono.Cecil.MethodDefinition>();
                var pending = new System.Collections.Generic.Stack<Mono.Cecil.MethodDefinition>(); pending.Push(run);
                while (pending.Count != 0)
                {
                    var method = pending.Pop(); if (!reachable.Add(method)) continue;
                    foreach (var target in targets(method)) pending.Push(target);
                }
                Mono.Cecil.MethodDefinition preparedBody = null;
                int rootInvoke = -1;
                foreach (var method in reachable)
                for (int index = 0; index < method.Body.Instructions.Count; index++)
                {
                    var called = method.Body.Instructions[index].Operand as Mono.Cecil.MethodReference;
                    if (called?.Name != nameof(APIShared.MissionLifecycleService.InvokeNativeAdmission) ||
                        called.DeclaringType.FullName != lifecycle.FullName) continue;
                    Assert(preparedBody == null, "The lifecycle producer has more than one root admission call site.");
                    preparedBody = method; rootInvoke = index;
                }
                Assert(preparedBody != null, "The real lifecycle producer no longer reaches its admission boundary.");
                var code = preparedBody.Body.Instructions;
                var prepares = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(
                    System.Linq.Enumerable.Range(0, code.Count), index =>
                        code[index].Operand is Mono.Cecil.MethodReference called && called.Name == "ApplyBeforeLoad" &&
                        called.DeclaringType.FullName == typeof(APIShared.SavegameModSettings).FullName));
                Assert(prepares.Length == 1 && prepares[0] < rootInvoke,
                    "Root Vanilla load can precede prepared settings, or preparation is duplicated.");
                // Delegated producers retain the Operation + Func<T> -> T contract.
                // Other local helpers are graph edges, not independent producer routes.
                // Delegate references express possible paths, not proof of live hook execution.
                var routes = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Where(targets(run), isProducer));
                Assert(routes.Length != 0, "The root producer has no delegated lifecycle route.");
                foreach (var route in routes)
                {
                    var seen = new System.Collections.Generic.HashSet<Mono.Cecil.MethodDefinition>();
                    pending.Clear(); pending.Push(route);
                    while (pending.Count != 0)
                    {
                        var method = pending.Pop(); if (!seen.Add(method)) continue;
                        foreach (var target in targets(method)) pending.Push(target);
                    }
                    Assert(seen.Contains(preparedBody), "A delegated lifecycle route bypasses the prepared root boundary.");
                }
                var boundary = System.Linq.Enumerable.Single(lifecycle.Methods, m => m.Name ==
                    nameof(APIShared.MissionLifecycleService.InvokeNativeAdmission));
                Assert(System.Linq.Enumerable.Count(boundary.Body.Instructions, instruction =>
                    instruction.Operand is Mono.Cecil.MethodReference method && method.Name == "Invoke" &&
                    method.DeclaringType.FullName.StartsWith("System.Func")) == 1,
                    "The admission boundary must retain one Original invocation.");
                var settings = assembly.MainModule.GetType("APIShared.SavegameModSettings");
                var begin = System.Linq.Enumerable.Single(settings.Methods, m => m.Name == "BeginLoad");
                bool validates = false, readsDirectly = false, trustsArchiveFlag = false;
                foreach (var instruction in begin.Body.Instructions)
                {
                    var call = instruction.Operand as Mono.Cecil.MethodReference;
                    validates |= call?.Name == "ValidateStart";
                    readsDirectly |= call?.Name == "ReadRecord" || call?.Name == "ReadRecordForLoad";
                    trustsArchiveFlag |= call?.Name == "get_IsSaveFile";
                }
                Assert(validates && readsDirectly && !trustsArchiveFlag,
                    "Save entry trusts the misclassified archive callback or bypasses multiplayer admission.");
            }
        }
        // These cases execute the real private service without creating its hook owner.
        // Reflection selects production entry points; private names are not API contracts.
        private const System.Reflection.BindingFlags MissionInstance =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

        private static APIShared.MissionLifecycleService CreateMissionProducer() =>
            (APIShared.MissionLifecycleService)typeof(APIShared.MissionLifecycleService).GetConstructor(
                MissionInstance, null, new[] { typeof(BepInEx.Logging.ManualLogSource) }, null)
                .Invoke(new object[] { null });

        private static APIShared.MissionLifecycleState MissionProducerState(APIShared.MissionLifecycleService service) =>
            (APIShared.MissionLifecycleState)typeof(APIShared.MissionLifecycleService).GetFields(MissionInstance)
                .Single(field => field.FieldType == typeof(APIShared.MissionLifecycleState)).GetValue(service);

        private static APIShared.MissionNativeAdmission MissionProducerAdmission(APIShared.MissionLifecycleService service) =>
            (APIShared.MissionNativeAdmission)typeof(APIShared.MissionLifecycleService).GetFields(MissionInstance)
                .Single(field => field.FieldType == typeof(APIShared.MissionNativeAdmission)).GetValue(service);

        private static object NewMissionProducerOperation()
        {
            var type = typeof(APIShared.MissionLifecycleService).GetNestedType("Operation", System.Reflection.BindingFlags.NonPublic);
            object operation = System.Activator.CreateInstance(type, true);
            type.GetField("Kind", MissionInstance).SetValue(operation, APIShared.MissionStartKind.NewGame);
            // No save/map sidecar is active in these refusal inputs. Exercising their
            // real settings/platform cleanup requires a separately bounded fixture.
            type.GetField("Multiplayer", MissionInstance).SetValue(operation, false);
            type.GetField("RetirementEntry", MissionInstance).SetValue(operation, APIShared.MissionNativeOuterEntry.Unsupported);
            return operation;
        }

        private static object InvokeMissionProducer(System.Reflection.MethodInfo method, object service, params object[] args)
        {
            try { return method.Invoke(service, args); }
            catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
                throw;
            }
        }

        private static int RunMissionProducer(APIShared.MissionLifecycleService service, object operation, System.Func<int> original) =>
            (int)InvokeMissionProducer(typeof(APIShared.MissionLifecycleService).GetMethods(MissionInstance)
                .Single(method => method.Name == "Run" && method.IsGenericMethodDefinition).MakeGenericMethod(typeof(int)),
                service, operation, original);

        private sealed class MissionProducerOwner : APIShared.IMissionNativeRetirementOwner
        {
            internal bool Demand;
            internal int CloseCalls, RetireCalls;
            internal System.Exception CloseFailure;
            public bool RequiresBoundary => Demand;
            public void CloseAdmission(APIShared.MissionNativeAdmission.Attempt attempt)
            { CloseCalls++; if (CloseFailure != null) throw CloseFailure; }
            public bool TryRetire(APIShared.MissionNativeAdmission.Attempt attempt, object engine, out string reason)
            { RetireCalls++; reason = "Unexpected retirement in a pre-engine refusal fixture."; return false; }
        }

        private static APIShared.MissionContext ProducerContext() => new APIShared.MissionContext(0,
            APIShared.MissionStartKind.NewGame, default, "fixture.map", "fixture", false, 160, 1, 1);

        [TestMethod]
        [DoNotParallelize]
        public void ServiceStartAndEndObserversSeeTheExistingCurrentBoundary()
        {
            var service = CreateMissionProducer();
            var state = MissionProducerState(service);
            var published = typeof(APIShared.MissionLifecycleService).GetField("published",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            object previous = published.GetValue(null);
            try
            {
                published.SetValue(null, service);
                var binding = service.Bind("producer-fixture");
                int starts = 0, replays = 0, ends = 0;
                bool startedFacts = true, endedFacts = true;
                Assert(binding.TryRegisterObserver("early", notification => {
                    startedFacts &= APIShared.MissionLifecycleService.HasStartedMission &&
                        object.ReferenceEquals(binding.Current, notification.Context) && !notification.IsReplay;
                    starts++;
                }, notification => {
                    endedFacts &= !APIShared.MissionLifecycleService.HasStartedMission && binding.Current == null;
                    ends++;
                }, null, out _), "The actual early binding refused a valid managed observer.");
                var context = ProducerContext();
                long id = state.Begin(context);
                Assert(!APIShared.MissionLifecycleService.HasStartedMission && binding.Current == null && starts == 0,
                    "A pending mission was exposed as started.");
                state.Ready(id, context);
                Assert(starts == 1 && startedFacts, "Start observers did not see the actual ready Current.");
                Assert(binding.TryRegisterObserver("late", notification => {
                    startedFacts &= APIShared.MissionLifecycleService.HasStartedMission &&
                        object.ReferenceEquals(binding.Current, notification.Context) && notification.IsReplay;
                    replays++;
                }, notification => {
                    endedFacts &= !APIShared.MissionLifecycleService.HasStartedMission && binding.Current == null;
                    ends++;
                }, null, out _), "The actual binding refused a late observer.");
                Assert(replays == 1 && startedFacts, "Late registration lost the active Start replay.");
                state.End(APIShared.MissionEndReason.Unloaded);
                Assert(ends == 2 && endedFacts && !APIShared.MissionLifecycleService.HasStartedMission && binding.Current == null,
                    "End observers retained the retired Current.");
                // Observer exceptions are isolated by production. Assert recorded facts
                // here, outside callbacks, so a failed assertion cannot be swallowed.
            }
            finally { published.SetValue(null, previous); }
        }

        [TestMethod]
        public void RealDormantNestedRunPreservesOriginalAndExecutingOwnership()
        {
            foreach (bool attachedQuietOwner in new[] { false, true })
            {
                var service = CreateMissionProducer();
                var owner = new MissionProducerOwner();
                if (attachedQuietOwner) MissionProducerAdmission(service).Attach(owner);
                var executing = typeof(APIShared.MissionLifecycleService).GetField("executing", MissionInstance);
                object sentinel = NewMissionProducerOperation(); executing.SetValue(service, sentinel);
                int calls = 0;
                Assert(RunMissionProducer(service, NewMissionProducerOperation(), () => { calls++; return 17; }) == 17 && calls == 1,
                    "The real dormant nested producer changed Original's result or call count.");
                var failure = new System.ApplicationException("original identity");
                System.Exception caught = null;
                try { RunMissionProducer(service, NewMissionProducerOperation(), () => { calls++; throw failure; }); }
                catch (System.Exception error) { caught = error; }
                Assert(object.ReferenceEquals(caught, failure) && calls == 2 &&
                    object.ReferenceEquals(executing.GetValue(service), sentinel),
                    "Dormant nested forwarding replaced the exception or executing owner.");
                Assert(owner.CloseCalls == 0 && owner.RetireCalls == 0 && MissionProducerState(service).Context == null,
                    "Dormant nested forwarding entered retirement or began a lifecycle attempt.");
            }
        }

        [TestMethod]
        public void RealRequiredRunRefusesBeforeManagedStateOrOriginalAndPreservesFailures()
        {
            foreach (bool closeThrows in new[] { false, true })
            {
                var service = CreateMissionProducer();
                var state = MissionProducerState(service);
                var context = ProducerContext(); long id = state.Begin(context); state.Ready(id, context);
                var previous = state.Current;
                int notifications = 0, originals = 0;
                Assert(service.Bind("refusal-fixture").TryRegisterObserver("observer", _ => notifications++,
                    _ => notifications++, _ => notifications++, out _), "The managed refusal observer was not registered.");
                int before = notifications;
                var marker = new System.ApplicationException("close identity");
                var owner = new MissionProducerOwner { Demand = true, CloseFailure = closeThrows ? marker : null };
                MissionProducerAdmission(service).Attach(owner);
                System.Exception caught = null;
                try { RunMissionProducer(service, NewMissionProducerOperation(), () => { originals++; return 1; }); }
                catch (System.Exception error) { caught = error; }
                Assert(closeThrows ? object.ReferenceEquals(caught, marker) :
                    caught is System.InvalidOperationException rejected && rejected.InnerException is APIShared.MissionNativeAdmission.Abort,
                    "The real producer replaced an unrelated failure or hid its admission refusal.");
                Assert(owner.CloseCalls == 1 && owner.RetireCalls == 0 && originals == 0 &&
                    object.ReferenceEquals(state.Current, previous) && notifications == before,
                    "Pre-engine refusal entered managed initialization, retirement or Original.");
            }
        }

        [TestMethod]
        public void RealRequiredRunOnAnotherThreadRefusesBeforeAdmissionOrManagedState()
        {
            int primaryThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            var service = CreateMissionProducer();
            var state = MissionProducerState(service);
            var context = ProducerContext();
            long id = state.Begin(context); state.Ready(id, context);
            var previous = state.Current;
            var previousContext = state.Context;
            int notifications = 0, originals = 0, workerThread = 0;
            Assert(service.Bind("off-thread-fixture").TryRegisterObserver("observer", _ => notifications++,
                _ => notifications++, _ => notifications++, out _), "The isolated managed observer was not registered.");
            int before = notifications;
            // Even a broken thread flag must stop at this pure probe rather than
            // proceed to an engine lookup. The correct path never calls it.
            var closeMarker = new System.ApplicationException("unexpected off-thread close");
            var owner = new MissionProducerOwner { Demand = true, CloseFailure = closeMarker };
            var admission = MissionProducerAdmission(service);
            admission.Attach(owner);
            object operation = NewMissionProducerOperation();
            System.Exception caught = null;
            var worker = new System.Threading.Thread(() => {
                workerThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                try { RunMissionProducer(service, operation, () => { originals++; return 1; }); }
                catch (System.Exception error) { caught = error; }
            }) { IsBackground = true };
            worker.Start();
            Assert(worker.Join(2000), "The bounded real-service BCL probe did not finish.");
            Assert(System.Threading.Thread.CurrentThread.ManagedThreadId == primaryThread && workerThread != primaryThread,
                "The service's owning thread and actual worker were not distinct.");
            Assert(caught is System.InvalidOperationException rejected &&
                rejected.InnerException is APIShared.MissionNativeAdmission.Abort,
                "Actual Run did not preserve the admission refusal as its bound cause.");
            var refusal = (APIShared.MissionNativeAdmission.Abort)caught.InnerException;
            Assert(refusal.Attempt.ThreadId == workerThread && refusal.Attempt.Entry == APIShared.MissionNativeOuterEntry.Unsupported &&
                refusal.Attempt.Closed && !refusal.Attempt.Retired &&
                !refusal.Attempt.OriginalOpen && !refusal.Attempt.InnerOpen,
                "The refusal was not bound to the closed, unretired worker attempt.");
            Assert(owner.CloseCalls == 0 && owner.RetireCalls == 0 && originals == 0 &&
                object.ReferenceEquals(state.Current, previous) && object.ReferenceEquals(state.Context, previousContext) &&
                notifications == before && admission.LastCleanupFailure == null,
                "Off-thread Run reached admission, managed initialization, Original or changed the existing mission.");
        }

        [TestMethod]
        public void RealUntrackedNativeWrapperRefusesBeforeItsCoreOrOriginal()
        {
            var service = CreateMissionProducer();
            var state = MissionProducerState(service);
            var context = ProducerContext(); long id = state.Begin(context);
            state.BeginNativeLoad(id); state.CompleteNativeLoad(id, true); state.Ready(id, context);
            var previousContext = state.Context; var previousClock = state.NativeClock;
            Assert(previousClock != null, "The core-entry sentinel was not established.");
            var owner = new MissionProducerOwner { Demand = true };
            MissionProducerAdmission(service).Attach(owner);
            int originals = 0;
            System.Func<EngineInterface.LoadMapReturnData> original = () => { originals++; return default; };
            var capture = typeof(APIShared.MissionLifecycleService).GetMethod("CaptureNative", MissionInstance);
            System.Exception caught = null;
            try { InvokeMissionProducer(capture, service, APIShared.MissionNativeInnerEntry.EditorLoad, original); }
            catch (System.Exception error) { caught = error; }
            Assert(caught is System.InvalidOperationException && originals == 0 && owner.CloseCalls == 0 && owner.RetireCalls == 0 &&
                object.ReferenceEquals(state.Context, previousContext) && object.ReferenceEquals(state.NativeClock, previousClock),
                "An untracked native wrapper reached its actual core/Original or invalidated the prior native clock.");
        }

        [TestMethod]
        [DoNotParallelize]
        public void DialogCleanupCannotRevokeCommittedSaveAdmission()
        {
            var type = typeof(APIShared.SavegameLoadCoordinator);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var pending = type.GetField("pending", flags);
            var released = type.GetField("released", flags);
            var loading = type.GetField("loading", flags);
            var owner = type.GetField("owner", flags);
            object oldPending = pending.GetValue(null), oldReleased = released.GetValue(null), oldLoading = loading.GetValue(null);
            object oldOwner = owner.GetValue(null);
            try
            {
                var packet = new APIShared.SavegameLoadPacket { Token = "committed", SaveName = "save" };
                pending.SetValue(null, packet); released.SetValue(null, true); loading.SetValue(null, false);
                Assert(!APIShared.SavegameLoadCoordinator.CancelSelection() && ReferenceEquals(pending.GetValue(null), packet),
                    "Dialog cleanup revoked the committed host request.");
                released.SetValue(null, false); loading.SetValue(null, true);
                Assert(!APIShared.SavegameLoadCoordinator.CancelSelection() && ReferenceEquals(pending.GetValue(null), packet),
                    "Dialog cleanup revoked an executing save request.");
                owner.SetValue(null, null); loading.SetValue(null, false);
                Assert(APIShared.SavegameLoadCoordinator.CancelSelection() && pending.GetValue(null) == null,
                    "Uncommitted cancellation kept the old transaction available for a later acknowledgement.");
            }
            finally { pending.SetValue(null, oldPending); released.SetValue(null, oldReleased); loading.SetValue(null, oldLoading); owner.SetValue(null, oldOwner); }
        }
        // Regression: an unreadable APIShared record (damaged appended ZIP, newer schema) aborted the
        // whole save load. The save must load like a save without a record, with current settings.
        [TestMethod]
        [DoNotParallelize]
        public void UnreadableSaveRecordStillLoadsWithCurrentSettings()
        {
            var type = typeof(APIShared.SavegameModSettings);
            var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
            var fields = new[] { "loaded", "legacyChoice", "chosenCurrent",
                "activeCreatorRules", "activeTrailCustomizeAllowed" };
            var saved = new object[fields.Length];
            for (int i = 0; i < fields.Length; i++) saved[i] = type.GetField(fields[i], flags).GetValue(null);
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "apishared-badrecord-" + System.Guid.NewGuid().ToString("N").Substring(0, 8) + ".sav");
            try
            {
                // Save payload followed by an identifiable but damaged appended ZIP.
                System.IO.File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 0x50, 0x4B, 0x03, 0x04, 9, 9, 9,
                    0x50, 0x4B, 0x05, 0x06, 0, 0 });
                Assert(APIShared.SavegameModSettings.GetLoadChoiceState(path) == APIShared.SavegameLoadChoiceState.Invalid,
                    "Fixture is not an unreadable record; the regression would not be exercised.");
                // A non-null current choice keeps the test away from live host settings capture.
                type.GetField("chosenCurrent", flags).SetValue(null,
                    new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, byte[]>>());
                type.GetField("legacyChoice", flags).SetValue(null, false);
                type.GetMethod("BeginLoad", flags).Invoke(null, new object[] { path, false });
                bool restored = APIShared.SavegameModSettings.TryGetRestoredMode(out var kind, out _);
                Assert(!restored && kind == APIShared.GameModes.GameModeKind.Unknown,
                    "Unreadable save record blocked the load or locked its settings instead of loading as legacy.");
            }
            finally
            {
                for (int i = 0; i < fields.Length; i++) type.GetField(fields[i], flags).SetValue(null, saved[i]);
                try { System.IO.File.Delete(path); } catch { }
            }
        }
        [TestMethod] public void MarkedSelectionHarmonyContract() => MarkedSelectionHarmonyTests.Run(Assert);
    }
}

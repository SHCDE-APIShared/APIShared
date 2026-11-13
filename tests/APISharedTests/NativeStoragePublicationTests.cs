using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;

namespace APISharedTests
{
    // Exercise the production storage installation, not a parallel readiness model. The real
    // HookTransaction uses a managed fixture backend: Enable represents a patch becoming visible
    // by entering that hook's actual callback immediately. No executable memory or game code runs.
    [TestClass, DoNotParallelize]
    public sealed class NativeStoragePublicationTests
    {
        private const ulong Target = 0x100000;
        private const int OriginalResult = 7;
        private delegate int ProbeCall();

        [TestMethod]
        [DataRow("commit")]
        [DataRow("prepared")]
        [DataRow("prevalidation")]
        public void FailureBeforePublicationCleansOnlyUnpublishedResources(string stage)
        {
            var fixture = new Fixture();
            if (stage == "commit") fixture.Backend.FailingPreparation = 2;
            if (stage == "prepared") fixture.Prepared = false;
            if (stage == "prevalidation") fixture.FailingStage = stage;
            Assert.IsNotNull(Failure(fixture.Install));
            Assert.IsFalse(fixture.Installation.Ready);
            Assert.IsFalse(fixture.Installation.Publication.HasStarted);
            Assert.IsFalse(fixture.Installation.RetainsCandidate);
            Assert.AreEqual(1, fixture.Deactivations);
            Assert.AreEqual(1, fixture.Cleanups);
            Assert.AreEqual(0, fixture.Backend.EnableCalls);
            Assert.AreEqual(0, fixture.Backend.CallbackCalls);
            Assert.IsFalse(fixture.ManagedApplied);
            Assert.IsTrue(fixture.Backend.Disposals > 0, "Prepared fixture hooks must be disposed before releasing unpublished ownership.");
            Assert.AreEqual(0, fixture.Backend.Failures.Count);
            int disposals = fixture.Backend.Disposals;
            // Candidate's outer catch also attempts cleanup after Installation's catch.
            fixture.Installation.RollbackUnpublished(fixture.Pending, () => fixture.Cleanups++);
            Assert.AreEqual(1, fixture.Cleanups);
            Assert.AreEqual(disposals, fixture.Backend.Disposals);
            Assert.IsNotNull(Failure(fixture.Install), "A failed candidate cannot be reused after its ownership was released.");
            Assert.AreEqual(0, fixture.Backend.EnableCalls);
            Assert.AreEqual(1, fixture.Cleanups);
        }

        [TestMethod]
        public void UnpublishedBackendDisposalFailureStillReleasesCandidateResources()
        {
            var fixture = new Fixture { FailingStage = "prevalidation" };
            fixture.Backend.ThrowOnDispose = true;
            Assert.IsNotNull(Failure(fixture.Install));
            Assert.IsTrue(fixture.Backend.ThrowingDisposals > 0, "The backend must reach the failing unpublished disposal boundary.");
            Assert.IsFalse(fixture.Installation.Ready);
            Assert.IsFalse(fixture.Installation.Publication.HasStarted);
            Assert.IsFalse(fixture.Installation.RetainsCandidate);
            Assert.AreEqual(1, fixture.Deactivations);
            Assert.AreEqual(1, fixture.Cleanups, "Other private resources and the site claim must be released even when transaction disposal throws.");
            Assert.AreEqual(0, fixture.Backend.EnableCalls);
            int disposals = fixture.Backend.Disposals;
            fixture.Installation.RollbackUnpublished(fixture.Pending, () => fixture.Cleanups++);
            Assert.AreEqual(disposals, fixture.Backend.Disposals);
            Assert.AreEqual(1, fixture.Cleanups);
        }

        [TestMethod]
        [DataRow(0, false)]
        [DataRow(0, true)]
        [DataRow(1, false)]
        [DataRow(1, true)]
        [DataRow(2, false)]
        [DataRow(2, true)]
        [DataRow(3, false)]
        [DataRow(3, true)]
        public void EveryEnableFailureRetainsOriginalsAndKeepsTheMixedCohortDormant(int failing, bool afterWrite)
        {
            var fixture = new Fixture();
            fixture.Backend.FailingEnable = failing;
            fixture.Backend.ThrowAfterWrite = afterWrite;
            Assert.IsNotNull(Failure(fixture.Install));
            AssertFailedPublished(fixture);
            Assert.AreEqual(failing + 1, fixture.Backend.EnableCalls);
            Assert.AreEqual(failing + (afterWrite ? 1 : 0), fixture.Backend.CallbackCalls);
            Assert.IsFalse(fixture.ManagedApplied);
            for (int i = 0; i < fixture.Handles.Length; i++)
                Assert.AreEqual(i < failing || i == failing && afterWrite, fixture.Handles[i].IsInstalled, "Simulated native hook " + i);
            InvokeEveryOriginal(fixture, false);
            fixture.Installation.RollbackUnpublished(fixture.Pending, () => fixture.Cleanups++);
            Assert.IsNotNull(Failure(fixture.Install), "A potentially published candidate must never retry its patch writes.");
            Assert.AreEqual(failing + 1, fixture.Backend.EnableCalls);
            Assert.AreEqual(0, fixture.Cleanups + fixture.Backend.Disables + fixture.Backend.Disposals);
        }

        [TestMethod]
        [DataRow("postvalidation")]
        [DataRow("managed-throw")]
        [DataRow("managed-false")]
        public void NativeOrManagedCompletionFailureRetainsThePublishedCohort(string stage)
        {
            var fixture = new Fixture { FailingStage = stage };
            Exception error = Failure(fixture.Install);
            Assert.IsNotNull(error);
            if (stage != "managed-false") Assert.AreSame(fixture.ExpectedFailure, error);
            AssertFailedPublished(fixture);
            Assert.AreEqual(4, fixture.Backend.EnableCalls);
            Assert.AreEqual(4, fixture.Backend.CallbackCalls);
            Assert.IsTrue(fixture.Handles.All(handle => handle.IsInstalled));
            Assert.AreEqual(stage != "postvalidation", fixture.ManagedApplied,
                "A managed Apply that throws after its write is still potentially published and must remain rooted.");
            InvokeEveryOriginal(fixture, false);
        }

        [TestMethod]
        public void ReadinessFollowsAllNativeWritesValidationAndTheManagedAppliedCheck()
        {
            var fixture = new Fixture();
            fixture.Install();
            Assert.IsTrue(fixture.Installation.Ready);
            Assert.IsTrue(fixture.Installation.Publication.IsReady);
            Assert.IsTrue(fixture.Installation.RetainsCandidate);
            Assert.AreEqual(4, fixture.Backend.EnableCalls);
            Assert.AreEqual(4, fixture.Backend.CallbackCalls);
            Assert.IsTrue(fixture.Readiness.All(value => !value), "Callbacks at patch time must remain dormant.");
            CollectionAssert.AreEqual(new[] { "prepared", "native-before", "native-after", "managed-apply", "managed-check" }, fixture.Stages.ToArray());
            Assert.AreEqual(0, fixture.Deactivations + fixture.Cleanups + fixture.Backend.Disables + fixture.Backend.Disposals);
            Assert.AreEqual(0, fixture.Backend.Failures.Count);
            InvokeEveryOriginal(fixture, true);
        }

        [TestMethod]
        public void NativeCapacityPolicyWaitsForAKnownModeAndTheExactPublishedWorld()
        {
            var world = new object();
            Assert.IsFalse(NativeStorageNativeRuntime.CanActivateWorld(false, world, world));
            Assert.IsFalse(NativeStorageNativeRuntime.CanActivateWorld(true, null, null));
            Assert.IsFalse(NativeStorageNativeRuntime.CanActivateWorld(true, world, null));
            Assert.IsFalse(NativeStorageNativeRuntime.CanActivateWorld(true, null, world));
            Assert.IsFalse(NativeStorageNativeRuntime.CanActivateWorld(true, new object(), world));
            Assert.IsTrue(NativeStorageNativeRuntime.CanActivateWorld(true, world, world));
        }

        [TestMethod]
        public void FailedMixedPublicationKeepsItsActualCallbackOwnerAndTransactionAcrossCollection()
        {
            WeakReference[] references = PublishedRoots();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Assert.IsTrue(references.All(reference => reference.IsAlive), "A potentially live callback owner or transaction lost its process root.");
            var fixture = (Fixture)references[0].Target;
            AssertFailedPublished(fixture);
            InvokeEveryOriginal(fixture, false);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference[] PublishedRoots()
        {
            var fixture = new Fixture { FailingStage = "managed-throw" };
            Assert.IsNotNull(Failure(fixture.Install));
            return new[] { new WeakReference(fixture), new WeakReference(fixture.Installation),
                new WeakReference(fixture.Pending), new WeakReference(fixture.Backend),
                new WeakReference(fixture.Handles[3]) };
        }

        private static void AssertFailedPublished(Fixture fixture)
        {
            Assert.IsFalse(fixture.Installation.Ready);
            Assert.IsTrue(fixture.Installation.Publication.HasStarted);
            Assert.IsFalse(fixture.Installation.Publication.IsReady);
            Assert.IsTrue(fixture.Installation.RetainsCandidate);
            Assert.AreEqual(1, fixture.Deactivations);
            Assert.AreEqual(0, fixture.Cleanups, "Publication may have written a patch even when its first Enable throws.");
            Assert.AreEqual(0, fixture.Backend.Disables + fixture.Backend.Disposals);
            Assert.IsTrue(fixture.Readiness.All(value => !value));
            Assert.AreEqual(0, fixture.Backend.Failures.Count, "A callback during publication failed to reach its already-attached Original.");
        }
        private static void InvokeEveryOriginal(Fixture fixture, bool ready)
        {
            foreach (ProbeCall callback in fixture.Callbacks) Assert.AreEqual(OriginalResult, callback());
            Assert.IsTrue(fixture.Readiness.Skip(fixture.Readiness.Count - 4).All(value => value == ready));
        }
        private static Exception Failure(Action action)
        { try { action(); return null; } catch (Exception error) { return error; } }

        private sealed class Fixture
        {
            internal readonly NativeStorageNativeRuntime.Installation Installation = new NativeStorageNativeRuntime.Installation();
            internal readonly FixtureBackend Backend = new FixtureBackend();
            internal readonly DetourHandle<ProbeCall>[] Handles = Enumerable.Range(0, 4).Select(_ => new DetourHandle<ProbeCall>()).ToArray();
            internal readonly List<ProbeCall> Callbacks = new List<ProbeCall>();
            internal readonly List<bool> Readiness = new List<bool>();
            internal readonly List<string> Stages = new List<string>();
            internal readonly InvalidOperationException ExpectedFailure = new InvalidOperationException("Storage fixture boundary failed.");
            internal readonly HookTransaction Pending;
            internal bool Prepared = true, ManagedApplied;
            internal string FailingStage;
            internal int Deactivations, Cleanups;
            internal Fixture()
            {
                Pending = new HookTransaction(ScanRegion.FromMemory(new byte[256], Target, "Native storage publication fixture"), null,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow,
                        OwnsHooks = true, InstallOnCommit = false, Backend = Backend });
                // Three entry detours and a fourth synthetic boundary stand in for the mixed
                // native cohort. Actual inline encoding has its own emitter/disassembly tests.
                for (int i = 0; i < Handles.Length; i++)
                {
                    int index = i;
                    ProbeCall callback = () => { Readiness.Add(Installation.Ready); return Handles[index].Original(); };
                    Callbacks.Add(callback);
                    Pending.AddDetour(Handles[i], HookTarget.FromAddress(Target + (ulong)i * 32), callback, "storage-" + i);
                }
            }
            internal void Install() => Installation.Install(Pending,
                () => { Stages.Add("prepared"); AssertDormant(); return Prepared && Handles.All(handle => handle.Success); },
                installed => {
                    Stages.Add(installed ? "native-after" : "native-before"); AssertDormant();
                    Assert.AreEqual(installed ? 4 : 0, Backend.EnableCalls);
                    Assert.AreEqual(installed, Handles.All(handle => handle.IsInstalled));
                    if (FailingStage == (installed ? "postvalidation" : "prevalidation")) throw ExpectedFailure;
                },
                () => {
                    Stages.Add("managed-apply"); AssertDormant(); Assert.AreEqual(4, Backend.CallbackCalls);
                    ManagedApplied = true;
                    if (FailingStage == "managed-throw") throw ExpectedFailure;
                },
                () => { Stages.Add("managed-check"); AssertDormant(); return ManagedApplied && FailingStage != "managed-false"; },
                this,
                () => { Assert.IsFalse(Installation.Ready); Deactivations++; },
                () => { Assert.IsFalse(Installation.Publication.HasStarted); Cleanups++; });
            private void AssertDormant()
            { Assert.IsFalse(Installation.Ready); Assert.IsFalse(Installation.Publication.IsReady); }
        }

        // Minimal original fixture backend, matching the repository's issue-28 fixture contract.
        // All behavior is managed; the fabricated target addresses are never dereferenced.
        private sealed class FixtureBackend : IHookBackend
        {
            internal readonly List<Exception> Failures = new List<Exception>();
            internal int FailingPreparation = -1, FailingEnable = -1;
            internal bool ThrowAfterWrite, ThrowOnDispose;
            internal int Created, EnableCalls, CallbackCalls, Disables, Disposals, ThrowingDisposals;
            public string Name => "Native storage managed publication fixture";
            public IDetour<TFunction> CreateDetour<TFunction>(in DetourRequest<TFunction> request) where TFunction : Delegate
            {
                int index = Created++;
                if (index == FailingPreparation) throw new InvalidOperationException("Fixture preparation failed.");
                return new FixtureDetour<TFunction>(this, index, request.Name, request.TargetAddress, request.Callback);
            }
            internal void Call(Delegate callback)
            {
                CallbackCalls++;
                try { Assert.AreEqual(OriginalResult, (int)callback.DynamicInvoke()); }
                catch (TargetInvocationException error) { Failures.Add(error.InnerException); }
                catch (Exception error) { Failures.Add(error); }
            }
        }
        private sealed class FixtureDetour<TFunction> : IDetour<TFunction> where TFunction : Delegate
        {
            private readonly FixtureBackend backend;
            private readonly int index;
            private readonly TFunction callback;
            internal FixtureDetour(FixtureBackend backend, int index, string name, ulong target, TFunction callback)
            {
                this.backend = backend; this.index = index; this.callback = callback;
                Name = name; TargetAddress = target; State = HookState.Prepared;
                Original = (TFunction)(Delegate)(ProbeCall)(() => OriginalResult);
            }
            public string Name { get; }
            public ulong TargetAddress { get; }
            public HookState State { get; private set; }
            public bool IsInstalled => State == HookState.Installed;
            public IntPtr TrampolineAddress => IntPtr.Zero;
            public TFunction Original { get; }
            public void Enable()
            {
                backend.EnableCalls++;
                if (index == backend.FailingEnable && !backend.ThrowAfterWrite) throw new InvalidOperationException("Fixture failed before native write.");
                State = HookState.Installed; backend.Call(callback);
                if (index == backend.FailingEnable) throw new InvalidOperationException("Fixture failed after native write.");
            }
            public void Disable() { backend.Disables++; if (IsInstalled) State = HookState.Disabled; }
            public void Dispose()
            {
                backend.Disposals++; State = HookState.Disposed;
                if (backend.ThrowOnDispose)
                { backend.ThrowingDisposals++; throw new InvalidOperationException("Fixture disposal failed."); }
            }
        }
    }
}

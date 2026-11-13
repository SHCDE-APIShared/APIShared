using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace APISharedTests
{
    // The production admission class is supplied by the APIShared ProjectReference.
    // OwnerProbe is an interface boundary recorder, not a stat owner/retirement model.
    // A BCL monitor and an admitted-thread flag do not qualify the real engine or Unity thread.
    [TestClass]
    [TestCategory("MissionLifecycle")]
    public sealed class MissionNativeAdmissionTests
    {
        private sealed class OwnerProbe : IMissionNativeRetirementOwner
        {
            internal bool Enabled = true, Outstanding, AdmissionClosed, RetireResult = true;
            internal int DemandReads, Closes, Retirements, RetirementThread;
            internal bool CloseHeldEngine, RetirementHeldEngine;
            internal object ExpectedEngine, ReceivedEngine;
            internal MissionNativeAdmission.Attempt LastAttempt;
            internal MissionNativeAdmission.Attempt CloseAttempt, RetirementAttempt;
            internal Exception CloseFailure, RetirementFailure;
            internal List<string> Order;
            public bool RequiresBoundary { get { DemandReads++; return Enabled || Outstanding; } }
            public void CloseAdmission(MissionNativeAdmission.Attempt attempt)
            {
                Closes++; LastAttempt = CloseAttempt = attempt; AdmissionClosed = true;
                CloseHeldEngine = ExpectedEngine != null && Monitor.IsEntered(ExpectedEngine);
                Order?.Add("close");
                if (CloseFailure != null) throw CloseFailure;
            }
            public bool TryRetire(MissionNativeAdmission.Attempt attempt, object engine, out string reason)
            {
                Retirements++; LastAttempt = RetirementAttempt = attempt; ReceivedEngine = engine;
                RetirementThread = Thread.CurrentThread.ManagedThreadId;
                RetirementHeldEngine = Monitor.IsEntered(engine);
                Order?.Add("retire");
                reason = "recorded retirement refusal";
                if (RetirementFailure != null) throw RetirementFailure;
                return RetireResult;
            }
        }

        private static MissionNativeAdmission Attached(OwnerProbe owner)
        {
            var admission = new MissionNativeAdmission();
            admission.Attach(owner);
            return admission;
        }

        private static MissionNativeAdmission.Abort BoundAbort(InvalidOperationException failure)
        {
            Assert.IsTrue(failure.InnerException is MissionNativeAdmission.Abort,
                "An owned refusal must unwind rather than manufacture a native success result.");
            return (MissionNativeAdmission.Abort)failure.InnerException;
        }

        [TestMethod]
        public void DormantAdmissionPreservesResultWithoutEngineOwnerOrCleanupWork()
        {
            var admission = new MissionNativeAdmission();
            object expected = new object();
            int calls = 0, engines = 0, cleanups = 0;
            object result = admission.Execute(MissionNativeOuterEntry.Unsupported, false,
                () => { engines++; throw new InvalidOperationException("Dormant engine lookup."); },
                () => { calls++; return expected; }, () => cleanups++);
            Assert.AreSame(expected, result);
            Assert.AreEqual(1, calls); Assert.AreEqual(0, engines); Assert.AreEqual(0, cleanups);
            Assert.IsFalse(admission.Required);
            using (admission.EnterInner(MissionNativeInnerEntry.MapLoad)) { }
            admission.RequireContinuingInner();
            bool normal = true;
            admission.CompleteOriginal(ref normal);
            Assert.IsTrue(normal);
        }

        [TestMethod]
        public void AttachedDormantOwnerOnlySuppliesItsDemandRead()
        {
            var owner = new OwnerProbe { Enabled = false };
            var admission = Attached(owner);
            int engines = 0, cleanups = 0, originals = 0;
            Assert.AreEqual(29, admission.Execute(MissionNativeOuterEntry.MultiplayerSave, false,
                () => { engines++; return null; }, () => { originals++; return 29; }, () => cleanups++));
            Assert.IsTrue(owner.DemandReads > 0);
            Assert.AreEqual(0, owner.Closes); Assert.AreEqual(0, owner.Retirements);
            Assert.AreEqual(0, engines); Assert.AreEqual(0, cleanups); Assert.AreEqual(1, originals);
        }

        [TestMethod]
        public void AdmissionClosesBeforeLogicalWorkRetiresUnderEngineAndRunsOriginalUnlocked()
        {
            object engine = new object();
            var order = new List<string>();
            var owner = new OwnerProbe { ExpectedEngine = engine, Order = order };
            var admission = Attached(owner);
            int cleanups = 0;
            int result = admission.Execute(MissionNativeOuterEntry.EditorCreate, true,
                () => { order.Add("engine"); return engine; }, () => {
                    order.Add("logical");
                    Assert.IsTrue(owner.AdmissionClosed && owner.LastAttempt.Retired);
                    Assert.IsFalse(owner.LastAttempt.Closed);
                    Assert.IsFalse(Monitor.IsEntered(engine));
                    using (admission.OpenOriginal())
                    using (admission.EnterInner(MissionNativeInnerEntry.EditorCreate)) order.Add("original");
                    return 37;
                }, () => cleanups++);
            Assert.AreEqual(37, result);
            Assert.IsTrue(order.SequenceEqual(new[] { "close", "engine", "retire", "logical", "original" }));
            Assert.AreEqual(1, owner.Closes); Assert.AreEqual(1, owner.Retirements);
            Assert.AreSame(owner.CloseAttempt, owner.RetirementAttempt);
            Assert.AreSame(engine, owner.ReceivedEngine);
            Assert.IsFalse(owner.CloseHeldEngine); Assert.IsTrue(owner.RetirementHeldEngine);
            Assert.AreEqual(Thread.CurrentThread.ManagedThreadId, owner.RetirementThread);
            Assert.AreEqual(0, cleanups);
            Assert.IsTrue(owner.AdmissionClosed && owner.LastAttempt.Closed);
        }

        [TestMethod]
        public void UnqualifiedOuterEntriesOrRetirementRefusalsAbandonOnceBeforeOriginal()
        {
            var cases = new[] {
                (Entry: MissionNativeOuterEntry.Unsupported, Unity: true, NullEngine: false, Retire: true, Close: 1, Engines: 0, Retirements: 0),
                (Entry: (MissionNativeOuterEntry)999, Unity: true, NullEngine: false, Retire: true, Close: 1, Engines: 0, Retirements: 0),
                (Entry: MissionNativeOuterEntry.MultiplayerStart, Unity: true, NullEngine: false, Retire: true, Close: 1, Engines: 0, Retirements: 0),
                (Entry: MissionNativeOuterEntry.MultiplayerSave, Unity: true, NullEngine: false, Retire: true, Close: 1, Engines: 0, Retirements: 0),
                (Entry: MissionNativeOuterEntry.EditorCreate, Unity: false, NullEngine: false, Retire: true, Close: 0, Engines: 0, Retirements: 0),
                (Entry: MissionNativeOuterEntry.EditorCreate, Unity: true, NullEngine: true, Retire: true, Close: 1, Engines: 1, Retirements: 0),
                (Entry: MissionNativeOuterEntry.EditorCreate, Unity: true, NullEngine: false, Retire: false, Close: 1, Engines: 1, Retirements: 1)
            };
            foreach (var item in cases)
            {
                object engine = new object();
                var owner = new OwnerProbe { ExpectedEngine = engine, RetireResult = item.Retire };
                var admission = Attached(owner);
                int engines = 0, originals = 0, cleanups = 0;
                bool cleanupHeldEngine = false;
                var error = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(item.Entry, item.Unity,
                    () => { engines++; return item.NullEngine ? null : engine; },
                    () => { originals++; return 7; }, () => { cleanupHeldEngine = Monitor.IsEntered(engine); cleanups++; }));
                MissionNativeAdmission.Abort refusal = BoundAbort(error);
                Assert.IsTrue(refusal.Attempt.Closed); Assert.IsFalse(refusal.Attempt.Retired);
                Assert.AreEqual(item.Close, owner.Closes, item.Entry.ToString());
                Assert.AreEqual(item.Engines, engines); Assert.AreEqual(item.Retirements, owner.Retirements);
                Assert.AreEqual(0, originals); Assert.AreEqual(1, cleanups);
                Assert.IsFalse(cleanupHeldEngine); Assert.IsNull(admission.LastCleanupFailure);
            }
        }

        [TestMethod]
        public void UnrelatedPreEntryExceptionsRetainIdentityAndReleaseEarlyContextOnce()
        {
            foreach (string stage in new[] { "close", "engine", "retire" })
            {
                object engine = new object();
                var failure = new InvalidOperationException(stage);
                var owner = new OwnerProbe { CloseFailure = stage == "close" ? failure : null,
                    RetirementFailure = stage == "retire" ? failure : null };
                var admission = Attached(owner);
                int originals = 0, cleanups = 0;
                var seen = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(MissionNativeOuterEntry.EditorLoad, true,
                    () => { if (stage == "engine") throw failure; return engine; },
                    () => { originals++; return 3; }, () => cleanups++));
                Assert.AreSame(failure, seen); Assert.AreEqual(0, originals); Assert.AreEqual(1, cleanups);
                Assert.IsTrue(owner.AdmissionClosed && owner.LastAttempt.Closed);
                Assert.IsFalse(Monitor.IsEntered(engine));
            }
        }

        [TestMethod]
        public void CleanupAndReporterFailureCannotReplaceThePrimaryAdmissionFailure()
        {
            var primary = new InvalidOperationException("retirement failed");
            var cleanup = new ApplicationException("cleanup failed");
            var owner = new OwnerProbe { RetirementFailure = primary };
            var admission = Attached(owner);
            object engine = new object();
            int originals = 0, cleanups = 0, reports = 0;
            Exception reported = null;
            var seen = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(MissionNativeOuterEntry.EditorCreate, true,
                () => engine, () => { originals++; return 3; }, () => { cleanups++; throw cleanup; },
                error => { reports++; reported = error; throw new Exception("report failed"); }));
            Assert.AreSame(primary, seen); Assert.AreSame(cleanup, admission.LastCleanupFailure);
            Assert.AreSame(cleanup, reported);
            Assert.AreEqual(0, originals); Assert.AreEqual(1, cleanups); Assert.AreEqual(1, reports);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void EnteredOriginalExceptionIsPreservedWithoutDuplicatingOuterCleanup()
        {
            var failure = new ApplicationException("original failed");
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int cleanups = 0, originals = 0;
            var seen = Assert.ThrowsExactly<ApplicationException>(() => admission.Execute<int>(MissionNativeOuterEntry.SaveLoad, true,
                () => new object(), () => {
                    originals++;
                    using (admission.OpenOriginal())
                    using (admission.EnterInner(MissionNativeInnerEntry.SaveLoad)) throw failure;
                },
                () => cleanups++));
            Assert.AreSame(failure, seen); Assert.AreEqual(1, originals); Assert.AreEqual(0, cleanups);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void InnerMismatchUnwindsOnlyItsRetiredOuterAttempt()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int originals = 0, afterInner = 0, cleanups = 0;
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(MissionNativeOuterEntry.EditorCreate, true,
                () => new object(), () => {
                    originals++;
                    using (admission.OpenOriginal())
                    using (admission.EnterInner(MissionNativeInnerEntry.SaveLoad)) afterInner++;
                    return 3;
                }, () => cleanups++));
            var abort = BoundAbort(error);
            Assert.AreSame(owner.LastAttempt, abort.Attempt);
            Assert.AreEqual(1, originals); Assert.AreEqual(0, afterInner); Assert.AreEqual(0, cleanups);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void ForeignAttemptAbortIsNeverSwallowedOrRebound()
        {
            foreach (bool beforeOriginal in new[] { true, false })
            {
                var foreignAttempt = new MissionNativeAdmission.Attempt(MissionNativeOuterEntry.SaveLoad);
                var foreign = new MissionNativeAdmission.Abort(foreignAttempt, "foreign attempt");
                var owner = new OwnerProbe { RetirementFailure = beforeOriginal ? foreign : null };
                var admission = Attached(owner);
                int originals = 0, cleanups = 0;
                var seen = Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => admission.Execute<int>(
                    MissionNativeOuterEntry.EditorLoad, true, () => new object(),
                    () => { originals++; using (admission.OpenOriginal()) throw foreign; }, () => cleanups++));
                Assert.AreSame(foreign, seen); Assert.AreSame(foreignAttempt, seen.Attempt);
                Assert.IsFalse(foreignAttempt.Closed); Assert.IsTrue(owner.LastAttempt.Closed);
                Assert.AreEqual(beforeOriginal ? 0 : 1, originals);
                Assert.AreEqual(beforeOriginal ? 1 : 0, cleanups);
            }
        }

        [TestMethod]
        public void ReentrantStartCannotCreateASecondRetirementOrCleanupScope()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int originals = 0, nestedOriginals = 0, nestedEngines = 0, nestedCleanups = 0, earlyCleanups = 0;
            Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(MissionNativeOuterEntry.EditorCreate, true,
                () => new object(), () => {
                    originals++;
                    return admission.Execute(MissionNativeOuterEntry.EditorCreate, true,
                        () => { nestedEngines++; return new object(); },
                        () => { nestedOriginals++; return 7; }, () => nestedCleanups++);
                }, () => earlyCleanups++));
            Assert.AreEqual(1, originals); Assert.AreEqual(0, nestedOriginals); Assert.AreEqual(0, nestedEngines);
            Assert.AreEqual(0, nestedCleanups); Assert.AreEqual(0, earlyCleanups);
            Assert.AreEqual(1, owner.Closes); Assert.AreEqual(1, owner.Retirements);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void SupportedOuterEntriesAdmitOnlyTheirOrderedNativeStages()
        {
            var rows = new[] {
                (Outer: MissionNativeOuterEntry.EditorCreate, Sequence: new[] { MissionNativeInnerEntry.EditorCreate }),
                (Outer: MissionNativeOuterEntry.EditorLoad, Sequence: new[] { MissionNativeInnerEntry.EditorLoad }),
                (Outer: MissionNativeOuterEntry.SaveLoad, Sequence: new[] { MissionNativeInnerEntry.SaveLoad }),
                (Outer: MissionNativeOuterEntry.SkirmishStart, Sequence: new[] { MissionNativeInnerEntry.MultiplayerInit, MissionNativeInnerEntry.MapLoad })
            };
            foreach (var row in rows)
            {
                var owner = new OwnerProbe();
                var admission = Attached(owner);
                int cleanups = 0;
                Assert.AreEqual(73, admission.Execute(row.Outer, true, () => new object(), () => {
                    using (admission.OpenOriginal())
                        foreach (var stage in row.Sequence) using (admission.EnterInner(stage)) { }
                    return 73;
                }, () => cleanups++));
                Assert.AreEqual(1, owner.Retirements); Assert.AreEqual(0, cleanups);
                foreach (MissionNativeInnerEntry first in Enum.GetValues(typeof(MissionNativeInnerEntry)))
                {
                    if (first == row.Sequence[0]) continue;
                    owner = new OwnerProbe(); admission = Attached(owner);
                    var error = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(row.Outer, true,
                        () => new object(), () => {
                            using (admission.OpenOriginal())
                            using (admission.EnterInner(first)) { }
                            return 73;
                        }, null));
                    Assert.AreSame(owner.LastAttempt, BoundAbort(error).Attempt);
                }
            }
        }

        [TestMethod]
        public void AcceptedNativeStageCannotBeRetriedAfterFailureOrReordered()
        {
            var sequences = new[] {
                (Outer: MissionNativeOuterEntry.EditorCreate, Stages: new[] { MissionNativeInnerEntry.EditorCreate, MissionNativeInnerEntry.EditorCreate }),
                (Outer: MissionNativeOuterEntry.EditorLoad, Stages: new[] { MissionNativeInnerEntry.EditorLoad, MissionNativeInnerEntry.EditorLoad }),
                (Outer: MissionNativeOuterEntry.SaveLoad, Stages: new[] { MissionNativeInnerEntry.SaveLoad, MissionNativeInnerEntry.SaveLoad }),
                (Outer: MissionNativeOuterEntry.SkirmishStart, Stages: new[] { MissionNativeInnerEntry.MapLoad }),
                (Outer: MissionNativeOuterEntry.SkirmishStart, Stages: new[] { MissionNativeInnerEntry.MultiplayerInit, MissionNativeInnerEntry.MultiplayerInit }),
                (Outer: MissionNativeOuterEntry.SkirmishStart, Stages: new[] { MissionNativeInnerEntry.MultiplayerInit, MissionNativeInnerEntry.MapLoad, MissionNativeInnerEntry.MapLoad }),
                (Outer: MissionNativeOuterEntry.SkirmishStart, Stages: new[] { MissionNativeInnerEntry.MultiplayerInit, MissionNativeInnerEntry.MapLoad, MissionNativeInnerEntry.MultiplayerInit })
            };
            foreach (var item in sequences)
            {
                var owner = new OwnerProbe();
                var admission = Attached(owner);
                var error = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(item.Outer, true,
                    () => new object(), () => {
                        using (admission.OpenOriginal())
                            for (int i = 0; i < item.Stages.Length; i++)
                            {
                                // Unwinding a stage scope must not restore its consumed phase.
                                try { using (admission.EnterInner(item.Stages[i])) if (i == 0) throw new ApplicationException("stage work failed"); }
                                catch (ApplicationException) { }
                            }
                        return 7;
                    }, null));
                Assert.AreSame(owner.LastAttempt, BoundAbort(error).Attempt);
                Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
            }
        }

        [TestMethod]
        public void FailedRetirementStaysClosedAndOutstandingDebtSurvivesPolicyDisable()
        {
            var owner = new OwnerProbe { RetireResult = false, Outstanding = true };
            var admission = Attached(owner);
            int originals = 0, cleanups = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var error = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(MissionNativeOuterEntry.EditorCreate,
                    true, () => new object(), () => { originals++; return 7; }, () => cleanups++));
                Assert.IsTrue(BoundAbort(error).Attempt.Closed && owner.AdmissionClosed);
                owner.Enabled = false;
                Assert.IsTrue(admission.Required, "Disabling the policy must not erase the attached owner's debt demand.");
            }
            Assert.AreEqual(2, owner.Closes); Assert.AreEqual(2, owner.Retirements);
            Assert.AreEqual(0, originals); Assert.AreEqual(2, cleanups);
            Assert.ThrowsExactly<InvalidOperationException>(() => {
                using (admission.EnterInner(MissionNativeInnerEntry.EditorCreate)) { }
            });
        }

        [TestMethod]
        public void CaughtPreOriginalRefusalCannotOpenOrSuccessfullyCompleteTheAttempt()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int nativeCalls = 0, continued = 0, cleanups = 0;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(
                MissionNativeOuterEntry.EditorCreate, true, () => new object(), () => {
                    var refusal = Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                        using (admission.EnterInner(MissionNativeInnerEntry.EditorCreate)) nativeCalls++;
                    });
                    Assert.AreSame(owner.LastAttempt, refusal.Attempt);
                    Assert.ThrowsExactly<InvalidOperationException>(() => { using (admission.OpenOriginal()) { } });
                    continued++; return 31;
                }, () => cleanups++));
            Assert.AreSame(owner.LastAttempt, BoundAbort(failure).Attempt);
            Assert.AreEqual(0, nativeCalls); Assert.AreEqual(1, continued); Assert.AreEqual(0, cleanups);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void CaughtNestedInnerRefusalCannotBorrowTheNextStageOrReturnSuccess()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int admittedStages = 0, nestedCalls = 0, continued = 0;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(
                MissionNativeOuterEntry.SkirmishStart, true, () => new object(), () => {
                    using (admission.OpenOriginal())
                    {
                        using (admission.EnterInner(MissionNativeInnerEntry.MultiplayerInit))
                        {
                            admittedStages++;
                            var refusal = Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                                using (admission.EnterInner(MissionNativeInnerEntry.MapLoad)) nestedCalls++;
                            });
                            Assert.AreSame(owner.LastAttempt, refusal.Attempt);
                        }
                        // Even after the enclosing stage disposes, the caught refusal stays sticky.
                        Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                            using (admission.EnterInner(MissionNativeInnerEntry.MapLoad)) nestedCalls++;
                        });
                    }
                    continued++; return 31;
                }, null));
            Assert.AreSame(owner.LastAttempt, BoundAbort(failure).Attempt);
            Assert.AreEqual(1, admittedStages); Assert.AreEqual(0, nestedCalls); Assert.AreEqual(1, continued);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void CaughtOutOfOrderInnerRefusalCannotResumeWithAnOtherwiseValidStage()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            int stages = 0, continued = 0;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(
                MissionNativeOuterEntry.SkirmishStart, true, () => new object(), () => {
                    using (admission.OpenOriginal())
                    {
                        Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                            using (admission.EnterInner(MissionNativeInnerEntry.MapLoad)) stages++;
                        });
                        Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                            using (admission.EnterInner(MissionNativeInnerEntry.MultiplayerInit)) stages++;
                        });
                    }
                    continued++; return 31;
                }, null));
            Assert.AreSame(owner.LastAttempt, BoundAbort(failure).Attempt);
            Assert.AreEqual(0, stages); Assert.AreEqual(1, continued);
            Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
        }

        [TestMethod]
        public void CaughtNestedRefusalStopsBeforeOrAfterTheNativeContinuation()
        {
            foreach (bool beforeNative in new[] { true, false })
            {
                var owner = new OwnerProbe();
                var admission = Attached(owner);
                int nativeCalls = 0, followingWork = 0;
                var failure = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(
                    MissionNativeOuterEntry.SkirmishStart, true, () => new object(), () => {
                        using (admission.OpenOriginal())
                        using (admission.EnterInner(MissionNativeInnerEntry.MultiplayerInit))
                        {
                            Action isolatedCallback = () => {
                                Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                                    using (admission.EnterInner(MissionNativeInnerEntry.MapLoad)) { }
                                });
                            };
                            if (beforeNative) isolatedCallback();
                            admission.RequireContinuingInner();
                            nativeCalls++; // Managed boundary counter, never a native function.
                            if (!beforeNative) isolatedCallback();
                            admission.RequireContinuingInner();
                            followingWork++;
                        }
                        return 31;
                    }, null));
                Assert.AreSame(owner.LastAttempt, BoundAbort(failure).Attempt);
                Assert.AreEqual(beforeNative ? 0 : 1, nativeCalls);
                Assert.AreEqual(0, followingWork);
                Assert.IsTrue(owner.LastAttempt.Closed && owner.AdmissionClosed);
            }
        }

        [TestMethod]
        public void CaughtInnerFailureMarksOriginalAbnormalBeforeLifecycleFinish()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            bool normal = true;
            int finishCalls = 0;
            var failure = Assert.ThrowsExactly<InvalidOperationException>(() => admission.Execute(
                MissionNativeOuterEntry.EditorCreate, true, () => new object(), () => {
                    using (admission.OpenOriginal())
                    using (admission.EnterInner(MissionNativeInnerEntry.EditorCreate))
                    {
                        Assert.ThrowsExactly<MissionNativeAdmission.Abort>(() => {
                            using (admission.EnterInner(MissionNativeInnerEntry.EditorCreate)) { }
                        });
                    }
                    admission.CompleteOriginal(ref normal);
                    finishCalls++; return 31;
                }, null));
            Assert.AreSame(owner.LastAttempt, BoundAbort(failure).Attempt);
            Assert.IsFalse(normal); Assert.AreEqual(0, finishCalls);
            Assert.IsTrue(owner.LastAttempt.Closed);
        }

        [TestMethod]
        public void CompletedObserverEntryIsRefusedWithoutPoisoningTheCompletedResult()
        {
            var owner = new OwnerProbe();
            var admission = Attached(owner);
            bool normal = true;
            int stages = 0, observerCalls = 0, cleanups = 0;
            int result = admission.Execute(MissionNativeOuterEntry.EditorLoad, true, () => new object(), () => {
                using (admission.OpenOriginal())
                using (admission.EnterInner(MissionNativeInnerEntry.EditorLoad))
                {
                    admission.RequireContinuingInner(); stages++;
                    admission.RequireContinuingInner();
                }
                admission.CompleteOriginal(ref normal);
                Assert.IsTrue(owner.LastAttempt.Completed);
                Assert.ThrowsExactly<InvalidOperationException>(() => {
                    using (admission.EnterInner(MissionNativeInnerEntry.EditorLoad)) observerCalls++;
                });
                Assert.ThrowsExactly<InvalidOperationException>(() => { using (admission.OpenOriginal()) { } });
                Assert.IsFalse(owner.LastAttempt.Failed);
                return 31;
            }, () => cleanups++);
            Assert.AreEqual(31, result); Assert.IsTrue(normal);
            Assert.AreEqual(1, stages); Assert.AreEqual(0, observerCalls); Assert.AreEqual(0, cleanups);
            Assert.IsFalse(owner.LastAttempt.Failed); Assert.IsTrue(owner.LastAttempt.Closed);
        }
    }
}

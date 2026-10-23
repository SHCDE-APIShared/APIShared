using APIShared;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    // Production routing with the native import/upload boundary replaced by a recorder.
    [TestClass]
    [TestCategory("MissionLifecycle")]
    public sealed class CoopAiStartImportTests
    {
        private sealed class Recorder : CoopAiStartImportService.IBackend
        {
            internal readonly List<string> Calls = new List<string>();
            public void UploadDefault(int lordType, int playerId, bool everySkirmishSet, bool everyHistoricalSet) =>
                Calls.Add("default:" + lordType + ":" + playerId + ":" + everySkirmishSet + ":" + everyHistoricalSet);
            public void ImportCandidate(int bankIndex, int candidateIndex, short[] data) =>
                Calls.Add("import:" + bankIndex + ":" + candidateIndex + ":" + data[0]);
            public void SetLordConfiguration(EngineInterface.AILordConfigTransferData configuration, int playerId) =>
                Calls.Add("aic:" + playerId + ":" + configuration.opponent_type);
        }

        private static EngineInterface.AILordConfigTransferData Aic(int marker) =>
            new EngineInterface.AILordConfigTransferData { opponent_type = marker };

        private static CoopAiStartImportService Service(Recorder recorder, params Func<CoopAiStartRequest, CoopAiStartContribution>[] providers)
        {
            var service = new CoopAiStartImportService(null, recorder);
            ICoopAiStartImportCapability capability = service.Bind("test.owner");
            for (int index = 0; index < providers.Length; index++)
                Assert.IsTrue(capability.TryRegisterProvider("provider-" + index, providers[index], out _));
            return service;
        }

        private static CoopAiStartContribution Plans(params CoopAiSlotPlan[] plans) => new CoopAiStartContribution(plans);

        [TestMethod]
        public void WithoutPlansVanillaUploadIsUnchanged()
        {
            var recorder = new Recorder();
            CoopAiStartImportService service = Service(recorder, _ => null);
            service.OnUpload(3, 2, false, false);
            service.BeginAttempt(1, 4);
            service.OnUpload(5, 3, true, false);
            service.EndAttempt();
            CollectionAssert.AreEqual(new[] { "default:3:2:False:False", "default:5:3:True:False" }, recorder.Calls);
        }

        [TestMethod]
        public void EverySourceImportsOnceAndSetsTheAicAfterTheCastles()
        {
            var recorder = new Recorder();
            CoopAiStartImportService service = Service(recorder, request => Plans(
                new CoopAiSlotPlan(2, 7, CoopAiCastleSource.Standard, null, Aic(70)),
                new CoopAiSlotPlan(3, 8, CoopAiCastleSource.Community, null, null),
                new CoopAiSlotPlan(4, 9, CoopAiCastleSource.Historical, null, Aic(90)),
                new CoopAiSlotPlan(5, 1, CoopAiCastleSource.Explicit,
                    new[] { new short[] { 11 }, new short[] { 12 }, new short[] { 13 } }, Aic(10))));
            service.BeginAttempt(2, 1);
            service.OnUpload(7, 2, false, false);
            service.OnUpload(8, 3, false, false);
            service.OnUpload(9, 4, false, false);
            service.OnUpload(1, 5, false, false);
            service.OnUpload(6, 6, false, false);
            service.EndAttempt();
            CollectionAssert.AreEqual(new[]
            {
                "default:7:2:False:False", "aic:2:70",
                "default:8:3:True:False",
                "default:9:4:False:True", "aic:4:90",
                "import:4:0:11", "import:4:1:12", "import:4:2:13", "aic:5:10",
                "default:6:6:False:False",
            }, recorder.Calls);
        }

        [TestMethod]
        public void IdenticalPlansMergeAndDifferentPlansAbort()
        {
            Func<CoopAiStartRequest, CoopAiStartContribution> first = _ => Plans(
                new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1, 2 } }, Aic(5)));
            var recorder = new Recorder();
            CoopAiStartImportService same = Service(recorder, first, first);
            same.BeginAttempt(1, 1);
            same.OnUpload(2, 3, false, false);
            CollectionAssert.AreEqual(new[] { "import:2:0:1", "aic:3:5" }, recorder.Calls);

            foreach (Func<CoopAiStartRequest, CoopAiStartContribution> other in new Func<CoopAiStartRequest, CoopAiStartContribution>[]
            {
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1, 3 } }, Aic(5))),
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1, 2 } }, Aic(6))),
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Standard, null, Aic(5))),
            })
            {
                var conflict = new Recorder();
                CoopAiStartImportService service = Service(conflict, first, other);
                service.BeginAttempt(1, 1);
                Assert.ThrowsExactly<InvalidOperationException>(() => service.OnUpload(4, 2, false, false));
                Assert.AreEqual(0, conflict.Calls.Count, "a conflicting attempt fell back to Vanilla castles");
            }
        }

        [TestMethod]
        public void IncompletePlansYieldToAMatchingCompleteListInAnyOrder()
        {
            Func<CoopAiStartRequest, CoopAiStartContribution> lobbyPrefix = _ => Plans(new CoopAiSlotPlan(3, 2,
                CoopAiCastleSource.Explicit, new[] { new short[] { 1 } }, Aic(5), candidateListComplete: false));
            Func<CoopAiStartRequest, CoopAiStartContribution> complete = _ => Plans(new CoopAiSlotPlan(3, 2,
                CoopAiCastleSource.Explicit, new[] { new short[] { 1 }, new short[] { 2 } }, Aic(5)));
            foreach (var order in new[] { new[] { lobbyPrefix, complete }, new[] { complete, lobbyPrefix } })
            {
                var recorder = new Recorder();
                CoopAiStartImportService service = Service(recorder, order);
                service.BeginAttempt(1, 1);
                service.OnUpload(2, 3, false, false);
                CollectionAssert.AreEqual(new[] { "import:2:0:1", "import:2:1:2", "aic:3:5" }, recorder.Calls);
            }

            var alone = new Recorder();
            CoopAiStartImportService onlyPrefix = Service(alone, lobbyPrefix, lobbyPrefix);
            onlyPrefix.BeginAttempt(1, 1);
            onlyPrefix.OnUpload(2, 3, false, false);
            CollectionAssert.AreEqual(new[] { "import:2:0:1", "aic:3:5" }, alone.Calls);

            var contradicting = new Recorder();
            CoopAiStartImportService mismatch = Service(contradicting, complete, _ => Plans(new CoopAiSlotPlan(3, 2,
                CoopAiCastleSource.Explicit, new[] { new short[] { 9 } }, Aic(5), candidateListComplete: false)));
            mismatch.BeginAttempt(1, 1);
            Assert.ThrowsExactly<InvalidOperationException>(() => mismatch.OnUpload(2, 3, false, false));
            Assert.AreEqual(0, contradicting.Calls.Count);
            Assert.IsTrue(new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Standard, null, null, false).CandidateListComplete);
        }

        [TestMethod]
        public void PartiallyConfirmedListYieldsToAFullyConfirmedListMatchingItsConfirmedPrefix()
        {
            // A packaged list compared with the lobby only through candidate 0, and a synchronized host list.
            Func<CoopAiStartRequest, CoopAiStartContribution> packaged = _ => Plans(new CoopAiSlotPlan(3, 2,
                CoopAiCastleSource.Explicit, new[] { new short[] { 1 }, new short[] { 2 }, new short[] { 3 } }, Aic(5), true, 1));
            Func<CoopAiStartRequest, CoopAiStartContribution> synchronized = _ => Plans(new CoopAiSlotPlan(3, 2,
                CoopAiCastleSource.Explicit, new[] { new short[] { 1 }, new short[] { 7 } }, Aic(5)));
            foreach (var order in new[] { new[] { packaged, synchronized }, new[] { synchronized, packaged } })
            {
                var recorder = new Recorder();
                CoopAiStartImportService service = Service(recorder, order);
                service.BeginAttempt(1, 1);
                service.OnUpload(2, 3, false, false);
                CollectionAssert.AreEqual(new[] { "import:2:0:1", "import:2:1:7", "aic:3:5" }, recorder.Calls);
            }

            // Alone, the packaged list is imported completely, as before.
            var alone = new Recorder();
            CoopAiStartImportService single = Service(alone, packaged);
            single.BeginAttempt(1, 1);
            single.OnUpload(2, 3, false, false);
            CollectionAssert.AreEqual(new[] { "import:2:0:1", "import:2:1:2", "import:2:2:3", "aic:3:5" }, alone.Calls);

            // A different confirmed candidate, or two differing partially confirmed lists, still contradict.
            foreach (var other in new Func<CoopAiStartRequest, CoopAiStartContribution>[] {
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 9 }, new short[] { 7 } }, Aic(5))),
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1 }, new short[] { 8 } }, Aic(5), true, 1)) })
            {
                var recorder = new Recorder();
                CoopAiStartImportService service = Service(recorder, packaged, other);
                service.BeginAttempt(1, 1);
                Assert.ThrowsExactly<InvalidOperationException>(() => service.OnUpload(2, 3, false, false));
                Assert.AreEqual(0, recorder.Calls.Count);
            }
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit,
                new[] { new short[] { 1 } }, null, true, 2));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit,
                new[] { new short[] { 1 } }, null, true, 0));
            Assert.AreEqual(1, new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1 } }, null).ConfirmedCandidateCount);
        }

        [TestMethod]
        public void PreviewCombinesPlansWithoutImportOrAttemptAndReportsConflicts()
        {
            var requests = new List<CoopAiStartRequest>();
            var recorder = new Recorder();
            CoopAiStartImportService service = Service(recorder,
                request => { requests.Add(request); return Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit,
                    new[] { new short[] { 1 }, new short[] { 2 } }, Aic(5), true, 1)); },
                request => request.IsPreview ? Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit,
                    new[] { new short[] { 1 }, new short[] { 4 } }, Aic(5))) : null);
            var preview = (ICoopAiStartPreviewCapability)service.Bind("test.consumer");
            Assert.IsTrue(preview.TryPreview(2, 5, out CoopAiStartPreview result, out _));
            Assert.IsTrue(result.Succeeded);
            Assert.IsTrue(requests.Single().IsPreview);
            Assert.AreEqual(2, requests.Single().CoopTrail);
            Assert.AreEqual(5, requests.Single().CoopMission);
            Assert.AreEqual(4, result.PlanFor(3).CopyCandidate(1)[0]);
            Assert.AreEqual("test.owner/provider-1", result.OwnerOf(3));
            Assert.IsNull(result.PlanFor(4));
            // Nothing was imported and no attempt remains: a later upload keeps Vanilla.
            service.OnUpload(2, 3, false, false);
            CollectionAssert.AreEqual(new[] { "default:2:3:False:False" }, recorder.Calls);

            CoopAiStartImportService conflicting = Service(new Recorder(),
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 1 } }, Aic(5))),
                _ => Plans(new CoopAiSlotPlan(3, 2, CoopAiCastleSource.Explicit, new[] { new short[] { 2 } }, Aic(5))));
            Assert.IsTrue(((ICoopAiStartPreviewCapability)conflicting.Bind("test.consumer")).TryPreview(1, 1, out result, out _));
            Assert.IsFalse(result.Succeeded);
            StringAssert.Contains(result.Failure, "contradicting plans");

            service.BeginAttempt(1, 1);
            Assert.IsFalse(preview.TryPreview(1, 1, out result, out _), "no preview inside a start attempt");
            Assert.IsNull(result);
            service.EndAttempt();
        }

        [TestMethod]
        public void ProviderFailuresAbortTheAttempt()
        {
            var failing = new Recorder();
            CoopAiStartImportService thrown = Service(failing, _ => throw new InvalidOperationException("not confirmed"));
            thrown.BeginAttempt(1, 1);
            Assert.ThrowsExactly<InvalidOperationException>(() => thrown.OnUpload(2, 2, false, false));
            Assert.AreEqual(0, failing.Calls.Count);
            Assert.ThrowsExactly<ArgumentException>(() => Plans(
                new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Standard, null, null),
                new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Standard, null, null)));
        }

        [TestMethod]
        public void ChangedLordTypesAndRepeatedUploadsAbortAndAttemptsDoNotLeak()
        {
            var recorder = new Recorder();
            CoopAiStartImportService service = Service(recorder,
                _ => Plans(new CoopAiSlotPlan(2, 3, CoopAiCastleSource.Standard, null, null)));
            service.BeginAttempt(1, 1);
            Assert.ThrowsExactly<InvalidOperationException>(() => service.OnUpload(4, 2, false, false));
            service.BeginAttempt(1, 1);
            service.OnUpload(3, 2, false, false);
            Assert.ThrowsExactly<InvalidOperationException>(() => service.OnUpload(3, 2, false, false));
            service.EndAttempt();
            service.OnUpload(4, 2, false, false);
            CollectionAssert.AreEqual(new[] { "default:3:2:False:False", "default:4:2:False:False" }, recorder.Calls);
        }

        [TestMethod]
        public void PlansAreImmutableAndValidated()
        {
            short[] candidate = { 1, 2 };
            var plan = new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Explicit, new[] { candidate }, null);
            candidate[0] = 9;
            plan.CopyCandidate(0)[1] = 9;
            CollectionAssert.AreEqual(new short[] { 1, 2 }, plan.CopyCandidate(0));
            Assert.IsFalse(plan.HasLordConfiguration);
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new CoopAiSlotPlan(1, 0, CoopAiCastleSource.Standard, null, null));
            Assert.ThrowsExactly<ArgumentException>(() => new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Explicit, null, null));
            Assert.ThrowsExactly<ArgumentException>(() => new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Standard, new[] { candidate }, null));
            Assert.ThrowsExactly<ArgumentException>(() => new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Explicit, new[] { new short[0] }, null));
            Assert.ThrowsExactly<ArgumentException>(() => new CoopAiSlotPlan(2, 0, CoopAiCastleSource.Explicit,
                Enumerable.Repeat(new short[] { 1 }, CoopAiSlotPlan.MaximumCandidates + 1).ToArray(), null));

            ICoopAiStartImportCapability capability = new CoopAiStartImportService(null, new Recorder()).Bind("owner");
            Assert.IsTrue(capability.TryRegisterProvider("id", _ => null, out _));
            Assert.IsFalse(capability.TryRegisterProvider("id", _ => null, out NativeCapabilityDiagnostic duplicate));
            Assert.AreEqual(NativeCapabilityState.ValidationFailed, duplicate.State);
            Assert.IsFalse(capability.TryRegisterProvider("other", null, out _));
        }
    }
}

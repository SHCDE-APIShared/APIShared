using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using APIShared.Economy;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class WorkerProductionAgreementTests
    {
        private static WorkerProductionAgreementDescriptor Ready(string key="native",string rules="rules",string wire="wire",int required=0,bool valid=true) =>
            new WorkerProductionAgreementDescriptor(key,rules,wire,"sidecar",1,required,true,false,valid,"saved");
        [TestMethod]
        public void ExactDescriptorsDistinguishRulesHistoryWireAndRequiredData()
        {
            var value=Ready();Assert.IsTrue(value.Matches(Ready()));Assert.AreEqual(value.Digest(),Ready().Digest());
            Assert.IsFalse(value.Matches(Ready(key:"")),"Inactive consumers still need identical installed native history guards.");
            Assert.IsFalse(value.Matches(Ready(rules:"other")));Assert.IsFalse(value.Matches(Ready(wire:"other")));
            Assert.AreEqual("Sidecar",Ready(required:2).LocalFailure);Assert.AreEqual("InvalidData",Ready(valid:false).LocalFailure);
            var unavailable=new WorkerProductionAgreementDescriptor("","baseline","legacy","",0,0,false,false,true);
            Assert.IsTrue(unavailable.Matches(unavailable),"Explicitly identical dormant Vanilla support differs from a missing report.");
            Assert.AreEqual("Unavailable",new WorkerProductionAgreementDescriptor("","rules","extended","sidecar",1,1,false,false,true).LocalFailure);
        }
        [TestMethod]
        public void OnlyCurrentAuthenticatedRosterReceiptsCanCompleteARound()
        {
            var round=new WorkerProductionAgreementRound();round.Reset("first",new ulong[]{12,13});
            Assert.AreEqual("Pending",round.Resolve(Ready()).Code);
            Assert.IsFalse(round.Report(99,"first",Ready()));Assert.IsFalse(round.Report(12,"old",Ready()));
            Assert.IsTrue(round.Report(12,"first",Ready()));Assert.AreEqual("Pending",round.Resolve(Ready()).Code);
            Assert.IsTrue(round.Report(13,"first",Ready(rules:"other")));Assert.AreEqual("Mismatch",round.Resolve(Ready()).Code);
            round.Report(13,"first",Ready());Assert.IsTrue(round.Resolve(Ready()).Ready);
            round.Reset("second",new ulong[]{12});Assert.IsFalse(round.Report(12,"first",Ready()));
            Assert.AreEqual("Pending",round.Resolve(Ready()).Code);round.Report(12,"second",Ready());Assert.IsTrue(round.Resolve(Ready()).Ready);
            round.Reset("solo",Array.Empty<ulong>());Assert.IsTrue(round.Resolve(Ready()).Ready);
        }
        [TestMethod]
        public void DormantRulesStillRequireMatchingExecutionProtectionButAcceptReadableNewerSchema()
        {
            var available=Ready();
            var partial=new WorkerProductionAgreementDescriptor("native","rules","wire","sidecar",1,0,false,false,true,"saved");
            Assert.IsNull(available.LocalFailure);Assert.IsNull(partial.LocalFailure);
            Assert.IsFalse(available.Matches(partial),"Equal native keys cannot conceal an unavailable engine/safety integration, even with no demand.");
            var round=new WorkerProductionAgreementRound();round.Reset("epoch",new ulong[]{12});
            round.Report(12,"epoch",partial);Assert.AreEqual("Mismatch",round.Resolve(available).Code);
            round.Report(12,"epoch",available);Assert.IsTrue(round.Resolve(available).Ready);
            var newer=new WorkerProductionAgreementDescriptor("native","rules","wire","sidecar",2,0,true,false,true,"saved");
            Assert.IsTrue(available.Matches(newer),"Different readable maxima are compatible when both read the required schema.");
        }

        [TestMethod]
        [DoNotParallelize]
        public void RepublishedReceiptNotifiesWaitingMissionEvenIfStatusStaysReady()
        {
            const BindingFlags fields = BindingFlags.Static | BindingFlags.NonPublic;
            var agreement = typeof(WorkerProductionAgreement);
            var current = agreement.GetField("current", fields);
            var rosterKey = agreement.GetField("rosterKey", fields);
            var localDigest = agreement.GetField("localDigest", fields);
            var approvedDigest = agreement.GetField("approvedDescriptorDigest", fields);
            var setResult = agreement.GetMethod("SetResult", fields);
            var stateFields = new[] { current, rosterKey, localDigest, approvedDigest };
            var previous = Array.ConvertAll(stateFields, field => field.GetValue(null));
            string digest = Ready().Digest();
            string observed = null;
            int notifications = 0;
            void OnChanged()
            {
                observed = WorkerProductionAgreement.ApprovedDescriptorDigest;
                notifications++;
            }

            WorkerProductionAgreement.Changed += OnChanged;
            try
            {
                current.SetValue(null, new WorkerProductionAgreementResult(""));
                rosterKey.SetValue(null, "authenticated-roster");
                localDigest.SetValue(null, digest);
                // RequestRefresh invalidates the receipt before the queued refresh runs.
                approvedDigest.SetValue(null, "");

                setResult.Invoke(null, new object[] { "", "" });
                Assert.AreEqual(digest, observed);
                Assert.AreEqual(1, notifications);

                setResult.Invoke(null, new object[] { "", "" });
                Assert.AreEqual(1, notifications, "An identical published receipt needs no second admission attempt.");

                string newDigest = Ready(rules: "updated-rules").Digest();
                localDigest.SetValue(null, newDigest);
                setResult.Invoke(null, new object[] { "", "" });
                Assert.AreEqual(newDigest, observed);
                Assert.AreEqual(2, notifications, "A different approved digest is observable even while Ready.");
            }
            finally
            {
                WorkerProductionAgreement.Changed -= OnChanged;
                for (int i = 0; i < stateFields.Length; i++) stateFields[i].SetValue(null, previous[i]);
            }
        }

        [TestMethod]
        public void RepeatedFailuresYieldBetweenAttemptsAndExpireExactlyOnce()
        {
            long now = 0;
            int attempts = 0, timeouts = 0;
            var timers = new Queue<ScheduledStep>();
            var wait = new WorkerProductionAdmissionWait(() => now, (run, delay) =>
            {
                var step = new ScheduledStep(run, delay);
                timers.Enqueue(step);
                return step;
            }, () => { attempts++; return true; }, () => timeouts++);
            wait.Arm();
            for (int seconds = 1; seconds <= WorkerProductionAdmissionWait.TimeoutSeconds; seconds++)
            {
                wait.Arm();
                Assert.AreEqual(1, timers.Count);
                var step = timers.Dequeue();
                Assert.AreEqual(TimeSpan.FromSeconds(1), step.Delay);
                now += Stopwatch.Frequency;
                step.Run();
                Assert.IsTrue(step.Disposed);
                step.Run(); // Simulates an already delivered callback being encountered again.
            }
            Assert.AreEqual(29, attempts);
            Assert.AreEqual(1, timeouts);
            Assert.AreEqual(0, timers.Count);
        }

        [TestMethod]
        public void RecoveryOrRetirementCancelsAnAlreadyEnqueuedCheck()
        {
            ScheduledStep scheduled = null;
            WorkerProductionAdmissionWait wait = null;
            int activations = 0;
            long now = 0;
            wait = new WorkerProductionAdmissionWait(() => now,
                (run, delay) => scheduled = new ScheduledStep(run, delay),
                () => { activations++; return false; }, () => Assert.Fail("A finished wait must not expire."));
            wait.Arm();
            var recovered = scheduled;
            now += Stopwatch.Frequency;
            recovered.Run(); recovered.Run();
            Assert.AreEqual(1, activations);
            Assert.AreSame(recovered, scheduled);

            var retired = new WorkerProductionAdmissionWait(() => now,
                (run, delay) => scheduled = new ScheduledStep(run, delay),
                () => { Assert.Fail("A retired wait must not refresh the replacement mission."); return false; },
                () => Assert.Fail("A retired wait must not terminate the replacement mission."));
            retired.Arm();
            retired.Cancel();
            now += 31 * Stopwatch.Frequency;
            Assert.IsTrue(scheduled.Disposed);
            scheduled.Run();
        }

        private sealed class ScheduledStep : IDisposable
        {
            internal ScheduledStep(Action run, TimeSpan delay) { Run = run; Delay = delay; }
            internal Action Run { get; }
            internal TimeSpan Delay { get; }
            internal bool Disposed { get; private set; }
            public void Dispose() => Disposed = true;
        }

        [TestMethod]
        public void ExplicitFormatterRetainsEveryAdmissionRelevantField()
        {
            var packet=new WorkerProductionAgreementPacket{Operation=2,Lobby=78,Roster="roster",Token=new string('a',32),Descriptor=Ready(required:1)};
            var decoded=MessagePackSerializer.Deserialize<WorkerProductionAgreementPacket>(MessagePackSerializer.Serialize(packet));
            Assert.AreEqual(packet.Operation,decoded.Operation);Assert.AreEqual(packet.Lobby,decoded.Lobby);
            Assert.AreEqual(packet.Roster,decoded.Roster);Assert.AreEqual(packet.Token,decoded.Token);
            Assert.AreEqual(packet.Descriptor.Digest(),decoded.Descriptor.Digest());
        }
    }
}

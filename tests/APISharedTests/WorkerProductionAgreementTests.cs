using System;
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

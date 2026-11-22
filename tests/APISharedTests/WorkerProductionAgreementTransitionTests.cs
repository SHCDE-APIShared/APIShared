using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using APIShared;
using APIShared.Economy;
using APIShared.Networking;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using Steamworks;

namespace APISharedTests
{
    // Execute the production Observe/Refresh/Receive/Send/SetResult bodies. Only
    // external Steam identity/transport and UI/status notifications are adapted.
    // Swapping private process state and the round storage models two processes;
    // no second implementation of the agreement state machine is used here.
    [TestClass, DoNotParallelize]
    public sealed class WorkerProductionAgreementTransitionTests
    {
        private const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const string OldToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private static Harness active;
        private static WorkerProductionAgreementDescriptor Rules(string value = "same") =>
            new WorkerProductionAgreementDescriptor("service", value, "native", "", 0, 0, true, false, true);
        private static LobbyStateSnapshot Roster(ulong lobby, ulong self) => new LobbyStateSnapshot(lobby,
            new Dictionary<int, ulong> { [1] = 1001, [2] = 1002 }, false, self == 1001 ? 1 : 2, false, "", "");

        [TestMethod]
        public void EarlyHostChallengeIsRecoveredByTheNewRosterHello()
        {
            using (var fixture = new Harness())
            {
                fixture.Host.Observe(20);
                fixture.DeliverTo(fixture.Client); // Challenge arrives while the client still knows lobby 10.
                Assert.AreEqual(OldToken, fixture.Client.Token);
                fixture.Client.Observe(20);
                Assert.IsTrue(fixture.Sent.Any(p => p.Sender == 1002 && p.Packet.Operation == 0),
                    "Same rules in a replacement lobby must still request a new challenge.");
                fixture.AssertUnapproved();
                fixture.Drain(); fixture.AssertApproved();
            }
        }

        [TestMethod]
        public void EarlyClientHelloIsRecoveredByTheHostsNewRosterChallenge()
        {
            using (var fixture = new Harness())
            {
                fixture.Client.Observe(20);
                fixture.DeliverTo(fixture.Host); // Hello arrives before the host observes lobby 20.
                Assert.AreEqual(OldToken, fixture.Host.Token);
                fixture.Host.Observe(20);
                fixture.AssertUnapproved();
                fixture.Drain(); fixture.AssertApproved();
            }
        }

        [TestMethod]
        public void UnchangedRosterRefreshesDoNotGenerateMoreChallengesOrHellos()
        {
            using (var fixture = new Harness())
            {
                fixture.Host.Observe(20); fixture.Client.Observe(20); fixture.Drain(); fixture.AssertApproved();
                int sent = fixture.Sent.Count;
                string token = fixture.Host.Token;
                for (int i = 0; i < 20; i++)
                {
                    fixture.Host.Observe(20); fixture.Client.Observe(20);
                    fixture.Host.Refresh(); fixture.Client.Refresh();
                }
                Assert.AreEqual(sent, fixture.Sent.Count);
                Assert.AreEqual(token, fixture.Host.Token);
                fixture.AssertApproved();
            }
        }

        [TestMethod]
        public void OldLobbyOldTokenAndUnknownPeersCannotRestoreAnOldApproval()
        {
            using (var fixture = new Harness())
            {
                fixture.Host.Observe(20); fixture.Client.Observe(20); fixture.AssertUnapproved();
                foreach (var packet in new[] {
                    Packet(2, 10, OldToken), Packet(2, 20, OldToken),
                    Packet(2, 20, fixture.Host.Token, roster: "20:1001,9999") })
                    fixture.Host.Receive(1002, packet);
                fixture.Host.Receive(9999, Packet(2, 20, fixture.Host.Token));
                fixture.Client.Receive(1001, Packet(3, 20, OldToken));
                fixture.AssertUnapproved();
                Assert.AreEqual("Pending", fixture.Host.Code);
                Assert.AreEqual("Pending", fixture.Client.Code);
                fixture.Drain(); fixture.AssertApproved();
            }
        }

        [TestMethod]
        public void ARealRuleMismatchStillBlocksBothApprovals()
        {
            using (var fixture = new Harness(clientRules: Rules("different")))
            {
                fixture.Host.Observe(20); fixture.Client.Observe(20); fixture.Drain();
                Assert.AreEqual("Mismatch", fixture.Host.Code);
                fixture.AssertUnapproved();
            }
        }

        [TestMethod]
        public void AnExpectedMapTransitionPreservesTheExistingWorldReceipt()
        {
            using (var fixture = new Harness())
            {
                string approved = fixture.Client.Approved;
                fixture.Client.Observe(new LobbyStateSnapshot(null, null, false, 0, true, "", ""));
                Assert.AreEqual(approved, fixture.Client.Approved);
                Assert.AreEqual(OldToken, fixture.Client.Token);
                Assert.AreEqual(0, fixture.Sent.Count);
            }
        }

        [TestMethod]
        public void AnUnresolvedReplacementCannotReuseApprovalAndResolvedPlayersRequestANewRound()
        {
            using (var fixture = new Harness())
            {
                fixture.Client.Observe(new LobbyStateSnapshot(20, null, true, 0, false, "", ""));
                Assert.AreEqual("", fixture.Client.Approved);
                Assert.AreEqual("Roster", fixture.Client.Code);
                Assert.AreEqual(0, fixture.Sent.Count);
                fixture.Client.Observe(20);
                Assert.IsTrue(fixture.Sent.Any(p => p.Sender == 1002 && p.Packet.Operation == 0));
                fixture.Host.Observe(20); fixture.Drain(); fixture.AssertApproved();
            }
        }

        [TestMethod]
        public void LeavingThenRejoiningWithUnchangedRulesRequiresFreshPeerApproval()
        {
            using (var fixture = new Harness())
            {
                fixture.Client.Observe(new LobbyStateSnapshot(null, null, false, 0, false, "", ""));
                Assert.AreEqual("", fixture.Client.Approved);
                Assert.AreEqual(0, fixture.Sent.Count);
                fixture.Client.Observe(20);
                Assert.AreEqual("Pending", fixture.Client.Code);
                Assert.AreEqual("", fixture.Client.Approved);
                Assert.IsTrue(fixture.Sent.Any(p => p.Sender == 1002 && p.Packet.Operation == 0));
                fixture.Host.Observe(20); fixture.Drain(); fixture.AssertApproved();
            }
        }

        [DataTestMethod, DataRow(true), DataRow(false)]
        public void FailedNewEpochSendRemainsUnapprovedAndTheNextRefreshCanRetry(bool hostFails)
        {
            using (var fixture = new Harness())
            {
                var failed = hostFails ? fixture.Host : fixture.Client;
                failed.FailNextSend = true; failed.Observe(20);
                Assert.AreEqual("Unavailable", failed.Code);
                Assert.AreEqual("", failed.Approved);
                Assert.AreEqual("fixture transport failure", failed.LastSendFailure);
                failed.Refresh();
                (hostFails ? fixture.Client : fixture.Host).Observe(20);
                fixture.Drain(); fixture.AssertApproved();
            }
        }

        private static WorkerProductionAgreementPacket Packet(int operation, ulong lobby, string token, string roster = null) =>
            new WorkerProductionAgreementPacket { Operation = operation, Lobby = lobby,
                Roster = roster ?? lobby + ":1001,1002", Token = token, Descriptor = Rules() };
        private sealed class Envelope
        {
            internal ulong Sender, Recipient;
            internal WorkerProductionAgreementPacket Packet;
        }
        private sealed class Node
        {
            internal readonly ulong Id;
            internal readonly WorkerProductionAgreementDescriptor Descriptor;
            internal readonly WorkerProductionAgreementRound Round = new WorkerProductionAgreementRound();
            internal readonly Platform_Multiplayer Platform;
            internal readonly Dictionary<FieldInfo, object> State;
            internal bool FailNextSend;
            internal Node(ulong id, WorkerProductionAgreementDescriptor descriptor, Dictionary<FieldInfo, object> initial)
            {
                Id = id; Descriptor = descriptor; State = new Dictionary<FieldInfo, object>(initial);
                Platform = (Platform_Multiplayer)FormatterServices.GetUninitializedObject(typeof(Platform_Multiplayer));
                Platform.activeLobby = new Platform_Multiplayer.MPLobby { id = new CSteamID(20) };
                Set("capture", (Func<WorkerProductionAgreementDescriptor>)Capture);
                Set("roster", Roster(10, id)); Set("rosterKey", "10:1001,1002"); Set("host", 1001UL); Set("localId", id);
                Set("local", descriptor); Set("localDigest", descriptor.Digest()); Set("token", OldToken); Set("committed", true);
                Set("current", new WorkerProductionAgreementResult("")); Set("approvedDescriptorDigest", descriptor.Digest());
                Set("Changed", null); Set("lastSendFailure", null);
            }
            private void Set(string field, object value) => State[Owner.GetField(field, Any)] = value;
            private T Get<T>(string field) => (T)State[Owner.GetField(field, Any)];
            internal string Token => Get<string>("token");
            internal string Approved => Get<string>("approvedDescriptorDigest");
            internal string LastSendFailure => Get<string>("lastSendFailure");
            internal string Code => Get<WorkerProductionAgreementResult>("current").Code;
            internal void Invoke(Action run)
            {
                active.Current = this;
                foreach (var item in State) item.Key.SetValue(null, item.Value);
                try { run(); }
                finally
                {
                    foreach (var field in State.Keys.ToArray()) State[field] = field.GetValue(null);
                    active.Current = null;
                }
            }
            internal void Observe(ulong lobby) => Observe(Roster(lobby, Id));
            internal void Observe(LobbyStateSnapshot roster) => Invoke(() => active.Observe(roster));
            internal void Refresh() => Invoke(active.Refresh);
            internal void Receive(ulong sender, WorkerProductionAgreementPacket packet) =>
                Invoke(() => active.Receive(new NetworkMessage<WorkerProductionAgreementPacket>(packet, sender)));
        }
        private static readonly Type Owner = typeof(WorkerProductionAgreement);
        private sealed class Harness : IDisposable
        {
            private readonly Dictionary<FieldInfo, object> saved;
            internal readonly List<Envelope> Sent = new List<Envelope>();
            private readonly List<Envelope> pending = new List<Envelope>();
            internal readonly Node Host, Client;
            internal Node Current;
            internal readonly Action Refresh, Begin, Complete;
            internal readonly Action<LobbyStateSnapshot> Observe;
            internal readonly Action<NetworkMessage<WorkerProductionAgreementPacket>> Receive;
            internal readonly Action<int, ulong, string> Send;
            internal readonly Action<string, string> PublishResult;
            internal Harness(WorkerProductionAgreementDescriptor clientRules = null)
            {
                saved = Owner.GetFields(Any).Where(f => f.IsStatic && !f.IsInitOnly).ToDictionary(f => f, f => f.GetValue(null));
                active = this;
                Refresh = Clone<Action>("Refresh"); Observe = Clone<Action<LobbyStateSnapshot>>("Observe");
                Receive = Clone<Action<NetworkMessage<WorkerProductionAgreementPacket>>>("Receive");
                Begin = Clone<Action>("BeginChallenge"); Complete = Clone<Action>("CompleteHost");
                Send = Clone<Action<int, ulong, string>>("Send"); PublishResult = Clone<Action<string, string>>("SetResult");
                Host = new Node(1001, Rules(), saved); Client = new Node(1002, clientRules ?? Rules(), saved);
            }
            internal void DeliverTo(Node recipient)
            {
                int index = pending.FindIndex(p => p.Recipient == recipient.Id);
                Assert.IsTrue(index >= 0, "Expected an outgoing message to this peer.");
                var value = pending[index]; pending.RemoveAt(index); recipient.Receive(value.Sender, value.Packet);
            }
            internal void Drain()
            {
                int delivered = 0;
                while (pending.Count != 0)
                {
                    Assert.IsTrue(++delivered < 100, "Agreement unexpectedly generated an unbounded message loop.");
                    DeliverTo(pending[0].Recipient == Host.Id ? Host : Client);
                }
            }
            internal NetworkSendResult Submit(WorkerProductionAgreementPacket packet, ulong peer)
            {
                if (Current.FailNextSend)
                {
                    Current.FailNextSend = false;
                    return new NetworkSendResult(NetworkSendStatus.TransportFailed, "fixture transport failure", null);
                }
                var envelope = new Envelope { Sender = Current.Id, Recipient = peer, Packet = packet };
                Sent.Add(envelope); pending.Add(envelope);
                return new NetworkSendResult(NetworkSendStatus.Submitted, "", null);
            }
            internal void AssertUnapproved()
            { Assert.AreEqual("", Host.Approved); Assert.AreEqual("", Client.Approved); }
            internal void AssertApproved()
            {
                Assert.AreEqual("", Host.Code); Assert.AreEqual("", Client.Code);
                Assert.AreEqual(Host.Descriptor.Digest(), Host.Approved);
                Assert.AreEqual(Client.Descriptor.Digest(), Client.Approved);
            }
            public void Dispose()
            {
                foreach (var item in saved) item.Key.SetValue(null, item.Value);
                active = null;
            }
        }
        private static WorkerProductionAgreementDescriptor Capture() => active.Current.Descriptor;
        private static WorkerProductionAgreementRound Round() => active.Current.Round;
        private static Platform_Multiplayer Platform() => active.Current.Platform;
        private static CSteamID Local() => new CSteamID(active.Current.Id);
        private static CSteamID Host(CSteamID unused) => new CSteamID(1001);
        private static void Refresh() => active.Refresh();
        private static void Begin() => active.Begin();
        private static void Complete() => active.Complete();
        private static void Send(int operation, ulong peer, string token) => active.Send(operation, peer, token);
        private static void PublishResult(string code, string diagnostic) => active.PublishResult(code, diagnostic);
        private static NetworkSendResult Submit(MessageChannel<WorkerProductionAgreementPacket> unused, WorkerProductionAgreementPacket packet, ulong peer) => active.Submit(packet, peer);
        private static void UpdatePeerReadiness(string code, string diagnostic) { }
        private static void RefreshStatus() { }
        private static T Clone<T>(string name) where T : Delegate
        {
            using (var method = new DynamicMethodDefinition(Owner.GetMethod(name, Any)))
            using (var il = new ILContext(method.Definition))
            {
                foreach (var instruction in il.Body.Instructions)
                {
                    if (instruction.OpCode == OpCodes.Ldsfld && instruction.Operand is FieldReference field &&
                        field.DeclaringType.FullName == Owner.FullName && field.Name == "round")
                    {
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = il.Import(typeof(WorkerProductionAgreementTransitionTests).GetMethod(nameof(Round), Any));
                        continue;
                    }
                    if (!(instruction.Operand is MethodReference called)) continue;
                    string boundary = null;
                    if (called.DeclaringType.FullName == Owner.FullName)
                    {
                        if (called.Name == "Refresh") boundary = nameof(Refresh);
                        else if (called.Name == "BeginChallenge") boundary = nameof(Begin);
                        else if (called.Name == "CompleteHost") boundary = nameof(Complete);
                        else if (called.Name == "Send") boundary = nameof(Send);
                        else if (called.Name == "SetResult") boundary = nameof(PublishResult);
                    }
                    else if (called.DeclaringType.FullName == "Platform_Multiplayer" && called.Name == "get_Instance") boundary = nameof(Platform);
                    else if (called.DeclaringType.FullName == "Steamworks.SteamUser" && called.Name == "GetSteamID") boundary = nameof(Local);
                    else if (called.DeclaringType.FullName == "Steamworks.SteamMatchmaking" && called.Name == "GetLobbyOwner") boundary = nameof(Host);
                    else if (called.DeclaringType.FullName.StartsWith("APIShared.Networking.MessageChannel`1", StringComparison.Ordinal) && called.Name == "SendToSteamPeer") boundary = nameof(Submit);
                    else if (called.DeclaringType.FullName == typeof(WorkerProductionModifiers).FullName && called.Name == "UpdatePeerReadiness") boundary = nameof(UpdatePeerReadiness);
                    else if (called.DeclaringType.FullName == "APIShared.Lobby.LobbyLaunchStatus" && called.Name == "RequestRefresh") boundary = nameof(RefreshStatus);
                    if (boundary == null) continue;
                    instruction.OpCode = OpCodes.Call;
                    instruction.Operand = il.Import(typeof(WorkerProductionAgreementTransitionTests).GetMethod(boundary, Any));
                }
                return (T)method.Generate().CreateDelegate(typeof(T));
            }
        }
    }
}

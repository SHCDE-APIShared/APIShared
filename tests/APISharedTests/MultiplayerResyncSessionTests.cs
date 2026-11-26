using System;
using System.Collections.Generic;
using System.Linq;
using APIShared.Multiplayer;
using APIShared.Networking;
using MessagePack;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Packet = APIShared.Multiplayer.MultiplayerResyncDataPacket;
using Record = APIShared.Multiplayer.MultiplayerResyncDataRuntime.Record;
using World = APIShared.Multiplayer.SimulationAgreement.AdmittedResyncWorld;

namespace APISharedTests
{
    // Runs the production session, not a protocol imitation. Only Steam, native facts,
    // Unity dispatch, the engine monitor and time are controlled external boundaries.
    [TestClass, DoNotParallelize]
    public sealed class MultiplayerResyncSessionTests
    {
        private enum Broken { None, Capture, Prepare, Commit }
        private sealed class Envelope
        {
            internal Node From, To;
            internal Packet Packet;
        }
        private sealed class Mesh
        {
            internal readonly List<Node> Nodes = new List<Node>();
            internal readonly List<Envelope> Wire = new List<Envelope>();
            internal readonly List<string> Events = new List<string>();
            internal Func<Envelope, bool> Withhold;
            internal long Now = 1000;
            internal Mesh(bool allEarly = false)
            {
                for (int i = 1; i <= 3; i++) Nodes.Add(new Node(this, i, allEarly));
            }
            internal Node Host => Nodes[0];
            internal void Start() { foreach (var node in Nodes) node.Engine(node.Session.OnNativeStart); }
            internal void Run() { foreach (var node in Nodes) node.Engine(node.Session.OnEngineRun); Pump(); }
            internal void Pump()
            {
                int guard = 0;
                while (Nodes.Any(n => n.Unity.Count != 0) || Wire.Count != 0)
                {
                    Assert.IsTrue(++guard < 10000, "A bounded transfer must quiesce.");
                    foreach (var node in Nodes)
                        if (node.Unity.Count != 0) node.Unity.Dequeue()();
                    if (Wire.Count == 0) continue;
                    var item = Wire[0]; Wire.RemoveAt(0);
                    if (Withhold != null && Withhold(item)) continue;
                    if (item.Packet.Operation == Packet.Kind.Chunk) item.To.ChunksReceived++;
                    item.To.Session.Receive(item.From.Steam, item.Packet);
                }
            }
            internal void NativeReady() { foreach (var n in Nodes) n.NativeState = 3; }
            internal void PrepareAll()
            {
                Run(); NativeReady();
                Assert.IsFalse(Host.Engine(() => Host.Session.AllowHostCompletion(1)));
                Pump(); Run();
            }
            internal bool ReleaseHost() => Host.Engine(() =>
            {
                if (!Host.Session.AllowHostCompletion(1)) return false;
                Assert.IsTrue(Host.Session.BeforeNativeComplete());
                Host.NativeState = 0; Host.NativeCompletions++;
                Host.Session.OnNativeCompleted();
                return true;
            });
            internal void ReleaseClients()
            {
                foreach (var n in Nodes.Skip(1)) n.Engine(() =>
                {
                    Assert.IsTrue(n.Session.AllowRemoteCompletion(1, 1));
                    Assert.IsTrue(n.Session.BeforeNativeComplete());
                    n.NativeState = 0; n.NativeCompletions++;
                    n.Session.OnNativeCompleted();
                });
            }
        }
        private sealed class Node
        {
            private readonly Mesh mesh;
            internal readonly int Id;
            internal readonly ulong Steam;
            internal readonly Queue<Action> Unity = new Queue<Action>();
            internal readonly MultiplayerResyncDataSession Session;
            internal World World;
            internal int NativeState = 10, NativeCompletions, Captures, Prepares, Commits, Chunks, ChunksReceived, Requests;
            internal bool EngineHeld, Faulted, Dispatcher = true, Transport = true, Roster = true, QueueFree = true;
            internal bool InactiveEarly, OversizeEarly;
            internal Broken Break;
            internal string Fault;
            internal byte Early = 17;
            internal byte[] Late = Enumerable.Repeat((byte)29, Packet.ChunkBytes * Packet.Window + 37).ToArray();
            internal readonly List<byte[]> Published = new List<byte[]>();
            private ulong nonce;
            internal ulong Nonce => nonce;
            internal Node(Mesh mesh, int id, bool allEarly)
            {
                this.mesh = mesh; Id = id; Steam = (ulong)(1000 + id); nonce = Steam;
                if (id == 3) { Early = 18; Late[Late.Length - 1] = 30; }
                World = NewWorld(1);
                var records = new[]
                {
                    new Record("mod", "early", 1, 1,
                        () => Capture(InactiveEarly ? Array.Empty<byte>() : OversizeEarly ? new byte[2] : new[] { Early }),
                        Prepare, Commit, true),
                    new Record("mod", "late", 1, Late.Length, () => Capture(Late), Prepare, Commit, allEarly)
                };
                Session = new MultiplayerResyncDataSession(records, new MultiplayerResyncDataSession.Services
                {
                    GetWorld = () => Faulted ? null : World,
                    BindWorld = () => World,
                    IsWorldCurrent = world => ReferenceEquals(World, world) && !Faulted,
                    VerifyRoster = world => Roster,
                    IsCohortAdmitted = () => World != null,
                    IsFaulted = () => Faulted,
                    IsHost = () => Id == 1,
                    IsClientReady = () => Id != 1 && (NativeState == 3 || NativeState == 4),
                    HoldsEngineMonitor = () => EngineHeld,
                    NativeRepairState = () => NativeState,
                    NativeRosterMatches = (manager, mask) => { Assert.IsTrue(EngineHeld); return Roster && mask == 14; },
                    HasFreeQueueSlot = manager => QueueFree,
                    Timestamp = () => mesh.Now,
                    Frequency = 1000,
                    NewNonce = () => ++nonce,
                    EnqueueUnity = action => { if (!Dispatcher) return false; Unity.Enqueue(action); return true; },
                    UnderEngineMonitor = Engine,
                    ReportFault = reason => { Assert.IsTrue(EngineHeld); Faulted = true; Fault = reason; },
                    Send = Send,
                    Completed = (repair, count, requested, bytes) => mesh.Events.Add("done:" + Id)
                });
            }
            internal World NewWorld(long epoch)
            {
                var decision = new SimulationAgreement.Receipt { Lobby = 7, Round = 8, Host = 1001, Local = Steam };
                var attempt = new SimulationAgreement.Attempt { Decision = decision, Epoch = epoch, Session = Id + epoch * 10 };
                return new World(attempt, new Dictionary<int, ulong> { [1] = 1001, [2] = 1002, [3] = 1003 }, "digest");
            }
            internal void Engine(Action action)
            { bool previous = EngineHeld; EngineHeld = true; try { action(); } finally { EngineHeld = previous; } }
            internal T Engine<T>(Func<T> action)
            { bool previous = EngineHeld; EngineHeld = true; try { return action(); } finally { EngineHeld = previous; } }
            private byte[] Capture(byte[] value)
            {
                Assert.IsTrue(EngineHeld); Captures++;
                if (Break == Broken.Capture) throw new InvalidOperationException("capture");
                return value;
            }
            private object Prepare(ReadOnlyMemory<byte> bytes)
            {
                Assert.IsTrue(EngineHeld); Prepares++;
                if (Break == Broken.Prepare) throw new InvalidOperationException("prepare");
                mesh.Events.Add("prepare:" + Id);
                return bytes.ToArray();
            }
            private void Commit(object value)
            {
                Assert.IsTrue(EngineHeld);
                Assert.IsTrue(mesh.Nodes.All(n => n.Prepares == 2), "Every peer must prepare every record before first commit.");
                if (Break == Broken.Commit) throw new InvalidOperationException("commit");
                Commits++; Published.Add((byte[])value); mesh.Events.Add("commit:" + Id);
            }
            private NetworkSendResult Send(ulong target, Packet packet)
            {
                Assert.IsFalse(EngineHeld, "Control-plane sends must be dispatched outside the engine caller.");
                if (!Transport) return new NetworkSendResult(NetworkSendStatus.TransportFailed, "fixture send", null);
                // Both directions use the actual production formatter and its validation.
                var copy = MessagePackSerializer.Deserialize<Packet>(MessagePackSerializer.Serialize(packet));
                if (packet.Operation == Packet.Kind.Chunk) Chunks++;
                if (packet.Operation == Packet.Kind.Request) Requests++;
                mesh.Wire.Add(new Envelope { From = this, To = mesh.Nodes.Single(n => n.Steam == target), Packet = copy });
                return new NetworkSendResult(NetworkSendStatus.Submitted, "", null);
            }
        }

        [TestMethod]
        public void ThreePeersMixedCaptureReuseMatchingBytesAndTransferMultipleWindowsBeforeCommit()
        {
            var mesh = new Mesh(); mesh.Start();
            mesh.Host.Early = 99; // Early snapshot stays frozen, while late capture reads the current world.
            mesh.PrepareAll();
            Assert.AreEqual(0, mesh.Nodes.Sum(n => n.Commits));
            Assert.IsTrue(mesh.Host.Chunks > Packet.Window);
            Assert.AreEqual(0, mesh.Nodes[1].Requests, "Matching snapshots need no payload request.");
            Assert.AreEqual(0, mesh.Nodes[1].ChunksReceived);
            Assert.AreEqual(2, mesh.Nodes[2].Requests);
            Assert.IsTrue(mesh.ReleaseHost()); mesh.ReleaseClients();
            foreach (var n in mesh.Nodes)
            {
                Assert.AreEqual(2, n.Captures); Assert.AreEqual(2, n.Prepares);
                Assert.AreEqual(2, n.Commits); Assert.AreEqual(1, n.NativeCompletions);
                CollectionAssert.AreEqual(new byte[] { 17 }, n.Published[0]);
                CollectionAssert.AreEqual(mesh.Host.Late, n.Published[1]);
            }
            Assert.IsTrue(mesh.Events.IndexOf("commit:1") > mesh.Events.LastIndexOf("prepare:3"));
        }

        [TestMethod]
        public void FinalChunkMustArriveBeforeClientPreparationAndHostCompletion()
        {
            var mesh = new Mesh();
            var client = mesh.Nodes[2];
            Envelope withheld = null;
            mesh.Withhold = item =>
            {
                if (item.To != client || item.Packet.Operation != Packet.Kind.Chunk ||
                    item.Packet.Index != 1 || item.Packet.Offset + item.Packet.Body.Length != mesh.Host.Late.Length)
                    return false;
                Assert.IsNull(withheld, "The final chunk must be sent only once.");
                withheld = item;
                return true;
            };
            mesh.Start(); mesh.PrepareAll();
            Assert.IsNotNull(withheld);
            Assert.AreEqual(Packet.ChunkBytes * Packet.Window, withheld.Packet.Offset);
            Assert.AreEqual(37, withheld.Packet.Body.Length);
            for (int cycle = 0; cycle < 3; cycle++)
            {
                mesh.Run();
                Assert.IsFalse(mesh.ReleaseHost(), "An incomplete client snapshot must keep host completion blocked.");
                Assert.AreEqual(0, client.Prepares, "A valid but incomplete snapshot must not reach the provider.");
                Assert.IsTrue(mesh.Nodes.All(n => n.Commits == 0 && n.NativeCompletions == 0 && !n.Faulted));
            }

            // Keep the attempt valid: calling AllowRemoteCompletion prematurely is deliberately terminal.
            mesh.Withhold = null; mesh.Wire.Add(withheld); mesh.Pump(); mesh.Run();
            Assert.AreEqual(2, client.Prepares);
            Assert.IsTrue(mesh.ReleaseHost()); mesh.ReleaseClients();
            foreach (var node in mesh.Nodes)
            {
                Assert.AreEqual(2, node.Commits); Assert.AreEqual(1, node.NativeCompletions);
                Assert.IsFalse(node.Faulted);
                CollectionAssert.AreEqual(new byte[] { 17 }, node.Published[0]);
                CollectionAssert.AreEqual(mesh.Host.Late, node.Published[1]);
            }
        }

        [TestMethod]
        public void EarlyManifestCanPrepareMatchingPeerButDefersChangedPayloadUntilNativeHostGate()
        {
            var mesh = new Mesh(true); mesh.Start(); mesh.NativeReady(); mesh.Run(); mesh.Run();
            Assert.AreEqual(2, mesh.Nodes[1].Prepares);
            Assert.AreEqual(0, mesh.Nodes[2].Prepares);
            Assert.AreEqual(0, mesh.Host.Chunks);
            Assert.IsFalse(mesh.Host.Engine(() => mesh.Host.Session.AllowHostCompletion(1)));
            mesh.Pump(); mesh.Run();
            Assert.IsTrue(mesh.ReleaseHost()); mesh.ReleaseClients();
            Assert.IsTrue(mesh.Host.Chunks > Packet.Window);
        }

        [TestMethod]
        public void InactiveHostRecordClearsClientDataWithoutPayloadAndOversizedCaptureIsRejected()
        {
            var mesh = new Mesh(); mesh.Host.InactiveEarly = true; mesh.Start(); mesh.PrepareAll();
            Assert.IsTrue(mesh.ReleaseHost()); mesh.ReleaseClients();
            foreach (var node in mesh.Nodes) Assert.AreEqual(0, node.Published[0].Length);
            Assert.AreEqual(0, mesh.Nodes[1].Requests, "An empty host snapshot needs no payload request.");
            Assert.AreEqual(1, mesh.Nodes[2].Requests, "Only the different late record is transferred.");

            var oversized = new Mesh(); oversized.Host.OversizeEarly = true; oversized.Start();
            Assert.IsTrue(oversized.Host.Faulted);
            StringAssert.Contains(oversized.Host.Fault, "declared bound");
            Assert.IsFalse(oversized.ReleaseHost());
            Assert.AreEqual(0, oversized.Nodes.Sum(n => n.NativeCompletions));
            Assert.AreEqual(0, oversized.Nodes.Sum(n => n.Commits));
        }

        [TestMethod]
        public void DelayedHelloAndStaleIdentityOrNonceCannotCompleteAnotherAttempt()
        {
            var mesh = new Mesh(); mesh.Start();
            foreach (var n in mesh.Nodes) n.Engine(n.Session.OnEngineRun);
            foreach (var n in mesh.Nodes) while (n.Unity.Count > 0) n.Unity.Dequeue()();
            var hello = mesh.Wire.First(e => e.From.Id == 2).Packet;
            mesh.Wire.Clear();
            mesh.NativeReady();
            Assert.IsFalse(mesh.Host.Engine(() => mesh.Host.Session.AllowHostCompletion(1)));
            mesh.Host.Session.Receive(1002, new Packet(hello.Operation, hello.Lobby, hello.Round, hello.Epoch + 1,
                hello.Repair, hello.Nonce, hello.Index, hello.Offset, hello.Body));
            mesh.Pump(); Assert.AreEqual(0, mesh.Nodes.Sum(n => n.Commits));
            mesh.Host.Session.Receive(1002, hello); mesh.Pump();
            // Non-Hello data from the right Steam peer with the wrong nonce is ignored.
            mesh.Host.Session.Receive(1002, new Packet(Packet.Kind.Ready, 7, 8, 1, 1, hello.Nonce + 1, -1, 0, new byte[32]));
            Assert.IsFalse(mesh.Host.Engine(() => mesh.Host.Session.AllowHostCompletion(1)));
            mesh.Now += 1001; mesh.NativeReady(); mesh.Run(); mesh.Run();
            Assert.IsTrue(mesh.ReleaseHost()); mesh.ReleaseClients();
        }

        [TestMethod]
        public void QueuePressureRosterLossTimeoutAndNativeAbortNeverResumeSimulation()
        {
            foreach (string failure in new[] { "queue", "roster", "timeout", "abort" })
            {
                var mesh = new Mesh(); mesh.Start(); mesh.PrepareAll();
                if (failure == "queue") mesh.Host.QueueFree = false;
                if (failure == "roster") mesh.Host.Roster = false;
                if (failure == "timeout") mesh.Now += 91000;
                if (failure == "abort") mesh.Host.NativeState = 0;
                if (failure == "timeout" || failure == "abort") mesh.Host.Engine(mesh.Host.Session.OnEngineRun);
                Assert.IsFalse(mesh.ReleaseHost(), failure);
                Assert.IsTrue(mesh.Host.Faulted, failure);
                Assert.AreEqual(0, mesh.Nodes.Sum(n => n.NativeCompletions), failure);
                Assert.AreEqual(0, mesh.Nodes.Sum(n => n.Commits), failure);
            }
        }

        [TestMethod]
        public void ProviderFailuresAndMalformedPartialChunksStayTerminal()
        {
            foreach (Broken broken in new[] { Broken.Capture, Broken.Prepare, Broken.Commit })
            {
                var mesh = new Mesh();
                if (broken == Broken.Capture) mesh.Nodes[2].Break = broken;
                mesh.Start();
                if (broken != Broken.Capture)
                {
                    if (broken == Broken.Prepare) mesh.Host.Break = broken;
                    mesh.PrepareAll();
                    if (broken == Broken.Commit) mesh.Host.Break = broken;
                    Assert.IsFalse(mesh.ReleaseHost());
                }
                Assert.IsTrue(mesh.Nodes.Any(n => n.Faulted));
                Assert.AreEqual(0, mesh.Nodes.Sum(n => n.NativeCompletions));
            }
            var malformed = new Mesh(true);
            malformed.Nodes[2].Early = 17; // Reuse early bytes, request the large late record.
            malformed.Start(); malformed.Run();
            // Produce an otherwise valid packet using the client nonce from the queued request.
            var client = malformed.Nodes[2];
            client.Session.Receive(1001, new Packet(Packet.Kind.Chunk, 7, 8, 1, 1, client.Nonce, 1, 0, new byte[1]));
            malformed.Pump();
            Assert.IsTrue(client.Faulted, "A partial first chunk must not become a completed snapshot.");
        }

        [TestMethod]
        public void ClientPreparationCommitQueueAndPrematureCompletionFailuresNeverResumeThatClient()
        {
            foreach (string failure in new[] { "prepare", "commit", "queue", "premature", "timeout" })
            {
                var mesh = new Mesh(); mesh.Start();
                var client = mesh.Nodes[2];
                if (failure == "prepare") client.Break = Broken.Prepare;
                if (failure == "premature")
                {
                    Assert.IsFalse(client.Engine(() => client.Session.AllowRemoteCompletion(1, 1)));
                }
                else if (failure == "timeout")
                {
                    mesh.NativeReady(); mesh.Run();
                    mesh.Now += 91000; client.Engine(client.Session.OnEngineRun);
                }
                else
                {
                    mesh.PrepareAll();
                    if (failure == "prepare") Assert.IsFalse(mesh.ReleaseHost());
                    else
                    {
                        Assert.IsTrue(mesh.ReleaseHost());
                        if (failure == "queue") client.QueueFree = false;
                        else client.Break = Broken.Commit;
                        client.Engine(() =>
                        {
                            bool allowed = client.Session.AllowRemoteCompletion(1, 1);
                            if (allowed) Assert.IsFalse(client.Session.BeforeNativeComplete());
                            else Assert.AreEqual("queue", failure);
                        });
                    }
                }
                mesh.Pump();
                Assert.IsTrue(client.Faulted, failure);
                Assert.AreEqual(0, client.NativeCompletions, failure);
            }
        }

        [TestMethod]
        public void DuplicateHelloIsIdempotentButChangedNonceAndUnsentProgressAreTerminal()
        {
            foreach (bool changedNonce in new[] { false, true })
            {
                var mesh = new Mesh(true); mesh.Start(); mesh.Run();
                var client = mesh.Nodes[2];
                var hello = new Packet(Packet.Kind.Hello, 7, 8, 1, 1, client.Nonce, -1, 0, Array.Empty<byte>());
                mesh.Host.Session.Receive(client.Steam, hello); mesh.Pump();
                Assert.IsFalse(mesh.Host.Faulted, "Same-attempt hello retransmission is harmless.");
                if (changedNonce)
                    mesh.Host.Session.Receive(client.Steam, new Packet(Packet.Kind.Hello, 7, 8, 1, 1,
                        client.Nonce + 1, -1, 0, Array.Empty<byte>()));
                else
                    // Early request is queued but no payload window has been sent yet.
                    mesh.Host.Session.Receive(client.Steam, new Packet(Packet.Kind.Progress, 7, 8, 1, 1,
                        client.Nonce, 0, 1, Array.Empty<byte>()));
                mesh.Pump();
                Assert.IsTrue(mesh.Host.Faulted);
                Assert.AreEqual(0, mesh.Nodes.Sum(n => n.NativeCompletions));
            }
        }

        [TestMethod]
        public void SendDispatchFailuresAndWorldRetirementCannotLeakQueuedWorkIntoReplacementWorld()
        {
            foreach (bool dispatch in new[] { false, true })
            {
                var mesh = new Mesh(); mesh.Start();
                if (dispatch) mesh.Nodes[1].Dispatcher = false; else mesh.Nodes[1].Transport = false;
                mesh.Run(); Assert.IsTrue(mesh.Nodes[1].Faulted);
                Assert.AreEqual(0, mesh.Nodes.Sum(n => n.NativeCompletions));
            }
            var retired = new Mesh(); retired.Start();
            foreach (var n in retired.Nodes) n.Engine(n.Session.OnEngineRun);
            // Fail one queued send, leaving its terminal notification deferred on Unity.
            retired.Nodes[1].Transport = false;
            retired.Nodes[1].Unity.Dequeue()();
            foreach (var n in retired.Nodes) { n.World = n.NewWorld(2); n.Session.Reset(); }
            retired.Nodes[1].Transport = true;
            retired.Pump();
            Assert.AreEqual(0, retired.Wire.Count);
            Assert.AreEqual(0, retired.Nodes.Sum(n => n.Commits));
            Assert.IsFalse(retired.Nodes.Any(n => n.Faulted), "A retired attempt's delayed failure cannot stop its replacement.");
            retired.Start(); retired.PrepareAll(); Assert.IsTrue(retired.ReleaseHost()); retired.ReleaseClients();
        }

        [TestMethod]
        public void OrdinaryEngineCallsWithoutActiveRepairAllocateNothingAfterWarmup()
        {
            var node = new Mesh().Host;
            for (int i = 0; i < 1000; i++) node.Session.OnEngineRun();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30000; i++) node.Session.OnEngineRun();
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.AreEqual(0, node.Unity.Count);

            node.World = null;
            node.Session.OnEngineRun();
            Assert.AreEqual(1, node.Unity.Count, "Binding is coalesced until the queued Unity work executes.");
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 30000; i++) node.Session.OnEngineRun();
            Assert.AreEqual(0L, GC.GetAllocatedBytesForCurrentThread() - before);
            Assert.AreEqual(1, node.Unity.Count);
        }
    }
}

using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.API.Components.Network;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace APISharedTests
{
    [TestClass]
    public class LobbySettingsReceiveTests
    {
        [TestMethod]
        public void WorkerReceiveCopiesLargeValuesAndSharesFifoWithTheSetupTransition()
        {
            var posted = new ConcurrentQueue<Action>();
            var observed = new List<string>();
            int mainThread = Thread.CurrentThread.ManagedThreadId;
            LobbySettingsReceiveQueue receiver = null;
            receiver = new LobbySettingsReceiveQueue(action => { posted.Enqueue(action); return true; },
                generation => generation == receiver.Generation,
                (packet, sender) => {
                    Assert.AreEqual(mainThread, Thread.CurrentThread.ManagedThreadId);
                    Assert.AreEqual((ulong?)77, sender);
                    Assert.AreEqual(65536, packet.SerializedValue.Length);
                    Assert.AreEqual((byte)17, packet.SerializedValue[0]);
                    observed.Add(packet.PropertyName);
                }, error => Assert.Fail(error.ToString()));
            var raw = new LobbyModSettingSyncPacket { ModName = "Foreign.Mod", PropertyName = "Amount",
                TypeName = "System.Byte[]", SourcePlayerId = 8, SerializedValue = new byte[65536] };
            raw.SerializedValue[0] = 17;
            Task.Run(() => {
                Assert.IsTrue(receiver.Enqueue(raw, 77));
                posted.Enqueue(() => observed.Add("Customize"));
            }).GetAwaiter().GetResult();
            raw.PropertyName = "mutated";
            raw.SerializedValue[0] = 99;
            Assert.AreEqual(0, observed.Count, "Raw receive touched live state.");
            while (posted.TryDequeue(out var action)) action();
            CollectionAssert.AreEqual(new[] { "Amount", "Customize" }, observed);
        }

        [TestMethod]
        public void StaleQueuedWorkAndMissingDispatchNeverApplyAndFailuresDoNotStopLaterPackets()
        {
            var posted = new Queue<Action>();
            var received = new List<int>();
            var failures = new List<Exception>();
            LobbySettingsReceiveQueue receiver = null;
            receiver = new LobbySettingsReceiveQueue(action => { posted.Enqueue(action); return true; },
                generation => generation == receiver.Generation,
                (packet, sender) => {
                    if (packet.SourcePlayerId == 2) throw new InvalidOperationException("injected receiver failure");
                    received.Add(packet.SourcePlayerId);
                }, failures.Add);
            var packet = new LobbyModSettingSyncPacket { SerializedValue = new byte[] { 1 }, SourcePlayerId = 1 };
            receiver.Enqueue(packet, 77);
            receiver.Invalidate();
            packet.SourcePlayerId = 2; receiver.Enqueue(packet, 77);
            packet.SourcePlayerId = 3; receiver.Enqueue(packet, 77);
            while (posted.Count != 0) posted.Dequeue()();
            CollectionAssert.AreEqual(new[] { 3 }, received);
            Assert.AreEqual(1, failures.Count);
            var unavailable = new LobbySettingsReceiveQueue(_ => false, _ => true,
                (_, __) => Assert.Fail("Missing dispatcher used an inline fallback."), failures.Add);
            Assert.IsFalse(unavailable.Enqueue(packet, 77));
        }

        // Regression: packets Vanilla receives inside Platform_Multiplayer.StartGame were queued and
        // applied only after the map had loaded (or dropped). The start flushes them; their own
        // dispatcher turns must then stay no-ops, and later packets keep their FIFO position.
        [TestMethod]
        public void FlushDeliversQueuedPacketsBeforeTheirTurnsWithoutShiftingLaterPackets()
        {
            var posted = new Queue<Action>();
            var received = new List<int>();
            bool flushInsidePost = false;
            LobbySettingsReceiveQueue receiver = null;
            receiver = new LobbySettingsReceiveQueue(action => {
                    if (flushInsidePost) { receiver.Flush(); return false; }
                    posted.Enqueue(action); return true;
                },
                generation => generation == receiver.Generation,
                (packet, sender) => received.Add(packet.SourcePlayerId), error => Assert.Fail(error.ToString()));
            var raw = new LobbyModSettingSyncPacket { SerializedValue = new byte[] { 1 } };
            raw.SourcePlayerId = 1; receiver.Enqueue(raw, 77);
            raw.SourcePlayerId = 2; receiver.Enqueue(raw, 77);
            receiver.Flush();
            CollectionAssert.AreEqual(new[] { 1, 2 }, received, "Flush did not deliver the queued packets in order.");
            posted.Enqueue(() => received.Add(100));
            raw.SourcePlayerId = 3; receiver.Enqueue(raw, 77);
            while (posted.Count != 0) posted.Dequeue()();
            CollectionAssert.AreEqual(new[] { 1, 2, 100, 3 }, received, "A flushed packet was repeated or a later packet moved.");

            // A flush between queueing and a failed post delivers the packet; its unused credit is returned.
            flushInsidePost = true;
            raw.SourcePlayerId = 4; Assert.IsFalse(receiver.Enqueue(raw, 77));
            flushInsidePost = false;
            raw.SourcePlayerId = 5; receiver.Enqueue(raw, 77);
            while (posted.Count != 0) posted.Dequeue()();
            CollectionAssert.AreEqual(new[] { 1, 2, 100, 3, 4, 5 }, received, "A returned credit swallowed a later packet.");
        }
    }
}

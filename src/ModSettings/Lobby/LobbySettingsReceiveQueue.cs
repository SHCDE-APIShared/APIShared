using System;
using System.Collections.Concurrent;
using System.Threading;
using SHCDESE.API.Components.Network;

namespace APIShared.ModSettings
{
    // Only copying and queueing run on the transport callback. Context/authority checks
    // and the original Extender receiver run on the validated Unity thread: at the posted
    // dispatcher turn of each packet, or earlier through Flush. One post exists per queued
    // packet; Flush records credits so the posts of packets it delivered stay no-ops and
    // later packets keep their own dispatcher position.
    internal sealed class LobbySettingsReceiveQueue
    {
        private const int Pending = 0, Delivered = 1, Withdrawn = 2;
        private sealed class Delivery
        {
            internal LobbyModSettingSyncPacket Packet;
            internal ulong? Sender;
            internal long Generation;
            internal int State;
        }

        private readonly ConcurrentQueue<Delivery> queue = new ConcurrentQueue<Delivery>();
        private readonly Func<Action, bool> post;
        private readonly Func<long, bool> validate;
        private readonly Action<LobbyModSettingSyncPacket, ulong?> apply;
        private readonly Action<Exception> report;
        private readonly Action deliverNext;
        private long generation;
        private int flushedCredits;

        internal LobbySettingsReceiveQueue(Func<Action, bool> post, Func<long, bool> validate,
            Action<LobbyModSettingSyncPacket, ulong?> apply, Action<Exception> report)
        {
            this.post = post; this.validate = validate; this.apply = apply; this.report = report;
            deliverNext = DeliverNext;
        }

        internal long Generation => Interlocked.Read(ref generation);
        internal void Invalidate() => Interlocked.Increment(ref generation);

        internal bool Enqueue(LobbyModSettingSyncPacket packet, ulong? sender)
        {
            if (packet == null) return false;
            var delivery = new Delivery
            {
                Generation = Generation, Sender = sender,
                Packet = new LobbyModSettingSyncPacket {
                    ModName = packet.ModName, PropertyName = packet.PropertyName,
                    SerializedValue = packet.SerializedValue == null ? null : (byte[])packet.SerializedValue.Clone(),
                    TypeName = packet.TypeName, SourcePlayerId = packet.SourcePlayerId
                }
            };
            queue.Enqueue(delivery);
            if (post(deliverNext)) return true;
            // Never deliver on the transport thread. A packet Flush already delivered
            // consumed a credit that no post will redeem; return that credit instead.
            if (Interlocked.CompareExchange(ref delivery.State, Withdrawn, Pending) != Pending)
                Interlocked.Decrement(ref flushedCredits);
            return false;
        }

        // Delivers every queued packet now, in FIFO order. Unity thread only.
        internal void Flush()
        {
            while (queue.TryDequeue(out Delivery delivery))
            {
                if (Interlocked.CompareExchange(ref delivery.State, Delivered, Pending) != Pending) continue;
                Interlocked.Increment(ref flushedCredits);
                Deliver(delivery);
            }
        }

        private void DeliverNext()
        {
            int credits;
            while ((credits = Volatile.Read(ref flushedCredits)) > 0)
                if (Interlocked.CompareExchange(ref flushedCredits, credits - 1, credits) == credits) return;
            while (queue.TryDequeue(out Delivery delivery))
            {
                if (Interlocked.CompareExchange(ref delivery.State, Delivered, Pending) != Pending) continue;
                Deliver(delivery);
                return;
            }
        }

        private void Deliver(Delivery delivery)
        {
            try
            {
                if (validate(delivery.Generation)) apply(delivery.Packet, delivery.Sender);
            }
            catch (Exception error) { report(error); }
        }
    }
}

using System;
using System.Collections.Generic;

namespace APIShared
{
    /// <summary>The committed result of a perspective transaction.</summary>
    public enum PlayerPerspectiveChangeReason
    {
        /// <summary>A different native spectator view was confirmed.</summary>
        Selected,
        /// <summary>The API identity override was cleared; reacquire the next session's view.</summary>
        Cleared,
        /// <summary>Neither the requested view nor restoration could be confirmed.</summary>
        Uncertain
    }

    /// <summary>Immutable process-ordered notification. Player -1 means unavailable/uncertain.</summary>
    public sealed class PlayerPerspectiveChange
    {
        internal PlayerPerspectiveChange(long sequence, int previous, int current, PlayerPerspectiveChangeReason reason)
        { Sequence = sequence; PreviousPlayerId = previous; CurrentPlayerId = current; Reason = reason; }
        /// <summary>Monotonically increasing process-local delivery sequence; gaps are permitted.</summary>
        public long Sequence { get; }
        /// <summary>The previously published player, or -1 when uncertain; native neutral view can be 0.</summary>
        public int PreviousPlayerId { get; }
        /// <summary>The confirmed player, or -1 when the override was cleared or the view is uncertain.</summary>
        public int CurrentPlayerId { get; }
        /// <summary>The transaction outcome, distinct from a rejected request with successful rollback.</summary>
        public PlayerPerspectiveChangeReason Reason { get; }
    }

    // Transactions enqueue while holding their gate, then drain outside it. Reentrant changes
    // join the same FIFO, so every observer sees sequence N before sequence N+1.
    internal sealed class PlayerPerspectiveChangeHub
    {
        private readonly object gate = new object();
        private readonly List<Registration> observers = new List<Registration>();
        private readonly Queue<Delivery> pending = new Queue<Delivery>();
        private long sequence;
        private bool draining;
        internal Action<Exception> OnError;

        internal IDisposable Subscribe(string owner, string id, Action<PlayerPerspectiveChange> callback)
        {
            if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("An owner GUID is required.", nameof(owner));
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("An owner-local ID is required.", nameof(id));
            if (callback == null) throw new ArgumentNullException(nameof(callback));
            lock (gate)
            {
                foreach (var observer in observers)
                    if (observer.Owner == owner && observer.Id == id)
                        throw new InvalidOperationException("Perspective observer already registered: " + owner + "/" + id);
                var registration = new Registration(this, owner, id, callback);
                observers.Add(registration);
                return registration;
            }
        }

        internal void Enqueue(int previous, int current, PlayerPerspectiveChangeReason reason)
        {
            if (previous == current && reason == PlayerPerspectiveChangeReason.Selected) return;
            lock (gate)
            {
                long next = ++sequence;
                if (observers.Count == 0) return;
                pending.Enqueue(new Delivery(new PlayerPerspectiveChange(next, previous, current, reason), observers.ToArray()));
            }
        }

        internal void Drain()
        {
            lock (gate) { if (draining) return; draining = true; }
            while (true)
            {
                Delivery delivery;
                lock (gate)
                {
                    if (pending.Count == 0) { draining = false; return; }
                    delivery = pending.Dequeue();
                }
                foreach (var observer in delivery.Observers)
                {
                    try { observer.Callback(delivery.Change); }
                    catch (Exception error) { try { OnError?.Invoke(error); } catch { } }
                }
            }
        }

        private sealed class Delivery
        {
            internal readonly PlayerPerspectiveChange Change;
            internal readonly Registration[] Observers;
            internal Delivery(PlayerPerspectiveChange change, Registration[] observers) { Change = change; Observers = observers; }
        }
        private sealed class Registration : IDisposable
        {
            private readonly PlayerPerspectiveChangeHub hub;
            internal readonly string Owner, Id;
            internal readonly Action<PlayerPerspectiveChange> Callback;
            internal Registration(PlayerPerspectiveChangeHub hub, string owner, string id, Action<PlayerPerspectiveChange> callback)
            { this.hub = hub; Owner = owner; Id = id; Callback = callback; }
            public void Dispose() { lock (hub.gate) hub.observers.Remove(this); }
        }
    }
}

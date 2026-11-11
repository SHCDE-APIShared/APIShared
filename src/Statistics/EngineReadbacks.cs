using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Statistics
{
    /// <summary>Demand-driven raw reads immediately before Vanilla publishes a completed run buffer.
    /// Uses APIShared's existing run/buffer publisher; no additional native or managed hook.
    /// Callbacks run on the simulation caller while it owns EngineInterface.threadLock.
    /// They must only copy bounded native data into prepared private storage: no UI, providers,
    /// allocations, blocking, logging or retained native pointers. Session/rights validation and
    /// main-thread delivery remain the consumer's responsibility. Not a tick or time-advance event.</summary>
    public static class EngineReadbacks
    {
        private static readonly object registrations = new object();
        private static EngineReadbackRegistration[] entries = new EngineReadbackRegistration[0];
        private static readonly Func<EngineReadbackRegistration, RegistrationKey> RegistrationKeyOf =
            entry => new RegistrationKey(entry.Owner, entry.Id);
        private static object gate;
        private static int pending;
        [ThreadStatic] private static int runDepth;
        /// <summary>True after the existing managed run/buffer publisher has initialized successfully.</summary>
        public static bool IsAvailable => Volatile.Read(ref gate) != null;
        /// <summary>Register once after APIShared initialization, with a stable owner GUID and owner-local ID.
        /// Registrations are process-lived and ordered by owner/ID; duplicate IDs are rejected.
        /// No replay occurs. Each Request coalesces until a completed buffer is available; Cancel is logical.
        /// Callback exceptions are isolated and retained for TryTakeError outside the engine lock.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action copy,
            out EngineReadbackRegistration registration)
            => TryRegister(ownerGuid, id, copy, out registration, out _);
        /// <summary>Same contract as the overload without reason; a rejected registration also reports
        /// whether the publisher is unavailable, an argument is missing or the owner-local ID is a duplicate.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action copy,
            out EngineReadbackRegistration registration, out string reason)
        {
            registration = null;
            if (!IsAvailable) { reason = "The engine run/buffer publisher is not initialized."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || copy == null)
            { reason = "Owner GUID, registration ID and copy callback are required."; return false; }
            lock (registrations)
            {
                var current = entries;
                if (OrderedRegistrations.Contains(current, RegistrationKeyOf, ownerGuid, id)) { reason = "Duplicate engine readback registration: " + ownerGuid + "/" + id; return false; }
                registration = new EngineReadbackRegistration(ownerGuid, id, copy);
                Volatile.Write(ref entries, OrderedRegistrations.WithAdded(current, registration, RegistrationKeyOf));
                reason = null;
                return true;
            }
        }
        internal static void Initialize(object engineGate) => Volatile.Write(ref gate, engineGate);
        internal static bool HasPending => Volatile.Read(ref pending) != 0;
        internal static void EnterRun() => runDepth++;
        internal static void ExitRun() => runDepth--;
        internal static void Added() => Interlocked.Increment(ref pending);
        internal static void Removed() => Interlocked.Decrement(ref pending);
        internal static void BeforeBufferPublication()
        {
            var engineGate = Volatile.Read(ref gate);
            if (runDepth <= 0 || !HasPending || engineGate == null || !Monitor.IsEntered(engineGate)) return;
            var current = Volatile.Read(ref entries);
            for (int i = 0; i < current.Length; i++) current[i].Drain();
        }
    }

    /// <summary>Permanent raw-read registration. Request and Cancel may run on any thread;
    /// neither waits for an in-flight callback. Consumer storage must remain owned until that callback
    /// finishes, and stale results must be discarded. Multiple requests coalesce; a request during
    /// a callback is retained for a subsequent publication. No automatic periodic work.</summary>
    public sealed class EngineReadbackRegistration
    {
        internal readonly string Owner, Id;
        private readonly Action copy;
        private int requested;
        private Exception error;
        internal EngineReadbackRegistration(string owner, string id, Action copy)
        { Owner = owner; Id = id; this.copy = copy; }
        /// <summary>Queue a coalesced read. Does not run the callback or touch game state.</summary>
        public void Request()
        {
            // Increment first: publication must never see a false zero after the request is visible.
            EngineReadbacks.Added();
            if (Interlocked.Exchange(ref requested, 1) != 0) EngineReadbacks.Removed();
        }
        /// <summary>Drop queued work. An already running callback must be retired by the consumer.</summary>
        public void Cancel() { if (Interlocked.Exchange(ref requested, 0) != 0) EngineReadbacks.Removed(); }
        /// <summary>Remove the most recent callback exception. Call outside the engine critical section.</summary>
        public bool TryTakeError(out Exception exception) { exception = Interlocked.Exchange(ref error, null); return exception != null; }
        internal void Drain()
        {
            if (Interlocked.Exchange(ref requested, 0) == 0) return;
            EngineReadbacks.Removed();
            try { copy(); } catch (Exception failure) { Interlocked.Exchange(ref error, failure); }
        }
    }
}

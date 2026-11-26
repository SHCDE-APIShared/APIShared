using System;
using System.Threading;

namespace APIShared.Multiplayer
{
    /// <summary>Engine-serialized notifications at Vanilla's common multiplayer repair boundaries.</summary>
    /// <remarks>
    /// Register during startup, after native initialization and before the first world. Callbacks are
    /// process-rooted and ordered by order, owner GUID and registration ID. They run outside registry
    /// locks on the native caller thread, not necessarily Unity. Started runs before executed Chore54;
    /// Completed runs after executed Chore67. Serialization and size measurement emit no notifications.
    /// These are critical simulation callbacks: an exception reports a terminal agreed-world fault.
    /// There is no replay, cancellation, unregistration or synthetic abort-completion notification.
    /// Do not block, touch UI or start an engine run. Consumers own their deterministic state resets.
    /// </remarks>
    public static class MultiplayerResyncEvents
    {
        internal static readonly MultiplayerResyncRegistry Registry = new MultiplayerResyncRegistry(
            MultiplayerResyncNativeRuntime.ReportFault);

        /// <summary>Whether both native boundaries, mission admission owner and terminal engine-run guard initialized successfully.</summary>
        public static bool IsAvailable => MultiplayerResyncNativeRuntime.IsAvailable;
        /// <summary>True during repair with registered participants; without demand the service stays dormant.
        /// False while successful completion callbacks run; a callback failure
        /// restores the fence and reports a terminal fault. World retirement also clears it.</summary>
        public static bool IsInProgress => Registry.IsInProgress;
        /// <summary>Checks the published native entry spans without changing readiness or writing code.</summary>
        /// <remarks>For startup/admission checks, not per-tick polling; requires a live native image.</remarks>
        public static bool CheckIntegrity() => MultiplayerResyncNativeRuntime.CheckIntegrity();

        /// <summary>Registers one critical pair. Returns false for unavailable hooks, invalid or duplicate IDs,
        /// a missing callback, or registration after world admission. Initially no callback is replayed.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action started, Action completed,
            out string reason, int order = 0)
        {
            if (!IsAvailable) { reason = "The shared multiplayer resync boundaries are unavailable."; return false; }
            return Registry.TryRegister(ownerGuid, registrationId, started, completed, out reason, order);
        }
    }

    // Pure lifecycle/dispatch rules. A writer, local engine counter and native transfer's post-state
    // cannot influence this state machine. Only the phase captured at handler ENTRY authorizes a step.
    internal sealed class MultiplayerResyncRegistry
    {
        private sealed class Registration
        {
            internal string Owner, Id;
            internal int Order;
            internal Action Started, Completed;
        }
        private readonly object sync = new object();
        private readonly Action<Exception> fault;
        private Registration[] registrations = Array.Empty<Registration>();
        private Registration[] current = Array.Empty<Registration>();
        private int inProgress, sealedRegistrations, faulted;
        private long world;
        internal MultiplayerResyncRegistry(Action<Exception> fault) => this.fault = fault;
        internal bool IsInProgress => Volatile.Read(ref inProgress) != 0;
        internal bool TryRegister(string owner, string id, Action started, Action completed, out string reason, int order)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || started == null || completed == null)
            { reason = "An owner, registration ID and both callbacks are required."; return false; }
            lock (sync)
            {
                if (sealedRegistrations != 0) { reason = "Resync registrations must precede the first native world."; return false; }
                foreach (var old in registrations)
                    if (old.Owner == owner && old.Id == id) { reason = "Duplicate resync owner/registration ID."; return false; }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration { Owner = owner, Id = id, Order = order, Started = started, Completed = completed };
                Array.Sort(next, (a, b) => {
                    int value = a.Order.CompareTo(b.Order);
                    if (value == 0) value = string.CompareOrdinal(a.Owner, b.Owner);
                    return value != 0 ? value : string.CompareOrdinal(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            reason = string.Empty; return true;
        }
        internal void BeginWorld(long session)
        {
            lock (sync) sealedRegistrations = 1;
            if (world == session) return;
            world = session; Volatile.Write(ref faulted, 0); RetireRepair();
        }
        internal void EndWorld(long session)
        { if (world == session) { world = 0; Volatile.Write(ref faulted, 0); RetireRepair(); } }
        private void RetireRepair()
        { Volatile.Write(ref inProgress, 0); current = Array.Empty<Registration>(); }

        internal void Start(int entryPhase)
        {
            if (entryPhase != 0 || IsInProgress || Volatile.Read(ref registrations).Length == 0) return;
            current = Volatile.Read(ref registrations);
            Volatile.Write(ref inProgress, 1);
            Publish(current, true);
        }
        internal void Complete(int entryPhase)
        {
            if (entryPhase != 0 || !IsInProgress || Volatile.Read(ref faulted) != 0) return;
            // Original67 has fully returned under the engine boundary. Consumers can now publish
            // their admitted logical state. A participant failure re-fences the terminal world.
            Volatile.Write(ref inProgress, 0);
            if (Publish(current, false)) RetireRepair();
            else Volatile.Write(ref inProgress, 1);
        }
        internal bool ValidateNativeState(int nativeRepairState)
        {
            if (!IsInProgress || nativeRepairState != 0) return true;
            Report(new InvalidOperationException("Native repair resumed without the common executed Chore67 completion."));
            return false;
        }
        private bool Publish(Registration[] snapshot, bool starting)
        {
            bool success = true;
            foreach (var item in snapshot)
                try { if (starting) item.Started(); else item.Completed(); }
                catch (Exception error) { success = false; Report(error); }
            return success;
        }
        private void Report(Exception error)
        {
            if (Interlocked.Exchange(ref faulted, 1) != 0) return;
            try { fault?.Invoke(error); } catch { /* No exception may cross a native Chore boundary. */ }
        }
    }
}

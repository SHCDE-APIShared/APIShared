using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using APIShared.Internal;

namespace APIShared.Savegames
{
    /// <summary>Local progress of the existing multiplayer save preparation; no phase proves simulation progress.</summary>
    public enum MultiplayerSavePreparationPhase
    {
        /// <summary>The local selection and file provenance are validated; peer preparation can still be pending.</summary>
        Preparing,
        /// <summary>This client submitted its readiness acknowledgement to the host.</summary>
        Acknowledged,
        /// <summary>The host released this preparation to the ordinary native loader.</summary>
        Released,
        /// <summary>Final local native-start admission succeeded; mission initialization can still fail.</summary>
        NativeLoading,
        /// <summary>The coordinator relinquished preparation ownership.</summary>
        Ended
    }
    /// <summary>Why preparation ownership ended. ScopeFinished is cleanup, not proof of a loaded mission.</summary>
    public enum MultiplayerSavePreparationEndReason
    {
        /// <summary>The preparation is active.</summary>
        None,
        /// <summary>The local selection was cancelled.</summary>
        Cancelled,
        /// <summary>A different save preparation replaced this attempt.</summary>
        Replaced,
        /// <summary>Preparation exceeded its existing coordinator deadline.</summary>
        Timeout,
        /// <summary>Validation, readiness, transport or native admission failed.</summary>
        Rejected,
        /// <summary>Internal load cleanup ended ownership; this does not claim a successfully started mission.</summary>
        ScopeFinished
    }

    /// <summary>Immutable provenance of one authenticated preparation on this computer.</summary>
    /// <remarks>LocalPath and LocalHash describe this peer's own file; different peers' whole-file hashes
    /// need not match. Retained snapshots are observations, never permission to launch or mutate a later world.</remarks>
    public sealed class MultiplayerSavePreparationSnapshot
    {
        internal MultiplayerSavePreparationSnapshot(string operationId, long revision, ulong lobbyId, ulong hostId,
            bool isHost, IEnumerable<ulong> humanPeers, string saveName, int checksum, int time,
            string localPath, string localHash, MultiplayerSavePreparationPhase phase,
            MultiplayerSavePreparationEndReason endReason = MultiplayerSavePreparationEndReason.None, string detail = null)
        {
            OperationId = operationId; Revision = revision; LobbyId = lobbyId; HostId = hostId; IsHost = isHost;
            HumanPeers = Array.AsReadOnly(humanPeers.ToArray()); SaveName = saveName; Checksum = checksum; Time = time;
            LocalPath = localPath; LocalHash = localHash; Phase = phase; EndReason = endReason; Detail = detail;
        }
        /// <summary>Authenticated existing readiness token, shared by the participants.</summary>
        public string OperationId { get; }
        /// <summary>Process-local monotonically increasing publication revision.</summary>
        public long Revision { get; }
        /// <summary>Steam lobby authenticated for this attempt.</summary>
        public ulong LobbyId { get; }
        /// <summary>Authenticated Steam lobby owner.</summary>
        public ulong HostId { get; }
        /// <summary>Whether this computer owns the host preparation.</summary>
        public bool IsHost { get; }
        /// <summary>Detached immutable roster of participating human Steam IDs.</summary>
        public IReadOnlyList<ulong> HumanPeers { get; }
        /// <summary>Native selected save name.</summary>
        public string SaveName { get; }
        /// <summary>Native multiplayer save checksum, not a whole-file hash.</summary>
        public int Checksum { get; }
        /// <summary>Native multiplayer save time identity.</summary>
        public int Time { get; }
        /// <summary>Canonical path to this computer's own selected save.</summary>
        public string LocalPath { get; }
        /// <summary>SHA-256 captured from this computer's selected file; not a shared cross-peer fingerprint.</summary>
        public string LocalHash { get; }
        /// <summary>Current local preparation phase.</summary>
        public MultiplayerSavePreparationPhase Phase { get; }
        /// <summary>Terminal reason, or None while active.</summary>
        public MultiplayerSavePreparationEndReason EndReason { get; }
        /// <summary>Optional local diagnostic explanation; not a machine-readable decision code.</summary>
        public string Detail { get; }
        internal MultiplayerSavePreparationSnapshot With(long revision, MultiplayerSavePreparationPhase phase,
            MultiplayerSavePreparationEndReason reason = MultiplayerSavePreparationEndReason.None, string detail = null)
            => new MultiplayerSavePreparationSnapshot(OperationId, revision, LobbyId, HostId, IsHost, HumanPeers,
                SaveName, Checksum, Time, LocalPath, LocalHash, phase, reason, detail);
    }

    /// <summary>Observes the existing multiplayer save-load coordinator without starting a second operation.</summary>
    public static class MultiplayerSavePreparation
    {
        internal static readonly MultiplayerSavePreparationState State = new MultiplayerSavePreparationState(
            error => ReportError?.Invoke(error));
        internal static Action<Exception> ReportError;
        /// <summary>Current immutable local preparation, or null. Safe to read on any thread.</summary>
        public static MultiplayerSavePreparationSnapshot Current => State.Current;
        /// <summary>Registers a process-lived owner-local observer on the initialized Unity thread.
        /// Callbacks and optional current-state replay run synchronously on Unity, outside registry locks,
        /// in order then ordinal owner/ID order. Exceptions are isolated. Reentrant notifications are queued.
        /// Ending removes that operation from Current before queuing its terminal notification and emits
        /// it once. Reentrant callbacks can already have installed a newer operation when Ended is delivered;
        /// compare OperationId and Revision rather than assuming Current is null during the callback. A replay of
        /// an active state is not a new transition. Use mission lifecycle events to prove actual startup.</summary>
        public static bool TryRegisterObserver(string ownerGuid, string registrationId,
            Action<MultiplayerSavePreparationSnapshot> observer, out string diagnostic, bool replayCurrent = true, int order = 0)
        {
            if (!UnityMainThreadDispatch.IsMainThread)
            { diagnostic = "Save preparation observers must register on the initialized Unity thread."; return false; }
            return State.TryRegister(ownerGuid, registrationId, observer, out diagnostic, replayCurrent, order);
        }
    }

    // Pure production state/dispatch machinery: tests exercise the same implementation.
    internal sealed class MultiplayerSavePreparationState
    {
        private sealed class Observer
        { internal string Owner, Id; internal int Order; internal Action<MultiplayerSavePreparationSnapshot> Callback; }
        private readonly Action<Exception> report;
        private readonly object gate = new object();
        private Observer[] observers = Array.Empty<Observer>();
        private readonly Queue<Action> notifications = new Queue<Action>();
        private readonly Queue<Action> continuations = new Queue<Action>();
        private MultiplayerSavePreparationSnapshot current;
        private bool dispatching;
        private bool runningContinuations;
        private int notificationBoundaries;
        private long revision;
        internal MultiplayerSavePreparationState(Action<Exception> report) { this.report = report; }
        private sealed class NotificationBoundary : IDisposable
        {
            private MultiplayerSavePreparationState state;
            internal NotificationBoundary(MultiplayerSavePreparationState state) { this.state = state; }
            public void Dispose()
            {
                var owner = state; if (owner == null) return; state = null;
                owner.notificationBoundaries--; owner.Drain();
            }
        }
        // Cleanup wrappers still clear Current immediately, but publish callbacks only
        // when the outermost synchronous cleanup has finished. Never waits for a frame.
        internal IDisposable DeferNotifications()
        { notificationBoundaries++; return new NotificationBoundary(this); }
        internal MultiplayerSavePreparationSnapshot Current => Volatile.Read(ref current);
        internal bool CanDeliverSynchronously => !dispatching && notificationBoundaries == 0;
        internal bool TryRegister(string owner, string id, Action<MultiplayerSavePreparationSnapshot> callback,
            out string diagnostic, bool replay = true, int order = 0)
        {
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null)
            { diagnostic = "Owner GUID, registration ID and observer are required."; return false; }
            var entry = new Observer { Owner = owner, Id = id, Order = order, Callback = callback };
            lock (gate)
            {
                if (observers.Any(item => item.Owner == owner && item.Id == id))
                { diagnostic = "Duplicate save preparation observer identity."; return false; }
                observers = observers.Concat(new[] { entry }).OrderBy(item => item.Order)
                    .ThenBy(item => item.Owner, StringComparer.Ordinal).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
            }
            diagnostic = null;
            var snapshot = Current;
            if (replay && snapshot != null) Dispatch(snapshot, new[] { entry });
            return true;
        }
        internal bool Begin(MultiplayerSavePreparationSnapshot snapshot) => Begin(snapshot, null);
        internal bool Begin(MultiplayerSavePreparationSnapshot snapshot, Action afterPreparingDelivered)
        {
            if (snapshot == null || Current != null) throw new InvalidOperationException("A save preparation is already active or missing.");
            var next = snapshot.With(++revision, MultiplayerSavePreparationPhase.Preparing);
            Volatile.Write(ref current, next);
            // This queue is distinct from observer notifications: admission must run
            // outside notification dispatch so later phase observers stay synchronous.
            if (afterPreparingDelivered != null)
                continuations.Enqueue(() => {
                    if (Current?.OperationId == next.OperationId) afterPreparingDelivered();
                });
            Dispatch(next, observers);
            return Current?.OperationId == next.OperationId;
        }
        internal bool Advance(string operation, MultiplayerSavePreparationPhase phase)
        {
            var before = Current;
            if (before == null || before.OperationId != operation) return false;
            if (!Enum.IsDefined(typeof(MultiplayerSavePreparationPhase), phase) || phase == MultiplayerSavePreparationPhase.Ended || phase < before.Phase)
                throw new InvalidOperationException("Invalid save preparation phase transition.");
            if (phase != before.Phase)
            {
                var next = before.With(++revision, phase);
                Volatile.Write(ref current, next); Dispatch(next, observers);
            }
            return Current?.OperationId == operation;
        }
        internal void End(string operation, MultiplayerSavePreparationEndReason reason, string detail)
        {
            var before = Current;
            if (before == null || before.OperationId != operation) return;
            Volatile.Write(ref current, null);
            Dispatch(before.With(++revision, MultiplayerSavePreparationPhase.Ended, reason, detail), observers);
        }
        private void Dispatch(MultiplayerSavePreparationSnapshot snapshot, Observer[] recipients)
        {
            notifications.Enqueue(() => {
                foreach (var item in recipients)
                    try { item.Callback(snapshot); }
                    catch (Exception error) { try { report?.Invoke(error); } catch { } }
            });
            Drain();
        }
        private void Drain()
        {
            if (dispatching || notificationBoundaries != 0) return;
            dispatching = true;
            try { while (notifications.Count != 0) notifications.Dequeue()(); }
            finally { dispatching = false; }
            DrainContinuations();
        }
        private void DrainContinuations()
        {
            if (runningContinuations || !CanDeliverSynchronously) return;
            runningContinuations = true;
            try
            {
                while (continuations.Count != 0 && CanDeliverSynchronously)
                {
                    // Dispatch remains allowed while a continuation runs. Released and
                    // NativeLoading observers must finish before their caller proceeds.
                    try { continuations.Dequeue()(); }
                    catch (Exception error) { try { report?.Invoke(error); } catch { } }
                    Drain();
                }
            }
            finally { runningContinuations = false; }
        }
    }
}

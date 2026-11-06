using System;
using System.Collections.Generic;

namespace APIShared
{
    // The production state machine uses only reference identity, not game-object fields or a timer.
    // Lifecycle capture can happen on a publisher thread; delivery happens only through Pump on Unity.
    internal sealed class MissionPlayStateReadinessState
    {
        private sealed class Observer
        {
            internal string Key;
            internal Action<MissionPlayStateReadyNotification> Callback;
            internal long DeliveredSession, ReplaySession;
        }
        private readonly object gate = new object();
        private readonly SortedDictionary<string, Observer> observers = new SortedDictionary<string, Observer>(StringComparer.Ordinal);
        private readonly Action request;
        private readonly Action<Exception> report;
        private readonly Func<bool> isUnityThread;
        private readonly Func<long, bool> isCurrentSession;
        private long session;
        private object before;
        private MissionContext started, ready;
        private bool delivering, retired;

        internal MissionPlayStateReadinessState(Action request, Action<Exception> report, Func<bool> isUnityThread,
            Func<long, bool> isCurrentSession)
        {
            this.request = request ?? throw new ArgumentNullException(nameof(request)); this.report = report;
            this.isUnityThread = isUnityThread ?? throw new ArgumentNullException(nameof(isUnityThread));
            this.isCurrentSession = isCurrentSession ?? throw new ArgumentNullException(nameof(isCurrentSession));
        }
        internal MissionContext Current { get { lock (gate) return ready != null && isCurrentSession(ready.SessionId) ? ready : null; } }
        internal bool Pending { get { lock (gate) return started != null && ready == null; } }

        internal void Handle(MissionLifecycleNotification notification, object playState)
        {
            if (notification?.Context == null) return;
            long id = notification.Context.SessionId;
            bool wake = false;
            lock (gate)
            {
                if (id <= 0 || id < session || (id == session && retired)) return;
                if (notification.Kind == MissionLifecycleKind.End)
                {
                    if (id != session) return;
                    before = null; started = ready = null; retired = true;
                    return;
                }
                if (id != session)
                {
                    session = id; before = playState; started = ready = null; retired = false;
                }
                // Further BeforeLoad/BeforeNativeStart checkpoints never replace the earliest capture.
                if (notification.Kind == MissionLifecycleKind.Start)
                { started = notification.Context; wake = true; }
            }
            if (wake) request();
        }

        internal bool Register(string owner, string id, Action<MissionPlayStateReadyNotification> callback)
        {
            if (!isUnityThread() || string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || callback == null) return false;
            string key = owner.Length + ":" + owner + id;
            lock (gate)
            {
                if (observers.ContainsKey(key)) return false;
                observers.Add(key, new Observer { Key = key, Callback = callback, ReplaySession = ready?.SessionId ?? 0 });
            }
            return true;
        }

        internal void Pump(object playState)
        {
            if (!isUnityThread()) return;
            lock (gate)
            {
                if (delivering || !isCurrentSession(session)) return;
                if (started != null && ready == null && playState != null && !ReferenceEquals(before, playState))
                { ready = started; before = null; }
                if (ready == null) return;
                delivering = true;
            }
            try
            {
                while (true)
                {
                    Observer next = null;
                    MissionPlayStateReadyNotification notification;
                    lock (gate)
                    {
                        if (ready == null || !isCurrentSession(ready.SessionId)) return;
                        foreach (var observer in observers.Values)
                            if (observer.DeliveredSession != ready.SessionId) { next = observer; break; }
                        if (next == null) return;
                        next.DeliveredSession = ready.SessionId;
                        notification = new MissionPlayStateReadyNotification(ready, next.ReplaySession == ready.SessionId);
                    }
                    try { next.Callback(notification); }
                    catch (Exception error) { try { report?.Invoke(error); } catch { } }
                }
            }
            finally { lock (gate) delivering = false; }
        }
    }
}

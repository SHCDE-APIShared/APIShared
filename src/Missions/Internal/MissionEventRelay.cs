// APIShared-owned implementation; independent of workspace Shared helpers.
// Initial provenance: a7888900e, Shared/GameplaySessionLifecycle.cs. No automatic synchronization.
using APIShared;
using BepInEx.Logging;
using R3;
using System;
using System.Collections.Generic;

namespace APIShared.Internal
{
    // Per-assembly delivery only. Detection and mode decisions live in APIShared.
    internal static class MissionEvents
    {
        private static readonly List<Action<MissionLifecycleNotification>> observers = new List<Action<MissionLifecycleNotification>>();
        private static IMissionLifecycleCapability capability;
        private static MissionLifecycleNotification latest;
#if API_SHARED_PRESET_TESTS
        private static readonly ManualLogSource log = null;
#else
        private static readonly ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("Mission adapter");
#endif
        private static void EnsureConnected()
        {
#if !API_SHARED_PRESET_TESTS
            if (capability != null) return;
            if (!ApiShared.Current.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out var service, out var diagnostic))
                throw new InvalidOperationException("Mission lifecycle unavailable: " + diagnostic?.Reason);
            capability = service;
            if (!service.TryRegisterObserver("Shared.MissionEvents." + typeof(MissionEvents).Assembly.GetName().Name, Deliver, Deliver, Deliver, out diagnostic))
            { capability = null; throw new InvalidOperationException("Mission lifecycle registration failed: " + diagnostic?.Reason); }
#endif
        }
        private static void Deliver(MissionLifecycleNotification notification)
        {
            latest = notification.Kind == MissionLifecycleKind.End ? null : notification;
            foreach (var callback in observers.ToArray())
            {
                try { callback(notification); }
                catch (Exception ex) { Report("Mission subscriber failed: ", ex); }
            }
        }
        private static void Report(string message, Exception exception)
        {
            try { NativeApiLog.Error(log, message + exception); } catch { }
        }
        private static Observable<MissionLifecycleNotification> Observe(Func<MissionLifecycleNotification, bool> predicate, bool replay = false) =>
            new RelayObservable(predicate, replay);
        private sealed class RelayObservable : Observable<MissionLifecycleNotification>
        {
            private readonly Func<MissionLifecycleNotification, bool> predicate;
            private readonly bool replay;
            internal RelayObservable(Func<MissionLifecycleNotification, bool> predicate, bool replay)
            { this.predicate = predicate; this.replay = replay; }
            protected override IDisposable SubscribeCore(Observer<MissionLifecycleNotification> observer)
            {
                long deliveredStart = 0;
                Action<MissionLifecycleNotification> callback = e =>
                {
                    if (!predicate(e)) return;
                    if (e.Kind == MissionLifecycleKind.Start)
                    {
                        if (deliveredStart == e.Context.SessionId) return;
                        deliveredStart = e.Context.SessionId;
                    }
                    observer.OnNext(e);
                };
                observers.Add(callback);
                try
                {
                    EnsureConnected();
                    if (replay && latest?.Kind == MissionLifecycleKind.Start)
                        callback(latest.AsReplay());
                    return new LocalSubscription(callback);
                }
                catch { observers.Remove(callback); throw; }
            }
        }
        internal static Observable<MissionLifecycleNotification> Started => Observe(e => e.Kind == MissionLifecycleKind.Start, true);
        internal static Observable<MissionLifecycleNotification> Ended => Observe(e => e.Kind == MissionLifecycleKind.End);
        internal static Observable<MissionLifecycleNotification> Initialization => Observe(e => e.Kind == MissionLifecycleKind.Initialization);
        internal static Observable<MissionLifecycleNotification> NativeStart => Observe(e => e.Kind == MissionLifecycleKind.Initialization &&
            (e.Phase == MissionInitializationPhase.BeforeNativeStart || e.Phase == MissionInitializationPhase.AfterNativeStart));
        private sealed class LocalSubscription : IDisposable
        {
            private Action<MissionLifecycleNotification> callback;
            internal LocalSubscription(Action<MissionLifecycleNotification> callback) { this.callback = callback; }
            public void Dispose() { observers.Remove(callback); callback = null; }
        }
    }
}

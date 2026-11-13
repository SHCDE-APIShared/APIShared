using System;

namespace APIShared
{
    /// <summary>Immutable receipt that the current managed mission has published a fresh PlayState.</summary>
    public sealed class MissionPlayStateReadyNotification
    {
        internal MissionPlayStateReadyNotification(MissionContext context, bool isReplay)
        { Context = context; IsReplay = isReplay; }
        /// <summary>The managed-start context for this receipt. Recheck the capability's Current before acting later.</summary>
        public MissionContext Context { get; }
        /// <summary>True when registration occurred after this session was already ready.</summary>
        public bool IsReplay { get; }
    }

    /// <summary>Observes the first current-mission PlayState independently of managed Start and wall-clock time.</summary>
    /// <remarks>Readiness means managed initialization completed and a nonnull PlayState replaced the earliest
    /// pre-initialization reference. It does not prove HUD roots, player identity, roster convergence, a selected
    /// perspective or gameplay permissions. Loading, network waiting and pause never expire readiness.
    /// End/replacement invalidates Current immediately; no old receipt authorizes work in another session.</remarks>
    public interface IMissionPlayStateReadinessCapability
    {
        /// <summary>Ready current mission, or null while loading, waiting or outside a mission. Safe to query on any thread.</summary>
        MissionContext Current { get; }
        /// <summary>Registers a process-lived callback on Unity, using a nonempty unique owner-local ID.</summary>
        /// <remarks>Delivery and already-ready replay run on Unity, outside service locks, at most once per session
        /// per registration. Replay may run before registration returns. Reentrant registrations are delivered after
        /// the current callback; a session ended or replaced by a callback is never delivered to remaining observers.
        /// Callbacks are exception-isolated. There is no unregistration or automatic action/UI authorization.</remarks>
        bool TryRegisterObserver(string registrationId, Action<MissionPlayStateReadyNotification> onReady,
            out NativeCapabilityDiagnostic diagnostic);
    }
}

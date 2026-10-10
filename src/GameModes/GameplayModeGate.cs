using System;
using System.Threading;

namespace APIShared.GameModes
{
    /// <summary>Consumer-owned mode gate. Retain it with your process-lifetime runtime and feed it lifecycle notifications before your own feature observers.</summary>
    /// <remarks>No hooks, subscriptions or settings mutations are installed. Update on the lifecycle publisher thread; reads use an atomically published immutable decision. Notification order remains the caller's responsibility.</remarks>
    public sealed class GameplayModeGate
    {
        private sealed class Decision
        {
            internal Decision(GameModeSnapshot snapshot, bool allowed, string reason)
            { Snapshot = snapshot; Allowed = allowed; Reason = reason; }
            internal readonly GameModeSnapshot Snapshot;
            internal readonly bool Allowed;
            internal readonly string Reason;
        }

        private readonly GameplayModActivationProfile profile;
        private Decision decision = new Decision(default, false, "unknown-fail-closed");

        /// <summary>Creates an inactive gate for an arbitrary consumer profile. No automatic lifecycle registration occurs.</summary>
        public GameplayModeGate(GameplayModActivationProfile profile) { this.profile = profile; }
        /// <summary>Latest supplied mode, or an unknown snapshot after End.</summary>
        public GameModeSnapshot Snapshot => Volatile.Read(ref decision).Snapshot;
        /// <summary>Whether the mode permits this profile, independently of configured setting values.</summary>
        public bool IsAllowed => Volatile.Read(ref decision).Allowed;
        /// <summary>Diagnostic reason from the existing optional evaluator.</summary>
        public string Reason => Volatile.Read(ref decision).Reason;
        /// <summary>Raised after an allowed/blocked transition on the Update caller thread. Each listener is isolated; exceptions cannot prevent other listeners or alter the published decision.</summary>
        public event Action<bool> StateChanged;
        /// <summary>Combines the configured setting with the latest mode decision without mutating either.</summary>
        public bool IsEnabled(bool configuredEnabled) => configuredEnabled && IsAllowed;

        /// <summary>Processes Initialization/Start (including ready-session replay) or resets on End. Call before evaluating features for this notification. Null throws; there is no thread dispatch or replay of this event.</summary>
        public void Update(MissionLifecycleNotification notification)
        {
            if (notification == null) throw new ArgumentNullException(nameof(notification));
            Update(notification.Kind == MissionLifecycleKind.End ? default : notification.Context.Mode);
        }

        /// <summary>Publishes a supplied snapshot using the unchanged optional profile policy. Useful when the consumer already owns lifecycle delivery; default resets to inactive.</summary>
        public void Update(GameModeSnapshot snapshot)
        {
            bool allowed = GameplayModModePolicy.IsAllowed(profile, snapshot, out string reason);
            Decision previous = Interlocked.Exchange(ref decision, new Decision(snapshot, allowed, reason));
            if (previous.Allowed == allowed) return;
            foreach (Delegate listener in StateChanged?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<bool>)listener)(allowed); }
                catch { /* A consumer listener must not break other lifecycle observers. */ }
            }
        }
    }
}

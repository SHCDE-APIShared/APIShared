namespace APIShared.Units
{
    // MissionLifecycleState intentionally queues observer delivery across reentrant replacements.
    // Archive Prepared runs synchronously, so its current-session receipt can be newer than a
    // queued BeforeLoad/End. Never let those old notifications erase newly prepared identity data.
    internal sealed class UnitIdentityLifetime
    {
        internal long PreparedSession { get; private set; }
        internal void Prepared(long session) { PreparedSession = session; }
        internal void Clear() { PreparedSession = 0; }
        internal bool ClearBeforeLoad(long notificationSession, long activeSession, bool nativeWorldAvailable) =>
            notificationSession == activeSession && !nativeWorldAvailable && PreparedSession != activeSession;
        internal bool ClearEnded(long notificationSession, long activeSession) =>
            activeSession == 0 && (PreparedSession == 0 || PreparedSession == notificationSession);
        internal static bool CanApply(long notificationSession, long activeSession, long worldSession) =>
            activeSession != 0 && notificationSession == activeSession && worldSession == activeSession;
    }
}

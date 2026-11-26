using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Savegames
{
    /// <summary>A successful raw native restore, before the first simulation fold.</summary>
    public readonly struct NativeWorldRestoreContext
    {
        internal NativeWorldRestoreContext(long invocation, MissionContext mission)
        { InvocationId = invocation; Mission = mission; }
        /// <summary>Process-local restore invocation identity; never persist or synchronize it.</summary>
        public long InvocationId { get; }
        /// <summary>Current pending managed mission, or null when no managed context exists.
        /// It is not a MissionStarted notification or a guarantee of PlayState readiness.</summary>
        public MissionContext Mission { get; }
    }

    /// <summary>Owned ordered participants at the existing raw87D0 restore boundary.</summary>
    public static class NativeWorldRestoreEvents
    {
        private sealed class Entry
        {
            internal RegistrationKey Key;
            internal Func<NativeWorldRestoreContext, bool> Callback;
        }
        private static readonly object Gate = new object();
        private static readonly Func<Entry, RegistrationKey> Key = entry => entry.Key;
        private static Entry[] entries = Array.Empty<Entry>();
        private static long invocation, failures;
        /// <summary>Whether the single process-owned raw restore hook is published.</summary>
        public static bool IsAvailable => Units.AiMilitary.Native.AiMilitaryNativeObservers.IsReady && Internal.EngineMonitor.Current != null;
        /// <summary>Number of isolated participant exceptions.</summary>
        public static long CallbackFailures => Interlocked.Read(ref failures);
        /// <summary>Register once after native initialization, before the first relevant load.
        /// Ordered by order, ordinal owner and registration ID; no replay or removal. Runs outside
        /// registry locks, on the native restore caller under its engine monitor, after existing
        /// internal restore participants but before AI restoration/folds. Return false or throw to
        /// reject native initialization; original87D0 is never retried. Callbacks must not initiate
        /// another load/save or dispatch UI. Decode private candidates in VersionedSaveData.Prepared;
        /// apply them here only for the matching mission. NoRecord may explicitly restore empty state.
        /// Registration does not synchronize state or grant multiplayer authority.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<NativeWorldRestoreContext, bool> callback, out string reason, int order = 0) =>
            TryRegisterCore(ownerGuid, registrationId, callback, out reason, order, IsAvailable);

        internal static bool TryRegisterCore(string ownerGuid, string registrationId,
            Func<NativeWorldRestoreContext, bool> callback, out string reason, int order, bool available)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || callback == null)
            { reason = "Owner, registration ID and callback are required."; return false; }
            if (!available) { reason = "The raw native restore owner is unavailable; register after native initialization."; return false; }
            lock (Gate)
            {
                if (OrderedRegistrations.Contains(entries, Key, ownerGuid, registrationId))
                { reason = "Duplicate native restore participant."; return false; }
                Volatile.Write(ref entries, OrderedRegistrations.WithAdded(entries,
                    new Entry { Key = new RegistrationKey(ownerGuid, registrationId, order), Callback = callback }, Key));
            }
            reason = ""; return true;
        }
        internal static bool Restore()
        {
            if (Volatile.Read(ref entries).Length == 0) return true;
            object engine = Internal.EngineMonitor.Current;
            if (engine == null) return false;
            lock (engine)
                return Dispatch(new NativeWorldRestoreContext(Interlocked.Increment(ref invocation), MissionLifecycleService.ActiveContext));
        }
        internal static bool Dispatch(NativeWorldRestoreContext context)
        {
            var snapshot = Volatile.Read(ref entries);
            foreach (var entry in snapshot)
                try { if (!entry.Callback(context)) return false; }
                catch (Exception error) { Interlocked.Increment(ref failures); InterceptionDiagnostics.Report("NativeWorldRestoreEvents/participant", error); return false; }
            return true;
        }
    }
}

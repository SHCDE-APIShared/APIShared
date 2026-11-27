using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Ordered Unity playback rules for safely attributed native unit speech, including civilian comments,
    /// recruitment and special actions. SFX, Lord dialogue, music and unknown speech remain outside this broker.</summary>
    public static class UnitVoiceEvents
    {
        private static readonly object DemandSync = new object();
        private static int enabledCount;
        [ThreadStatic] private static UnitVoiceContext filteringContext;
        [ThreadStatic] private static int callbackDepth;
        internal static bool InCallback => callbackDepth != 0;
        internal static bool Demand => Volatile.Read(ref enabledCount) != 0;
        internal static readonly InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs> Registry =
            new InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>(hasDemand: () => Demand);
        private static readonly Func<UnitVoicePreEventArgs, object, UnitVoicePostEventArgs> PostFactory =
            (pre, state) => new UnitVoicePostEventArgs(pre, state);
        /// <summary>True when the shared managed playback and native output contracts are ready.
        /// Check individual source availability before relying on a particular generator family.</summary>
        public static bool Available => UnitVoiceRuntime.Status == "Ready";
        /// <summary>Initialization or compatibility diagnostic. Unsupported paths remain Vanilla.</summary>
        public static string Diagnostic => UnitVoiceRuntime.Status;
        /// <summary>Reports readiness of one ordinal source-family key from UnitVoiceCatalog.SourceKeys.
        /// Unknown keys return false; other available source families remain independent.</summary>
        public static bool IsSourceAvailable(string sourceKey) => Available &&
            UnitVoiceNativeRuntime.GetSourceStatus(sourceKey) == "Ready";
        /// <summary>Returns the source-family diagnostic, including shared pipeline unavailability.
        /// Unknown or null keys return an unavailable diagnostic instead of throwing.</summary>
        public static string GetSourceDiagnostic(string sourceKey)
        {
            if (!UnitVoiceCatalog.IsKnownSourceKey(sourceKey)) return "Unavailable: unknown unit voice source key.";
            return UnitVoiceRuntime.ManagedReady ? UnitVoiceNativeRuntime.GetSourceStatus(sourceKey) : Diagnostic;
        }
        /// <summary>Isolated callback failures; a throwing Pre rolls back only its own mutations and State.</summary>
        public static long CallbackFailures => Registry.CallbackFailures;
        /// <summary>Registers from any thread, including before runtime initialization. Callbacks begin only after the
        /// runtime is ready and run synchronously on Unity without replay,
        /// ordered by order, ordinal owner GUID and registration ID, outside registry locks. Both callbacks are optional,
        /// but at least one is required. Selections and activation freeze per request, keeping matching Pre/Post pairs.
        /// Recursive calls from Pre/Post bypass notifications. No I/O, decoding or blocking in callbacks. Handles and hooks
        /// persist until exit. Suppression affects presentation only. Post means normal return, never audible acceptance.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<UnitVoicePreEventArgs> pre,
            Action<UnitVoicePostEventArgs> post, out UnitVoiceRegistration registration, out string reason, int order = 0)
            => Register(ownerGuid, registrationId, pre, post, out registration, out reason, order);
        /// <summary>Registers with an immutable source selection as the third argument. The threading, ordering,
        /// failure, lifetime and Pre/Post contracts are identical to the unrestricted overload. A null selection is rejected.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, UnitVoiceSelection sources,
            Action<UnitVoicePreEventArgs> pre, Action<UnitVoicePostEventArgs> post,
            out UnitVoiceRegistration registration, out string reason, int order = 0)
        {
            registration = null;
            if (sources == null) { reason = "A unit voice selection is required."; return false; }
            return Register(ownerGuid, registrationId, pre, post, out registration, out reason, order, sources);
        }
        internal static bool Register(string ownerGuid, string registrationId, Action<UnitVoicePreEventArgs> pre,
            Action<UnitVoicePostEventArgs> post, out UnitVoiceRegistration registration, out string reason, int order = 0,
            UnitVoiceSelection sources = null, InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs> registry = null)
        {
            registration = null;
            var candidate = new UnitVoiceRegistration(sources ?? UnitVoiceSelection.All);
            if (!(registry ?? Registry).TryRegister(ownerGuid, registrationId,
                pre == null ? (Action<UnitVoicePreEventArgs>)null : args => CallPre(pre, args),
                post == null ? (Action<UnitVoicePostEventArgs>)null : args => CallPost(post, args), out reason, order,
                isActive: () => candidate.Enabled && (filteringContext == null || candidate.Sources.Matches(filteringContext.Source)))) return false;
            // Raise demand before the handle is returned. No enabled published handle can meet false demand.
            lock (candidate.ActivationSync)
            {
                lock (DemandSync)
                {
                    Volatile.Write(ref enabledCount, enabledCount + 1); candidate.Attached = true;
                    UnitVoiceNativeRuntime.RefreshDemand();
                    Volatile.Write(ref candidate.EnabledValue, 1);
                }
            }
            registration = candidate; return true;
        }
        internal static void SetEnabled(UnitVoiceRegistration registration, bool value)
        {
            lock (registration.ActivationSync)
            {
                if (registration.Enabled == value) return;
                lock (DemandSync)
                {
                    if (value)
                    {
                        if (registration.Attached) Volatile.Write(ref enabledCount, enabledCount + 1);
                        if (registration.Attached) UnitVoiceNativeRuntime.RefreshDemand();
                        Volatile.Write(ref registration.EnabledValue, 1);
                    }
                    else
                    {
                        Volatile.Write(ref registration.EnabledValue, 0);
                        if (registration.Attached) Volatile.Write(ref enabledCount, enabledCount - 1);
                        if (registration.Attached) UnitVoiceNativeRuntime.RefreshDemand();
                    }
                }
            }
        }
        private static void CallPre(Action<UnitVoicePreEventArgs> callback, UnitVoicePreEventArgs args)
        { callbackDepth++; try { callback(args); } finally { callbackDepth--; } }
        private static void CallPost(Action<UnitVoicePostEventArgs> callback, UnitVoicePostEventArgs args)
        { callbackDepth++; try { callback(args); } finally { callbackDepth--; } }

        internal static void Dispatch(UnitVoiceContext context, Action<UnitVoicePreEventArgs> playback,
            InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs> registry = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (playback == null) throw new ArgumentNullException(nameof(playback));
            registry = registry ?? Registry;
            var pre = new UnitVoicePreEventArgs(context);
            if (!registry.CanNotify) { pre.Freeze(); playback(pre); return; }
            var previous = filteringContext;
            InterceptionEvent<UnitVoicePreEventArgs, UnitVoicePostEventArgs>.Invocation invocation;
            filteringContext = context;
            try { invocation = registry.Begin(pre); }
            finally { filteringContext = previous; }
            if (!pre.SkipOriginalFunction) playback(pre);
            if (invocation.HasPostObservers) invocation.Complete(pre, PostFactory);
        }
    }
}

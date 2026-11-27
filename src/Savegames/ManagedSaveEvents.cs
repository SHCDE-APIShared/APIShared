using System;
using System.Reflection;
using System.Threading;
using APIShared.Events;
using HarmonyLib;

namespace APIShared.Savegames
{
    /// <summary>Arguments observed before the managed save call on its calling thread.</summary>
    public sealed class ManagedSaveCall
    {
        internal ManagedSaveCall(string path, int screenCentreX, int screenCentreY,
            int realScreenCentreX, int realScreenCentreY, bool lockMap, bool tempLockOnly, bool mapSave)
        {
            Path = path;
            ScreenCentreX = screenCentreX;
            ScreenCentreY = screenCentreY;
            RealScreenCentreX = realScreenCentreX;
            RealScreenCentreY = realScreenCentreY;
            LockMap = lockMap;
            TempLockOnly = tempLockOnly;
            MapSave = mapSave;
        }

        /// <summary>Destination path as seen by the observer prefix.</summary>
        public string Path { get; }
        /// <summary>Saved screen centre X.</summary>
        public int ScreenCentreX { get; }
        /// <summary>Saved screen centre Y.</summary>
        public int ScreenCentreY { get; }
        /// <summary>Saved real screen centre X.</summary>
        public int RealScreenCentreX { get; }
        /// <summary>Saved real screen centre Y.</summary>
        public int RealScreenCentreY { get; }
        /// <summary>Whether Vanilla was asked to lock the map.</summary>
        public bool LockMap { get; }
        /// <summary>Whether the requested map lock is temporary only.</summary>
        public bool TempLockOnly { get; }
        /// <summary>Whether the request is a map export.</summary>
        public bool MapSave { get; }
    }

    /// <summary>Completion seen by the save observer finalizer.</summary>
    public sealed class ManagedSaveCompletion
    {
        internal ManagedSaveCompletion(ManagedSaveCall call, string path, bool ranOriginal, bool result, Exception error)
        {
            Call = call;
            Path = path;
            WasSkipped = !ranOriginal;
            Result = ranOriginal && error == null ? result : (bool?)null;
            Exception = error;
        }

        /// <summary>The same immutable call object passed to Begin.</summary>
        public ManagedSaveCall Call { get; }
        /// <summary>Destination as seen by the observer finalizer. Other patches can rewrite it.</summary>
        public string Path { get; }
        /// <summary>Harmony reports that a prefix skipped the original.</summary>
        public bool WasSkipped { get; }
        /// <summary>Managed result at the finalizer, or null on a skip or an exception.</summary>
        public bool? Result { get; }
        /// <summary>Exception at the finalizer, if any.</summary>
        public Exception Exception { get; }
    }

    /// <summary>Observes the normal managed save entry, including map export.</summary>
    public static class ManagedSaveEvents
    {
        private sealed class Registration
        {
            internal readonly RegistrationKey Key;
            internal readonly Func<ManagedSaveCall, object> Begin;
            internal readonly Action<ManagedSaveCompletion, object> End;
            internal long Failures;

            internal Registration(RegistrationKey key, Func<ManagedSaveCall, object> begin,
                Action<ManagedSaveCompletion, object> end)
            {
                Key = key;
                Begin = begin;
                End = end;
            }
        }

        private sealed class Invocation
        {
            internal readonly ManagedSaveCall Call;
            internal readonly Registration[] Observers;
            internal readonly object[] States;
            internal int CompletionClaimed;

            internal Invocation(ManagedSaveCall call, Registration[] observers)
            {
                Call = call;
                Observers = observers;
                States = new object[observers.Length];
            }
        }

        private static readonly Harmony Hook = new Harmony("APIShared.ManagedSaveEvents");
        private static readonly object Gate = new object();
        private static readonly Func<Registration, RegistrationKey> GetKey = entry => entry.Key;
        private static Registration[] registrations = Array.Empty<Registration>();
        private static string installFailure;

        /// <summary>Registers two passive callbacks for the process lifetime, without replay.</summary>
        /// <remarks>
        /// Register during startup. Both callbacks run on the caller thread in ascending order,
        /// owner GUID, and registration ID (ordinal), using one snapshot per call.
        /// Begin returns state visible only to its paired completion. Nested calls have their own
        /// state. Callback failures are isolated. Observers cannot veto or rewrite the save, must
        /// not initiate another save, and cannot attest that a file was committed to disk.
        /// Arguments and outcome are snapshots at the observer's Harmony prefix and finalizer.
        /// Completion is published once, even if Harmony re-enters the finalizer.
        /// Synchronized native saves that bypass this managed method emit no notifications.
        /// </remarks>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<ManagedSaveCall, object> begin, Action<ManagedSaveCompletion, object> end,
            out string reason, int order = 0) =>
            TryRegister(ResolveTarget, ownerGuid, registrationId, begin, end, out reason, order);

        internal static bool TryRegister(Func<MethodInfo> resolveTarget, string ownerGuid, string registrationId,
            Func<ManagedSaveCall, object> begin, Action<ManagedSaveCompletion, object> end,
            out string reason, int order)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) ||
                begin == null || end == null)
            {
                reason = "Owner GUID, registration ID and both callbacks are required.";
                return false;
            }

            lock (Gate)
            {
                if (installFailure != null)
                {
                    reason = installFailure;
                    return false;
                }
                if (OrderedRegistrations.Contains(registrations, GetKey, ownerGuid, registrationId))
                {
                    reason = "The owner and registration ID are already registered.";
                    return false;
                }
                if (registrations.Length == 0)
                {
                    try
                    {
                        var target = resolveTarget();
                        if (target == null || target.ReturnType != typeof(bool))
                            throw new MissingMethodException("EngineInterface.SaveSaveGame");
                        Hook.Patch(target,
                            prefix: new HarmonyMethod(typeof(ManagedSaveEvents), nameof(Begin)) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(ManagedSaveEvents), nameof(End)) { priority = Priority.Last });
                    }
                    catch (Exception error)
                    {
                        // Harmony can retain patch metadata when wrapper generation fails.
                        // Keep any partially installed callbacks inert for this process.
                        reason = installFailure = error.Message;
                        InterceptionDiagnostics.Report("ManagedSaveEvents/install", error);
                        return false;
                    }
                }

                var entry = new Registration(new RegistrationKey(ownerGuid, registrationId, order), begin, end);
                Volatile.Write(ref registrations, OrderedRegistrations.WithAdded(registrations, entry, GetKey));
            }
            reason = string.Empty;
            return true;
        }

        private static MethodInfo ResolveTarget() => typeof(EngineInterface).GetMethod("SaveSaveGame",
            BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string),
                typeof(int), typeof(int), typeof(int), typeof(int), typeof(bool), typeof(bool), typeof(bool) }, null);

        // Installed HarmonyX runs subsequent prefixes after a veto by default.
        // Harmony owns this state per invocation, including nested calls and exceptions.
        private static void Begin(string __0, int __1, int __2, int __3, int __4, bool __5, bool __6, bool __7,
            out Invocation __state)
        {
            __state = null;
            var observers = Volatile.Read(ref registrations);
            if (observers.Length == 0) return;
            var call = new ManagedSaveCall(__0, __1, __2, __3, __4, __5, __6, __7);
            var invocation = __state = new Invocation(call, observers);
            for (int i = 0; i < observers.Length; i++)
            {
                var observer = observers[i];
                try { invocation.States[i] = observer.Begin(call); }
                catch (Exception error) { Report(observer, error); }
            }
        }

        private static void End(string __0, bool __runOriginal, bool __result, Exception __exception, Invocation __state)
        {
            var invocation = __state;
            // A later Harmony finalizer can throw and cause a second finalizer pass.
            if (invocation == null || Interlocked.Exchange(ref invocation.CompletionClaimed, 1) != 0) return;
            var completion = new ManagedSaveCompletion(invocation.Call, __0, __runOriginal, __result, __exception);
            for (int i = 0; i < invocation.Observers.Length; i++)
            {
                var observer = invocation.Observers[i];
                try { observer.End(completion, invocation.States[i]); }
                catch (Exception error) { Report(observer, error); }
            }
        }

        private static void Report(Registration observer, Exception error)
        {
            if (FailureThrottle.ShouldReport(ref observer.Failures))
                InterceptionDiagnostics.Report("ManagedSaveEvents", error);
        }
    }
}

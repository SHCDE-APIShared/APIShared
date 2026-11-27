using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Simulation
{
    /// <summary>Observes calls admitted to the existing managed engine-run wrapper.</summary>
    public static class EngineRunEvents
    {
        private static readonly EngineRunObserverRegistry Observers = new EngineRunObserverRegistry(
            error => InterceptionDiagnostics.Error?.Invoke("EngineRunEvents", error));

        /// <summary>Registers a pair of synchronous callbacks for the process lifetime.</summary>
        /// <remarks>
        /// Register after APIShared managed initialization. Both callbacks run on the calling
        /// thread, in ascending order, owner GUID and registration ID (ordinal). The same
        /// registration snapshot is used for both phases. Nested calls have their own pair.
        /// Completion also runs after a zero result or an original exception and does not
        /// establish that simulation advanced. Calls rejected by engine admission emit no pair.
        /// Callback failures are isolated. Callbacks cannot veto or replace the original call,
        /// and must not access Unity UI or initiate an engine run. There is no replay.
        /// </remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, Action begin, Action end,
            out string reason, int order = 0)
        {
            if (!MarkedUnitSelectionAPI.HasRunBoundary)
            {
                reason = "The shared engine-run boundary is unavailable.";
                return false;
            }

            return Observers.TryRegister(ownerGuid, registrationId, begin, end, out reason, order);
        }

        internal static int Invoke(bool mpFrameSkip, Func<bool, int> original) =>
            Observers.Invoke(mpFrameSkip, original);
    }

    internal sealed class EngineRunObserverRegistry
    {
        private sealed class Registration
        {
            internal readonly RegistrationKey Key;
            internal readonly Action Begin, End;
            internal long Failures;

            internal Registration(RegistrationKey key, Action begin, Action end)
            {
                Key = key;
                Begin = begin;
                End = end;
            }
        }

        private static readonly Func<Registration, RegistrationKey> GetKey = entry => entry.Key;
        private readonly object sync = new object();
        private readonly Action<Exception> report;
        private Registration[] registrations = Array.Empty<Registration>();

        internal EngineRunObserverRegistry(Action<Exception> report = null) => this.report = report;

        internal bool TryRegister(string ownerGuid, string registrationId, Action begin, Action end,
            out string reason, int order)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) ||
                begin == null || end == null)
            {
                reason = "Owner GUID, registration ID and both callbacks are required.";
                return false;
            }

            lock (sync)
            {
                if (OrderedRegistrations.Contains(registrations, GetKey, ownerGuid, registrationId))
                {
                    reason = "The owner and registration ID are already registered.";
                    return false;
                }

                var entry = new Registration(new RegistrationKey(ownerGuid, registrationId, order), begin, end);
                Volatile.Write(ref registrations, OrderedRegistrations.WithAdded(registrations, entry, GetKey));
            }

            reason = string.Empty;
            return true;
        }

        internal int Invoke(bool mpFrameSkip, Func<bool, int> original)
        {
            var snapshot = Volatile.Read(ref registrations);
            if (snapshot.Length == 0) return original(mpFrameSkip);

            try
            {
                foreach (var observer in snapshot) Notify(observer, observer.Begin);
                return original(mpFrameSkip);
            }
            finally
            {
                foreach (var observer in snapshot) Notify(observer, observer.End);
            }
        }

        private void Notify(Registration observer, Action callback)
        {
            try { callback(); }
            catch (Exception error)
            {
                if (report == null || !FailureThrottle.ShouldReport(ref observer.Failures)) return;
                try { report(error); }
                catch { }
            }
        }
    }
}

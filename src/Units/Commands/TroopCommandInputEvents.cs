using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Events;
using CrusaderDE;

namespace APIShared.Units
{
    /// <summary>One local troop-command release observed before Vanilla processes it.</summary>
    public sealed class TroopCommandInput
    {
        internal TroopCommandInput(int x, int y, int[] units)
        {
            X = x;
            Y = y;
            UnitsUnderCursor = Array.AsReadOnly(units);
        }

        /// <summary>Final local map-tile X passed to the simulation tick.</summary>
        public int X { get; }
        /// <summary>Final local map-tile Y passed to the simulation tick.</summary>
        public int Y { get; }
        /// <summary>One-based unit IDs from the native selection submission in cursor order.</summary>
        public IReadOnlyList<int> UnitsUnderCursor { get; }
    }

    /// <summary>Observes local troop-command releases after Extender selection processing.</summary>
    public static class TroopCommandInputEvents
    {
        private sealed class Registration
        {
            internal readonly RegistrationKey Key;
            internal readonly Action<TroopCommandInput> Callback;
            internal long Failures;
            internal Registration(RegistrationKey key, Action<TroopCommandInput> callback)
            { Key = key; Callback = callback; }
        }

        private static readonly object Gate = new object();
        private static readonly Func<Registration, RegistrationKey> GetKey = item => item.Key;
        private static Registration[] registrations = Array.Empty<Registration>();
        [ThreadStatic] private static bool inRun;
        [ThreadStatic] private static int[] pending;

        /// <summary>Registers a synchronous, process-lifetime observer.</summary>
        /// <remarks>
        /// Register after APIShared managed initialization. Notifications run on the engine
        /// thread under its existing monitor in ascending order, owner GUID and registration ID
        /// (ordinal). They describe a local command release, without classifying its action or
        /// confirming native acceptance. Extender replacement selections are excluded. Callbacks
        /// must not issue engine commands, access Unity UI or treat local input as multiplayer
        /// authority. Callback exceptions are isolated. No replay.
        /// </remarks>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Action<TroopCommandInput> callback, out string reason, int order = 0)
        {
            if (!TroopCommandInputHooks.IsReady || !MarkedUnitSelectionAPI.HasRunBoundary)
            {
                reason = "The shared troop-command input boundary is unavailable.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || callback == null)
            {
                reason = "Owner GUID, registration ID and callback are required.";
                return false;
            }
            lock (Gate)
            {
                if (OrderedRegistrations.Contains(registrations, GetKey, ownerGuid, registrationId))
                {
                    reason = "The owner and registration ID are already registered.";
                    return false;
                }
                var entry = new Registration(new RegistrationKey(ownerGuid, registrationId, order), callback);
                Volatile.Write(ref registrations, OrderedRegistrations.WithAdded(registrations, entry, GetKey));
            }
            reason = string.Empty;
            return true;
        }

        internal static int Run(bool mpFrameSkip, Func<bool, int> original)
        {
            if (Volatile.Read(ref registrations).Length == 0) return original(mpFrameSkip);
            var previous = pending;
            bool nested = inRun;
            inRun = true;
            pending = null;
            try { return original(mpFrameSkip); }
            finally { pending = previous; inRun = nested; }
        }

        internal static bool IsCommandRelease(bool classicControls, int mouseState, bool rightUp,
            bool selecting, bool established, int screenX, int screenY) =>
            (classicControls ? mouseState == 3 : rightUp) && !selecting && !established &&
            screenX >= 0 && screenY >= 0;

        internal static void Capture(int state, byte rightUp, byte selecting, byte established,
            uint count, ulong address, int screenX, int screenY)
        {
            if (!inRun) return;
            pending = null;
            if (!IsCommandRelease(ConfigSettings.Settings_SH1RTSControls, state, rightUp != 0,
                selecting != 0, established != 0, screenX, screenY)) return;
            if (count == 0) { pending = Array.Empty<int>(); return; }
            // The Extender receives a pointer pinned by the managed wrapper for this call.
            if (address == 0 || count > 10000) return;
            var units = new int[count];
            Marshal.Copy(new IntPtr(unchecked((long)address)), units, 0, units.Length);
            pending = RemoveFilteredUnits(units);
        }

        internal static int[] RemoveFilteredUnits(int[] units)
        {
            int count = 0;
            foreach (int unit in units)
                if (unit > 0) units[count++] = unit;
            if (count == units.Length) return units;
            if (count == 0) return Array.Empty<int>();
            Array.Resize(ref units, count);
            return units;
        }

        internal static void Publish(int x, int y)
        {
            var units = pending;
            pending = null;
            if (!inRun || units == null || x < 0 || y < 0) return;
            var input = new TroopCommandInput(x, y, units);
            foreach (var registration in Volatile.Read(ref registrations))
            {
                try { registration.Callback(input); }
                catch (Exception error)
                {
                    if (FailureThrottle.ShouldReport(ref registration.Failures))
                        InterceptionDiagnostics.Report("TroopCommandInputEvents", error);
                }
            }
        }
    }
}

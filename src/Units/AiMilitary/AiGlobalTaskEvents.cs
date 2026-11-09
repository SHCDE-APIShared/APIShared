using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Units.AiMilitary
{
    /// <summary>One native rebuilding of the shared siege-task lists. NativeManager is an immediate,
    /// invocation-local diagnostic view. Completion identifies whose lists are currently published;
    /// it does not mean that a unit received or completed any task.</summary>
    public sealed class AiGlobalTaskObservation
    {
        internal AiGlobalTaskObservation(long invocation, IntPtr manager, int owner, int target, bool completed)
        { InvocationId = invocation; NativeManager = manager; OwnerPlayerId = owner; TargetPlayerId = target; OriginalCompleted = completed; }
        /// <summary>Process-local invocation identity pairing before/after.</summary>
        public long InvocationId { get; }
        /// <summary>Native task manager for immediate diagnostics; never retain it.</summary>
        public IntPtr NativeManager { get; }
        /// <summary>Actual one-based AI owner.</summary>
        public int OwnerPlayerId { get; }
        /// <summary>Actual target player.</summary>
        public int TargetPlayerId { get; }
        /// <summary>Whether the original list builder returned normally.</summary>
        public bool OriginalCompleted { get; }
    }

    /// <summary>Shared process-lived observation of native global siege-task generation. Register after
    /// LibraryLoaded; callbacks use the original caller thread with no replay, dispatch or cancellation.
    /// Ordered by ordinal owner/ID, exceptions isolated, original invoked exactly once.</summary>
    public static class AiGlobalTaskEvents
    {
        private static readonly object sync = new object();
        private static Entry[] entries = new Entry[0];
        private static long invocation;
        /// <summary>True when the single native publisher is validated and installed.</summary>
        public static bool IsAvailable => Native.AiMilitaryNativeObservers.IsReady;
        /// <summary>Registers one unique owner-local pair; at least one callback is required.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action<AiGlobalTaskObservation> before,
            Action<AiGlobalTaskObservation> after, out string reason)
        {
            if (!IsAvailable) { reason = "The native global task publisher is unavailable."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || before == null && after == null)
            { reason = "Owner, registration ID and a callback are required."; return false; }
            lock (sync)
            {
                if (OrderedRegistrations.Contains(entries, Entry.Key, ownerGuid, id)) { reason = "Duplicate global task registration."; return false; }
                Volatile.Write(ref entries, OrderedRegistrations.WithAdded(entries, new Entry(ownerGuid, id, before, after), Entry.Key));
                Native.AiMilitaryNativeObservers.EnableObservation();
            }
            reason = string.Empty; return true;
        }
        internal static Entry[] Snapshot => Volatile.Read(ref entries);
        internal static long NextInvocation() => Interlocked.Increment(ref invocation);
        internal static void Publish(Entry[] snapshot, bool before, AiGlobalTaskObservation observation)
        {
            foreach (var entry in snapshot)
                try { (before ? entry.Before : entry.After)?.Invoke(observation); }
                catch (Exception error)
                {
                    try { Internal.RuntimeHealth.Failure(ApiSharedRuntime.ProcessInstance.Logger,
                        "ai-global-task-observer/" + entry.Owner + "/" + entry.Id, true, "Global task observer failed: " + error); } catch { }
                }
        }
        internal sealed class Entry
        {
            internal Entry(string owner, string id, Action<AiGlobalTaskObservation> before, Action<AiGlobalTaskObservation> after)
            { Owner = owner; Id = id; Before = before; After = after; }
            internal readonly string Owner, Id;
            internal readonly Action<AiGlobalTaskObservation> Before, After;
            internal static readonly Func<Entry, RegistrationKey> Key = entry => new RegistrationKey(entry.Owner, entry.Id);
        }
    }
}

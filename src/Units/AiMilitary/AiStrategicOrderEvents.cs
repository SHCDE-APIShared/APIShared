using System;
using System.Threading;

namespace APIShared.Units.AiMilitary
{
    /// <summary>Immutable observation of the actual native strategic dispatch. ActionCode is a native
    /// task code, never a unit-role or storage-slot value. NativeManager is invocation-local and must
    /// not be retained. Callbacks run synchronously on the native caller; defer UI work.</summary>
    public sealed class AiStrategicOrderObservation
    {
        internal AiStrategicOrderObservation(long invocation, IntPtr manager, int tribe, int action, bool completed, long result)
        { InvocationId = invocation; NativeManager = manager; TribeId = tribe; ActionCode = action; OriginalCompleted = completed; NativeResult = result; }
        /// <summary>Process-local invocation identity pairing before and after, including recursive calls.</summary>
        public long InvocationId { get; }
        /// <summary>Invocation-local native manager; only immediate diagnostic use is supported.</summary>
        public IntPtr NativeManager { get; }
        /// <summary>Actual native group ID.</summary>
        public int TribeId { get; }
        /// <summary>Actual strategic task code.</summary>
        public int ActionCode { get; }
        /// <summary>Whether the native original returned normally; false in before observations.</summary>
        public bool OriginalCompleted { get; }
        /// <summary>Unmodified native result. Meaningful only when OriginalCompleted is true; not a per-unit success.</summary>
        public long NativeResult { get; }
    }

    /// <summary>Single owner of native strategic-dispatch observation. Registers process-lived ordered
    /// before/after observers without replacing or canceling Vanilla. No replay or thread dispatch.
    /// Callback exceptions are isolated; native execution occurs once. Acquire after LibraryLoaded.</summary>
    public static class AiStrategicOrderEvents
    {
        private static readonly object sync = new object();
        private static Entry[] entries = new Entry[0];
        private static long nextInvocation;
        /// <summary>True when the shared native observer is validated and installed.</summary>
        public static bool IsAvailable => Native.AiMilitaryNativeObservers.IsReady;
        /// <summary>Registers a unique owner-local ID. Registrations are ordered ordinally by owner/ID;
        /// later registrations start on the next invocation. Either callback may be null, but not both.</summary>
        public static bool TryRegister(string ownerGuid, string id, Action<AiStrategicOrderObservation> before,
            Action<AiStrategicOrderObservation> after, out string reason)
        {
            if (!IsAvailable) { reason = "The native strategic order observer is unavailable."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || before == null && after == null)
            { reason = "Owner, registration ID and a callback are required."; return false; }
            lock (sync)
            {
                foreach (var existing in entries)
                    if (existing.Owner == ownerGuid && existing.Id == id) { reason = "Duplicate strategic order registration."; return false; }
                var next = new Entry[entries.Length + 1]; Array.Copy(entries, next, entries.Length);
                next[next.Length - 1] = new Entry(ownerGuid, id, before, after);
                Array.Sort(next, (a, b) => { int owner = string.CompareOrdinal(a.Owner, b.Owner); return owner != 0 ? owner : string.CompareOrdinal(a.Id, b.Id); });
                Volatile.Write(ref entries, next);
                Native.AiMilitaryNativeObservers.EnableObservation();
            }
            reason = string.Empty; return true;
        }
        internal static Entry[] Snapshot => Volatile.Read(ref entries);
        internal static long NextInvocation() => Interlocked.Increment(ref nextInvocation);
        internal static void Publish(Entry[] snapshot, bool before, AiStrategicOrderObservation observation)
        {
            foreach (var entry in snapshot)
                try { (before ? entry.Before : entry.After)?.Invoke(observation); }
                catch (Exception error)
                {
                    try { Internal.RuntimeHealth.Failure(ApiSharedRuntime.ProcessInstance.Logger,
                        "ai-strategic-observer/" + entry.Owner + "/" + entry.Id, true, "Strategic order observer failed: " + error); } catch { }
                }
        }
        internal sealed class Entry
        {
            internal Entry(string owner, string id, Action<AiStrategicOrderObservation> before, Action<AiStrategicOrderObservation> after)
            { Owner = owner; Id = id; Before = before; After = after; }
            internal readonly string Owner, Id;
            internal readonly Action<AiStrategicOrderObservation> Before, After;
        }
    }
}

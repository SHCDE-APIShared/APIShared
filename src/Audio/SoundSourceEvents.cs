using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Native-stage observers of attributed sound calls on the positional wrappers Audio
    /// already owns (0x2960/0x2990): the 91 soldier catalog sources and melee kill cues. Consumers
    /// must not detour these wrappers themselves.</summary>
    /// <remarks>Requires the native audio context (<see cref="Available"/>). Only calls whose source,
    /// unit and coordinates validated are published; all other wrapper calls pass through unchanged.
    /// A replacement is written into the native record, so SoundEvents and Vanilla later see the
    /// replaced effect (as Unknown context, because the replacement no longer matches the audited source).</remarks>
    public static class SoundSourceEvents
    {
        internal static readonly SoundDemand Demand = SoundNativeDemand.Source;
        private static readonly object sync = new object();
        private static Registration[] registrations = Array.Empty<Registration>();
        [ThreadStatic] private static int depth;
        private static long failures;
        /// <summary>True while the native source context is installed and active.</summary>
        public static bool Available => SoundRuntime.ContextStatus == "Ready";
        /// <summary>Native context availability or failure reason.</summary>
        public static string Diagnostic => SoundRuntime.ContextStatus;
        /// <summary>True while melee kill cues are published: requires <see cref="Available"/> and the audited melee damage routine.</summary>
        public static bool MeleeKillAvailable => Available && SoundRuntime.KillStatus == "Ready";
        /// <summary>Melee kill attribution availability or failure reason, independent of the soldier catalog.</summary>
        public static string MeleeKillDiagnostic => SoundRuntime.KillStatus;
        /// <summary>Isolated callback failures. A failing callback's replacement is rolled back; later callbacks still run.</summary>
        public static long CallbackFailures => Interlocked.Read(ref failures);

        /// <summary>Registers a process-lifetime native-stage observer. Any thread; registration does not
        /// require the native context to be ready yet, and calls start once it is.</summary>
        /// <remarks>Callbacks run synchronously on the thread executing the native game tick, inside the
        /// wrapper and before the original, without replay. Do not call Unity APIs or block there. Order is
        /// numeric order, ordinal owner GUID, then ID. <see cref="SoundRegistration.SetEnabled"/> affects
        /// subsequent calls. Sound calls made from inside a callback are not published again.</remarks>
        /// <param name="ownerGuid">Owning plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique per owner.</param>
        /// <param name="pre">Callback receiving the call by reference.</param>
        /// <param name="registration">Rooted activation handle on success.</param>
        /// <param name="reason">Failure reason, empty on success.</param>
        /// <param name="order">Ascending callback order.</param>
        /// <returns>False for missing arguments or a duplicate owner/ID pair.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, SoundSourceHandler pre,
            out SoundRegistration registration, out string reason, int order = 0)
            => TryRegister(ownerGuid, registrationId, pre, SoundSourceSelection.All, out registration, out reason, order);

        /// <summary>Registers a process-lifetime native-stage observer for the selected sources only.
        /// Same contract as the overload without a selection, which equals <see cref="SoundSourceSelection.All"/>.</summary>
        /// <remarks>The callback receives only calls whose <see cref="SoundSourceArgs.SourceKey"/> is in
        /// <paramref name="sources"/>. While no enabled registration selects a source, its wrapper calls stay
        /// native (no managed transition, no unit lookup). Order across registrations is unchanged.</remarks>
        /// <param name="ownerGuid">Owning plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique per owner.</param>
        /// <param name="pre">Callback receiving the call by reference.</param>
        /// <param name="sources">Sources this registration observes, from <see cref="SoundSourceSelection.TryCreate"/>.</param>
        /// <param name="registration">Rooted activation handle on success.</param>
        /// <param name="reason">Failure reason, empty on success.</param>
        /// <param name="order">Ascending callback order.</param>
        /// <returns>False for missing arguments or a duplicate owner/ID pair.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, SoundSourceHandler pre, SoundSourceSelection sources,
            out SoundRegistration registration, out string reason, int order = 0)
        {
            registration = null;
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || pre == null || sources == null)
            { reason = "Owner GUID, registration ID, a callback and a source selection are required."; return false; }
            var handle = new SoundRegistration(sources);
            lock (sync)
            {
                foreach (Registration entry in registrations)
                    if (entry.Owner == ownerGuid && entry.Id == registrationId)
                    { reason = "This owner already registered that ID."; return false; }
                var next = new Registration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = new Registration(ownerGuid, registrationId, pre, order, handle);
                Array.Sort(next, (a, b) => {
                    int comparison = a.Order.CompareTo(b.Order);
                    if (comparison == 0) comparison = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
                    return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Id, b.Id);
                });
                Volatile.Write(ref registrations, next);
            }
            Demand.Attach(handle);
            registration = handle; reason = string.Empty; return true;
        }

        // Called only for validated sources while source demand exists. The snapshot is immutable;
        // a failing callback restores the sound it saw and later callbacks continue.
        // sourceIndex is the SoundSourceSelection demand index of the attributed source; -1 skips the
        // per-registration selection test (tests of ordering and rollback).
        internal static void Dispatch(ref SoundSourceArgs args) => Dispatch(ref args, Volatile.Read(ref registrations), -1);
        internal static void Dispatch(ref SoundSourceArgs args, int sourceIndex) => Dispatch(ref args, Volatile.Read(ref registrations), sourceIndex);
        internal static void Dispatch(ref SoundSourceArgs args, Registration[] snapshot) => Dispatch(ref args, snapshot, -1);
        internal static void Dispatch(ref SoundSourceArgs args, Registration[] snapshot, int sourceIndex)
        {
            if (depth != 0) return;
            depth++;
            try
            {
                for (int index = 0; index < snapshot.Length; index++)
                {
                    Registration entry = snapshot[index];
                    if (!entry.Handle.Enabled || sourceIndex >= 0 && !entry.Handle.Sources.Has(sourceIndex)) continue;
                    int before = args.SoundId;
                    try { entry.Pre(ref args); }
                    catch (Exception ex)
                    {
                        args.Restore(before);
                        Interlocked.Increment(ref failures);
                        long count = Interlocked.Increment(ref entry.Failures);
                        if ((count & (count - 1)) == 0)
                            try { InterceptionDiagnostics.Report(entry.Owner + "/" + entry.Id + " SoundSource", ex); } catch { }
                    }
                }
            }
            finally { depth--; }
        }

        internal sealed class Registration
        {
            internal long Failures;
            internal readonly string Owner, Id;
            internal readonly int Order;
            internal readonly SoundSourceHandler Pre;
            internal readonly SoundRegistration Handle;
            internal Registration(string owner, string id, SoundSourceHandler pre, int order, SoundRegistration handle)
            { Owner = owner; Id = id; Pre = pre; Order = order; Handle = handle; }
        }
    }
}

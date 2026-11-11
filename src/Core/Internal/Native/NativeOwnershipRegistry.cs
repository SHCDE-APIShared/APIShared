using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using APIShared.NativeSites;

namespace APIShared
{
    internal enum NativeReservationMode
    {
        Exclusive
    }

    internal readonly struct NativeInterval
    {
        public NativeInterval(long start, long end)
        {
            if (start < 0 || end <= start)
                throw new ArgumentOutOfRangeException(nameof(start));
            Start = start;
            End = end;
        }

        public long Start { get; }
        public long End { get; }
        public bool Overlaps(NativeInterval other) => Start < other.End && other.Start < End;
    }

    // Process-wide owner registry for native spans (FW-09), behind the public NativeSiteRegistry.
    // Two layers share one lock and one published snapshot:
    // - Sites, in CrusaderDE.dll RVA space: who writes, chains, calls or observes which bytes.
    //   Different owners conflict per NativeSiteRules; one owner never conflicts with itself.
    // - Capability reservations (TryReserve): a consumer's logical ownership of memory that an
    //   APIShared capability hooks, in the caller's address space. They are compared only with each
    //   other, exactly as before FW-09, and are published read-only with CapabilityId set.
    // Writes happen at install time and are serialized. Reads use the immutable snapshot without
    // locks or allocation. Callbacks (the refusal log) run outside the lock.
    internal sealed partial class NativeOwnershipRegistry
    {
        internal const int MaximumRecordedRefusals = 256;

        private readonly object sync = new object();
        private readonly List<Reservation> reservations = new List<Reservation>();
        private readonly List<NativeSiteRegistration> sites = new List<NativeSiteRegistration>();
        private readonly List<NativeSiteRegistrationResult> refusals = new List<NativeSiteRegistrationResult>();
        private Snapshot snapshot = Snapshot.Empty;
        private long nextSequence;
        private long refusalCount;
        private long moduleBase;
        private int imageLength;
        private Func<long, byte> readByte;
        private bool reportEmitted;
        private Action<string> refusalLog;

        internal bool IsBound { get { lock (sync) return readByte != null; } }
        internal IReadOnlyList<NativeSiteRegistration> Registrations => Volatile.Read(ref snapshot).View;
        internal IReadOnlyList<NativeSiteRegistrationResult> Refusals => Volatile.Read(ref snapshot).RefusalView;
        internal long RefusalCount { get { lock (sync) return refusalCount; } }

        // Binds the CrusaderDE.dll image once: enables image-bound span validation, expected-bytes
        // checks and the RVA view of absolute capability reservations. Later calls are ignored.
        internal bool BindModule(long moduleBase, int imageLength, Func<long, byte> readByte)
        {
            if (moduleBase <= 0 || imageLength <= 0 || readByte == null)
                return false;
            lock (sync)
            {
                if (this.readByte != null)
                    return this.moduleBase == moduleBase;
                this.moduleBase = moduleBase;
                this.imageLength = imageLength;
                this.readByte = readByte;
                foreach (Reservation reservation in reservations)
                    reservation.Views = CreateViews(reservation);
                Publish();
                return true;
            }
        }

        private void Publish()
        {
            var items = new List<NativeSiteRegistration>(sites);
            foreach (Reservation reservation in reservations)
                items.AddRange(reservation.Views);
            items.Sort((left, right) => left.Sequence != right.Sequence
                ? left.Sequence.CompareTo(right.Sequence)
                : left.Rva.CompareTo(right.Rva));
            Volatile.Write(ref snapshot, new Snapshot(items.ToArray(), refusals.ToArray()));
        }

        private sealed class Snapshot
        {
            internal static readonly Snapshot Empty =
                new Snapshot(Array.Empty<NativeSiteRegistration>(), Array.Empty<NativeSiteRegistrationResult>());

            internal Snapshot(NativeSiteRegistration[] items, NativeSiteRegistrationResult[] refusals)
            {
                Items = items;
                View = Array.AsReadOnly(items);
                RefusalView = Array.AsReadOnly(refusals);
            }

            internal NativeSiteRegistration[] Items { get; }
            internal ReadOnlyCollection<NativeSiteRegistration> View { get; }
            internal ReadOnlyCollection<NativeSiteRegistrationResult> RefusalView { get; }
        }

        private sealed class Reservation
        {
            public Reservation(string ownerGuid, string capabilityId, NativeReservationMode mode, NativeInterval[] intervals, long sequence)
            {
                OwnerGuid = ownerGuid;
                CapabilityId = capabilityId;
                Mode = mode;
                Intervals = intervals;
                Sequence = sequence;
            }

            public string OwnerGuid { get; }
            public string CapabilityId { get; }
            public NativeReservationMode Mode { get; }
            public NativeInterval[] Intervals { get; }
            public long Sequence { get; }
            public NativeSiteRegistration[] Views { get; set; } = Array.Empty<NativeSiteRegistration>();
        }
    }

    // Overlap rules between registrations of different owners (docs/NATIVE_SITES.md justifies each).
    // Nothing conflicts with a caller; readers conflict only with writers; writers share a span only as
    // chainable detours at one entry.
    internal static class NativeSiteRules
    {
        internal static NativeSiteOverlapRule Rule(NativeSiteMode first, NativeSiteMode second)
        {
            if (first == NativeSiteMode.CallOnly || second == NativeSiteMode.CallOnly)
                return NativeSiteOverlapRule.Compatible;
            if (first == NativeSiteMode.Observe && second == NativeSiteMode.Observe)
                return NativeSiteOverlapRule.Compatible;
            if (first == NativeSiteMode.ChainableEntry && second == NativeSiteMode.ChainableEntry)
                return NativeSiteOverlapRule.SameEntryChain;
            return NativeSiteOverlapRule.Conflict;
        }

        internal static bool Allows(NativeSiteMode existing, int existingRva, NativeSiteMode requested, int requestedRva)
        {
            NativeSiteOverlapRule rule = Rule(existing, requested);
            return rule == NativeSiteOverlapRule.Compatible ||
                (rule == NativeSiteOverlapRule.SameEntryChain && existingRva == requestedRva);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
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
    internal sealed class NativeOwnershipRegistry
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

        // Capability layer. Behavior is unchanged from the pre-FW-09 registry: identical requests are
        // idempotent, any overlap with another capability reservation names the first owner.
        public bool TryReserve(
            string ownerGuid,
            string capabilityId,
            NativeReservationMode mode,
            IReadOnlyList<NativeInterval> intervals,
            out string conflictOwner)
        {
            conflictOwner = null;
            lock (sync)
            {
                foreach (Reservation existing in reservations)
                {
                    if (string.Equals(existing.OwnerGuid, ownerGuid, StringComparison.Ordinal) &&
                        string.Equals(existing.CapabilityId, capabilityId, StringComparison.Ordinal) &&
                        existing.Mode == mode && IntervalsEqual(existing.Intervals, intervals))
                    {
                        return true;
                    }

                    if (AnyOverlap(existing.Intervals, intervals))
                    {
                        conflictOwner = existing.OwnerGuid;
                        return false;
                    }
                }

                var reservation = new Reservation(ownerGuid, capabilityId, mode, Copy(intervals), nextSequence++);
                reservation.Views = CreateViews(reservation);
                reservations.Add(reservation);
                Publish();
                return true;
            }
        }

        // Site layer. Atomic for the whole group: either every span is recorded or none is.
        internal NativeSiteRegistrationResult RegisterSites(string ownerGuid, string name, IReadOnlyList<NativeSiteSpan> spans)
        {
            NativeSiteRegistrationResult result = RegisterCore(ownerGuid, name, spans, out string lateLog, out Action<string> logger);
            if (lateLog != null)
            {
                try { logger?.Invoke(lateLog); }
                catch { }
            }
            return result;
        }

        private NativeSiteRegistrationResult RegisterCore(string ownerGuid, string name, IReadOnlyList<NativeSiteSpan> spans,
            out string lateLog, out Action<string> logger)
        {
            lateLog = null;
            logger = null;
            if (string.IsNullOrWhiteSpace(ownerGuid))
                return Invalid(ownerGuid, name, -1, "A non-empty BepInEx owner GUID is required.");
            if (string.IsNullOrWhiteSpace(name))
                return Invalid(ownerGuid, name, -1, "A non-empty registration name is required.");
            if (spans == null || spans.Count == 0)
                return Invalid(ownerGuid, name, -1, "At least one span is required.");

            var requested = new NativeSiteSpan[spans.Count];
            var hashes = new string[spans.Count];
            for (int index = 0; index < requested.Length; index++)
            {
                NativeSiteSpan span = spans[index];
                string failure = ValidateShape(span, out hashes[index]);
                if (failure != null)
                    return Invalid(ownerGuid, name, index, failure);
                requested[index] = span;
            }

            lock (sync)
            {
                if (readByte != null)
                {
                    for (int index = 0; index < requested.Length; index++)
                        if ((long)requested[index].Rva + requested[index].Length > imageLength)
                            return Invalid(ownerGuid, name, index,
                                $"Span {requested[index]} lies outside the CrusaderDE.dll image (0x{imageLength:X} bytes).");
                }

                NativeSiteRegistration[] existingGroup = FindGroup(ownerGuid, name);
                if (existingGroup.Length > 0)
                {
                    if (SameGroup(existingGroup, requested, hashes))
                        return new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.AlreadyRegistered, ownerGuid, name,
                            Array.AsReadOnly(existingGroup), null, -1, Array.Empty<NativeSiteRegistration>(),
                            "The same spans are already registered under this name.");
                    return Invalid(ownerGuid, name, -1,
                        "This name is already registered by the owner with different spans; unregister it first.");
                }

                var shared = new List<NativeSiteRegistration>();
                for (int index = 0; index < requested.Length; index++)
                {
                    NativeSiteSpan span = requested[index];
                    foreach (NativeSiteRegistration existing in sites)
                    {
                        if (string.Equals(existing.OwnerGuid, ownerGuid, StringComparison.Ordinal) ||
                            !existing.Overlaps(span.Rva, span.Length))
                            continue;
                        if (NativeSiteRules.Allows(existing.Mode, existing.Rva, span.Mode, span.Rva))
                        {
                            if (!shared.Contains(existing))
                                shared.Add(existing);
                            continue;
                        }
                        return Refuse(new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Conflict, ownerGuid, name,
                            null, existing, index, null,
                            $"Span {index} ({span}) conflicts with {existing} under rule " +
                            $"{NativeSiteRules.Rule(existing.Mode, span.Mode)}."), out lateLog, out logger);
                    }
                }

                var verified = new bool[requested.Length];
                for (int index = 0; index < requested.Length; index++)
                {
                    if (hashes[index].Length == 0 || readByte == null)
                        continue;
                    string actual;
                    try { actual = HashLive(requested[index]); }
                    catch (Exception ex)
                    {
                        return Invalid(ownerGuid, name, index, $"Live bytes of span {requested[index]} could not be read: {ex.Message}");
                    }
                    if (!string.Equals(actual, hashes[index], StringComparison.Ordinal))
                        return Refuse(new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.UnexpectedBytes, ownerGuid, name,
                            null, null, index, null,
                            $"Live bytes of span {index} ({requested[index]}) hash to {actual}, expected {hashes[index]}."),
                            out lateLog, out logger);
                    verified[index] = true;
                }

                var created = new NativeSiteRegistration[requested.Length];
                for (int index = 0; index < requested.Length; index++)
                {
                    NativeSiteSpan span = requested[index];
                    created[index] = new NativeSiteRegistration(ownerGuid, name, span.Rva, span.Length, span.Mode,
                        hashes[index], verified[index], nextSequence++, string.Empty);
                    sites.Add(created[index]);
                }
                Publish();
                shared.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
                return new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Accepted, ownerGuid, name,
                    Array.AsReadOnly(created), null, -1, shared.AsReadOnly(), string.Empty);
            }
        }

        // Removes an owner's named group. Intended only to roll back an installation that was not
        // published; published hooks stay registered for the process lifetime.
        internal int UnregisterSites(string ownerGuid, string name)
        {
            if (ownerGuid == null || name == null)
                return 0;
            lock (sync)
            {
                int removed = sites.RemoveAll(site =>
                    string.Equals(site.OwnerGuid, ownerGuid, StringComparison.Ordinal) &&
                    string.Equals(site.Name, name, StringComparison.Ordinal));
                if (removed > 0)
                    Publish();
                return removed;
            }
        }

        // Zero-allocation read: counts every registration overlapping [rva, rva + length) and copies
        // up to destination.Length of them in registration order. Destination may be null or empty.
        internal int Query(int rva, int length, NativeSiteRegistration[] destination)
        {
            if (length < 1)
                throw new ArgumentOutOfRangeException(nameof(length));
            NativeSiteRegistration[] items = Volatile.Read(ref snapshot).Items;
            int capacity = destination == null ? 0 : destination.Length;
            int count = 0;
            long end = (long)rva + length;
            for (int index = 0; index < items.Length; index++)
            {
                NativeSiteRegistration item = items[index];
                if (item.Rva < end && rva < item.EndRva)
                {
                    if (count < capacity)
                        destination[count] = item;
                    count++;
                }
            }
            return count;
        }

        // Zero-allocation read: the earliest site registration that writes the byte at rva
        // (Exclusive or ChainableEntry). Capability reservations are not writers.
        internal bool TryGetWriter(int rva, out NativeSiteRegistration registration)
        {
            NativeSiteRegistration[] items = Volatile.Read(ref snapshot).Items;
            for (int index = 0; index < items.Length; index++)
            {
                NativeSiteRegistration item = items[index];
                if (item.CapabilityId.Length == 0 &&
                    (item.Mode == NativeSiteMode.Exclusive || item.Mode == NativeSiteMode.ChainableEntry) &&
                    item.Rva <= rva && rva < item.EndRva)
                {
                    registration = item;
                    return true;
                }
            }
            registration = null;
            return false;
        }

        internal void SetRefusalLog(Action<string> log)
        {
            lock (sync) refusalLog = log;
        }

        // One summary of the registry state. The first call marks the startup report as emitted, so
        // every later refusal is logged individually through the refusal log.
        internal string EmitReport() => EmitReport(includeSuccessSummary: true);

        // API_SHARED_DIAGNOSTICS: complete the warning lifecycle atomically even
        // when the optional success summary is omitted. Later refusals must log.
        internal string EmitReport(bool includeSuccessSummary)
        {
            lock (sync)
            {
                reportEmitted = true;
                if (!includeSuccessSummary && refusalCount == 0) return null;
                var owners = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (NativeSiteRegistration site in sites)
                    owners[site.OwnerGuid] = owners.TryGetValue(site.OwnerGuid, out int count) ? count + 1 : 1;
                var text = new StringBuilder("NATIVE_SITES: ");
                text.Append(sites.Count).Append(" site registrations by ").Append(owners.Count).Append(" owners");
                if (owners.Count > 0)
                {
                    text.Append(" (");
                    bool first = true;
                    foreach (KeyValuePair<string, int> owner in owners)
                    {
                        if (!first) text.Append(", ");
                        text.Append(owner.Key).Append('=').Append(owner.Value);
                        first = false;
                    }
                    text.Append(')');
                }
                text.Append(", ").Append(reservations.Count).Append(" capability reservations");
                text.Append(readByte == null ? ", image not bound" : $", image bound (0x{imageLength:X} bytes)");
                text.Append("; refusals: ");
                if (refusalCount == 0)
                {
                    text.Append("none.");
                    return text.ToString();
                }
                text.Append(refusalCount).Append(": ");
                int shown = Math.Min(refusals.Count, 10);
                for (int index = 0; index < shown; index++)
                {
                    if (index > 0) text.Append(" | ");
                    text.Append(refusals[index]);
                }
                if (refusalCount > shown)
                    text.Append(" | +").Append(refusalCount - shown).Append(" more");
                return text.ToString();
            }
        }

        private NativeSiteRegistrationResult Refuse(NativeSiteRegistrationResult result, out string lateLog, out Action<string> logger)
        {
            refusalCount++;
            if (refusals.Count < MaximumRecordedRefusals)
                refusals.Add(result);
            Publish();
            lateLog = reportEmitted ? "NATIVE_SITES refusal: " + result : null;
            logger = refusalLog;
            return result;
        }

        private static NativeSiteRegistrationResult Invalid(string ownerGuid, string name, int index, string reason) =>
            new NativeSiteRegistrationResult(NativeSiteRegistrationStatus.Invalid, ownerGuid, name, null, null, index, null, reason);

        private static string ValidateShape(NativeSiteSpan span, out string hash)
        {
            hash = string.Empty;
            if (span.Rva < 0)
                return $"Span {span} has a negative RVA.";
            if (span.Length < 1)
                return $"Span {span} must cover at least one byte.";
            if ((long)span.Rva + span.Length > int.MaxValue)
                return $"Span {span} exceeds the 32-bit RVA range.";
            if (span.Mode < NativeSiteMode.Exclusive || span.Mode > NativeSiteMode.Observe)
                return $"Span {span} has an unknown mode.";
            string text = span.ExpectedOriginalSha256.Trim();
            if (text.Length == 0)
                return null;
            if (text.Length != 64)
                return $"Span {span} expected SHA-256 must have 64 hexadecimal digits.";
            foreach (char character in text)
                if (!Uri.IsHexDigit(character))
                    return $"Span {span} expected SHA-256 must have 64 hexadecimal digits.";
            hash = text.ToUpperInvariant();
            return null;
        }

        private string HashLive(NativeSiteSpan span)
        {
            var bytes = new byte[span.Length];
            long start = moduleBase + span.Rva;
            for (int index = 0; index < bytes.Length; index++)
                bytes[index] = readByte(start + index);
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty);
        }

        private NativeSiteRegistration[] FindGroup(string ownerGuid, string name)
        {
            var group = new List<NativeSiteRegistration>();
            foreach (NativeSiteRegistration site in sites)
                if (string.Equals(site.OwnerGuid, ownerGuid, StringComparison.Ordinal) &&
                    string.Equals(site.Name, name, StringComparison.Ordinal))
                    group.Add(site);
            return group.ToArray();
        }

        private static bool SameGroup(NativeSiteRegistration[] existing, NativeSiteSpan[] requested, string[] hashes)
        {
            if (existing.Length != requested.Length)
                return false;
            for (int index = 0; index < existing.Length; index++)
                if (existing[index].Rva != requested[index].Rva || existing[index].Length != requested[index].Length ||
                    existing[index].Mode != requested[index].Mode ||
                    !string.Equals(existing[index].ExpectedOriginalSha256, hashes[index], StringComparison.Ordinal))
                    return false;
            return true;
        }

        // Absolute capability intervals become RVAs once the module is bound. An unbound registry (an
        // isolated test runtime) publishes values that already fit the RVA range unchanged.
        private NativeSiteRegistration[] CreateViews(Reservation reservation)
        {
            long offset = readByte != null ? moduleBase : 0;
            var views = new List<NativeSiteRegistration>();
            foreach (NativeInterval interval in reservation.Intervals)
            {
                long start = interval.Start - offset;
                long end = interval.End - offset;
                if (start < 0 || end > int.MaxValue)
                    continue;
                views.Add(new NativeSiteRegistration(reservation.OwnerGuid, reservation.CapabilityId, (int)start,
                    (int)(end - start), NativeSiteMode.Exclusive, string.Empty, false, reservation.Sequence,
                    reservation.CapabilityId ?? string.Empty));
            }
            return views.ToArray();
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

        private static bool AnyOverlap(IReadOnlyList<NativeInterval> first, IReadOnlyList<NativeInterval> second)
        {
            for (int left = 0; left < first.Count; left++)
                for (int right = 0; right < second.Count; right++)
                    if (first[left].Overlaps(second[right]))
                        return true;
            return false;
        }

        private static bool IntervalsEqual(IReadOnlyList<NativeInterval> first, IReadOnlyList<NativeInterval> second)
        {
            if (first.Count != second.Count)
                return false;
            for (int index = 0; index < first.Count; index++)
                if (first[index].Start != second[index].Start || first[index].End != second[index].End)
                    return false;
            return true;
        }

        private static NativeInterval[] Copy(IReadOnlyList<NativeInterval> source)
        {
            var result = new NativeInterval[source.Count];
            for (int index = 0; index < source.Count; index++)
                result[index] = source[index];
            return result;
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

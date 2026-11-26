using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace APIShared
{
    internal enum RepairRejectionReason
    {
        None, BuildingManagerUnavailable, PlayerManagerUnavailable, InvalidBuildingId,
        PlayerResourcesUnavailable, BuildingUnavailable, OwnerMismatch, GlobalIdMismatch,
        NotAlive, ZeroMaximumHealth, ExcessiveMaximumHealth, NegativeHealth, InvalidBuildingType,
        Count
    }

    // Only values actually read by the ordered safety checks are present. No native pointer escapes.
    internal struct RepairRejectionSnapshot
    {
        internal int PlayerId, BuildingId, ExpectedGlobalId;
        internal int? Owner, GlobalId, Alive, MaximumHealth, Health, Type;
        internal BuildingLookupFailure? LookupFailure;
        internal long? Session;

        internal string Describe() =>
            "session=" + Value(Session) + "; player=" + PlayerId + "; building=" + BuildingId +
            "; expectedGlobalId=" + ExpectedGlobalId + "; owner=" + Value(Owner) +
            "; globalId=" + Value(GlobalId) + "; alive=" + Value(Alive) +
            "; maxHealth=" + Value(MaximumHealth) + "; health=" + Value(Health) +
            "; type=" + Value(Type) + "; lookup=" + (LookupFailure?.ToString() ?? "unavailable");

        private static string Value<T>(T? value) where T : struct => value.HasValue
            ? Convert.ToString(value.Value, CultureInfo.InvariantCulture) : "unavailable";
    }

    // Fixed process-lifetime storage: changing building identities cannot grow a dictionary or queue.
    // Producer work is limited to copying a value snapshot and incrementing two saturated counters.
    internal sealed class RepairRejectionDiagnostics
    {
        private struct Bucket
        {
            internal long Total, Pending, LastEmission;
            internal bool Emitted;
            internal RepairRejectionSnapshot Snapshot;
        }
        internal struct Report
        {
            internal RepairRejectionReason Reason;
            internal long Delta, ProcessTotal;
            internal RepairRejectionSnapshot Snapshot;
            internal string Describe() => "Building repair rejected; native repair skipped; reason=" + Reason +
                "; delta=" + Delta + "; processTotal=" + ProcessTotal + "; " + Snapshot.Describe();
        }

        private readonly object sync = new object();
        private readonly Bucket[] buckets = new Bucket[(int)RepairRejectionReason.Count];
        private readonly Func<long> clock;
        private readonly long interval;

        internal RepairRejectionDiagnostics() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
        internal RepairRejectionDiagnostics(Func<long> clock, long frequency)
        {
            if (clock == null || frequency <= 0 || frequency > long.MaxValue / 30)
                throw new ArgumentException("A monotonic clock and valid frequency are required.");
            this.clock = clock;
            interval = frequency * 30;
        }

        internal void Record(RepairRejectionReason reason, RepairRejectionSnapshot snapshot)
        {
            if (reason <= RepairRejectionReason.None || reason >= RepairRejectionReason.Count) return;
            lock (sync)
            {
                ref Bucket bucket = ref buckets[(int)reason];
                bucket.Total = Increment(bucket.Total);
                bucket.Pending = Increment(bucket.Pending);
                bucket.Snapshot = snapshot;
            }
        }

        // Called on the persistent HUD path. The caller formats/emits after this lock is released.
        internal bool TryTake(RepairRejectionReason reason, out Report report)
        {
            report = default;
            // A concurrent first record may wait until the next HUD callback; no event is discarded.
            if (Volatile.Read(ref buckets[(int)reason].Pending) == 0) return false;
            lock (sync)
            {
                ref Bucket bucket = ref buckets[(int)reason];
                if (bucket.Pending == 0) return false;
                long now = clock();
                if (bucket.Emitted && now - bucket.LastEmission < interval) return false;
                report = new Report { Reason = reason, Delta = bucket.Pending,
                    ProcessTotal = bucket.Total, Snapshot = bucket.Snapshot };
                bucket.Pending = 0;
                bucket.Emitted = true;
                bucket.LastEmission = now;
                return true;
            }
        }

        internal static long Increment(long count) => count == long.MaxValue ? count : count + 1;
    }
}

using BepInEx.Logging;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

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

    internal sealed class NativeOwnershipRegistry
    {
        private readonly object sync = new object();
        private readonly List<Reservation> reservations = new List<Reservation>();

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

                reservations.Add(new Reservation(ownerGuid, capabilityId, mode, Copy(intervals)));
                return true;
            }
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

        private sealed class Reservation
        {
            public Reservation(string ownerGuid, string capabilityId, NativeReservationMode mode, NativeInterval[] intervals)
            {
                OwnerGuid = ownerGuid;
                CapabilityId = capabilityId;
                Mode = mode;
                Intervals = intervals;
            }

            public string OwnerGuid { get; }
            public string CapabilityId { get; }
            public NativeReservationMode Mode { get; }
            public NativeInterval[] Intervals { get; }
        }
    }
}

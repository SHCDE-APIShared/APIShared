using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading;
using APIShared.NativeSites;

namespace APIShared
{
    internal sealed partial class NativeOwnershipRegistry
    {
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
    }
}

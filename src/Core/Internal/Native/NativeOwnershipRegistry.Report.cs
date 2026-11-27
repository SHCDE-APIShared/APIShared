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
    }
}

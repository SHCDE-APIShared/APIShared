using System;
using System.Collections.Generic;

namespace APIShared
{
    // Rare owner writes build one packed native snapshot. Four unsigned 16-bit lanes encode
    // tolerance+1 (zero means no owner); a single aligned 64-bit publication avoids torn policies.
    internal sealed class PlagueHeightPolicies
    {
        private readonly object sync = new object();
        private readonly Dictionary<string, ulong> owners = new Dictionary<string, ulong>(StringComparer.Ordinal);
        private readonly Action<ulong> publish;
        internal PlagueHeightPolicies(Action<ulong> publish) { this.publish = publish; }
        internal bool TrySet(string owner, bool active, PlagueSurfaceCategory categories, int tolerance)
        {
            if (string.IsNullOrWhiteSpace(owner) || (categories & ~PlagueSurfaceCategory.All) != 0 ||
                tolerance < 0 || tolerance > 65534) return false;
            ulong policy = 0;
            if (active)
                for (int lane = 0; lane < 4; lane++)
                    if (((int)categories & (1 << lane)) != 0) policy |= (ulong)(tolerance + 1) << (lane * 16);
            lock (sync)
            {
                owners[owner] = policy;
                ulong combined = 0;
                for (int lane = 0; lane < 4; lane++)
                {
                    ulong maximum = 0;
                    foreach (ulong value in owners.Values)
                        maximum = Math.Max(maximum, (value >> (lane * 16)) & 0xFFFF);
                    combined |= maximum << (lane * 16);
                }
                publish(combined);
            }
            return true;
        }
    }
}

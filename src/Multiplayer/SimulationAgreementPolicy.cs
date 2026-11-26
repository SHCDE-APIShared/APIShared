using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.Multiplayer
{
    internal static class SimulationAgreementPolicy
    {
        internal const string ResyncCohortKeyPrefix = "APIShared_Serp/resync-data-v1.";

        internal static bool IsResyncDigest(string digest) => digest != null && digest.Length == 64 &&
            digest.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');

        internal static bool TryDecide(string local, IEnumerable<string> reports, out string enabled, out string reason)
            => TryDecide(local, reports, out enabled, out reason, out _);

        internal static bool TryDecide(string local, IEnumerable<string> reports, out string enabled,
            out string reason, out bool waiting)
        {
            enabled = "";
            waiting = false;
            reason = "Players reported different simulation capability registrations.";
            if (!TryParse(local, out var own)) return false;
            if (own.Any(pair => IsResyncCohort(pair.Key) && pair.Value != "r1"))
            {
                reason = "The required multiplayer resync data service is unavailable.";
                return false;
            }
            var states = new List<Dictionary<string, string>>();
            bool missing = false;
            foreach (string report in reports)
            {
                if (report == null) { missing = true; continue; }
                if (!TryParse(report, out var state) || state.Count != own.Count ||
                    own.Any(pair => !state.TryGetValue(pair.Key, out string value) || value[0] != pair.Value[0]))
                    return false;
                states.Add(state);
            }

            var required = own.Where(pair => pair.Value[0] == 'r');
            foreach (var pair in required.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (!states.Any(state => state[pair.Key] != pair.Value)) continue;
                reason = "Players have different readiness for simulation fix " + pair.Key + ".";
                return false;
            }
            if (missing && required.Any())
            {
                waiting = true;
                reason = "Waiting for all players to report required simulation fixes.";
                return false;
            }
            if (!missing)
                enabled = string.Join(";", own.Where(pair => pair.Value[1] == '1' &&
                    states.All(state => state[pair.Key][1] == '1')).Select(pair => pair.Key).OrderBy(key => key, StringComparer.Ordinal));
            reason = null;
            return true;
        }

        internal static bool Accepts(string manifest, string enabled)
        {
            if (!TryParse(manifest, out var own) || enabled == null) return false;
            var selected = new HashSet<string>(StringComparer.Ordinal);
            if (enabled.Length != 0)
                foreach (string key in enabled.Split(';'))
                    if (!own.TryGetValue(key, out string state) || state[1] != '1' || !selected.Add(key))
                        return false;
            return own.All(pair => IsResyncCohort(pair.Key)
                ? selected.Contains(pair.Key)
                : pair.Value[0] != 'r' || (pair.Value[1] == '1') == selected.Contains(pair.Key));
        }

        private static bool IsResyncCohort(string key) =>
            key.StartsWith(ResyncCohortKeyPrefix, StringComparison.Ordinal);

        private static bool TryParse(string manifest, out Dictionary<string, string> entries)
        {
            entries = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(manifest)) return false;
            foreach (string entry in manifest.Split(';'))
            {
                int assignment = entry.IndexOf('=');
                if (assignment <= 0 || entry.Length != assignment + 3 ||
                    (entry[assignment + 1] != 'r' && entry[assignment + 1] != 'o') ||
                    (entry[assignment + 2] != '0' && entry[assignment + 2] != '1') || entries.Count == 32)
                    return false;
                string key = entry.Substring(0, assignment);
                if (entries.ContainsKey(key) || IsResyncCohort(key) &&
                    (entry[assignment + 1] != 'r' || !IsResyncDigest(key.Substring(ResyncCohortKeyPrefix.Length))))
                    return false;
                entries.Add(key, entry.Substring(assignment + 1));
            }
            return entries.Count != 0;
        }
    }
}

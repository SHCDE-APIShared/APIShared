using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared.Multiplayer
{
    internal static class SimulationAgreementPolicy
    {
        internal static bool TryDecide(string local, IEnumerable<string> reports, out string enabled, out string reason)
        {
            enabled = "";
            reason = "Players reported different simulation capability registrations.";
            if (!TryParse(local, out var own)) return false;
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

            var result = new List<string>();
            foreach (var pair in own.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (pair.Value[0] == 'r')
                {
                    if (missing)
                    {
                        reason = "Waiting for all players to report required simulation fixes.";
                        return false;
                    }
                    if (states.Any(state => state[pair.Key] != pair.Value))
                    {
                        reason = "Players have different readiness for simulation fix " + pair.Key + ".";
                        return false;
                    }
                }
                if (pair.Value[1] == '1' && !missing && states.All(state => state[pair.Key][1] == '1'))
                    result.Add(pair.Key);
            }
            enabled = string.Join(";", result);
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
            return own.All(pair => pair.Value[0] != 'r' || (pair.Value[1] == '1') == selected.Contains(pair.Key));
        }

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
                if (entries.ContainsKey(key)) return false;
                entries.Add(key, entry.Substring(assignment + 1));
            }
            return entries.Count != 0;
        }
    }
}

using System;
using System.Collections.Generic;

namespace APIShared.Workshop
{
    // Presence and a comparable version are different facts. Asset manifests may
    // carry author-defined version strings; these must not block unrelated mods.
    internal static class WorkshopInstalledVersions
    {
        internal static Dictionary<string, Version> Collect(IEnumerable<KeyValuePair<string, string>> plugins, IEnumerable<KeyValuePair<string, string>> assets)
        {
            var result = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in plugins) result[item.Key] = Comparable(item.Value);
            foreach (var item in assets)
                if (!result.ContainsKey(item.Key)) result[item.Key] = Comparable(item.Value);
            return result;
        }

        private static Version Comparable(string text)
        {
            if (!Version.TryParse(text, out var version) || version.Build < 0) return null;
            return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
        }
    }
}

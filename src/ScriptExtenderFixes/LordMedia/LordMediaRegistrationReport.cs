using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.ScriptExtenderFixes
{
    internal sealed class LordMediaSlot
    {
        internal LordMediaSlot(int playerId, string name, string displayName, string path, bool registered)
        { PlayerId = playerId; Name = name; DisplayName = displayName; Path = path; Registered = registered; }
        internal int PlayerId { get; }
        internal string Name { get; }
        internal string DisplayName { get; }
        internal string Path { get; }
        internal bool Registered { get; }
    }

    // Called only on the Unity thread, once managed mission initialization is complete.
    // File presence establishes an explicit SE opt-in, not a claim that any media file is missing.
    internal sealed class LordMediaRegistrationReport
    {
        private long session;
        private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);

        internal void Report(long expectedSession, Func<long?> currentSession, Func<IEnumerable<LordMediaSlot>> capture,
            Func<string, bool> exists, Action<string> warning, Action<Exception> failed)
        {
            try
            {
                if (currentSession() != expectedSession) return;
                if (session != expectedSession) { session = expectedSession; emitted.Clear(); }
                var groups = new Dictionary<string, List<LordMediaSlot>>(StringComparer.Ordinal);
                foreach (LordMediaSlot slot in capture())
                {
                    if (slot == null || slot.Registered || string.IsNullOrEmpty(slot.Name) || string.IsNullOrEmpty(slot.Path)) continue;
                    string key = slot.Name + "\n" + slot.Path;
                    if (!groups.TryGetValue(key, out List<LordMediaSlot> group))
                        groups.Add(key, group = new List<LordMediaSlot>());
                    group.Add(slot);
                }
                foreach (KeyValuePair<string, List<LordMediaSlot>> group in groups)
                {
                    if (currentSession() != expectedSession) return;
                    try
                    {
                        LordMediaSlot lord = group.Value[0];
                        if (!exists(Path.Combine(lord.Path, "lordmeta.json"))) continue;
                        bool missingInfo = !exists(Path.Combine(lord.Path, "info.json"));
                        string key = group.Key + (missingInfo ? "\nmissing-info" : "\nunregistered");
                        if (emitted.Contains(key)) continue;
                        var players = new SortedSet<int>();
                        foreach (LordMediaSlot slot in group.Value) players.Add(slot.PlayerId);
                        string reason = missingInfo
                            ? "lordmeta.json is present, but info.json is missing, so Script Extender could not register this Lord. "
                            : "lordmeta.json is present, but Script Extender did not register this Lord. ";
                        string message = "Lord \"" + Clean(string.IsNullOrEmpty(lord.DisplayName) ? lord.Name : lord.DisplayName) +
                            "\" (internal name: \"" + Clean(lord.Name) + "\"; players: " + string.Join(", ", players) + "): " + reason +
                            "Script Extender additions for this Lord were not loaded; original game media paths are retained. " +
                            "Check this Lord package's info.json and lordmeta.json and the earlier registration messages. Package: " + Clean(lord.Path) +
                            "; session=" + expectedSession;
                        // A replacement can begin while capture or the file provider is running.
                        if (currentSession() != expectedSession) return;
                        warning(message);
                        emitted.Add(key);
                    }
                    catch (Exception error) { failed(error); }
                }
            }
            catch (Exception error) { failed(error); }
        }

        internal void End(long endedSession)
        {
            if (session != endedSession) return;
            session = 0;
            emitted.Clear();
        }

        private static string Clean(string value) => value.Replace('\r', ' ').Replace('\n', ' ');
    }
}

using System;
using System.Collections.Generic;
using APIShared.ModSettings;

namespace APIShared
{
    internal sealed class SavegameMissionPresetLease
    {
        private readonly object sync = new object();
        private readonly Dictionary<long, Dictionary<string, IModSettingsPresetEndpoint>> sessions =
            new Dictionary<long, Dictionary<string, IModSettingsPresetEndpoint>>();

        internal void Track(long sessionId, string modId, IModSettingsPresetEndpoint endpoint)
        {
            if (sessionId <= 0 || string.IsNullOrEmpty(modId) || endpoint == null)
                throw new ArgumentException("A savegame preset requires a valid session and endpoint.");
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var participants))
                {
                    participants = new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal);
                    sessions.Add(sessionId, participants);
                }
                participants[modId] = endpoint;
            }
        }

        internal void ReleaseOnEnd(long sessionId, Action<string, Exception> reportError)
        {
            Dictionary<string, IModSettingsPresetEndpoint> participants;
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out participants)) return;
                sessions.Remove(sessionId);
            }
            foreach (KeyValuePair<string, IModSettingsPresetEndpoint> participant in participants)
            {
                try { participant.Value.System_ExitMissionPreset(); }
                catch (Exception error)
                {
                    try { reportError?.Invoke(participant.Key, error); }
                    catch { }
                }
            }
        }
    }
}

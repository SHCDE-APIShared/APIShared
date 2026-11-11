using System;
using System.Collections.Generic;
using APIShared.ModSettings;

namespace APIShared
{
    internal sealed class SavegameMissionPresetLease
    {
        private readonly object sync = new object();
        private sealed class Session
        {
            internal readonly Dictionary<string, IModSettingsPresetEndpoint> Participants =
                new Dictionary<string, IModSettingsPresetEndpoint>(StringComparer.Ordinal);
            internal string ApplicationContext;
        }
        private readonly Dictionary<long, Session> sessions = new Dictionary<long, Session>();

        // Saved author metadata belongs to the session even if no provider is installed.
        internal void TrackSession(long sessionId, string applicationContext = null)
        {
            if (sessionId <= 0) throw new ArgumentException("Invalid savegame session.");
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session))
                    sessions.Add(sessionId, session = new Session());
                if (!string.IsNullOrEmpty(applicationContext)) session.ApplicationContext = applicationContext;
            }
        }

        internal void Track(long sessionId, string modId, IModSettingsPresetEndpoint endpoint)
        {
            if (sessionId <= 0 || string.IsNullOrEmpty(modId) || endpoint == null)
                throw new ArgumentException("A savegame preset requires a valid session and endpoint.");
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out var session))
                {
                    session = new Session();
                    sessions.Add(sessionId, session);
                }
                session.Participants[modId] = endpoint;
            }
        }

        internal void ReleaseOnEnd(long sessionId, Action<string, Exception> reportError, bool releaseApplicationContext = true)
        {
            Session session;
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out session)) return;
                sessions.Remove(sessionId);
            }
            foreach (KeyValuePair<string, IModSettingsPresetEndpoint> participant in session.Participants)
            {
                try { participant.Value.System_ExitMissionPreset(); }
                catch (Exception error)
                {
                    try { reportError?.Invoke(participant.Key, error); }
                    catch { }
                }
            }
            // APIShared owns save contexts even without ExtendedData or any endpoint.
            // A subsequently prepared mission may already own a different context.
            if (releaseApplicationContext && session.ApplicationContext != null && ModSettingsApplication.ContextId == session.ApplicationContext)
            {
                try { ModSettingsApplication.ExitContext(); }
                catch (Exception error)
                {
                    try { reportError?.Invoke("APIShared_Serp", error); } catch { }
                }
            }
        }

        internal bool Contains(long sessionId)
        {
            lock (sync) return sessions.ContainsKey(sessionId);
        }

        internal void Transfer(long source, long destination)
        {
            if (source <= 0 || destination <= source) throw new ArgumentException("Invalid restart sessions.");
            lock (sync)
            {
                if (!sessions.TryGetValue(source, out var participants)) return;
                if (sessions.ContainsKey(destination)) throw new InvalidOperationException("Restart destination already owns savegame settings.");
                sessions.Add(destination, participants);
                sessions.Remove(source);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using APIShared.Events;
using APIShared.Internal;
using APIShared.Localization;
using BepInEx.Logging;
using CrusaderDE;
using SHCDESE.API;
using Steamworks;

namespace APIShared.Lobby
{
    // Announcement lines recently received from other players. Unity thread only.
    internal sealed class LobbyLaunchReceivedLines
    {
        internal static readonly TimeSpan Window = TimeSpan.FromSeconds(5);
        private const int Capacity = 64;
        private readonly Dictionary<string, DateTime> received = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> echoes = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        internal void Clear() { received.Clear(); echoes.Clear(); }

        // Steam delivers a sent lobby line back to its sender; that echo must not suppress the sender's own retry.
        internal void ExpectEcho(string line, DateTime now) => Put(echoes, line, now);

        internal void Note(string line, DateTime now)
        {
            if (echoes.TryGetValue(line, out DateTime sent) && now - sent < Window) { echoes.Remove(line); return; }
            Put(received, line, now);
        }

        internal bool Contains(string line, DateTime now) => received.TryGetValue(line, out DateTime at) && now - at < Window;

        private static void Put(Dictionary<string, DateTime> lines, string line, DateTime now)
        {
            if (lines.Count >= Capacity)
                foreach (string old in lines.Where(entry => now - entry.Value >= Window).Select(entry => entry.Key).ToArray())
                    lines.Remove(old);
            if (lines.Count >= Capacity) lines.Clear();
            lines[line] = now;
        }
    }
}

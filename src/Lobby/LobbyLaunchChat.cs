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
    internal static class LobbyLaunchChat
    {
        internal static void SendOrShowLocal(IEnumerable<string> messages, Func<string, bool> send, Action<string> showLocal)
        {
            // Only the failed parts fall back locally, avoiding duplicates when Steam
            // accepted an earlier part of the same system announcement.
            foreach (string message in messages)
                if (!send(message)) showLocal(message);
        }

        internal const int MaximumMessageLength = 280;

        // Numbered parts keep every text; a part never ends inside a surrogate pair.
        internal static string[] Format(string prefix, IEnumerable<string> texts)
        {
            string body = prefix + ": " + string.Join("; ", texts.Where(text => !string.IsNullOrWhiteSpace(text))
                .Distinct(StringComparer.Ordinal));
            const int numbering = 10; // "[99/99] "
            List<string> parts = Split(body, MaximumMessageLength - LobbyLaunchStatus.Marker.Length - numbering);
            return parts.Select((part, index) => LobbyLaunchStatus.Marker +
                (parts.Count == 1 ? string.Empty : "[" + (index + 1) + "/" + parts.Count + "] ") + part).ToArray();
        }

        internal static List<string> Split(string text, int limit)
        {
            var parts = new List<string>();
            int offset = 0;
            while (offset < text.Length)
            {
                int length = Math.Min(limit, text.Length - offset);
                if (offset + length < text.Length)
                {
                    // Prefer the last space in the final third of the part.
                    int space = text.LastIndexOf(' ', offset + length - 1, length);
                    if (space > offset + limit * 2 / 3) length = space - offset + 1;
                    else if (char.IsHighSurrogate(text[offset + length - 1])) length--;
                }
                parts.Add(text.Substring(offset, length).Trim());
                offset += length;
            }
            return parts;
        }
    }
}

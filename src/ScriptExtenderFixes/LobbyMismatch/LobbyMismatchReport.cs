using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.ScriptExtenderFixes
{
    // Diagnostic snapshots only. The Extender remains the owner of the join decision.
    internal sealed class LobbyMismatchMod
    {
        internal readonly string Guid, Name, Version;
        internal readonly bool Clientside;
        internal LobbyMismatchMod(string guid, string name, string version, bool clientside)
        {
            Guid = Clean(guid);
            Name = Clean(name);
            if (Name.Length == 0) Name = Guid;
            Version = Clean(version);
            if (Version.Length == 0) Version = "0.0.0";
            Clientside = clientside;
        }

        private static string Clean(string text)
        {
            // Keep diagnostic values complete; only popup presentation is shortened.
            return (text ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        }
    }

    internal sealed class LobbyMismatchDifference
    {
        internal readonly LobbyMismatchMod Local, Host;
        internal LobbyMismatchDifference(LobbyMismatchMod local, LobbyMismatchMod host) { Local = local; Host = host; }
        internal string LogText => $"{(Host ?? Local).Name} [{(Host ?? Local).Guid}]: " +
            $"local={(Local == null ? "not installed" : Local.Version)}, host={(Host == null ? "not installed" : Host.Version)}";

        internal string PopupText(bool german) => PopupText(german ? "de-DE" : "en-US");
        internal string PopupText(string locale) => Localization.UiText.ForLocale(locale, "LobbyMismatch.Difference",
            "Mod", Short((Host ?? Local).Name, 28),
            "Local", Local == null ? Localization.UiText.ForLocale(locale, "LobbyMismatch.NotInstalled") : Short(Local.Version, 16),
            "Host", Host == null ? Localization.UiText.ForLocale(locale, "LobbyMismatch.NotInstalled") : Short(Host.Version, 16));

        internal static string Short(string value, int maximum) =>
            value.Length <= maximum ? value : value.Substring(0, maximum - 1) + "\u2026";
    }

    internal sealed class LobbyMismatchReport
    {
        internal const string MetadataToken = "_SE_MODS_";
        internal readonly List<LobbyMismatchDifference> Differences;
        internal readonly string MetadataIssue;
        internal LobbyMismatchReport(List<LobbyMismatchDifference> differences, string metadataIssue)
        { Differences = differences; MetadataIssue = metadataIssue; }

        internal static LobbyMismatchReport Create(string json, IEnumerable<LobbyMismatchMod> assets,
            IEnumerable<LobbyMismatchMod> plugins)
        {
            if (!TryReadHost(json, out var host, out string issue))
                return new LobbyMismatchReport(new List<LobbyMismatchDifference>(), issue);
            var local = new Dictionary<string, LobbyMismatchMod>(StringComparer.OrdinalIgnoreCase);
            // Asset declarations supply NetworkMode and take precedence, just as in SE.
            foreach (var mod in assets) if (mod.Guid.Length != 0) local[mod.Guid] = mod;
            foreach (var mod in plugins) if (mod.Guid.Length != 0 && !local.ContainsKey(mod.Guid)) local.Add(mod.Guid, mod);
            var differences = new List<LobbyMismatchDifference>();
            foreach (var mod in local.Values.OrderBy(m => m.Guid, StringComparer.OrdinalIgnoreCase))
            {
                if (!host.TryGetValue(mod.Guid, out var remote))
                {
                    if (!mod.Clientside) differences.Add(new LobbyMismatchDifference(mod, null));
                }
                else if (!string.Equals(mod.Version, remote.Version, StringComparison.Ordinal) && (!mod.Clientside || !remote.Clientside))
                    differences.Add(new LobbyMismatchDifference(mod, remote));
            }
            foreach (var mod in host.Values.OrderBy(m => m.Guid, StringComparer.OrdinalIgnoreCase))
                if (!local.ContainsKey(mod.Guid) && !mod.Clientside) differences.Add(new LobbyMismatchDifference(null, mod));
            return new LobbyMismatchReport(differences, null);
        }

        internal static bool TryReadHost(string json, out Dictionary<string, LobbyMismatchMod> mods, out string issue)
        {
            mods = new Dictionary<string, LobbyMismatchMod>(StringComparer.OrdinalIgnoreCase);
            issue = "Host mod metadata is missing or invalid; a complete difference list cannot be determined.";
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                if (!(DependencyFreeJson.Parse(json) is List<object> items)) return false;
                foreach (object item in items)
                {
                    if (!(item is Dictionary<string, object> fields) ||
                        !Text(fields, "g", out string guid) || !Text(fields, "n", out string name) ||
                        !Text(fields, "v", out string version)) return false;
                    bool clientside = false;
                    if (fields.TryGetValue("c", out object mode))
                    {
                        if (!(mode is bool flag)) return false;
                        clientside = flag;
                    }
                    var mod = new LobbyMismatchMod(guid, name, version, clientside);
                    if (mod.Guid.Length == 0 || mods.ContainsKey(mod.Guid)) return false;
                    mods.Add(mod.Guid, mod);
                }
                issue = null;
                return true;
            }
            catch (InvalidDataException) { return false; }
        }

        // The Extender DTO defaults absent/null strings to the same cleaned values.
        private static bool Text(Dictionary<string, object> fields, string key, out string value)
        {
            value = "";
            if (!fields.TryGetValue(key, out object raw) || raw == null) return true;
            if (!(raw is string text)) return false;
            value = text;
            return true;
        }

        internal static string LogHint(bool german) => LogHint(german ? "de-DE" : "en-US");
        internal static string LogHint(string locale) => Localization.UiText.ForLocale(locale, "LobbyMismatch.LogHint");

        internal string PopupText(string reason, bool german) => PopupText(reason, german ? "de-DE" : "en-US");
        internal string PopupText(string reason, string locale)
        {
            string text;
            if (MetadataIssue != null)
                text = Localization.UiText.ForLocale(locale, "LobbyMismatch.MetadataUnavailable");
            else if (Differences.Count == 0)
                text = Localization.UiText.ForLocale(locale, "LobbyMismatch.UnexplainedRejection");
            else
                text = string.Join("\n", Differences.Take(2).Select(d => d.PopupText(locale)));
            if (MetadataIssue != null || Differences.Count == 0)
                text += "\n" + LobbyMismatchDifference.Short((reason ?? "").Replace('\r', ' ').Replace('\n', ' '), 100);
            if (Differences.Count > 2)
                text += "\n" + Localization.UiText.ForLocale(locale, "LobbyMismatch.MoreDifferences");
            return text + "\n\n" + LogHint(locale);
        }
    }
}

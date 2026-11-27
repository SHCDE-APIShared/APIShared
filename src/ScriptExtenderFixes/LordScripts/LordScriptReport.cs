using System.Collections.Generic;
using System.Linq;

namespace APIShared.ScriptExtenderFixes
{
    // One AI slot whose Lord runs a Script Extender Lord AI script (init.lua) on this computer.
    internal sealed class LordScriptSlot
    {
        internal LordScriptSlot(int playerId, string lordName, string packageGuid, string packageVersion,
            bool? networked, string initLuaSha256)
        {
            PlayerId = playerId;
            LordName = lordName ?? string.Empty;
            PackageGuid = packageGuid ?? string.Empty;
            PackageVersion = packageVersion ?? string.Empty;
            Networked = networked;
            InitLuaSha256 = initLuaSha256 ?? string.Empty;
        }

        internal int PlayerId { get; }
        internal string LordName { get; }
        internal string PackageGuid { get; }
        internal string PackageVersion { get; }
        // null: the Lord's package is not among the registered asset mods.
        internal bool? Networked { get; }
        internal string InitLuaSha256 { get; }
    }

    // Pure log decisions for scripted Lords in a real multiplayer match. Script Extender checks only
    // NetworkMode 1 asset mods when joining a lobby, and AllowScriptedLords is a local option; both can make
    // a Lord script run on some players only, which desyncs the match without any other log entry.
    internal static class LordScriptReport
    {
        internal static string Summary(IReadOnlyList<LordScriptSlot> slots, bool scriptsAllowed)
        {
            if (slots == null || slots.Count == 0) return null;
            return "Lord AI scripts in this multiplayer match: " + string.Join("; ", slots.Select(slot =>
                "player " + slot.PlayerId + " '" + slot.LordName + "' package=" +
                (slot.PackageGuid.Length == 0 ? "unknown" : slot.PackageGuid + " v" + slot.PackageVersion) +
                " networkMode=" + (slot.Networked == null ? "unknown" : slot.Networked.Value ? "Networked" : "Clientside") +
                " initLuaSha256=" + slot.InitLuaSha256)) +
                "; AllowScriptedLords=" + scriptsAllowed +
                ". Compare this line in every player's log; differences desync the match.";
        }

        internal static IReadOnlyList<string> Warnings(IReadOnlyList<LordScriptSlot> slots, bool scriptsAllowed)
        {
            var result = new List<string>();
            if (slots == null || slots.Count == 0) return result;
            if (!scriptsAllowed)
                result.Add("Lord AI scripts are disabled on this computer (Script Extender option AllowScriptedLords) while " +
                    Describe(slots) + " use an AI script (init.lua). All players must use the same setting; " +
                    "otherwise the match desyncs.");
            foreach (IGrouping<string, LordScriptSlot> package in slots.Where(slot => slot.Networked != true)
                .GroupBy(slot => slot.PackageGuid.Length == 0 ? "\u0000" + slot.LordName : slot.PackageGuid))
            {
                LordScriptSlot first = package.First();
                result.Add(first.Networked == null
                    ? "Lord '" + first.LordName + "' (" + Players(package) + ") runs an AI script (init.lua), but its package " +
                        "is not a registered Script Extender asset mod, so Script Extender cannot check that other players " +
                        "have it. If players differ, the match desyncs."
                    : "Lord '" + first.LordName + "' (" + Players(package) + ") runs an AI script (init.lua), but its package " +
                        first.PackageGuid + " v" + first.PackageVersion + " is declared client-side (info.json NetworkMode 0). " +
                        "Script Extender therefore does not check that other players have the identical package; if they " +
                        "differ, the match desyncs. The Lord author should set NetworkMode 1.");
            }
            return result;
        }

        private static string Describe(IEnumerable<LordScriptSlot> slots) => string.Join(", ", slots
            .Select(slot => "'" + slot.LordName + "' (player " + slot.PlayerId + ")"));

        private static string Players(IEnumerable<LordScriptSlot> slots) =>
            "player " + string.Join(", ", slots.Select(slot => slot.PlayerId));
    }
}

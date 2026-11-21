using APIShared.Internal;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace APIShared.Workshop
{
    // External mod managers (docs/WORKSHOP.md, "External mod managers"): a program outside the game
    // writes BepInEx/config/APIShared.ModManager.json to take Workshop packages over:
    //   { "SchemaVersion": 1, "Manager": "Name", "Items": { "3788821961": "managed", "*": "managed" } }
    // APIShared then never installs, updates, repairs, removes or offers a managed package, and the
    // preloader discards a change staged for it earlier. "*" takes over every package.
    // A file that exists but cannot be read or understood takes over every package (logged as
    // error): a manager's intent is never overridden by guessing. System types and the linked
    // DependencyFreeJson only, because the preloader (APIShared.DependencyBootstrap) reads it too.
    internal sealed class WorkshopManagerPolicy
    {
        internal const string FileName = "APIShared.ModManager.json";
        internal const string ManagedValue = "managed";
        internal static readonly WorkshopManagerPolicy None = new WorkshopManagerPolicy(null, false, new HashSet<ulong>(), null);

        internal readonly string Manager;   // display name; null without policy
        internal readonly string Error;     // why the file was not understood; null when valid
        private readonly bool all;
        private readonly HashSet<ulong> items;

        private WorkshopManagerPolicy(string manager, bool all, HashSet<ulong> items, string error)
        {
            Manager = manager; this.all = all; this.items = items; Error = error;
        }

        internal bool Manages(ulong workshopId) => all || items.Contains(workshopId);

        internal static string PathFor(string game) => Path.Combine(game, "BepInEx", "config", FileName);

        internal static WorkshopManagerPolicy Read(string game)
        {
            string path = PathFor(game);
            if (!File.Exists(path)) return None;
            try { return Parse(File.ReadAllText(path)); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new WorkshopManagerPolicy("external mod manager", true, new HashSet<ulong>(), FileName + " unreadable: " + ex.Message);
            }
        }

        internal static WorkshopManagerPolicy Parse(string text)
        {
            const string fallback = "external mod manager";
            Dictionary<string, object> json;
            try { json = DependencyFreeJson.Parse(text) as Dictionary<string, object>; }
            catch (Exception ex) { return new WorkshopManagerPolicy(fallback, true, new HashSet<ulong>(), FileName + " is not valid JSON: " + ex.Message); }
            if (json == null) return new WorkshopManagerPolicy(fallback, true, new HashSet<ulong>(), FileName + " must contain a JSON object.");
            object manager, schema, entries;
            string name = json.TryGetValue("Manager", out manager) && manager is string text1 && text1.Trim().Length > 0 ? text1.Trim() : fallback;
            if (!json.TryGetValue("SchemaVersion", out schema) || !(schema is IConvertible) || schema is string || Convert.ToInt64(schema, CultureInfo.InvariantCulture) != 1)
                return new WorkshopManagerPolicy(name, true, new HashSet<ulong>(), FileName + ": unsupported SchemaVersion (expected 1).");
            var managed = new HashSet<ulong>();
            bool everything = false;
            string error = null;
            if (json.TryGetValue("Items", out entries) && entries != null)
            {
                var map = entries as Dictionary<string, object>;
                if (map == null) return new WorkshopManagerPolicy(name, true, managed, FileName + ": Items must be an object.");
                foreach (var entry in map)
                {
                    ulong id = 0;
                    bool wildcard = entry.Key == "*";
                    if (!wildcard && (!ulong.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id == 0))
                    {
                        error = FileName + ": invalid Workshop ID '" + entry.Key + "'.";
                        everything = true; // the manager meant some item; which one is unknown
                        continue;
                    }
                    // Only "managed" is defined; an unknown value still keeps APIShared away from that item.
                    if (!string.Equals(entry.Value as string, ManagedValue, StringComparison.OrdinalIgnoreCase))
                        error = FileName + ": unknown value for '" + entry.Key + "' (only \"managed\" is defined); treated as managed.";
                    if (wildcard) everything = true; else managed.Add(id);
                }
            }
            return new WorkshopManagerPolicy(name, everything, managed, error);
        }
    }
}

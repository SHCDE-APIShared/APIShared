using System;
using System.Collections.Generic;

namespace APIShared.ModSettings
{
    /// <summary>Mutable schema-3 mission settings keyed by arbitrary mod GUIDs, using ordinal identity.
    /// This is authoring data, not a live settings source. A missing mod/property uses the mod-defined
    /// default; player names use the player's value and overrides provide fixed author values.</summary>
    public sealed class MissionModSettingsDocument
    {
        /// <summary>Wire schema version; the codec currently accepts only 3.</summary>
        public int SchemaVersion { get; set; } = 3;
        /// <summary>Per-mod entries. Normalization replaces this dictionary with ordinally ordered entries.</summary>
        public Dictionary<string, MissionModSettingsEntry> Mods { get; set; } =
            new Dictionary<string, MissionModSettingsEntry>(StringComparer.Ordinal);
        /// <summary>Creates an empty schema-3 document, selecting mod-defined defaults.</summary>
        public static MissionModSettingsDocument CreateModDefaults() => new MissionModSettingsDocument();
    }

    /// <summary>Mutable player-selected and fixed settings for one mod. The codec rejects conflicting names.</summary>
    public sealed class MissionModSettingsEntry
    {
        /// <summary>Names whose values are supplied by the player's settings; normalized ordinally and deduplicated.</summary>
        public string[] PlayerSettings { get; set; } = Array.Empty<string>();
        /// <summary>Fixed author values, limited to scalars and flat scalar sequences by the codec.</summary>
        public Dictionary<string, object> Overrides { get; set; } = new Dictionary<string, object>(StringComparer.Ordinal);
    }
}
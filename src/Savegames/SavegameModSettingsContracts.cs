using System.Collections.Generic;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared
{
    /// <summary>Versioned savegame metadata for host settings and mission permission mode.</summary>
    [MessagePackObject]
    [MessagePackFormatter(typeof(SavegameModSettingsRecordFormatter))]
    public sealed class SavegameModSettingsRecord
    {
        /// <summary>MessagePack schema version.</summary>
        [Key(0)] public int Version { get; set; }
        /// <summary>Saved mission kind.</summary>
        [Key(1)] public int Kind { get; set; }
        /// <summary>Saved Customize state.</summary>
        [Key(2)] public int Variant { get; set; }
        /// <summary>Persistent host properties keyed by plugin GUID and property name.</summary>
        [Key(3)] public Dictionary<string, Dictionary<string, byte[]>> Mods { get; set; }
        /// <summary>Whether the mission was locked by conflicting launch evidence when saved.</summary>
        [Key(4)] public bool LockedByConflict { get; set; }
        /// <summary>Trail author rules keyed by plugin GUID and host property.</summary>
        [Key(5)] public Dictionary<string, Dictionary<string, TrailCreatorRule>> CreatorRules { get; set; }
        /// <summary>Whether the player actually launched through Customize.</summary>
        [Key(6)] public bool TrailCustomizeAllowed { get; set; }
    }

    /// <summary>One Trail author's host-property rule. Mode: default=0, player=1, fixed=2.</summary>
    public sealed class TrailCreatorRule
    {
        /// <summary>Author-selected mode.</summary>
        public int Mode { get; set; }
        /// <summary>MessagePack value for a fixed rule.</summary>
        public byte[] FixedValue { get; set; }
    }

    /// <summary>Availability and permission state of one selected savegame.</summary>
    public enum SavegameLoadChoiceState
    {
        /// <summary>The save or its present metadata cannot be trusted.</summary>
        Invalid = 0,
        /// <summary>No APIShared record exists; current host settings are mandatory.</summary>
        Legacy = 1,
        /// <summary>Saved host settings are mandatory.</summary>
        SavedOnly = 2,
        /// <summary>The host may choose saved or current settings.</summary>
        Selectable = 3,
    }

}

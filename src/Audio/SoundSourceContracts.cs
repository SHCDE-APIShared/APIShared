namespace APIShared.Audio
{
    /// <summary>Receives one attributed native sound call by reference. Change the call only through
    /// <paramref name="args"/>; a stored copy is a detached snapshot and changes nothing.</summary>
    /// <param name="args">The call being prepared; valid only for the duration of the callback.</param>
    public delegate void SoundSourceHandler(ref SoundSourceArgs args);

    /// <summary>Native-stage view of one attributed soldier or melee-kill sound call, taken inside the
    /// positional wrapper before the native sound record is appended. A value type passed by reference:
    /// dispatch allocates nothing.</summary>
    /// <remarks>Identity members are read from the validated native call: game IDs are one-based
    /// UnitAccess IDs and global IDs are <c>GameUnit.r_GlobalId</c>. They are immediate values, not a
    /// lifetime guarantee. No player ownership is inferred.</remarks>
    public struct SoundSourceArgs
    {
        /// <summary>Highest effect ID a replacement may select. Vanilla treats larger IDs specially.</summary>
        public const int MaxEffectId = 10000;
        private int sound;
        internal SoundSourceArgs(int sound, int x, int y, bool unattenuated, SoundSituation situation, SituationEvidence evidence,
            string key, int unitId, uint unitGlobalId, int unitType, int? targetId, uint? targetGlobalId)
        {
            this.sound = OriginalSoundId = sound; TileX = x; TileY = y;
            Kind = unattenuated ? SoundPlaybackKind.SpatialUnattenuated : SoundPlaybackKind.Spatial;
            Situation = situation; SituationEvidence = evidence; SourceKey = key;
            UnitId = unitId; UnitGlobalId = unitGlobalId; UnitType = unitType;
            TargetUnitId = targetId; TargetGlobalId = targetGlobalId;
        }
        /// <summary>Vanilla effect ID the native code requested.</summary>
        public int OriginalSoundId { get; }
        /// <summary>Effect ID that will be written into the native record; initially the original.</summary>
        public int SoundId => sound;
        /// <summary>Sound tile X passed to the wrapper.</summary>
        public int TileX { get; }
        /// <summary>Sound tile Y passed to the wrapper.</summary>
        public int TileY { get; }
        /// <summary><see cref="SoundPlaybackKind.Spatial"/> for wrapper 0x2960, <see cref="SoundPlaybackKind.SpatialUnattenuated"/> for the flagged wrapper.</summary>
        public SoundPlaybackKind Kind { get; }
        /// <summary>Named situation of this source and original effect; never Unknown here.</summary>
        public SoundSituation Situation { get; }
        /// <summary>Evidence for this source, effect and unit type.</summary>
        public SituationEvidence SituationEvidence { get; }
        /// <summary>True only after source-specific in-game confirmation.</summary>
        public bool SituationConfirmed => SituationEvidence == SituationEvidence.InGameConfirmed;
        /// <summary>Stable catalog entry key, for example <c>Unit75.Source04</c> or <c>MeleeKill.Source01</c>.</summary>
        public string SourceKey { get; }
        /// <summary>Game ID of the acting unit: the emitting soldier, or the attacker for <see cref="SoundSituation.MeleeKill"/>.</summary>
        public int UnitId { get; }
        /// <summary>Global ID of the acting unit, read at the call.</summary>
        public uint UnitGlobalId { get; }
        /// <summary>Type (eChimps value) of the acting unit.</summary>
        public int UnitType { get; }
        /// <summary>Game ID of the unit the sound is about (the killed victim for <see cref="SoundSituation.MeleeKill"/>); null when the source has none.</summary>
        public int? TargetUnitId { get; }
        /// <summary>Global ID of <see cref="TargetUnitId"/>, read at the call; null when the source has no target.</summary>
        public uint? TargetGlobalId { get; }
        /// <summary>Selects another Vanilla effect-table ID (not an Extender clip/play-list ID) for this call.
        /// The last successful replacement in callback order wins. Returns false without a change outside
        /// 0..<see cref="MaxEffectId"/>; an in-range ID that has no effect entry plays nothing in Vanilla.</summary>
        public bool TryReplaceSound(int effectId)
        {
            if (effectId < 0 || effectId > MaxEffectId) return false;
            sound = effectId; return true;
        }
        internal void Restore(int value) => sound = value;
    }
}

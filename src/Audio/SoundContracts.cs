using System;
using System.Threading;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Situation names remain stable when their evidence is promoted. Check SituationEvidence for confidence.</summary>
    public enum SoundSituation
    {
        /// <summary>No audited source attribution.</summary>
        Unknown,
        /// <summary>Arabian swordsman type75, rest state1, sword-draw sound180 and spatial flag0.</summary>
        ArabianIdleSwordDraw,
        /// <summary>Soldier ranged weapon release.</summary>
        RangedWeaponFire,
        /// <summary>Soldier ranged weapon loading.</summary>
        RangedWeaponReload,
        /// <summary>Soldier weapon swing.</summary>
        MeleeWeaponSwing,
        /// <summary>Soldier metal contact.</summary>
        MeleeMetalContact,
        /// <summary>Soldier attacking stone.</summary>
        AttackStone,
        /// <summary>Soldier attacking wood.</summary>
        AttackWood,
        /// <summary>Soldier moat excavation.</summary>
        MoatDigging,
        /// <summary>Tunneler excavation.</summary>
        TunnelDigging,
        /// <summary>Ladder carrier placement.</summary>
        LadderPlacement,
        /// <summary>Ladder carrier break animation.</summary>
        LadderBreaking,
        /// <summary>Soldier climbing-related animation.</summary>
        Climbing,
        /// <summary>Mounted soldier death sound.</summary>
        MountedDeath,
        /// <summary>Mounted soldier fall sound.</summary>
        MountedFall,
        /// <summary>Arabian idle sheathing branch; static reachability remains unconfirmed.</summary>
        ArabianIdleSwordSheathe,
        /// <summary>Soldier fire-setting or ignition animation.</summary>
        FireSetting,
        /// <summary>Engineer oil release.</summary>
        OilPouring,
        /// <summary>Soldier wall demolition.</summary>
        WallDemolition,
        /// <summary>Tunneler collapse sequence.</summary>
        TunnelCollapse
    }
    /// <summary>Evidence for this particular source and unit type, not every use of the situation enum.</summary>
    public enum SituationEvidence
    {
        /// <summary>No safe attribution.</summary>
        Unknown,
        /// <summary>Source is technically attributed; action interpretation is provisional.</summary>
        Inferred,
        /// <summary>Native action semantics audited, without in-game acceptance.</summary>
        NativeAudited,
        /// <summary>Native source and action also confirmed in game.</summary>
        InGameConfirmed
    }
    /// <summary>The selected presentation target namespace.</summary>
    public enum SoundTargetKind
    {
        /// <summary>Vanilla effect-table ID.</summary>
        VanillaEffect,
        /// <summary>Stable APIShared clip handle, not an integer effect ID.</summary>
        CustomClip
    }
    /// <summary>Immutable selected presentation target.</summary>
    public sealed class SoundTarget
    {
        internal SoundTarget(int effect, SoundClipHandle clip = null) { EffectId = clip == null ? (int?)effect : null; Clip = clip; }
        /// <summary>Target namespace.</summary>
        public SoundTargetKind Kind => Clip == null ? SoundTargetKind.VanillaEffect : SoundTargetKind.CustomClip;
        /// <summary>Vanilla effect ID, null for a clip.</summary>
        public int? EffectId { get; }
        /// <summary>Clip identity, null for a Vanilla effect.</summary>
        public SoundClipHandle Clip { get; }
        /// <summary>Diagnostic identity; never interprets a clip as an effect ID.</summary>
        public override string ToString() => Clip == null ? "Effect:" + EffectId : "Clip:" + Clip.OwnerGuid + "/" + Clip.RegistrationId;
    }
    /// <summary>The original record branch, before optional presentation changes.</summary>
    public enum SoundPlaybackKind
    {
        /// <summary>Record not available or does not match the call.</summary>
        Unknown,
        /// <summary>Explicit volume and panorama record.</summary>
        Explicit,
        /// <summary>Tile-positioned playback with Vanilla spatial attenuation.</summary>
        Spatial,
        /// <summary>Tile-positioned playback with the nonzero native flag, without spatial gain attenuation.</summary>
        SpatialUnattenuated
    }

    /// <summary>Immutable record snapshot. No native pointer, live unit ID or player attribution crosses threads.</summary>

    public sealed class SoundContext
    {
        internal SoundContext(int id, float volume, float pan, SoundPlaybackKind kind, SoundSituation situation, int? x, int? y)
         : this(id, volume, pan, kind, situation, x, y,
            situation == SoundSituation.ArabianIdleSwordDraw ? SituationEvidence.InGameConfirmed : SituationEvidence.Unknown,
            situation == SoundSituation.ArabianIdleSwordDraw ? 75 : (int?)null, null) { }
        internal SoundContext(int id, float volume, float pan, SoundPlaybackKind kind, SoundSituation situation, int? x, int? y,
            SituationEvidence evidence, int? type, string source)
        { OriginalSoundId = id; OriginalVolume = volume; OriginalPan = pan; Kind = kind; Situation = situation; TileX = x; TileY = y; SituationEvidence = evidence; UnitType = type; SourceKey = source; }
        /// <summary>Original Vanilla effect ID, not an Extender registered clip ID.</summary>
        public int OriginalSoundId { get; }
        /// <summary>Vanilla's computed per-play gain; may exceed one. Clip and global gains remain downstream.</summary>
        public float OriginalVolume { get; }
        /// <summary>Vanilla's computed stereo panorama.</summary>
        public float OriginalPan { get; }
        /// <summary>Explicit or spatial record branch.</summary>
        public SoundPlaybackKind Kind { get; }
        /// <summary>Named situation or Unknown. Names may be provisional; inspect SituationEvidence. Sound ID alone never attributes a source.</summary>
        public SoundSituation Situation { get; }
        /// <summary>Evidence for this source and unit type.</summary>
        public SituationEvidence SituationEvidence { get; }
        /// <summary>True only after source-specific in-game confirmation.</summary>
        public bool SituationConfirmed => SituationEvidence == SituationEvidence.InGameConfirmed;
        /// <summary>Stable catalog entry key, null without source attribution.</summary>
        public string SourceKey { get; }
        /// <summary>Source tile X for spatial records; null for explicit/unknown records.</summary>
        public int? TileX { get; }
        /// <summary>Source tile Y for spatial records; null for explicit/unknown records.</summary>
        public int? TileY { get; }
        /// <summary>Snapshot type from validated native source attribution, otherwise null. Not a live unit ID.</summary>
        public int? UnitType { get; }
    }

    /// <summary>Synchronous Unity playback Pre. Mutations freeze afterward; owner-private State follows the shared event contract.</summary>

    public sealed class SoundPreEventArgs : InterceptionPreEventArgs
    {
        private readonly Func<int, bool> validEffect;
        private float factor = 1f, pan;
        private int sound;
        private SoundClipHandle clip;
        internal SoundPreEventArgs(SoundContext context, bool unstoppable, Func<int, bool> validEffect)
        { Context = context; Unstoppable = unstoppable; this.validEffect = validEffect; sound = context.OriginalSoundId; pan = context.OriginalPan; }
        /// <summary>Immutable original input and audited source context.</summary>
        public SoundContext Context { get; }
        /// <summary>Original channel-policy argument; preserved by this service.</summary>
        public bool Unstoppable { get; }
        /// <summary>Cumulative multiplicative gain, initially one.</summary>
        public float VolumeFactor => factor;
        /// <summary>Final computed gain before downstream clip/global multipliers.</summary>
        public float Volume => Context.OriginalVolume * factor;
        /// <summary>Selected effect ID, or -1 when the selected target is a custom clip.</summary>
        public int SoundId => clip == null ? sound : -1;
        /// <summary>Selected presentation namespace and identity.</summary>
        public SoundTarget Target => new SoundTarget(sound, clip);
        /// <summary>Current panorama, initially Vanilla's value.</summary>
        public float Pan => pan;
        /// <summary>Composes a finite nonnegative factor. Invalid or overflowing changes throw and roll back this callback's mutations.</summary>
        public void MultiplyVolume(float value)
        {
            CheckMutable();
            float next = factor * value;
            if (!Finite(value) || value < 0 || !Finite(next) || !Finite(Context.OriginalVolume * next) || (clip != null && !Finite(Context.OriginalVolume * next * clip.BaseVolume)))
                throw new ArgumentOutOfRangeException(nameof(value), "Gain and resulting volume must be finite and nonnegative.");
            factor = next;
        }
        /// <summary>Replaces this playback with a valid Vanilla effect-table ID. Custom play-list clip IDs are not accepted. Last valid ordered replacement wins.</summary>
        public void ReplaceSound(int effectId)
        { CheckMutable(); if (!validEffect(effectId)) throw new ArgumentOutOfRangeException(nameof(effectId)); sound = effectId; clip = null; }
        /// <summary>Selects a ready clip. Pending/invalid handles return false without mutation. Last valid ordered target wins.</summary>
        public bool TryReplaceClip(SoundClipHandle value)
        { CheckMutable(); if (value == null || !value.IsReady || !Finite(Volume * value.BaseVolume)) return false; clip = value; return true; }
        internal void FallBackToEffect() { clip = null; }
        /// <summary>Sets finite stereo panorama in [-1,1]. Last valid ordered replacement wins.</summary>
        public void SetPan(float value)
        { CheckMutable(); if (!Finite(value) || value < -1 || value > 1) throw new ArgumentOutOfRangeException(nameof(value)); pan = value; }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal override object Capture() => new Snapshot(factor, pan, sound, clip);
        internal override void Restore(object snapshot) { var s = (Snapshot)snapshot; factor = s.Factor; pan = s.Pan; sound = s.Sound; clip = s.Clip; }
        private readonly struct Snapshot
        { internal readonly float Factor, Pan; internal readonly int Sound; internal readonly SoundClipHandle Clip; internal Snapshot(float factor, float pan, int sound, SoundClipHandle clip) { Factor = factor; Pan = pan; Sound = sound; Clip = clip; } }
    }

    /// <summary>Completed presentation dispatch, including suppression. Submission is not proof of audible playback: Vanilla may cull/throttle it.</summary>

    public sealed class SoundPostEventArgs
    {
        internal SoundPostEventArgs(SoundPreEventArgs pre, object state)
        { Context = pre.Context; SoundId = pre.SoundId; Target = pre.Target; Volume = pre.Volume; Pan = pre.Pan; Unstoppable = pre.Unstoppable; WasSuppressed = pre.SkipOriginalFunction; State = state; }
        /// <summary>Original immutable context.</summary>
        public SoundContext Context { get; }
        /// <summary>Final Vanilla effect ID, or -1 for custom playback.</summary>
        public int SoundId { get; }
        /// <summary>Final target, including any fallback after clip invalidation.</summary>
        public SoundTarget Target { get; }
        /// <summary>Final per-play gain. Custom clip BaseVolume remains a downstream multiplier.</summary>
        public float Volume { get; }
        /// <summary>Final panorama.</summary>
        public float Pan { get; }
        /// <summary>Preserved channel-policy argument.</summary>
        public bool Unstoppable { get; }
        /// <summary>True when Pre suppressed the SFXManager call; false after successful submission. Original exceptions have no Post.</summary>
        public bool WasSuppressed { get; }
        /// <summary>This registration's Pre state only.</summary>
        public object State { get; }
    }

    /// <summary>Process-owned registration handle. Activation is atomic; no Dispose or executable patch toggles exist.</summary>

    public sealed class SoundRegistration
    {
        private int enabled = 1;
        internal SoundRegistration() { }
        /// <summary>Logical activation. In-flight invocations keep their captured observers through Post.</summary>
        public bool Enabled => Volatile.Read(ref enabled) != 0;
        /// <summary>May be called from any thread; affects subsequent notification snapshots.</summary>
        public void SetEnabled(bool value) => Volatile.Write(ref enabled, value ? 1 : 0);
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Events;

namespace APIShared.Audio
{
    /// <summary>Meaning of an attributed unit voice. Check source evidence; a cue alone does not identify its caller.</summary>
    public enum UnitVoiceSituation
    {
        /// <summary>No safe action interpretation.</summary>
        Unknown = 0,
        /// <summary>Unit selection acknowledgement.</summary>
        Selection = 1,
        /// <summary>Ordinary movement acknowledgement.</summary>
        Move = 2,
        /// <summary>Movement destination rejected as unreachable.</summary>
        MoveUnreachable = 3,
        /// <summary>Distant movement acknowledgement.</summary>
        MoveDistant = 4,
        /// <summary>Attack voice from the strong-target group.</summary>
        AttackStrong = 5,
        /// <summary>Attack voice from the weak-target group.</summary>
        AttackWeak = 6,
        /// <summary>Melee-range attack acknowledgement.</summary>
        AttackMelee = 7,
        /// <summary>Moat excavation order.</summary>
        DigMoat = 8,
        /// <summary>Disband acknowledgement.</summary>
        Disband = 9,
        /// <summary>Successful recruitment acknowledgement.</summary>
        Recruitment = 10,
        /// <summary>Bedouin healer action.</summary>
        Heal = 11,
        /// <summary>Sapper or demolisher special action.</summary>
        Sap = 12,
        /// <summary>Engineer or siege-device action; the original cue identifies the particular voice.</summary>
        EngineerAction = 13,
        /// <summary>Selected civilian's contextual comment.</summary>
        CivilianComment = 14
    }

    /// <summary>Strength of the evidence for one source or cue interpretation.</summary>
    public enum UnitVoiceEvidence
    {
        /// <summary>No safe attribution.</summary>
        Unknown = 0,
        /// <summary>Provisional action interpretation.</summary>
        Inferred = 1,
        /// <summary>Statically audited native source, without game acceptance.</summary>
        NativeAudited = 2,
        /// <summary>Source-specific game acceptance also completed.</summary>
        InGameConfirmed = 3
    }

    /// <summary>Immutable, non-owning identity of a built-in unit speech file; not an SFX or play-list integer.</summary>
    public sealed class UnitVoiceCue
    {
        internal UnitVoiceCue(string key, string fileName, string voiceBankKey, UnitVoiceSituation situation, UnitVoiceEvidence evidence)
        { Key = key; FileName = fileName; VoiceBankKey = voiceBankKey; Situation = situation; Evidence = evidence; }
        /// <summary>Stable ordinal catalog key.</summary>
        public string Key { get; }
        /// <summary>Original file basename, including extension.</summary>
        public string FileName { get; }
        /// <summary>Voice bank, independent of the originally requested unit type.</summary>
        public string VoiceBankKey { get; }
        /// <summary>File-level classification. A source may use this same file for a different action, such as recruitment.</summary>
        public UnitVoiceSituation Situation { get; }
        /// <summary>Evidence for the file-level classification, not for every caller.</summary>
        public UnitVoiceEvidence Evidence { get; }
    }

    /// <summary>Immutable catalog description of one audited native generator family. Availability is queried separately through UnitVoiceEvents.</summary>
    public sealed class UnitVoiceSourceDescriptor
    {
        internal UnitVoiceSourceDescriptor(string key, string description, UnitVoiceEvidence evidence)
        { Key = key; Description = description; Evidence = evidence; }
        /// <summary>Stable ordinal source-family key accepted by UnitVoiceSelection.</summary>
        public string Key { get; }
        /// <summary>Static source semantics and attribution limits; not a promise of a player or unit identity.</summary>
        public string Description { get; }
        /// <summary>Evidence for this family's static source contract; independent of runtime readiness.</summary>
        public UnitVoiceEvidence Evidence { get; }
    }

    /// <summary>Immutable catalog grouping of native voice intents by their actual selected bank.</summary>
    public sealed class UnitVoiceBankDescriptor
    {
        internal UnitVoiceBankDescriptor(string key, IReadOnlyList<UnitVoiceCue> cues) { Key = key; Cues = cues; }
        /// <summary>Stable ordinal bank key, independent of a requested unit type.</summary>
        public string Key { get; }
        /// <summary>Read-only cue collection for completeness comparisons. A native intent does not guarantee a Vanilla playback route.</summary>
        public IReadOnlyList<UnitVoiceCue> Cues { get; }
    }

    /// <summary>Native emission snapshot. No pointers or live unit identities cross into playback.</summary>
    public readonly struct UnitVoiceSource
    {
        internal UnitVoiceSource(int? requestingPlayerId, int? ownerPlayerId, int? requestedUnitType,
            string voiceBankKey, string originalCueKey, UnitVoiceSituation situation, UnitVoiceEvidence evidence, long sessionId, string sourceKey = null)
        {
            RequestingPlayerId = requestingPlayerId; OwnerPlayerId = ownerPlayerId; RequestedUnitType = requestedUnitType;
            VoiceBankKey = voiceBankKey; OriginalCueKey = originalCueKey; Situation = situation; Evidence = evidence; SessionId = sessionId; SourceKey = sourceKey;
        }
        /// <summary>One-based requesting/view player at emission, only where the source proves it; otherwise null.</summary>
        public int? RequestingPlayerId { get; }
        /// <summary>One-based actual owner only where independently known. Civilian owners can differ from the view player.</summary>
        public int? OwnerPlayerId { get; }
        /// <summary>Original SHCDESE eChimps value when proved by the source, otherwise null. Not inferred from the selected voice.</summary>
        public int? RequestedUnitType { get; }
        /// <summary>Actual selected voice bank; siege devices may use an Engineer bank.</summary>
        public string VoiceBankKey { get; }
        /// <summary>Catalog key of the file Vanilla selected before optional playback changes.</summary>
        public string OriginalCueKey { get; }
        /// <summary>Source-specific action interpretation.</summary>
        public UnitVoiceSituation Situation { get; }
        /// <summary>Evidence for this source's attribution.</summary>
        public UnitVoiceEvidence Evidence { get; }
        /// <summary>Process-local mission generation at emission. Not a saved or network identity.</summary>
        public long SessionId { get; }
        /// <summary>Stable native source key, independent of the chosen file; null without source attribution.</summary>
        public string SourceKey { get; }
    }

    /// <summary>Immutable original Unity playback request. Source fields describe emission, while live queries in callbacks describe playback time.</summary>
    public sealed class UnitVoiceContext
    {
        internal UnitVoiceContext(UnitVoiceSource source, string originalPath, long invocationId, int channel,
            float originalVolume, bool ignoreSpeechMuting = false, bool ignorePauseState = false,
            string uiLocale = null, string speechAssetBranch = null, MissionContext missionContext = null)
        {
            Source = source; OriginalPath = originalPath; InvocationId = invocationId; Channel = channel;
            OriginalVolume = originalVolume; IgnoreSpeechMuting = ignoreSpeechMuting; IgnorePauseState = ignorePauseState;
            UiLocale = uiLocale; SpeechAssetBranch = speechAssetBranch; MissionContext = missionContext;
        }
        /// <summary>Audited immutable emission snapshot.</summary>
        public UnitVoiceSource Source { get; }
        /// <summary>Original path after the native output's normal last-file coalescing.</summary>
        public string OriginalPath { get; }
        /// <summary>Process-local presentation invocation identity; not a saved or network identity.</summary>
        public long InvocationId { get; }
        /// <summary>Unmodified SFXManager speech channel argument.</summary>
        public int Channel { get; }
        /// <summary>Original SFXManager argument. Vanilla speech uses the game and master speech settings downstream.</summary>
        public float OriginalVolume { get; }
        /// <summary>Original speech-muting argument, preserved by the service.</summary>
        public bool IgnoreSpeechMuting { get; }
        /// <summary>Original pause argument, preserved by the service.</summary>
        public bool IgnorePauseState { get; }
        /// <summary>Current UI locale captured at playback, or null when unavailable. Independent of the speech asset branch.</summary>
        public string UiLocale { get; }
        /// <summary>Current resource branch: EnglishSpeech or LocalizedSpeech, or null when unavailable. This does not identify the spoken language.</summary>
        public string SpeechAssetBranch { get; }
        /// <summary>Current immutable mission/editor context captured at playback, or null when unavailable. Source.SessionId still describes emission.</summary>
        public MissionContext MissionContext { get; }
    }

    /// <summary>Namespace of the selected unit speech target.</summary>
    public enum UnitVoiceTargetKind
    {
        /// <summary>Unmodified original voice request.</summary>
        Original,
        /// <summary>A catalogued built-in speech file.</summary>
        Vanilla,
        /// <summary>A prepared owner-private resource.</summary>
        Resource
    }

    /// <summary>Immutable final target identity. Effect-table and Extender play-list IDs are never accepted.</summary>
    public sealed class UnitVoiceTarget
    {
        internal UnitVoiceTarget(UnitVoiceTargetKind kind, UnitVoiceCue cue = null, UnitVoiceResourceHandle resource = null)
        { Kind = kind; Cue = cue; Resource = resource; }
        /// <summary>Target namespace.</summary>
        public UnitVoiceTargetKind Kind { get; }
        /// <summary>Selected built-in cue, null for Original or Resource.</summary>
        public UnitVoiceCue Cue { get; }
        /// <summary>Prepared resource handle, null for Original or Vanilla.</summary>
        public UnitVoiceResourceHandle Resource { get; }
    }

    /// <summary>Synchronous Unity playback choices. Mutations freeze after Pre; exceptions roll back this callback's choices, veto and State.</summary>
    public sealed class UnitVoicePreEventArgs : InterceptionPreEventArgs
    {
        private static readonly UnitVoiceTarget Original = new UnitVoiceTarget(UnitVoiceTargetKind.Original);
        private UnitVoiceTarget target = Original, savedTarget;
        private float factor = 1f, savedFactor;
        internal bool? Submitted;
        internal string PlaybackDiagnostic, FallbackReason;
        internal UnitVoicePreEventArgs(UnitVoiceContext context) { Context = context ?? throw new ArgumentNullException(nameof(context)); }
        /// <summary>Immutable original request.</summary>
        public UnitVoiceContext Context { get; }
        /// <summary>Current choice. The last valid ordered replacement wins.</summary>
        public UnitVoiceTarget Target => target;
        /// <summary>Composed per-request factor, initially one; game volume settings remain downstream.</summary>
        public float VolumeFactor => factor;
        /// <summary>Deliberately selects the original request again. Existing gain, sticky veto and private State remain unchanged.</summary>
        public void UseOriginal() { CheckMutable(); target = Original; }
        /// <summary>Selects a cue from the current catalog with a known Vanilla speech route. Invalid or unmapped references leave the current choice unchanged.</summary>
        public bool TryReplaceVanillaCue(UnitVoiceCue cue)
        {
            CheckMutable();
            if (cue == null || !UnitVoiceCatalog.TryGetCue(cue.Key, out UnitVoiceCue registered) ||
                !ReferenceEquals(cue, registered) || UnitVoiceCatalog.SpeechFolder(cue) == null) return false;
            target = new UnitVoiceTarget(UnitVoiceTargetKind.Vanilla, cue); return true;
        }
        /// <summary>Selects a ready owner-private resource. Pending, invalid or overflowing choices return false without mutation.</summary>
        public bool TryReplaceResource(UnitVoiceResourceHandle resource)
        {
            CheckMutable();
            if (resource == null || !resource.IsReady || !Finite(resource.BaseVolume) || resource.BaseVolume < 0 || !Finite(factor * resource.BaseVolume)) return false;
            target = new UnitVoiceTarget(UnitVoiceTargetKind.Resource, resource: resource); return true;
        }
        /// <summary>Composes a finite nonnegative factor. Invalid values or products throw, rolling back the callback.</summary>
        public void MultiplyVolume(float value)
        {
            CheckMutable();
            float next = factor * value;
            float basis = target.Resource?.BaseVolume ?? 1f;
            if (!Finite(value) || value < 0 || !Finite(next) || !Finite(next * basis))
                throw new ArgumentOutOfRangeException(nameof(value), "Voice gain and its resource product must be finite and nonnegative.");
            factor = next;
        }
        internal void FallBackToOriginal(string reason = null) { target = Original; FallbackReason = reason; }
        internal void SetPlaybackOutcome(bool submitted, string diagnostic = null) { Submitted = submitted; PlaybackDiagnostic = diagnostic; }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal override object Capture() { savedTarget = target; savedFactor = factor; return this; }
        internal override void Restore(object snapshot)
        {
            if (!ReferenceEquals(snapshot, this)) throw new InvalidOperationException("Foreign unit voice checkpoint.");
            target = savedTarget; factor = savedFactor;
        }
        internal override void ClearCapture() { savedTarget = null; }
    }

    /// <summary>Result after normal submission or suppression. This does not prove channel acceptance, decoding or audible output.</summary>
    public sealed class UnitVoicePostEventArgs
    {
        internal UnitVoicePostEventArgs(UnitVoicePreEventArgs pre, object state)
        {
            Context = pre.Context; Target = pre.Target; VolumeFactor = pre.VolumeFactor; WasSuppressed = pre.SkipOriginalFunction; State = state;
            WasSubmitted = !WasSuppressed && (pre.Submitted ?? true); Diagnostic = pre.PlaybackDiagnostic; FallbackReason = pre.FallbackReason;
        }
        /// <summary>Original immutable request.</summary>
        public UnitVoiceContext Context { get; }
        /// <summary>Final submitted target, including a resource-failure fallback.</summary>
        public UnitVoiceTarget Target { get; }
        /// <summary>Final request factor before a selected resource's base volume.</summary>
        public float VolumeFactor { get; }
        /// <summary>True when Pre suppressed this presentation request. Native queueing and gameplay already happened.</summary>
        public bool WasSuppressed { get; }
        /// <summary>Whether the playback pipeline submitted the request. Does not prove channel acceptance or audible output.</summary>
        public bool WasSubmitted { get; }
        /// <summary>Submission diagnostic, or null when none was reported.</summary>
        public string Diagnostic { get; }
        /// <summary>Reason for a final return to the original target, or null when no fallback was reported.</summary>
        public string FallbackReason { get; }
        /// <summary>Only this registration's private Pre state.</summary>
        public object State { get; }
    }

    /// <summary>Process-owned logical activation handle. Disabling never removes executable hooks.</summary>
    public sealed class UnitVoiceRegistration
    {
        internal readonly object ActivationSync = new object();
        internal int EnabledValue;
        internal bool Attached;
        internal UnitVoiceRegistration(UnitVoiceSelection sources) { Sources = sources; }
        /// <summary>Immutable source filter for this registration.</summary>
        public UnitVoiceSelection Sources { get; }
        /// <summary>Activation for subsequent invocation snapshots. In-flight Pre/Post pairs remain intact.</summary>
        public bool Enabled => Volatile.Read(ref EnabledValue) != 0;
        /// <summary>Atomically changes logical activation from any thread.</summary>
        public void SetEnabled(bool value) => UnitVoiceEvents.SetEnabled(this, value);
    }
}

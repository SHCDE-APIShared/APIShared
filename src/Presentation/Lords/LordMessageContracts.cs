using System;
using System.Threading;
using APIShared.Events;
using CrusaderDE;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Presentation
{
    /// <summary>Namespace of one independently selected message component.</summary>
    public enum LordMessageSourceKind
    {
        /// <summary>The original presentation path; no substitution.</summary>
        Original,
        /// <summary>A component of a built-in Lord message.</summary>
        Vanilla,
        /// <summary>An owner-private indexed mod resource.</summary>
        ModAsset,
        /// <summary>Text already localized by the consumer.</summary>
        LiteralText
    }

    /// <summary>Immutable speech reference. This is not an effect-table or play-list ID.</summary>
    public sealed class LordMessageAudio
    {
        internal LordMessageAudio(LordMessageSourceKind kind, string path, bool ready = false) { Kind = kind; Path = path; IsReady = kind != LordMessageSourceKind.ModAsset || ready; }
        /// <summary>The reference namespace.</summary>
        public LordMessageSourceKind Kind { get; }
        /// <summary>Speech path, including provider qualification for private assets.</summary>
        public string Path { get; }
        /// <summary>Preparation found a usable resource. This snapshot is not decoder/output acceptance.</summary>
        public bool IsReady { get; }
        /// <summary>References existing speech without copying files or drawing game RNG.</summary>
        public static LordMessageAudio FromVanilla(VanillaLordMessage message) =>
            new LordMessageAudio(LordMessageSourceKind.Vanilla, (message ?? throw new ArgumentNullException(nameof(message))).AudioPath);
        /// <summary>Prepares an indexed private mod asset on Unity, outside Pre/Post. May materialize packaged files;
        /// retain the result and inspect IsReady. Selection performs no resource lookup or decoding.</summary>
        public static LordMessageAudio FromModAsset(string providerGuid, string relativePath)
        {
            string path = LordMessageAssets.Reference(providerGuid, relativePath);
            return new LordMessageAudio(LordMessageSourceKind.ModAsset, path, LordMessageAssets.Prepare(path, false));
        }
    }

    /// <summary>Immutable video reference, independent of the chosen speech.</summary>
    public sealed class LordMessageVideo
    {
        internal LordMessageVideo(LordMessageSourceKind kind, string path, bool ready = false) { Kind = kind; Path = path; IsReady = kind != LordMessageSourceKind.ModAsset || ready; }
        /// <summary>The reference namespace.</summary>
        public LordMessageSourceKind Kind { get; }
        /// <summary>Video path, including provider qualification for private assets.</summary>
        public string Path { get; }
        /// <summary>Preparation found a usable resource. This snapshot is not decoder/output acceptance.</summary>
        public bool IsReady { get; }
        /// <summary>References an existing game video without copying it.</summary>
        public static LordMessageVideo FromVanilla(VanillaLordMessage message) =>
            new LordMessageVideo(LordMessageSourceKind.Vanilla, (message ?? throw new ArgumentNullException(nameof(message))).VideoPath);
        /// <summary>Prepares an indexed private WebM/MP4 on Unity, outside Pre/Post. May materialize packaged files;
        /// retain the result and inspect IsReady. Selection performs no resource lookup.</summary>
        public static LordMessageVideo FromModAsset(string providerGuid, string relativePath)
        {
            string path = LordMessageAssets.Reference(providerGuid, relativePath);
            return new LordMessageVideo(LordMessageSourceKind.ModAsset, path, LordMessageAssets.Prepare(path, true));
        }
    }

    /// <summary>Immutable text reference. Vanilla translation follows the game's current UI language.</summary>
    public sealed class LordMessageText
    {
        internal LordMessageText(LordMessageSourceKind kind, int section, int index, string value)
        { Kind = kind; Section = section; Index = index; Value = value; }
        /// <summary>The reference namespace.</summary>
        public LordMessageSourceKind Kind { get; }
        /// <summary>Consumer-localized text, or null for a game translation/original path.</summary>
        public string Value { get; }
        internal int Section { get; }
        internal int Index { get; }
        /// <summary>References the translated text of an existing message.</summary>
        public static LordMessageText FromVanilla(VanillaLordMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            return new LordMessageText(LordMessageSourceKind.Vanilla, (int)message.TextSection, message.TextIndex, null);
        }
        /// <summary>Uses already localized Unicode text. Empty text is permitted; null is rejected.</summary>
        public static LordMessageText FromText(string text) =>
            new LordMessageText(LordMessageSourceKind.LiteralText, 0, 0, text ?? throw new ArgumentNullException(nameof(text)));
        internal string Resolve() => Kind == LordMessageSourceKind.LiteralText ? Value :
            Translate.Instance.lookUpText((global::Enums.eTextSections)Section, Index);
    }

    /// <summary>Snapshot of a validated message at Unity presentation, after native queueing and Extender media selection.</summary>
    public sealed class LordMessageContext
    {
        internal LordMessageContext(long invocation, int header, int section, int index, AILords? lord,
            int? player, AILordMessageType type, string audio, string video)
        {
            InvocationId = invocation; HeaderIndex = header; TextSection = section; TextIndex = index;
            VanillaLord = lord; PlayerId = player; MessageType = type; OriginalAudioPath = audio; OriginalVideoPath = video;
        }
        /// <summary>Process-local render invocation, not a saved/network message identity.</summary>
        public long InvocationId { get; }
        /// <summary>Record index within that invocation. Together with InvocationId identifies this presentation.</summary>
        public int HeaderIndex { get; }
        /// <summary>Vanilla speech identity encoded by the original text index; includes Vanilla Lords using custom AICs.</summary>
        public AILords? VanillaLord { get; }
        /// <summary>One-based player ID only when explicitly encoded by a custom-Lord text section; otherwise null.</summary>
        public int? PlayerId { get; }
        /// <summary>The original semantic category. IncomingMessage and nickname entries are not published.</summary>
        public AILordMessageType MessageType { get; }
        /// <summary>Original native text selector; negative sections encode custom-Lord player IDs.</summary>
        public int TextSection { get; }
        /// <summary>Original text index, unchanged in the native output and game OST.</summary>
        public int TextIndex { get; }
        /// <summary>Effective original speech path from this PlayState, after Extender resolution.</summary>
        public string OriginalAudioPath { get; }
        /// <summary>Effective original video path from this PlayState, after Extender resolution.</summary>
        public string OriginalVideoPath { get; }
    }

    /// <summary>Invocation-local choices. All parts initially pass through; setters are valid only during synchronous Pre.</summary>
    public sealed class LordMessagePreEventArgs
    {
        private readonly LordMessageMutation mutation;
        internal LordMessagePreEventArgs(LordMessageMutation value) { mutation = value; }
        /// <summary>Original immutable presentation snapshot.</summary>
        public LordMessageContext Context => mutation.Context;
        /// <summary>Current speech target.</summary>
        public LordMessageAudio Audio => mutation.Audio;
        /// <summary>Current video target.</summary>
        public LordMessageVideo Video => mutation.Video;
        /// <summary>Current text target.</summary>
        public LordMessageText Text => mutation.Text;
        /// <summary>Private state passed only to this registration's matching Post.</summary>
        public object State { get => mutation.State; set => mutation.State = value; }
        /// <summary>Selects speech. Invalid or unavailable references leave the current selection unchanged.</summary>
        public bool TryReplaceAudio(LordMessageAudio target)
        {
            mutation.CheckMutable();
            if (target == null || !target.IsReady || target.Kind == LordMessageSourceKind.Original || !LordMessageAssets.CanUse(target.Kind, target.Path)) return false;
            mutation.Audio = target; return true;
        }
        /// <summary>Selects video without changing loop/wait flags. Invalid/unavailable references do not mutate the choice.</summary>
        public bool TryReplaceVideo(LordMessageVideo target)
        {
            mutation.CheckMutable();
            if (target == null || !target.IsReady || target.Kind == LordMessageSourceKind.Original || !LordMessageAssets.CanUse(target.Kind, target.Path)) return false;
            mutation.Video = target; return true;
        }
        /// <summary>Selects literal or Vanilla text without altering the native text index.</summary>
        public bool TryReplaceText(LordMessageText target)
        {
            mutation.CheckMutable();
            if (target == null || target.Kind == LordMessageSourceKind.Original) return false;
            mutation.Text = target; return true;
        }
    }

    /// <summary>Final choices after normal media-call return. Not evidence of decoder, channel or hardware output.</summary>
    public sealed class LordMessagePostEventArgs
    {
        internal LordMessagePostEventArgs(LordMessageMutation pre, object state)
        { Context = pre.Context; Audio = pre.Audio; Video = pre.Video; Text = pre.Text; State = state; }
        /// <summary>The original message snapshot.</summary>
        public LordMessageContext Context { get; }
        /// <summary>Final submitted speech target.</summary>
        public LordMessageAudio Audio { get; }
        /// <summary>Final submitted video target.</summary>
        public LordMessageVideo Video { get; }
        /// <summary>Final bound text target; UI text is applied by the subsequent normal OST update.</summary>
        public LordMessageText Text { get; }
        /// <summary>This registration's private Pre state.</summary>
        public object State { get; }
    }

    /// <summary>Process-owned registration. No executable hooks are removed when disabled.</summary>
    public sealed class LordMessageRegistration
    {
        internal LordMessageRegistration() { }
        private int enabled = 1;
        /// <summary>Whether subsequent messages select this registration. A message already in progress retains its choices.</summary>
        public bool Enabled => Volatile.Read(ref enabled) != 0;
        /// <summary>Atomic logical activation, callable from any thread.</summary>
        public void SetEnabled(bool value) => Volatile.Write(ref enabled, value ? 1 : 0);
    }

    // Keep cancellation internal: this broker replaces components and never vetoes gameplay or native queueing.
    internal sealed class LordMessageMutation : InterceptionPreEventArgs
    {
        internal readonly LordMessageContext Context;
        internal readonly LordMessagePreEventArgs Public;
        internal LordMessageAudio Audio;
        internal LordMessageVideo Video;
        internal LordMessageText Text;
        private LordMessageAudio savedAudio;
        private LordMessageVideo savedVideo;
        private LordMessageText savedText;
        internal LordMessageMutation(LordMessageContext context)
        {
            Context = context; Public = new LordMessagePreEventArgs(this);
            Audio = new LordMessageAudio(LordMessageSourceKind.Original, context.OriginalAudioPath);
            Video = new LordMessageVideo(LordMessageSourceKind.Original, context.OriginalVideoPath);
            Text = new LordMessageText(LordMessageSourceKind.Original, context.TextSection, context.TextIndex, null);
        }
        internal override object Capture() { savedAudio = Audio; savedVideo = Video; savedText = Text; return this; }
        internal override void Restore(object state) { Audio = savedAudio; Video = savedVideo; Text = savedText; }
        internal override void ClearCapture() { savedAudio = null; savedVideo = null; savedText = null; }
    }
}

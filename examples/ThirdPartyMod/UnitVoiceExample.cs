using System.Threading;
using APIShared.Audio;

namespace ThirdPartyMod
{
    // This example is compiled, not automatically registered by the example plugin.
    internal static class UnitVoiceExample
    {
        internal enum Replacement { Original, Vanilla, ModResource, MapResource }
        private static UnitVoiceRegistration registration;
        private static UnitVoiceResourceHandle modVoice, mapVoice;
        private static UnitVoiceCue vanillaVoice;
        private static int replacement;

        // Call after APIShared initialization and after this owner's Extender resource index is ready.
        // Success means queued. Read IsReady/Diagnostic before expecting a replacement.
        internal static bool PrepareModVoice(string ownerGuid, out string reason) =>
            UnitVoiceResources.TryPrepareModResource(ownerGuid, "selection-voice", "Voices/selection.wav",
                value => modVoice = value, out modVoice, out reason);

        // Call from this mod's IMissionLifecycleCapability OnStart. The mission counts as started for every
        // Start observer, so the order of the observers does not matter.
        // End/initialization invalidates this handle. A later session gets a new handle with the same owner/ID.
        internal static bool PrepareCurrentMapVoice(string ownerGuid, out string reason) =>
            UnitVoiceResources.TryPrepareMapResource(ownerGuid, "map-selection-voice", "Voices/map-selection.ogg",
                value => mapVoice = value, out mapVoice, out reason, baseVolume: 0.8f);

        // Any thread, including before runtime readiness. The game invokes Pre/Post only on Unity.
        // ownerGuid must be the calling mod's own stable GUID; no dependency on another consumer is implied.
        internal static bool Register(string ownerGuid, int requestingPlayerId, out string reason)
        {
            if (!UnitVoiceCatalog.TryGetCue("unitvoice.arch_s1.wav", out vanillaVoice))
            { reason = "The requested donor cue is unavailable."; return false; }
            if (!UnitVoiceSelection.TryCreate(out var sources, out reason,
                situations: new[] { UnitVoiceSituation.Selection, UnitVoiceSituation.Move },
                requestingPlayerIds: new[] { requestingPlayerId })) return false;
            return UnitVoiceEvents.TryRegister(ownerGuid, "unit-voice-example", sources, BeforeVoice, null,
                out registration, out reason);
        }

        private static void BeforeVoice(UnitVoicePreEventArgs args)
        {
            // Filters above refer to emission. These immutable playback snapshots can support further policy:
            // args.Context.MissionContext?.Mode, UiLocale, SpeechAssetBranch, Source.OwnerPlayerId.
            args.MultiplyVolume(0.5f); // Per request; later game volume-slider changes still apply.
            switch ((Replacement)Volatile.Read(ref replacement))
            {
                case Replacement.Vanilla: args.TryReplaceVanillaCue(vanillaVoice); break;
                case Replacement.ModResource: args.TryReplaceResource(modVoice); break;
                case Replacement.MapResource: args.TryReplaceResource(mapVoice); break;
            }
            // A not-ready resource leaves the current choice unchanged. Never prepare/decode inside Pre/Post.
        }
        internal static void SetReplacement(Replacement value) => Volatile.Write(ref replacement, (int)value);
        internal static void SetEnabled(bool enabled) => registration?.SetEnabled(enabled);
    }
}

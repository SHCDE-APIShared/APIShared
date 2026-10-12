using APIShared.Audio;

namespace ThirdPartyMod
{
    // Consumer setting and arbitrary owner identity remain outside APIShared.
    internal static class SoundExample
    {
        private static SoundRegistration registration;
        private static SoundClipHandle clip;
        internal static bool Preload(string ownerGuid, out string reason) =>
            SoundClips.TryLoadModClip(ownerGuid, "Chime", "Sounds/chime.wav", value => clip = value, out clip, out reason);
        internal static bool Register(string ownerGuid, out string reason) =>
            SoundEvents.TryRegister(ownerGuid, "IdleSwordDraw", args => {
                if (args.Context.Situation == SoundSituation.ArabianIdleSwordDraw)
                    { args.MultiplyVolume(.25f); /* Optional: args.TryReplaceClip(clip); */ }
            }, null, out registration, out reason);
        internal static void SetEnabled(bool enabled) => registration?.SetEnabled(enabled);
    }
}

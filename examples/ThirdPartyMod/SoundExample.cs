using System.Collections.Generic;
using APIShared.Audio;

namespace ThirdPartyMod
{
    // Consumer setting and arbitrary owner identity remain outside APIShared.
    internal static class SoundExample
    {
        private static SoundRegistration registration, killCues;
        private static SoundClipHandle clip;
        // Consumer-owned unit identity (for example, units this mod converted); keyed by GameUnit global ID.
        private static readonly HashSet<uint> tagged = new HashSet<uint>();
        internal static bool Preload(string ownerGuid, out string reason) =>
            SoundClips.TryLoadModClip(ownerGuid, "Chime", "Sounds/chime.wav", value => clip = value, out clip, out reason);
        // Selecting only the needed situation keeps every other soldier sound out of managed code.
        internal static bool Register(string ownerGuid, out string reason) =>
            SoundSourceSelection.TryCreate(null, new[] { SoundSituation.ArabianIdleSwordDraw }, out var draw, out reason) &&
            SoundEvents.TryRegister(ownerGuid, "IdleSwordDraw", args => {
                if (args.Context.Situation == SoundSituation.ArabianIdleSwordDraw)
                    { args.MultiplyVolume(.25f); /* Optional: args.TryReplaceClip(clip); */ }
            }, null, draw, out registration, out reason);
        // Native stage: runs on the game-tick thread before the record exists; no Unity calls here.
        internal static bool RegisterKillCues(string ownerGuid, out string reason) =>
            SoundSourceSelection.TryCreate(null, new[] { SoundSituation.MeleeKill }, out var kills, out reason) &&
            SoundSourceEvents.TryRegister(ownerGuid, "TaggedPikemanKillCue", OnSource, kills, out killCues, out reason);
        private static void OnSource(ref SoundSourceArgs args)
        {
            // Pikeman (type 25) kill cues 240/241 become the Spearman bank 118/153 for tagged attackers only.
            if (args.Situation != SoundSituation.MeleeKill || args.UnitType != 25) return;
            lock (tagged) if (!tagged.Contains(args.UnitGlobalId)) return;
            if (args.SoundId == 240) args.TryReplaceSound(118);
            else if (args.SoundId == 241) args.TryReplaceSound(153);
        }
        internal static void Tag(uint globalId) { lock (tagged) tagged.Add(globalId); }
        internal static void SetEnabled(bool enabled) { registration?.SetEnabled(enabled); killCues?.SetEnabled(enabled); }
    }
}

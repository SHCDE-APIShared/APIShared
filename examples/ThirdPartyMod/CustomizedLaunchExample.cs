using APIShared.GameModes;

namespace ThirdPartyMod.Examples
{
    // Optional example for a mod that actually owns customized trail launches.
    // Ordinary mods only need mode snapshots or their own activation profile.
    internal static class CustomizedLaunchExample
    {
        private static readonly object Sync = new object();
        private static CustomizedLaunchOrigin current;

        internal static void Register(string ownerGuid) =>
            CustomizedLaunchOrigins.Register(ownerGuid, Capture);

        // Call before loading this mod's customized cooperative mission.
        internal static void BeginCoopLaunch(int zeroBasedTrailId, int missionId)
        {
            lock (Sync)
                current = new CustomizedLaunchOrigin(CustomizedLaunchOriginKind.CoopTrail,
                    -1, zeroBasedTrailId, missionId, restoredFromSave: false,
                    launchPending: true, supportsBuiltInOrigins: false);
        }

        // Clear when this mod no longer supplies customized launch evidence.
        internal static void Clear()
        {
            lock (Sync) current = default;
        }

        private static CustomizedLaunchOrigin Capture()
        {
            lock (Sync) return current;
        }
    }
}

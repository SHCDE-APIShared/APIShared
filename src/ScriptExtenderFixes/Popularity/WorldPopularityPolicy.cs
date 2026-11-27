namespace APIShared.ScriptExtenderFixes
{
    internal static class WorldPopularityPolicy
    {
        // Native projectile updater 0x9AC20 selects GM_BODY_INFO mask frames from
        // the popup's immutable displayed snapshot, not the current viewpoint.
        internal static int Normalize(bool popularityPopup, int originalMask, int displayedPopularity) =>
            !popularityPopup ? originalMask : displayedPopularity < 40 ? 130 : displayedPopularity < 60 ? 129 : 128;
    }
}

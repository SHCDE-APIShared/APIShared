namespace APIShared.Compatibility
{
    // Startup evidence rule of the Fixes wheat correction. The machine contract is derived from the actual
    // code by FixesWheatAnalysis; no binary hash, version or fixed RVA is part of it.
    internal static class FixesWheatContract
    {
        internal static bool IsStartup(bool initialized, bool loaded, bool ownInstanceAbsent,
            int awakeFrame, int chainFrame, int entryFrame, bool directorAbsent, bool mapAbsent)
            => initialized && !loaded && ownInstanceAbsent && awakeFrame >= 0 &&
                chainFrame > awakeFrame && entryFrame > chainFrame && directorAbsent && mapAbsent;
    }
}

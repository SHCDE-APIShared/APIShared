using System;
using APIShared.NativeSites;

namespace ThirdPartyMod
{
    // Declare a native hook site before installing it, so co-installed mods get a named refusal
    // instead of a silent double detour. Call from a LibraryLoaded handler of a plugin that declares
    // a (soft) BepInDependency on APIShared_Serp. Registration is never done on a hooked path.
    internal static class NativeSiteExample
    {
        private const string SiteName = "ExampleEntryDetour";
        // APIShared's native market buy query (docs/NATIVE_COMPATIBILITY.md); used here only for a query.
        private const int MarketBuyRva = 0xCEB10;

        // Reads only. The market entry is registered once a consumer of MarketPriceEvents installed it.
        internal static void Inspect(Action<string> log)
        {
            if (NativeSiteRegistry.TryGetWriter(MarketBuyRva, out NativeSiteRegistration writer))
                log($"0x{MarketBuyRva:X} is written by {writer}; use APIShared.Economy.MarketPriceEvents instead of a detour.");
            var overlaps = new NativeSiteRegistration[4];
            int count = NativeSiteRegistry.Query(MarketBuyRva, 31, overlaps);
            log($"Native sites over the market buy helper: {count}.");
        }

        // The pattern for a mod's own audited entry detour. entryRva and patchLength come from that
        // mod's own audited native contract; this example does not name a real game function.
        internal static bool TryClaimEntryDetour(string ownerGuid, int entryRva, int patchLength,
            string expectedOriginalSha256, Action install, Action<string> log)
        {
            NativeSiteRegistrationResult claim = NativeSiteRegistry.Register(ownerGuid, SiteName,
                entryRva, patchLength, NativeSiteMode.Exclusive, expectedOriginalSha256);
            if (!claim.Succeeded)
            {
                // Conflict names the existing owner, mode and span; keep Vanilla and report once.
                log("Example detour disabled: " + claim.Reason);
                return false;
            }
            foreach (NativeSiteRegistration shared in claim.SharedWith)
                log("Example detour shares its entry with " + shared);
            try
            {
                install();
                return true;
            }
            catch (Exception error)
            {
                // Only an installation that was never published may give its claim back.
                NativeSiteRegistry.Unregister(ownerGuid, SiteName);
                log("Example detour installation failed: " + error.Message);
                return false;
            }
        }
    }
}

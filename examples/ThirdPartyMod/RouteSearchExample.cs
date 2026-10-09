using System;
using APIShared;
using APIShared.Pathfinding;

namespace ThirdPartyMod
{
    // Optional, explicitly enabled policy. All multiplayer peers need the same policy.
    internal static class RouteSearchExample
    {
        private static string publisher;
        private static Action<string> log;

        internal static void Register(ModApiClient api, string publisherGuid, Action<string> logger)
        {
            publisher = publisherGuid;
            log = logger;
            if (!RouteSearchEvents.TryRegister(api.OwnerGuid, "route-preference", BeforeSearch, AfterSearch, out string reason))
                logger(reason);
        }

        private static void BeforeSearch(RouteSearchPreEventArgs args)
        {
            if (args.Context.SourceGuid != publisher || args.Context.Terrain != RouteSearchTerrain.FriendlyMoat)
                return;
            // Prefer a longer ground alternative over a moat route. This changes
            // selection cost, not native speed or which transitions are permitted.
            args.MoatEdgeCost = args.MoatEdgeCost <= args.Context.MaximumEdgeCost / 2
                ? args.MoatEdgeCost * 2 : args.Context.MaximumEdgeCost;
            // Set args.SkipOriginalFunction = true to skip this extra calculation.
            // Cancel the actual movement command through Script Extender instead.
        }

        private static void AfterSearch(RouteSearchPostEventArgs args)
        {
            if (args.Context.SourceGuid == publisher)
                log($"Additional route search: success={args.Success}, edges={args.RouteLength}, reason={args.Reason}");
        }
    }
}

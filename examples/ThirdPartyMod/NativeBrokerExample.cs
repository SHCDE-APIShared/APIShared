using System;
using System.Threading;
using APIShared.NativeBrokers;

namespace ThirdPartyMod
{
    // Shared native entries: register with the broker instead of detouring 0xE2610 or 0xD90D0.
    // Call from a LibraryLoaded handler of a plugin that declares a (soft) BepInDependency on
    // APIShared_Serp, and a soft one on any older consumer that still detours the entry itself
    // (BugfixesAndQoL_Serp), or later. Keep the handles: disposing one unregisters it.
    // Callbacks run on the native calling thread for every call: no logging, Unity calls or locks there.
    internal static class NativeBrokerExample
    {
        [ThreadStatic] private static int scopeDepth;
        private static long unreachable, refreshedTiles;
        private static NativeBrokerRegistration reachability, tiles;

        // Overrides the query only inside this mod's own scope (entered around a native call it makes),
        // and counts completed tile refreshes. False: keep the mod's previous path.
        internal static bool TryRegister(string ownerGuid, Action<string> log)
        {
            if (!PclReachabilityBroker.TryRegister(ownerGuid, "example.reachability", 0,
                    (ref PclReachabilityCall call) =>
                    {
                        // Pre decides; it does not observe, because an earlier override skips later Pres.
                        if (scopeDepth > 0) call.TryOverride(1);
                    },
                    (ref PclReachabilityCall call) =>
                    {
                        // Post sees every call. The original ran unless a Pre overrode the call.
                        if (call.OriginalCalled && call.OriginalResult == 0) Interlocked.Increment(ref unreachable);
                    },
                    out reachability, out string reason))
            {
                // State tells why: Pending (too early), ForeignOwner (another detour owns the entry),
                // Unavailable (unsupported build). The broker never patches over a foreign detour.
                log("Reachability broker unavailable (" + PclReachabilityBroker.State + "): " + reason);
                return false;
            }
            if (!TileRefreshBroker.TryRegister(ownerGuid, "example.tiles", 0,
                    call => Interlocked.Increment(ref refreshedTiles), out tiles, out reason))
                log("Tile refresh broker unavailable (" + TileRefreshBroker.State + "): " + reason);
            return true;
        }

        // Vanilla without any broker consumer, for a mod's own probes.
        internal static int QueryVanilla(IntPtr manager, int player, int firstPcl, int secondPcl, int mode) =>
            PclReachabilityBroker.CallOriginal(manager, player, firstPcl, secondPcl, mode);

        internal static void Unregister()
        {
            reachability?.Dispose();
            tiles?.Dispose();
        }
    }
}

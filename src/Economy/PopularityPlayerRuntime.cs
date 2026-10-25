using System;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal static unsafe class PopularityPlayerRuntime
    {
        // The native CB090 eligibility gate requires these same two positive fields.
        // The validated callback passes the pointer resolved from this exact player ID.
        internal static int Classify(int playerId,IntPtr resources)
        {
            if(playerId<1||playerId>GamePlayerManagerAPI.MAX_PLAYERS||resources==IntPtr.Zero)return 0;
            var r=(GamePlayerResources*)resources;
            if(unchecked((int)r->r_KeepId)<=0||unchecked((int)r->r_KeepDoorId)<=0)return 0;
            return GamePlayerManagerAPI.Instance.IsAIPlayer(playerId)?2:1;
        }
        internal static PopularitySelection CaptureReport(int playerId)
        {
            if(!PopularitySelection.NeedsPlayer)return PopularitySelection.Legacy;
            if(playerId<1||playerId>GamePlayerManagerAPI.MAX_PLAYERS||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(playerId,out GamePlayerResources* resources))return PopularitySelection.Legacy;
            return PopularitySelection.Capture(playerId,(IntPtr)resources);
        }
    }
}

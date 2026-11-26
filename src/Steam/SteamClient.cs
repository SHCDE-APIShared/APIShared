using System;
using APIShared.Internal;

namespace APIShared.Steam
{
    /// <summary>Reads local app access independently of APIShared native capability readiness.</summary>
    public sealed class SteamClient
    {
        internal SteamClient() { }

        /// <summary>Queries Steam for a related game's nonzero Steam App ID and the current local user.
        /// Call on the Unity main thread. Calls too early or from another thread return
        /// <see cref="SteamAppAccess.SteamUnavailable"/>. Retry on the main thread after Steam initializes.
        /// The result describes access as reported by Steam, including temporary access Steam may grant.
        /// It does not identify an installation or a permanent purchase. Results are not cached.</summary>
        /// <exception cref="ArgumentOutOfRangeException">The app ID is zero.</exception>
        public SteamAppAccess GetAppAccess(uint appId)
        {
            if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId));
            return UnityMainThreadDispatch.IsMainThread
                ? SteamAppAccessQuery.Get(appId) : SteamAppAccess.SteamUnavailable;
        }
    }
}

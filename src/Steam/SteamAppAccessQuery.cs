using System;
using System.Runtime.CompilerServices;
using Steamworks;

namespace APIShared.Steam
{
    // Shared with the early bootstrap, which runs before the APIShared plugin exists.
    internal static class SteamAppAccessQuery
    {
        internal static SteamAppAccess Get(uint appId)
        {
            try { return QueryClient(appId); }
            catch (InvalidOperationException) { return SteamAppAccess.SteamUnavailable; }
            catch (SystemException ex) when (
                ex is TypeLoadException || ex is TypeInitializationException ||
                ex is EntryPointNotFoundException || ex is DllNotFoundException ||
                ex is BadImageFormatException || ex is System.IO.FileNotFoundException ||
                ex is System.IO.FileLoadException)
            { return SteamAppAccess.SteamUnavailable; }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static SteamAppAccess QueryClient(uint appId)
        {
            if (!SteamUser.GetSteamID().IsValid()) return SteamAppAccess.SteamUnavailable;
            return SteamApps.BIsSubscribedApp(new AppId_t(appId))
                ? SteamAppAccess.Available : SteamAppAccess.NotAvailable;
        }
    }
}

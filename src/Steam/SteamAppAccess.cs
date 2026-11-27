namespace APIShared.Steam
{
    /// <summary>Local Steam entitlement for the current user at the time of the query.</summary>
#if API_SHARED_BOOTSTRAP
    internal
#else
    public
#endif
    enum SteamAppAccess
    {
        /// <summary>No answer was available from the Steam client.</summary>
        SteamUnavailable = 0,
        /// <summary>Steam reported that the active user has access.</summary>
        Available = 1,
        /// <summary>Steam answered and reported no access.</summary>
        NotAvailable = 2
    }
}

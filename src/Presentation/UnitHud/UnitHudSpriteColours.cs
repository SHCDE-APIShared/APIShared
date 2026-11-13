using APIShared.Internal;

namespace APIShared
{
    /// <summary>
    /// Exclusive lease that draws Vanilla's unit HUD sprites (build-menu troop tab, recruitment buttons, troop
    /// portraits and control groups) in another player's colour, for example a spectated or controlled player.
    /// </summary>
    /// <remarks>
    /// <para>The lease sits on APIShared's single <c>MainViewModel.UpdateUITroopSprites</c> hook; consumers install no
    /// hook of their own. While leased, every Vanilla sprite update and every APIShared replay calls the original with
    /// the leased player's colour from <see cref="PlayerColours.TryGetUiColour"/>. The Arabic variants follow the leased
    /// player's lord type where Vanilla exposes it (human multiplayer members); otherwise Vanilla's own value is kept.
    /// If the leased player's colour cannot be resolved, that update keeps Vanilla's values. Vanilla's requested values
    /// stay recorded as the restore values. Unit HUD image overrides receive the drawn (effective) colour.</para>
    /// <para>One owner at a time; other owners are rejected with a reason until the holder releases. Leasing applies
    /// the colours once, a different player for the same owner applies once more, and an unchanged lease does nothing.
    /// <see cref="Release"/> and the end of the mission replay Vanilla's last request once; mission end also ends the
    /// lease, so a new mission needs a new lease. A lease active at mission start is applied once more because
    /// single-player loading updates the sprites before Vanilla rebuilds its colour mapping. There is no per-frame
    /// work. A replay needs a known Vanilla request and a ready HUD; otherwise Vanilla's next sprite update applies the
    /// current state.</para>
    /// <para>Limits: only images Vanilla's sprite update recolours change. Keeps, outposts, flags and the other
    /// building icons have no colour variants, and some resource keys are identical in all colours. The lease does
    /// not change map sprites, labels, ownership, selection or control rights.</para>
    /// <para>Call on the Unity main thread; other threads are rejected. Requires the Unit HUD presentation hooks,
    /// installed by APIShared's native initialization; before or without them <see cref="TryLease"/> fails with
    /// the capability's reason.</para>
    /// </remarks>
    public static class UnitHudSpriteColours
    {
        /// <summary>
        /// Leases the unit HUD sprite colours for <paramref name="ownerGuid"/> and draws them in the colour of player
        /// <paramref name="playerId"/> (one-based native slot 1..8). The current holder may call again to change the player.
        /// </summary>
        /// <returns><see langword="true"/> when the caller holds the lease for that player; otherwise
        /// <see langword="false"/> with <paramref name="reason"/> (empty owner, invalid player, wrong thread,
        /// Unit HUD hooks unavailable, or another owner's lease).</returns>
        public static bool TryLease(string ownerGuid, int playerId, out string reason)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid))
            {
                reason = "A nonempty owner GUID is required.";
                return false;
            }
            if (!PlayerSlots.IsValidPlayerId(playerId))
            {
                reason = $"Player ID {playerId} is outside 1..8.";
                return false;
            }
            if (!UnityMainThreadDispatch.IsMainThread)
            {
                reason = "Unit HUD sprite colours can only be leased on the Unity main thread.";
                return false;
            }
            if (!ApiSharedRuntime.ProcessInstance.TryGetUnitHudService(out UnitHudPresentationService service, out reason))
                return false;
            return service.TryLeaseSpriteColours(ownerGuid, playerId, out reason);
        }

        /// <summary>
        /// Ends the lease of <paramref name="ownerGuid"/> and replays Vanilla's last requested sprite colours once.
        /// </summary>
        /// <returns><see langword="true"/> when that owner held the lease; <see langword="false"/> for other owners,
        /// a lease already ended by the mission end, other threads or unavailable Unit HUD hooks.</returns>
        public static bool Release(string ownerGuid)
        {
            if (string.IsNullOrWhiteSpace(ownerGuid) || !UnityMainThreadDispatch.IsMainThread ||
                !ApiSharedRuntime.ProcessInstance.TryGetUnitHudService(out UnitHudPresentationService service, out _))
                return false;
            return service.ReleaseSpriteColours(ownerGuid);
        }
    }
}

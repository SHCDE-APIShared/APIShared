using CrusaderDE;
using System;

namespace APIShared
{
    // Sprite-colour lease of UnitHudSpriteColours on the existing UpdateUITroopSprites hook. Main-thread state:
    // the broker rejects other threads and Vanilla, mission events and render replays run on the Unity thread.
    // lastSpriteColour/lastSpriteArabic keep Vanilla's own request as the restore values; every call of the
    // original and every image-override pass use the effective values from ResolveSpriteColours.
    internal sealed unsafe partial class UnitHudPresentationService
    {
        internal delegate bool SpriteColourResolver(int playerId, bool vanillaArabic, out int colour, out bool arabic);

        // Test seams; production reads Vanilla's player mapping and the ready HUD view model.
        internal SpriteColourResolver spriteColourResolver = ResolveLeasedSprite;
        internal Func<MainViewModel> spriteReplayTarget = GetSpriteReplayTarget;
        private string spriteLeaseOwner;
        private int spriteLeasePlayer;

        internal bool TryLeaseSpriteColours(string ownerGuid, int playerId, out string reason)
        {
            string current = spriteLeaseOwner;
            if (current != null && !string.Equals(current, ownerGuid, StringComparison.Ordinal))
            {
                reason = $"Unit HUD sprite colours are leased by '{current}' (player {spriteLeasePlayer}).";
                return false;
            }
            reason = null;
            if (current != null && spriteLeasePlayer == playerId) return true;
            spriteLeaseOwner = ownerGuid;
            spriteLeasePlayer = playerId;
            ReplaySpriteColours();
            return true;
        }

        internal bool ReleaseSpriteColours(string ownerGuid)
        {
            if (spriteLeaseOwner == null || !string.Equals(spriteLeaseOwner, ownerGuid, StringComparison.Ordinal)) return false;
            EndSpriteColourLease();
            return true;
        }

        // Mission end: player IDs belong to that mission, so the lease ends and Vanilla's request is replayed once.
        private void EndSpriteColourLease()
        {
            if (spriteLeaseOwner == null) return;
            spriteLeaseOwner = null;
            spriteLeasePlayer = 0;
            ReplaySpriteColours();
        }

        // Single-player loading calls the sprite update before Vanilla rebuilds the player colour mapping;
        // a lease that is active at mission start therefore resolves once more against the final mapping.
        private void OnSpriteMissionStarted()
        {
            if (spriteLeaseOwner != null) ReplaySpriteColours();
        }

        private void ResolveSpriteColours(int vanillaColour, bool vanillaArabic, out int colour, out bool arabic)
        {
            colour = vanillaColour;
            arabic = vanillaArabic;
            if (spriteLeaseOwner == null) return;
            try
            {
                // An unresolved player keeps Vanilla's values instead of drawing an invalid colour.
                if (spriteColourResolver(spriteLeasePlayer, vanillaArabic, out int leasedColour, out bool leasedArabic))
                {
                    colour = leasedColour;
                    arabic = leasedArabic;
                }
            }
            catch (Exception ex) { LogCallbackFailure("sprite colour lease", ex); }
        }

        private static bool ResolveLeasedSprite(int playerId, bool vanillaArabic, out int colour, out bool arabic)
        {
            arabic = PlayerColours.TryGetHumanLordArabic(playerId, out bool lordArabic) ? lordArabic : vanillaArabic;
            return PlayerColours.TryGetUiColour(playerId, out colour);
        }

        // Redraws the unit HUD sprites for Vanilla's last request with the effective (possibly leased) values,
        // bypassing the public hook entry so refreshes cannot re-enter it. The original rewrites every slot,
        // so recorded override images are cleared when the caller re-applies overrides from scratch.
        private void DrawSprites(MainViewModel main, bool clearRecorded, bool applyOverrides)
        {
            ResolveSpriteColours(lastSpriteColour, lastSpriteArabic, out int colour, out bool arabic);
            updateSpritesActive = true;
            try
            {
                updateSpritesOriginal(main, colour, arabic);
                if (clearRecorded) { originalImages.Clear(); appliedImages.Clear(); }
                if (applyOverrides) ApplyImageOverrides(main, colour, arabic);
            }
            finally { updateSpritesActive = false; }
        }

        // One original call with the effective values; without a known Vanilla request or ready HUD the next
        // Vanilla sprite update applies the current lease instead.
        private void ReplaySpriteColours()
        {
            if (!hasSpriteContext || updateSpritesActive) return;
            MainViewModel main;
            try { main = spriteReplayTarget(); }
            catch (Exception ex) { LogCallbackFailure("sprite colour replay", ex); return; }
            if (main == null) return;
            try { DrawSprites(main, clearRecorded: true, applyOverrides: activeImages && IsImageOverrideContextReady()); }
            catch (Exception ex) { LogCallbackFailure("sprite colour replay", ex); }
        }

        private static MainViewModel GetSpriteReplayTarget()
        {
            // Instance is a lazy constructor; HUD_Main is assigned after viewModelLoaded. Recolouring needs no
            // positive local player (an original spectator places for a chosen player); only the image
            // overrides keep their own local-player context check.
            if (!MainViewModel.viewModelLoaded) return null;
            MainViewModel main = MainViewModel.Instance;
            return main?.HUDmain != null ? main : null;
        }

    }
}

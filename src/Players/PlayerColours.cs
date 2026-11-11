using APIShared.Internal;
using SHCDESE.API;
using System;
using System.Runtime.CompilerServices;

namespace APIShared
{
    /// <summary>
    /// Read-only lookups of the colour Vanilla currently assigns to a player, in Vanilla's own colour domains.
    /// </summary>
    /// <remarks>
    /// Vanilla has two different 1..8 orders. Unit materials and player labels use the palette order
    /// red, blue, orange, yellow, purple, black/gray, light blue, green
    /// (<c>SpriteMapping.remapColours[SpriteMapping.RemapMPLoadedColour(player)]</c>). Portrait and HUD
    /// image resources use the UI order red, orange, yellow, blue, black/gray, purple, light blue, green,
    /// converted from the palette with <c>Platform_Multiplayer.Instance.UIColourRemap</c>.
    /// </remarks>
    public static class PlayerColours
    {
        private const int FirstColour = 1;
        private const int LastColour = 8;

        /// <summary>
        /// Gets the UI colour Vanilla's unit HUD sprite update (<c>MainViewModel.UpdateUITroopSprites</c>) uses for
        /// <paramref name="playerId"/>: UI order 1..8 as described on <see cref="PlayerColours"/>, never the material palette.
        /// </summary>
        /// <remarks>
        /// Player IDs are the one-based native player slots 1..8. The value is read from Vanilla's current mapping on
        /// every call (no caching, no allocation) and follows map, save and multiplayer remapping once Vanilla has
        /// published it. Between missions the mapping may still describe the previous mission; during map loading
        /// Vanilla can call its sprite update before it rebuilds the single-player mapping.
        /// Call on the Unity main thread. Returns <see langword="false"/> with <paramref name="uiColour"/> 0 for other
        /// threads, IDs outside 1..8, an unassigned slot, missing Vanilla mapping objects or changed game members.
        /// </remarks>
        public static bool TryGetUiColour(int playerId, out int uiColour)
        {
            uiColour = 0;
            if (!PlayerSlots.IsValidPlayerId(playerId) || !UnityMainThreadDispatch.IsMainThread) return false;
            try { return TryReadUiColour(playerId, out uiColour); }
            catch (Exception)
            {
                // A game update can rename the mapping members; the lookup then reports no context.
                uiColour = 0;
                return false;
            }
        }

        // Separate method: missing game members fail when this body is compiled, inside the caller's catch.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadUiColour(int playerId, out int uiColour)
        {
            Platform_Multiplayer platform = Platform_Multiplayer.Instance;
            return TryMapUiColour(SpriteMapping.RemapMPLoadedColour(playerId), SpriteMapping.remapColours,
                platform == null ? null : platform.UIColourRemap, out uiColour);
        }

        // Pure mapping step of TryGetUiColour: loaded player slot -> material palette -> UI order.
        internal static bool TryMapUiColour(int loadedPlayerSlot, int[] remapColours, int[] uiColourRemap, out int uiColour)
        {
            uiColour = 0;
            if (remapColours == null || uiColourRemap == null || loadedPlayerSlot < 0 || loadedPlayerSlot >= remapColours.Length)
                return false;
            int palette = remapColours[loadedPlayerSlot];
            if (palette < FirstColour || palette > LastColour || palette >= uiColourRemap.Length) return false;
            int ui = uiColourRemap[palette];
            if (ui < FirstColour || ui > LastColour) return false;
            uiColour = ui;
            return true;
        }


        // Arabic HUD variants for a non-local player, when Vanilla exposes that player's lord type: only human
        // multiplayer members carry the native lord type Vanilla registered (RegisterMPPlayer). AI members, single
        // player and missing objects return false; callers then keep Vanilla's own value. Main thread.
        internal static bool TryGetHumanLordArabic(int playerId, out bool arabic)
        {
            arabic = false;
            if (!PlayerSlots.IsValidPlayerId(playerId)) return false;
            try { return TryReadHumanLordArabic(playerId, out arabic); }
            catch (Exception)
            {
                arabic = false;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool TryReadHumanLordArabic(int playerId, out bool arabic)
        {
            arabic = false;
            // Game members survive the last multiplayer session; single-player missions must not read them.
            if (!GameNetworkAPI.IsMultiplayerGame()) return false;
            Platform_Multiplayer.MPGameMember member = Platform_Multiplayer.Instance?.getPlayer(playerId);
            if (member == null || member.skirmishAI || member.lobbyData == null || member.lobbyData.SkirmishMember)
                return false;
            arabic = IsArabicLordType(member.lordType);
            return true;
        }

        // Native lord types 1, 2, 6 and 7 use Vanilla's Arabic build and troop variants (same rule as lastGameState.lord_Type).
        internal static bool IsArabicLordType(int lordType) => lordType == 1 || lordType == 2 || lordType == 6 || lordType == 7;
    }
}

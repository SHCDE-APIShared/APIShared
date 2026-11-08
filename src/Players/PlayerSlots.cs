using SHCDESE.API;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    // One-based player IDs of the game's player slots and the final human roster built from them.
    internal static class PlayerSlots
    {
        internal const int FirstPlayerId = 1;
        internal const int LastPlayerId = GamePlayerManagerAPI.MAX_PLAYERS;
        internal const int Count = LastPlayerId - FirstPlayerId + 1;

        internal static bool IsValidPlayerId(int playerId) =>
            playerId >= FirstPlayerId && playerId <= LastPlayerId;

        // A final roster gives each slot one Steam identity and each Steam identity one slot.
        internal static bool TryAddPlayer(
            IDictionary<int, ulong> playersById,
            int playerId,
            ulong steamId,
            out string error)
        {
            if (!IsValidPlayerId(playerId) || steamId == 0)
            {
                error = $"A human player has an invalid final identity: playerId={playerId}, steamId={steamId}.";
                return false;
            }
            if (playersById.TryGetValue(playerId, out ulong existingSteamId) && existingSteamId != steamId)
            {
                error = $"Final player slot {playerId} is assigned to multiple Steam identities.";
                return false;
            }
            if (playersById.Any(player => player.Key != playerId && player.Value == steamId))
            {
                error = $"Steam identity {steamId} is assigned to multiple final player slots.";
                return false;
            }
            playersById[playerId] = steamId;
            error = string.Empty;
            return true;
        }
    }
}

using APIShared.Internal;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared
{
    /// <summary>The supported Vanilla appearances of a Lord unit.</summary>
    public enum LordSkin
    {
        /// <summary>No recognized Lord material was available.</summary>
        Unknown = 0,
        /// <summary>The default Crusader Lord.</summary>
        Crusader = 1,
        /// <summary>The Arabic Lord.</summary>
        Arabic = 2,
        /// <summary>The Bedouin Lord.</summary>
        Bedouin = 3,
        /// <summary>The Scribe.</summary>
        Scribe = 4,
        /// <summary>The female Crusader Lord.</summary>
        CrusaderFemale = 5,
        /// <summary>Bessy, the crowned cow.</summary>
        Bessy = 6,
        /// <summary>The female Arabic Lord.</summary>
        ArabicFemale = 7,
        /// <summary>The female Bedouin Lord.</summary>
        BedouinFemale = 8
    }

    /// <summary>Read-only identification of Lord appearances from the current unit material.</summary>
    public static class LordSkins
    {
        /// <summary>
        /// Reads the living registered Lord of a one-based player slot (1–8) in the current
        /// mission or editor. Call on the Unity main thread. Returns false and Unknown if the
        /// Lord is unavailable, the unit identity is stale, or the material is unsupported.
        /// The result is a snapshot and makes no prediction before the Lord spawns.
        /// </summary>
        public static unsafe bool TryGetForPlayer(int playerId, out LordSkin skin)
        {
            skin = LordSkin.Unknown;
            if (playerId < 1 || playerId > 8 || !UnityMainThreadDispatch.IsMainThread)
                return false;
            var players = GamePlayerManagerAPI.Instance;
            if (players == null || !players.TryGetPlayerResourcesById(playerId, out GamePlayerResources* player) ||
                player == null)
                return false;
            uint unitId = player->r_LordUnitId;
            uint globalId = player->r_LordUnitGlobalId;
            return unitId > 0 && unitId <= int.MaxValue && globalId != 0 &&
                UnitAccess.TryGetById((int)unitId, out GameUnit* lord, out _) &&
                TryResolveUnitSkin(playerId, globalId, lord, out skin);
        }

        /// <summary>Classifies a captured Lord material without accessing the game. Safe on any thread.</summary>
        public static bool TryGetFromMaterial(GM material, out LordSkin skin)
        {
            switch (material)
            {
                case GM.GM_BODY_LORD: skin = LordSkin.Crusader; break;
                case GM.GM_BODY_ARABIC_LORD: skin = LordSkin.Arabic; break;
                case GM.GM_BODY_BEDOUIN_LORD: skin = LordSkin.Bedouin; break;
                case GM.GM_BODY_SCRIBE_LORD: skin = LordSkin.Scribe; break;
                case GM.GM_BODY_LORD_FEMALE: skin = LordSkin.CrusaderFemale; break;
                case GM.GM_BODY_LORD_BESSY: skin = LordSkin.Bessy; break;
                case GM.GM_BODY_ARABIC_LORD_FEMALE: skin = LordSkin.ArabicFemale; break;
                case GM.GM_BODY_BEDOUIN_LORD_FEMALE: skin = LordSkin.BedouinFemale; break;
                default: skin = LordSkin.Unknown; return false;
            }
            return true;
        }

        internal static unsafe bool TryResolveUnitSkin(int playerId, uint globalId, GameUnit* lord, out LordSkin skin)
        {
            skin = LordSkin.Unknown;
            return globalId != 0 && lord != null && lord->r_GlobalId == globalId &&
                lord->r_ControllableForPlayerId == playerId && lord->r_UnitChimp == eChimps.CHIMP_TYPE_LORD &&
                UnitAccess.IsReallyAlive(lord) && lord->r_CurrentHealth > 0 &&
                TryGetFromMaterial(lord->r_GameMaterialIndex, out skin);
        }
    }
}

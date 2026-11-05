using System;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.Units.AiMilitary
{
    // FBCB9319: role writers 291F0/292F0/29360/29430/294B0, tables 2CEE00/2CEF80/2CEFC8;
    // terminal handlers via 321CB0. Semantic API roles deliberately do not equal storage slots.
    internal static unsafe class AiMilitaryCatalog
    {
        internal static readonly AiMilitaryRole[] Roles = (AiMilitaryRole[])Enum.GetValues(typeof(AiMilitaryRole));

        internal static ushort NativeRole(AiMilitaryRole role)
        {
            switch (role)
            {
                case AiMilitaryRole.None: return ushort.MaxValue;
                case AiMilitaryRole.CastleDefense: return 1;
                case AiMilitaryRole.Harassment: return 2;
                case AiMilitaryRole.DefensePatrol: return 4;
                case AiMilitaryRole.HomeMoat: return 5;
                case AiMilitaryRole.EconomyProtection: return 6;
                case AiMilitaryRole.Intervention: return 7;
                case AiMilitaryRole.SiegeEngineers: return 10;
                case AiMilitaryRole.SiegeMoat: return 11;
                case AiMilitaryRole.SiegeAssassins: return 12;
                case AiMilitaryRole.SiegeDiversion: return 13;
                case AiMilitaryRole.SiegeLadders: return 14;
                case AiMilitaryRole.SiegeTunnelers: return 15;
                case AiMilitaryRole.SiegeStorm: return 16;
                case AiMilitaryRole.SiegeCover: return 17;
                case AiMilitaryRole.SiegeShock: return 18;
                case AiMilitaryRole.SiegeReserve: return 19;
                case AiMilitaryRole.SiegeWall: return 20;
                case AiMilitaryRole.HarassmentEngineers: return 22;
                default: throw new ArgumentOutOfRangeException(nameof(role));
            }
        }

        internal static AiMilitaryArea Area(AiMilitaryRole role)
        {
            switch (role)
            {
                case AiMilitaryRole.None: return AiMilitaryArea.None;
                case AiMilitaryRole.CastleDefense:
                case AiMilitaryRole.DefensePatrol:
                case AiMilitaryRole.HomeMoat:
                case AiMilitaryRole.EconomyProtection:
                case AiMilitaryRole.Intervention: return AiMilitaryArea.Defense;
                case AiMilitaryRole.Harassment:
                case AiMilitaryRole.HarassmentEngineers: return AiMilitaryArea.Harassment;
                default: return AiMilitaryArea.Siege;
            }
        }

        internal static bool IsCombat(eChimps type) =>
            type >= eChimps.CHIMP_TYPE_ARCHER && type <= eChimps.CHIMP_TYPE_KNIGHT ||
            type == eChimps.CHIMP_TYPE_MONK ||
            type >= eChimps.CHIMP_TYPE_ARAB_BOW && type <= eChimps.CHIMP_TYPE_ARAB_GRENADIER ||
            type >= eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER && type <= eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER;

        internal static bool IsMobileMachine(eChimps type) => CrewSize(type) != 0;
        internal static int CrewSize(eChimps type)
        {
            switch (type)
            {
                case eChimps.CHIMP_TYPE_CATAPULT:
                case eChimps.CHIMP_TYPE_ARAB_BALLISTA: return 2;
                case eChimps.CHIMP_TYPE_SIEGE_TOWER:
                case eChimps.CHIMP_TYPE_BATTERING_RAM: return 4;
                case eChimps.CHIMP_TYPE_PORTABLE_SHIELD: return 1;
                default: return 0;
            }
        }

        internal static bool IsMilitary(eChimps type) => IsCombat(type) || IsMobileMachine(type) ||
            type == eChimps.CHIMP_TYPE_ENGINEER || type == eChimps.CHIMP_TYPE_LADDERMAN ||
            type == eChimps.CHIMP_TYPE_TUNNELER || type == eChimps.CHIMP_TYPE_TREBUCHET ||
            type == eChimps.CHIMP_TYPE_MANGONEL || type == eChimps.CHIMP_TYPE_BALLISTA;

        internal static bool CanDig(eChimps type)
        {
            switch (type)
            {
                case eChimps.CHIMP_TYPE_ARCHER:
                case eChimps.CHIMP_TYPE_SPEARMAN:
                case eChimps.CHIMP_TYPE_PIKEMAN:
                case eChimps.CHIMP_TYPE_MACEMAN:
                case eChimps.CHIMP_TYPE_ENGINEER:
                case eChimps.CHIMP_TYPE_ARAB_SLAVE:
                case eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH:
                case eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER:
                case eChimps.CHIMP_TYPE_BEDOUIN_SAPPER:
                case eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER: return true;
                default: return false;
            }
        }

        internal static bool SupportsType(AiMilitaryRole role, eChimps type)
        {
            switch (role)
            {
                // Civilian placement keeps its original behavior; there is no military role to isolate.
                case AiMilitaryRole.None: return true;
                case AiMilitaryRole.CastleDefense:
                case AiMilitaryRole.DefensePatrol:
                case AiMilitaryRole.EconomyProtection:
                case AiMilitaryRole.Intervention: return IsCombat(type);
                case AiMilitaryRole.HomeMoat:
                case AiMilitaryRole.SiegeMoat: return CanDig(type);
                case AiMilitaryRole.Harassment: return IsCombat(type) || IsMobileMachine(type);
                case AiMilitaryRole.HarassmentEngineers:
                case AiMilitaryRole.SiegeEngineers: return type == eChimps.CHIMP_TYPE_ENGINEER;
                case AiMilitaryRole.SiegeAssassins: return type == eChimps.CHIMP_TYPE_ARAB_ASSASIN;
                case AiMilitaryRole.SiegeLadders: return type == eChimps.CHIMP_TYPE_LADDERMAN;
                case AiMilitaryRole.SiegeTunnelers: return type == eChimps.CHIMP_TYPE_TUNNELER;
                case AiMilitaryRole.SiegeCover: return IsCombat(type);
                case AiMilitaryRole.SiegeDiversion:
                case AiMilitaryRole.SiegeStorm:
                case AiMilitaryRole.SiegeShock:
                case AiMilitaryRole.SiegeReserve:
                case AiMilitaryRole.SiegeWall:
                    return IsCombat(type) && type != eChimps.CHIMP_TYPE_BEDOUIN_HEALER;
                default: return false;
            }
        }

        internal static bool TryReadAic(int player, out InternalAIC* aic)
        {
            aic = null;
            if (player < 1 || player > GamePlayerManagerAPI.MAX_PLAYERS ||
                !GamePlayerManagerAPI.Instance.IsAIPlayer(player) ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(player, out GamePlayerResources* resources)) return false;
            var entries = GameAIManagerAPI.Instance.GetAICArray();
            // Despite the legacy field name, 379D0D0 stores the one-based AIC identity.
            // 2D120/3C0A0 subtract one before the 0x5E4 stride; storage-table anchors confirm this base.
            uint identity = resources->r_AILordMinusOne;
            if (identity == 0) return false;
            uint index = identity - 1;
            if (index >= entries.Length) return false;
            aic = entries.GetValuePointer((int)index);
            return aic != null;
        }

        internal static AiMilitaryOption Option(int player, eChimps type, AiMilitaryRole role,
            bool editor, GameUnit* existing = null)
        {
            var area = Area(role);
            if (!SupportsType(role, type)) return new AiMilitaryOption(role, area, false, false,
                AiMilitaryReason.UnsupportedRoleType, "The type has no supported complete execution contract for this role.");
            if (role == AiMilitaryRole.None) return new AiMilitaryOption(role, area, true, false, AiMilitaryReason.None, string.Empty);
            if (IsMobileMachine(type) && !HasCrew(existing, CrewSize(type))) return new AiMilitaryOption(role, area, false, false,
                AiMilitaryReason.IncompleteCrew, "Vanilla placement creates no engineers; a complete existing crew is required.");
            if (editor) return new AiMilitaryOption(role, area, true, true, AiMilitaryReason.None,
                "Final AI/AIC validation occurs at map start; an immediate editor preview revalidates its current AI.");
            if (!TryReadAic(player, out InternalAIC* aic)) return new AiMilitaryOption(role, area, editor, editor,
                editor ? AiMilitaryReason.None : AiMilitaryReason.NoAiContext,
                editor ? "Final AI/AIC validation occurs at map start." : "No initialized AI/AIC for this owner.");
            AiMilitaryReason problem = ValidateAic(role, type, in *aic);
            return new AiMilitaryOption(role, area, problem == AiMilitaryReason.None, false, problem,
                problem == AiMilitaryReason.None ? string.Empty : "The configured AIC type or physical group window does not support this assignment.");
        }

        internal static AiMilitaryReason ValidateAic(AiMilitaryRole role, eChimps type, in InternalAIC aic)
        {
            int expected = -1;
            switch (role)
            {
                case AiMilitaryRole.DefensePatrol:
                    if (aic.defense_patrols < 1 || aic.defense_patrols > 10) return AiMilitaryReason.InvalidGroupCount;
                    break;
                case AiMilitaryRole.SiegeMoat: expected = aic.siege_moat_troop; break;
                case AiMilitaryRole.SiegeDiversion: expected = aic.siege_herring_troop; break;
                case AiMilitaryRole.SiegeStorm:
                    if (aic.siege_storm_tribes < 1 || aic.siege_storm_tribes > 2) return AiMilitaryReason.InvalidGroupCount;
                    expected = aic.siege_storm_troop; break;
                case AiMilitaryRole.SiegeCover:
                    if (aic.siege_cover_tribes > 3) return AiMilitaryReason.InvalidGroupCount;
                    expected = aic.siege_cover_troop; break;
                case AiMilitaryRole.SiegeShock: expected = aic.siege_shock_troop; break;
                case AiMilitaryRole.SiegeReserve:
                    if (aic.siege_reserve_tribes > 2) return AiMilitaryReason.InvalidGroupCount;
                    expected = aic.siege_reserve_troop; break;
                case AiMilitaryRole.SiegeWall:
                    if (aic.siege_wall_tribes > 8) return AiMilitaryReason.InvalidGroupCount;
                    InternalAIC copy = aic;
                    int* list = &copy.siege_wall_troops1;
                    for (int i = 0; i < 24; i++)
                    {
                        if (list[i] == 0) break;
                        if (list[i] == (int)type) return AiMilitaryReason.None;
                    }
                    return AiMilitaryReason.AicMismatch;
            }
            return expected != -1 && expected != (int)type ? AiMilitaryReason.AicMismatch : AiMilitaryReason.None;
        }

        internal static bool HasCrew(GameUnit* machine, int count)
        {
            if (machine == null || count <= 0 || !UnitAccess.IsReallyAlive(machine)) return false;
            ushort* ids = &machine->r_AssignedEngineer1;
            uint* gids = &machine->r_AssignedEngineer1GlobalId;
            for (int i = 0; i < count; i++)
            {
                if (ids[i] == 0 || gids[i] == 0 || !UnitAccess.TryGetById(ids[i], out GameUnit* worker, out _) ||
                    worker->r_GlobalId != gids[i] || !UnitAccess.IsReallyAlive(worker) ||
                    worker->r_UnitChimp != eChimps.CHIMP_TYPE_ENGINEER ||
                    worker->r_ControllableForPlayerId != machine->r_ControllableForPlayerId) return false;
                for (int j = 0; j < i; j++) if (ids[j] == ids[i]) return false;
            }
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary.Native;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units.AiMilitary.Orders
{
    // Singleton adaptation of the hash-bound 11E960 member branches. Native selection
    // helpers share scratch, but never publish orders to old members. Only 196280 for
    // this recruit publishes a route; no group command or foreign route is replayed.
    internal sealed unsafe class AiAttackRecruitInitializer : IAiRecruitOrderHandler
    {
        private readonly AiMilitaryNativeBackend backend;
        private readonly PredictTarget predict;
        private readonly UnitCandidates unitCandidates;
        private readonly BuildingCandidates buildingCandidates;
        private readonly RamPosition ramPosition;
        private readonly Flood ground, assassin;
        private readonly WideFlood wide;
        private readonly AiMilitaryNativeBackend.GroupQueryDelegate allAssassins;
        private readonly AiMilitaryNativeBackend.UnitQueryDelegate missingCrew;
        private readonly AiMilitaryNativeBackend.UnitActionDelegate clearPath;
        private const int SingletonLimit = 50;
        private IntPtr PathManager => backend.Module + 0x60AD660;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PredictTarget(IntPtr tribes, int attacker, int target, ref int x, ref int y);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void UnitCandidates(IntPtr paths, int tribe, int target, int x, int y, int count, int pcl, int player);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BuildingCandidates(IntPtr paths, int tribe, int building, int count, int pcl, int player);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RamPosition(IntPtr paths, int unit, int building, ref int attackTile);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Flood(IntPtr paths, int x, int y, int tx, int ty, int budget, int resume);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int WideFlood(IntPtr paths, int x, int y, int tx, int ty, int budget);

        internal AiAttackRecruitInitializer(AiMilitaryNativeBackend backend, AiMilitaryNativeBindings bindings)
        {
            this.backend = backend;
            predict = bindings.Bind<PredictTarget>(AiMilitaryNativeBindings.PredictTargetRva);
            unitCandidates = bindings.Bind<UnitCandidates>(AiMilitaryNativeBindings.AttackUnitSearchRva);
            buildingCandidates = bindings.Bind<BuildingCandidates>(AiMilitaryNativeBindings.AttackBuildingSearchRva);
            ramPosition = bindings.Bind<RamPosition>(AiMilitaryNativeBindings.BuildingPositionSearchRva);
            ground = bindings.Bind<Flood>(AiMilitaryNativeBindings.NormalDistanceSearchRva); assassin = bindings.Bind<Flood>(AiMilitaryNativeBindings.AssassinDistanceSearchRva);
            wide = bindings.Bind<WideFlood>(AiMilitaryNativeBindings.MachineDistanceSearchRva);
            allAssassins = bindings.Bind<AiMilitaryNativeBackend.GroupQueryDelegate>(AiMilitaryNativeBindings.AllAssassinsRva);
            missingCrew = bindings.Bind<AiMilitaryNativeBackend.UnitQueryDelegate>(AiMilitaryNativeBindings.CrewCountRva);
            clearPath = bindings.Bind<AiMilitaryNativeBackend.UnitActionDelegate>(AiMilitaryNativeBindings.ClearPathRva);
        }

        public bool Handles(AiOrderKind kind) => kind == AiOrderKind.AttackUnit ||
            kind == AiOrderKind.AttackBuilding || kind == AiOrderKind.AttackTile;

        public bool TryInitialize(int id, uint global, AiNativeGroup group, AiCurrentOrder order, out string reason)
        {
            long world = backend.WorldGeneration;
            reason = string.Empty;
            if ((order.Kind == AiOrderKind.AttackUnit || order.Kind == AiOrderKind.AttackBuilding) &&
                (order.TargetId <= 0 || order.TargetGlobalId == 0))
            { reason = "The attack order lacks a stable target identity."; return false; }
            if (!SameWorld(world, out reason)) return false;
            if (!backend.TryUnit(id, global, out var unit, out reason) || !backend.HasMember(group, id)) return false;
            byte* raw = backend.NativeRawUnit(id);
            if (*(short*)(raw + 0x8F6) == 1 || raw[0xA6B] != 0)
            { reason = "The recruit cannot receive the native attack member order."; return false; }
            var type = unit->r_UnitChimp;
            if (IsMachine(type))
            {
                int missing = missingCrew(backend.UnitManager, id);
                if (!SameWorld(world, out reason)) return false;
                if (missing != 0) { reason = "The machine does not have its complete crew."; return false; }
            }
            switch (order.Kind)
            {
                case AiOrderKind.AttackUnit: return AttackUnit(id, global, group, order, type, raw, world, out reason);
                case AiOrderKind.AttackBuilding: return AttackBuilding(id, global, group, order, type, raw, world, out reason);
                case AiOrderKind.AttackTile: return AttackTile(id, group.Owner, order, type, raw, world, out reason);
                default: reason = "Unsupported attack family."; return false;
            }
        }

        private bool AttackUnit(int id, uint global, AiNativeGroup group, AiCurrentOrder order,
            eChimps type, byte* raw, long world, out string reason)
        {
            if (!backend.TryUnit(order.TargetId, order.TargetGlobalId, out var target, out reason)) return false;
            byte* targetRaw = backend.NativeRawUnit(order.TargetId);
            *(int*)(raw + 0x748) = 0;
            if (IsRangedUnit(type))
            {
                NormalizeRanged(raw); SetUnitTarget(raw, order); *(int*)(raw + 0x6F8) = unchecked((int)order.TargetGlobalId);
                *(short*)(raw + 0x99C) = (short)order.TargetId; clearPath(backend.UnitManager, id);
                return SameWorld(world, out reason);
            }
            if (type == eChimps.CHIMP_TYPE_ARAB_BALLISTA)
            {
                SetUnitTarget(raw, order); clearPath(backend.UnitManager, id);
                if (!SameWorld(world, out reason)) return false;
                *(short*)(raw + 0xA30) = 10;
                reason = string.Empty; return true;
            }
            if (type == eChimps.CHIMP_TYPE_CATAPULT)
            {
                raw[0xA1D] = 5; *(short*)(raw + 0x9F4) = 5;
                *(short*)(raw + 0xA40) = *(short*)(targetRaw + 0x71C); *(short*)(raw + 0xA42) = *(short*)(targetRaw + 0x71E);
                *(short*)(raw + 0x9FC) = 11; *(short*)(raw + 0xA30) = 10;
                reason = string.Empty; return true;
            }
            if (!IsMeleeUnit(type) || target->r_UnitChimp == eChimps.CHIMP_TYPE_LION || target->r_UnitChimp == eChimps.CHIMP_TYPE_GOAT ||
                (order.Extra == 1 && (*(short*)(targetRaw + 0x918) == 0x6A || *(short*)(raw + 0x918) == 0x6A)))
            { reason = "This unit/target pair has no native singleton attack branch."; return false; }
            byte* tracking = backend.Tribe(group);
            if (*(short*)(tracking + 0x63C) != 0 &&
                (*(short*)(tracking + 0x63C) != order.TargetId || *(uint*)(tracking + 0x640) != order.TargetGlobalId))
            { reason = "The native group's attack target differs from its order descriptor."; return false; }
            int x = *(short*)(raw + 0x71C), y = *(short*)(raw + 0x71E);
            predict(backend.TribeManager, id, order.TargetId, ref x, ref y);
            if (!SameWorld(world, out reason)) return false;
            if (!backend.IsMapPosition(x, y)) { reason = "The predicted target is not playable."; return false; }
            int pcl = SourcePcl(raw);
            if (pcl <= 0) { reason = "The recruit has no valid path component."; return false; }
            ClearCandidates(); unitCandidates(PathManager, group.Id, order.TargetId, x, y, 1, pcl, group.Owner);
            if (!SameWorld(world, out reason)) return false;
            var candidates = CopyCandidates();
            foreach (var point in candidates)
            {
                if (!backend.TryTilePosition(point.Tile, out x, out y)) continue;
                if (point.Context == 0 && type == eChimps.CHIMP_TYPE_LORD) continue;
                *(short*)(raw + 0x9F4) = point.Context == 0 ? (short)0 : (short)TribeAICommand.AttackUnit;
                if (point.Context != 0) { *(short*)(raw + 0x9F6) = (short)order.TargetId; *(uint*)(raw + 0x9F8) = order.TargetGlobalId; }
                short previousState = *(short*)(raw + 0x918);
                PrepareApproach(raw, x, y);
                if (!backend.TryBuildRecruitRoute(id, global, group, x, y, out reason)) return false;
                if (!backend.TryUnit(order.TargetId, order.TargetGlobalId, out _, out reason) || !backend.IsCurrent(group)) return false;
                *(short*)(raw + 0xA04) = -50;
                if (type == eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER && previousState == 0x82 &&
                    *(int*)((byte*)backend.Module + 0x8574BCC + group.Owner * 4) != -1) *(short*)(raw + 0xABA) = 200;
                byte* tribe = backend.Tribe(group);
                if (*(short*)(tribe + 0x63C) == 0)
                {
                    *(short*)(tribe + 0x63C) = (short)order.TargetId; *(uint*)(tribe + 0x640) = order.TargetGlobalId;
                    *(int*)(tribe + 0x644) = *(int*)(targetRaw + 0x72C);
                    *(int*)(tribe + 0x630) = Tile(*(short*)(targetRaw + 0x744), *(short*)(targetRaw + 0x746));
                    *(short*)(tribe + 0x63A) = (short)Math.Max(8, (*(short*)(raw + 0x754) / 2) * 8);
                }
                reason = string.Empty; return true;
            }
            reason = "Native selection returned no usable attack-unit position."; return false;
        }

        private bool AttackBuilding(int id, uint global, AiNativeGroup group, AiCurrentOrder order,
            eChimps type, byte* raw, long world, out string reason)
        {
            if (!TryBuilding(order, out var building, out reason)) return false;
            byte* tracking = backend.Tribe(group);
            if (*(short*)(tracking + 0x64C) != order.TargetId || *(uint*)(tracking + 0x650) != order.TargetGlobalId)
            { reason = "The native group's building target differs from its order descriptor."; return false; }
            int buildingType = *(ushort*)(building + 0x12E);
            bool artillery = type == eChimps.CHIMP_TYPE_CATAPULT || type == eChimps.CHIMP_TYPE_ARAB_BALLISTA;
            bool hybridRanged = type == eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER && *(short*)(raw + 0xAA8) < 1 &&
                (*(int*)((byte*)backend.Module + 0x8574B90) == 0 || *(int*)((byte*)backend.Module + 0x8574C44 + group.Owner * 4) == 0);
            if (artillery || IsRangedBuilding(type) || hybridRanged)
            {
                if (artillery && (order.Command == (int)TribeAICommand.Unknown38 || buildingType == 0x44))
                { reason = "The native machine branch ignores this building command."; return false; }
                if (!artillery)
                {
                    if (order.Command == (int)TribeAICommand.Unknown38 ||
                        (order.Command == (int)TribeAICommand.ForceAttackBuilding && !ForceBuildingType(buildingType)))
                    { reason = "The native ranged branch ignores this building command."; return false; }
                    NormalizeRanged(raw);
                    int size = *(short*)(building + 0x154);
                    *(short*)(raw + 0x716) = (short)(*(short*)(building + 0x14A) * 8 + size * 4);
                    *(short*)(raw + 0x718) = (short)(*(short*)(building + 0x14C) * 8 + size * 4);
                    *(short*)(raw + 0x71A) = *(short*)(building + 0x148); *(short*)(raw + 0x99C) = -1;
                }
                *(int*)(raw + 0x748) = 0; *(short*)(raw + 0x98E) = (short)order.TargetId;
                *(uint*)(raw + 0x9F8) = order.TargetGlobalId; raw[0xA1D] = 9; *(short*)(raw + 0x9F4) = 9;
                raw[0xA50] = 0; clearPath(backend.UnitManager, id);
                if (!SameWorld(world, out reason)) return false;
                *(short*)(raw + 0xA30) = 10;
                reason = string.Empty; return true;
            }
            bool ram = type == eChimps.CHIMP_TYPE_BATTERING_RAM;
            if ((!IsMeleeBuilding(type) && !ram && type != eChimps.CHIMP_TYPE_KNIGHT && type != eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER) || buildingType == 0x44)
            { reason = "This type has no native attack-building member branch."; return false; }
            int pcl = SourcePcl(raw);
            if (pcl <= 0) { reason = "The recruit has no valid path component."; return false; }
            ClearCandidates(); buildingCandidates(PathManager, group.Id, order.TargetId, 1, pcl,
                order.Command == (int)TribeAICommand.ForceAttackBuilding ? 0 : group.Owner);
            if (!SameWorld(world, out reason)) return false;
            var candidates = CopyCandidates();
            if (!RankCandidates(candidates, raw, group, ram || type == eChimps.CHIMP_TYPE_KNIGHT, world, out reason)) return false;
            if (candidates.Count == 0 || candidates[0].Context == 0)
            { reason = "Native selection produced no immediate building attack context."; return false; }
            int x, y, attackTile = 0;
            if (ram)
            {
                int approach = ramPosition(PathManager, id, order.TargetId, ref attackTile);
                if (!SameWorld(world, out reason)) return false;
                if (backend.TryTilePosition(approach, out x, out y))
                    return PublishBuildingApproach(id, global, group, order, raw, x, y, attackTile, out reason);
            }
            foreach (var candidate in candidates)
                if (backend.TryTilePosition(candidate.Tile, out x, out y))
                    return PublishBuildingApproach(id, global, group, order, raw, x, y, candidate.Context, out reason);
            reason = "Native selection returned no reachable building approach."; return false;
        }

        private bool PublishBuildingApproach(int id, uint global, AiNativeGroup group, AiCurrentOrder order,
            byte* raw, int x, int y, int attackTile, out string reason)
        {
            *(int*)(raw + 0x748) = 0; *(short*)(raw + 0x9F4) = 0; PrepareApproach(raw, x, y);
            if (!backend.TryBuildRecruitRoute(id, global, group, x, y, out reason)) return false;
            if (!TryBuilding(order, out _, out reason) || !backend.IsCurrent(group)) return false;
            *(int*)(raw + 0xA00) = attackTile;
            if (attackTile == 0) { *(short*)(raw + 0xA40) = 0; *(short*)(raw + 0xA42) = 0; }
            else if (backend.TryTilePosition(attackTile, out x, out y) &&
                *(short*)((byte*)backend.Module + 0x4B6AA50 + attackTile * 2) == order.TargetId)
            { *(short*)(raw + 0xA40) = (short)x; *(short*)(raw + 0xA42) = (short)y; }
            else { reason = "The selected building attack tile is invalid."; return false; }
            reason = string.Empty; return true;
        }

        private bool AttackTile(int id, int owner, AiCurrentOrder order, eChimps type, byte* raw, long world, out string reason)
        {
            if ((order.Command != (int)TribeAICommand.AttackTilePosition && order.Command != (int)TribeAICommand.Unknown22) ||
                !backend.IsMapPosition(order.X, order.Y) || (!IsRangedUnit(type) && type != eChimps.CHIMP_TYPE_ARAB_BALLISTA &&
                type != eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER && type != eChimps.CHIMP_TYPE_CATAPULT))
            { reason = "This type has no supported native attack-tile branch."; return false; }
            if (type != eChimps.CHIMP_TYPE_CATAPULT)
            { NormalizeRanged(raw); raw[0xA50] = 0; *(short*)(raw + 0x9F4) = (short)order.Command; }
            raw[0xA1D] = (byte)order.Command;
            if (type == eChimps.CHIMP_TYPE_CATAPULT && order.Command == (int)TribeAICommand.Unknown22)
            {
                if (*(int*)((byte*)backend.Module + 0x379D048 + owner * 0x583C) < 1) raw[0xA1D] = 5;
                else
                {
                    *(short*)(raw + 0x9F4) = 22; *(short*)(raw + 0x716) = (short)(order.X * 8);
                    *(short*)(raw + 0x718) = (short)(order.Y * 8);
                    *(short*)(raw + 0x71A) = ((byte*)backend.Module + 0x4DDD350)[Tile(order.X, order.Y)];
                }
            }
            *(short*)(raw + 0xA40) = (short)order.X; *(short*)(raw + 0xA42) = (short)order.Y;
            *(short*)(raw + 0x9FC) = (short)order.Extra; *(int*)(raw + 0x748) = 0;
            clearPath(backend.UnitManager, id);
            if (!SameWorld(world, out reason)) return false;
            *(short*)(raw + 0xA30) = 10;
            reason = string.Empty; return true;
        }

        private readonly struct Candidate
        {
            internal Candidate(int tile, int context, int ordinal, int distance = 0)
            { Tile = tile; Context = context; Ordinal = ordinal; Distance = distance; }
            internal readonly int Tile, Context, Ordinal, Distance;
        }
        private void ClearCandidates()
        { int* list = (int*)((byte*)PathManager + 0x1B344); for (int i = 0; i < SingletonLimit * 3; i++) list[i] = 0; }
        private List<Candidate> CopyCandidates()
        {
            var result = new List<Candidate>(SingletonLimit); int* list = (int*)((byte*)PathManager + 0x1B344);
            for (int i = 0; i < SingletonLimit && list[i * 3] != 0; i++) result.Add(new Candidate(list[i * 3], list[i * 3 + 1], i));
            return result;
        }
        private bool RankCandidates(List<Candidate> points, byte* raw, AiNativeGroup group, bool large, long world, out string reason)
        {
            reason = string.Empty;
            int x = *(short*)(raw + 0x71C), y = *(short*)(raw + 0x71E);
            if (!backend.IsMapPosition(x, y)) { points.Clear(); return true; }
            if (large) wide(PathManager, x, y, -1, -1, 400000);
            else
            {
                int assassins = allAssassins(backend.TribeManager, group.Id);
                if (!SameWorld(world, out reason)) return false;
                if (assassins != 0) assassin(PathManager, x, y, -1, -1, 400000, 0);
                else ground(PathManager, x, y, -1, -1, 400000, 0);
            }
            if (!SameWorld(world, out reason)) return false;
            int generation = *(int*)((byte*)PathManager + 4), output = 0;
            for (int i = 0; i < points.Count; i++)
            {
                var p = points[i];
                if (!backend.TryTilePosition(p.Tile, out _, out _) || *(short*)((byte*)backend.Module + 0x52C2550 + p.Tile * 2) != generation) continue;
                points[output++] = new Candidate(p.Tile, p.Context, p.Ordinal, *(short*)((byte*)backend.Module + 0x5225B10 + p.Tile * 2));
            }
            points.RemoveRange(output, points.Count - output);
            points.Sort((a, b) => { int value = (b.Context != 0).CompareTo(a.Context != 0); if (value != 0) return value;
                value = a.Distance.CompareTo(b.Distance); return value != 0 ? value : a.Ordinal.CompareTo(b.Ordinal); });
            return true;
        }
        private bool SameWorld(long world, out string reason)
        { bool current = backend.IsWorldCurrent(world); reason = current ? string.Empty : "The world changed during native attack preparation."; return current; }
        private int SourcePcl(byte* raw)
        { int tile = *(int*)(raw + 0x72C); return tile > 0 && tile < 640000 ? *(short*)((byte*)backend.Module + 0x50EC690 + tile * 2) : 0; }
        private int Tile(int x, int y) => backend.IsMapPosition(x, y) ? x + *(int*)((byte*)backend.Module + 0x402FF2C + y * 12) : 0;
        private bool TryBuilding(AiCurrentOrder order, out byte* raw, out string reason)
        {
            raw = null; reason = string.Empty;
            if (!BuildingAccess.TryGetById(GameBuildingManagerAPI.Instance, order.TargetId, out GameBuilding* building, out _) || building == null ||
                building->r_AliveState != AliveState.IsAlive || building->r_GlobalId != order.TargetGlobalId)
            { reason = "The attack building identity is stale."; return false; }
            raw = (byte*)backend.Module + 0x64CCBB0 + order.TargetId * 0x32C;
            int size = *(short*)(raw + 0x154);
            if (size < 1 || size > 13) { reason = "Unsupported native building footprint."; return false; }
            return true;
        }
        private static void SetUnitTarget(byte* raw, AiCurrentOrder order)
        { *(int*)(raw + 0x748) = 0; *(short*)(raw + 0x9F4) = 4; *(short*)(raw + 0x9F6) = (short)order.TargetId;
            *(uint*)(raw + 0x9F8) = order.TargetGlobalId; raw[0xA50] = 0; }
        private static void NormalizeRanged(byte* raw) { if (*(short*)(raw + 0x918) == 0x69) *(short*)(raw + 0x918) = 0x65; }
        private static void PrepareApproach(byte* raw, int x, int y)
        { if (*(short*)(raw + 0x918) == 0x6A) *(short*)(raw + 0x92A) = (short)(*(short*)(raw + 0x9A4) * -4);
            *(short*)(raw + 0x934) = (short)x; *(short*)(raw + 0x936) = (short)y; *(int*)(raw + 0xA00) = 0;
            *(short*)(raw + 0x99E) = 0; *(short*)(raw + 0x918) = 0x65; }
        private static bool ForceBuildingType(int value) => value >= 0x2D && value <= 0x30 || value >= 0x4A && value <= 0x4E;
        private static bool IsMachine(eChimps t) => t == eChimps.CHIMP_TYPE_CATAPULT || t == eChimps.CHIMP_TYPE_ARAB_BALLISTA ||
            t == eChimps.CHIMP_TYPE_SIEGE_TOWER || t == eChimps.CHIMP_TYPE_BATTERING_RAM || t == eChimps.CHIMP_TYPE_PORTABLE_SHIELD;
        private static bool IsRangedUnit(eChimps t) => t == eChimps.CHIMP_TYPE_ARCHER || t == eChimps.CHIMP_TYPE_XBOWMAN ||
            t == eChimps.CHIMP_TYPE_ARAB_BOW || t == eChimps.CHIMP_TYPE_ARAB_SLINGER || t == eChimps.CHIMP_TYPE_ARAB_HORSEMAN ||
            t == eChimps.CHIMP_TYPE_ARAB_GRENADIER || t == eChimps.CHIMP_TYPE_BEDOUIN_AMBUSHER || t == eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL;
        private static bool IsRangedBuilding(eChimps t) => IsRangedUnit(t) && t != eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL;
        private static bool IsMeleeUnit(eChimps t) => IsMeleeBuilding(t) || t == eChimps.CHIMP_TYPE_KNIGHT ||
            t == eChimps.CHIMP_TYPE_BEDOUIN_HEALER || t == eChimps.CHIMP_TYPE_BEDOUIN_SKIRMISHER;
        private static bool IsMeleeBuilding(eChimps t) => t == eChimps.CHIMP_TYPE_TUNNELER || t == eChimps.CHIMP_TYPE_SPEARMAN ||
            t == eChimps.CHIMP_TYPE_PIKEMAN || t == eChimps.CHIMP_TYPE_MACEMAN || t == eChimps.CHIMP_TYPE_SWORDSMAN ||
            t == eChimps.CHIMP_TYPE_MONK || t == eChimps.CHIMP_TYPE_LORD || t == eChimps.CHIMP_TYPE_ARAB_SLAVE ||
            t == eChimps.CHIMP_TYPE_ARAB_ASSASIN || t == eChimps.CHIMP_TYPE_ARAB_SWORDSMAN || t == eChimps.CHIMP_TYPE_BEDOUIN_CAMEL_LANCER ||
            t == eChimps.CHIMP_TYPE_BEDOUIN_EUNUCH || t == eChimps.CHIMP_TYPE_BEDOUIN_HEAVY_CAMEL ||
            t == eChimps.CHIMP_TYPE_BEDOUIN_SAPPER || t == eChimps.CHIMP_TYPE_BEDOUIN_DEMOLISHER;
    }
}

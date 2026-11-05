using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Units.AiMilitary.Orders;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units.AiMilitary.Native
{
    // All operations require the service's quiescent engine-monitor scope. This class owns
    // no timer, Unity object, hook or publisher. The process-owned service retains it.
    internal sealed unsafe partial class AiMilitaryNativeBackend
    {
        internal const ushort UnmanagedRole = ushort.MaxValue;
        internal const int UnitStride = 0x490, TribeStride = 0x688, TribeCapacity = 4500;
        internal readonly IntPtr Module;
        internal IntPtr UnitManager => Module + 0x67E8400;
        internal IntPtr TribeManager => Module + 0x7CC6720;
        internal IntPtr LordManager => Module + 0x366C210;
        private readonly AllocateDelegate allocate;
        private readonly UnitGroupDelegate assign;
        private readonly ClassifyDelegate classify;
        private readonly GroupForClassDelegate defense, fallback, harassment;
        private readonly UnitClassifierDelegate fallbackClass;
        private readonly List<IAiRecruitOrderHandler> handlers = new List<IAiRecruitOrderHandler>();
        private AiExpandedDefenseStorage expanded;
        private AiExpandedDefenseStorage ExpandedStorage => expanded ?? (expanded = AiExpandedDefenseStorage.Discover());
        private readonly HashSet<AiNativeGroup> allocatedHere = new HashSet<AiNativeGroup>();
        private bool admitting;
        private long worldGeneration, admittingGeneration;
        internal long WorldGeneration => worldGeneration;
        internal bool IsWorldCurrent(long value) => value == worldGeneration && AiRecruitMutationReceipts.IsCurrentWorld;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AllocateDelegate(IntPtr manager, int owner);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void UnitGroupDelegate(IntPtr manager, int unitId, int tribeId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ClassifyDelegate(IntPtr manager, int unitId, int flags);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int UnitClassifierDelegate(IntPtr manager, int unitId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int GroupForClassDelegate(IntPtr manager, int player, int value);

        private AiMilitaryNativeBackend(IntPtr module, AiMilitaryNativeBindings bindings)
        {
            Module = module;
            allocate = bindings.Bind<AllocateDelegate>(AiMilitaryNativeBindings.AllocateRva);
            assign = bindings.Bind<UnitGroupDelegate>(AiMilitaryNativeBindings.AssignRva);
            classify = bindings.Bind<ClassifyDelegate>(AiMilitaryNativeBindings.ClassifyDefenseRva);
            fallbackClass = bindings.Bind<UnitClassifierDelegate>(AiMilitaryNativeBindings.DefenseFallbackClassRva);
            defense = bindings.Bind<GroupForClassDelegate>(AiMilitaryNativeBindings.DefenseRva);
            fallback = bindings.Bind<GroupForClassDelegate>(AiMilitaryNativeBindings.DefenseFallbackRva);
            harassment = bindings.Bind<GroupForClassDelegate>(AiMilitaryNativeBindings.HarassmentRva);
            expanded = AiExpandedDefenseStorage.Discover();
            InitializeRouting(bindings);
            AttachHandler(new AiMoveRecruitInitializer(this, bindings));
            AttachHandler(new AiStrategicRecruitInitializer(this));
            AttachHandler(new AiAttackRecruitInitializer(this, bindings));
        }

        internal static bool TryCreate(IntPtr module, ReadOnlySpan<byte> pristineImage, string nativeHash,
            out AiMilitaryNativeBackend backend, out string reason)
        {
            backend = null;
            try
            {
                ValidateLayout();
                backend = new AiMilitaryNativeBackend(module, new AiMilitaryNativeBindings(module, pristineImage, nativeHash));
                reason = string.Empty; return true;
            }
            catch (Exception error) { reason = error.Message; return false; }
        }

        internal void AttachHandler(IAiRecruitOrderHandler handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            handlers.Add(handler);
        }

        internal bool TryGetGroup(int id, int owner, out AiNativeGroup group)
        {
            group = default(AiNativeGroup);
            if (id <= 0 || id >= TribeCapacity || owner < 1 || owner > 8) return false;
            byte* record = (byte*)TribeManager + id * TribeStride;
            if (*(ushort*)(record + 0x40) != (ushort)AliveState.IsAlive || *(int*)(record + 0x2C) != owner) return false;
            group = new AiNativeGroup(id, *(uint*)(record + 0x34), owner); return group.GlobalId != 0;
        }
        internal bool IsCurrent(AiNativeGroup group) => TryGetGroup(group.Id, group.Owner, out var current) && current.Equals(group);
        internal byte* Tribe(AiNativeGroup group) => (byte*)TribeManager + group.Id * TribeStride;

        internal bool TryCreatePrivateGroup(int owner, out AiNativeGroup group, out string reason)
            => TryAllocateGroup(owner, true, out group, out reason);

        internal bool TryAllocateGroup(int owner, bool aggressive, out AiNativeGroup group, out string reason)
        {
            long operationWorld = worldGeneration;
            using (AiRecruitMutationReceipts.Enter(() => worldGeneration == operationWorld))
            {
            group = default(AiNativeGroup);
            if (owner < 1 || owner > 8) { reason = "Invalid private-group owner."; return false; }
            int id = allocate(TribeManager, owner);
            if (!IsWorldCurrent(operationWorld)) { reason = "The world changed during group allocation."; return false; }
            if (!TryGetGroup(id, owner, out group)) { reason = "No free native tribe."; return false; }
            allocatedHere.Add(group);
            if (aggressive) ((GameTribe*)(Tribe(group) + 0x2A))->r_TribeStance = TribeStance.Aggressive;
            reason = string.Empty; return true;
            }
        }

        internal void BeginWorld(long generation) { worldGeneration = generation; allocatedHere.Clear(); }
        internal bool TryDeleteEmptyGroup(AiNativeGroup group)
        {
            if (group.Id <= 0 || group.Id >= TribeCapacity || group.Owner < 1 || group.Owner > 8 || group.GlobalId == 0) return false;
            byte* raw = Tribe(group);
            if (!allocatedHere.Contains(group) || *(uint*)(raw + 0x34) != group.GlobalId || *(int*)(raw + 0x2C) != group.Owner ||
                (*(ushort*)(raw + 0x40) != (ushort)AliveState.IsAlive && *(ushort*)(raw + 0x40) != (ushort)AliveState.MarkedForDeletion) ||
                *(short*)(Tribe(group) + 0x5C) != 0 ||
                *(short*)(Tribe(group) + 0x5A) != 0) return false;
            for (int i = 0; i < 625; i++) if (*(ushort*)(Tribe(group) + 0x60 + i * 2) != 0) return false;
            if (*(ushort*)(raw + 0x40) == (ushort)AliveState.IsAlive && !GameTribeManagerAPI.Instance.DeleteTribeSafe(group.Id)) return false;
            if (*(uint*)(raw + 0x34) != group.GlobalId || *(int*)(raw + 0x2C) != group.Owner ||
                *(ushort*)(raw + 0x40) != (ushort)AliveState.MarkedForDeletion) return false;
            // Vanilla resolves by ID/global pair without checking Alive. Clear all
            // matching references before the native deletion phase can run.
            if (GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(group.Owner, out GamePlayerResources* resources) && resources != null)
                for (int i = 0; i < 300; i++)
                    if (resources->r_AITribeIdsByStorageRole[i] == group.Id && resources->r_AITribeGlobalIdsByStorageRole[i] == group.GlobalId)
                    { resources->r_AITribeIdsByStorageRole[i] = 0; resources->r_AITribeGlobalIdsByStorageRole[i] = 0; }
            ExpandedStorage?.ClearReference(group);
            allocatedHere.Remove(group); return true;
        }

        // Resolves/allocates only. No unit role, bitset or unit order is written here.
        // detail=-1 selects native automatic distribution; detail>=0 is a populated ordinal.
        internal bool TryResolveGroup(int playerId, int unitId, ushort unitRole, int detail,
            out AiNativeGroup group, out string reason)
        {
            long operationWorld = worldGeneration;
            using (AiRecruitMutationReceipts.Enter(() => worldGeneration == operationWorld))
            {
                bool result = TryResolveGroupCore(playerId, unitId, unitRole, detail, out group, out reason);
                if (!IsWorldCurrent(operationWorld))
                { group = default(AiNativeGroup); reason = "The world changed during AI group resolution."; return false; }
                return result;
            }
        }
        private bool TryResolveGroupCore(int playerId, int unitId, ushort unitRole, int detail,
            out AiNativeGroup group, out string reason)
        {
            group = default(AiNativeGroup);
            if (!TryUnit(unitId, 0, out var unit, out reason) || unit->r_ControllableForPlayerId != playerId)
            { if (reason.Length == 0) reason = "Unit owner differs from the requested AI."; return false; }
            if (!GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(playerId, out GamePlayerResources* resources) ||
                resources == null || !AiMilitaryCatalog.TryReadAic(playerId, out InternalAIC* aic))
            { reason = "The requested player has no active AI profile."; return false; }
            if (unitRole == 1) return ResolveDefense(resources, playerId, unitId, detail, out group, out reason);
            if (unitRole == 2)
            {
                int id = harassment(LordManager, playerId, unitId);
                if (TryGetGroup(id, playerId, out group)) { reason = string.Empty; return true; }
                reason = "Native harassment classification/allocation failed."; return false;
            }
            int slot;
            switch (unitRole)
            {
                case 4:
                    return ResolveWindow(resources, playerId, 170, aic->defense_patrols, 10, detail, false, out group, out reason);
                case 5: slot = 10; break;
                case 6: slot = 166; break;
                case 7: slot = 167; break;
                case 10: slot = 18; break;
                case 11: slot = 17; break;
                case 12: slot = 11; break;
                case 13: slot = 12; break;
                case 14: slot = 13; break;
                case 15: slot = 14; break;
                case 16: return ResolveWindow(resources, playerId, 15, aic->siege_storm_tribes, 2, detail, false, out group, out reason);
                case 17: return ResolveWindow(resources, playerId, 186, aic->siege_cover_tribes, 3, detail, true, out group, out reason);
                case 18: slot = 189; break;
                case 19: return ResolveWindow(resources, playerId, 190, aic->siege_reserve_tribes, 2, detail, true, out group, out reason);
                case 20: return ResolveWindow(resources, playerId, 192, aic->siege_wall_tribes, 8, detail, true, out group, out reason);
                case 22: slot = 165; break;
                default: reason = "This native unit role is not a free AI group."; return false;
            }
            return ResolveSlot(resources, playerId, slot, out group, out reason);
        }

        private bool ResolveWindow(GamePlayerResources* resources, int owner, int first, int configured, int maximum,
            int ordinal, bool zeroUsesBase, out AiNativeGroup group, out string reason)
        {
            group = default(AiNativeGroup);
            if (!AiGroupWindowPolicy.TryCount(configured, maximum, zeroUsesBase, out int count))
            { reason = "The AIC group window exceeds its verified physical storage."; return false; }
            if (ordinal >= count || ordinal < -1) { reason = "The requested group ordinal is not configured."; return false; }
            if (ordinal >= 0) return ResolveSlot(resources, owner, first + ordinal, out group, out reason);
            int chosen = -1, minimum = 1000;
            for (int i = 0; i < count; i++)
            {
                int slot = first + i;
                if (!TryGetGroup(resources->r_AITribeIdsByStorageRole[slot], owner, out var candidate) ||
                    candidate.GlobalId != resources->r_AITribeGlobalIdsByStorageRole[slot])
                    return ResolveSlot(resources, owner, slot, out group, out reason);
                int members = *(short*)(Tribe(candidate) + 0x5C);
                if (members < minimum) { minimum = members; chosen = slot; }
            }
            if (chosen < 0) { reason = "Native automatic distribution has no group below its 1000-member selection limit."; return false; }
            return ResolveSlot(resources, owner, chosen, out group, out reason);
        }

        private bool ResolveSlot(GamePlayerResources* resources, int owner, int slot, out AiNativeGroup group, out string reason)
        {
            int id = resources->r_AITribeIdsByStorageRole[slot];
            if (TryGetGroup(id, owner, out group) && group.GlobalId == resources->r_AITribeGlobalIdsByStorageRole[slot])
            { reason = string.Empty; return true; }
            if (!TryAllocateGroup(owner, false, out group, out reason)) return false;
            resources->r_AITribeIdsByStorageRole[slot] = (ushort)group.Id;
            resources->r_AITribeGlobalIdsByStorageRole[slot] = group.GlobalId;
            return true;
        }

        private bool ResolveDefense(GamePlayerResources* resources, int owner, int unitId, int ordinal,
            out AiNativeGroup group, out string reason)
        {
            group = default(AiNativeGroup);
            int cls = DefensiveClass(resources, unitId);
            if ((uint)cls >= 30) { reason = "Invalid native defensive class."; return false; }
            if (ordinal < 0)
            {
                int id = resources->r_AIDefensivePositionCountsByClass.r_Counts[cls] > 0
                    ? defense(LordManager, owner, cls)
                    : fallback(LordManager, owner, fallbackClass(LordManager, unitId) == 0 ? 1 : 0);
                if (TryGetGroup(id, owner, out group)) { reason = string.Empty; return true; }
                reason = "Native defensive allocation failed."; return false;
            }
            var expandedStorage = ExpandedStorage;
            if (expandedStorage != null)
            {
                if (expandedStorage.TryResolve(this, resources, owner, cls, ordinal, out group, out reason)) return true;
                if (reason.Length != 0) return false;
            }
            int count = resources->r_AIDefensivePositionCountsByClass.r_Counts[cls];
            if (cls == 8 || cls == 10 || cls == 17)
                count = Math.Min(count, ((InternalAIC*)LordManager)[resources->r_AILordMinusOne - 1].max_mobile_groups);
            if (ordinal >= Math.Min(10, count)) { reason = "The requested defensive position is not configured."; return false; }
            int roleBase = *(int*)((byte*)Module + 0x2CEE80 + cls * 4);
            int storage = roleBase + ordinal;
            if (storage >= 164 && storage <= 167)
            { reason = "This defensive ordinal overlaps a reserved native role slot."; return false; }
            return ResolveSlot(resources, owner, storage, out group, out reason);
        }

        internal bool TryDetach(int unitId, uint globalId, out string reason)
        {
            long operationWorld = worldGeneration;
            using (AiRecruitMutationReceipts.Enter(() => worldGeneration == operationWorld))
            {
            if (!TryUnit(unitId, globalId, out var unit, out reason)) return false;
            int id = unit->r_TribeId;
            if (id == 0) return true;
            if (!TryGetGroup(id, unit->r_ControllableForPlayerId, out var group) ||
                !HasMember(group, unitId) || *(short*)(Tribe(group) + 0x5C) < 1 ||
                unit->r_TribeLeaderUnitId != *(ushort*)(Tribe(group) + 0x5A))
            { reason = "Unit/tribe membership is inconsistent."; return false; }
            uint expectedGlobal = unit->r_GlobalId;
            int beforeCount = *(short*)(Tribe(group) + 0x5C);
            bool removed = GameTribeManagerAPI.Instance.UnassignUnit(id, unitId);
            if (!IsWorldCurrent(operationWorld)) { reason = "The world changed during public unit removal."; return false; }
            if (!removed ||
                unit->r_GlobalId != expectedGlobal || unit->r_TribeId != 0 || unit->UnknownAIFlag != 0 ||
                unit->r_TribeLeaderUnitId != 0 || RawHasMember(group, unitId) ||
                *(short*)(Tribe(group) + 0x5C) != beforeCount - 1)
            { reason = "The public removal wrapper did not completely detach the unit."; return false; }
            return true;
            }
        }

        internal bool TryAdmit(int unitId, uint unitGlobalId, AiNativeGroup group, ushort unitRole,
            AiCurrentOrder order, out string reason)
        {
            reason = string.Empty;
            long operationWorld = worldGeneration;
            if (admitting && admittingGeneration == operationWorld) { reason = "Recursive AI admission is not permitted."; return false; }
            if (!IsCurrent(group) || !TryUnit(unitId, unitGlobalId, out var unit, out reason))
            { if (reason.Length == 0) reason = "The target AI group no longer exists."; return false; }
            if (unit->r_ControllableForPlayerId != group.Owner) { reason = "AI group owner differs."; return false; }
            if (unit->r_TribeId == group.Id || HasMember(group, unitId)) { reason = "The unit is already a member."; return false; }
            int memberCount = *(short*)(Tribe(group) + 0x5C), bitCount = 0;
            for (int i = 0; i < 625; i++)
            {
                uint bits = *(ushort*)(Tribe(group) + 0x60 + i * 2);
                while (bits != 0) { bits &= bits - 1; bitCount++; }
            }
            if (memberCount < 0 || memberCount >= 9999 || bitCount != memberCount || (*(ushort*)(Tribe(group) + 0x60) & 1) != 0)
            { reason = "The native group is full or its membership count is inconsistent."; return false; }
            int leaderId = *(ushort*)(Tribe(group) + 0x5A);
            if ((memberCount == 0 && leaderId != 0) || (memberCount != 0 &&
                (!HasMember(group, leaderId) || !TryUnit(leaderId, 0, out var leader, out _) ||
                leader->r_TribeId != group.Id || leader->r_ControllableForPlayerId != group.Owner)))
            { reason = "The native group's leader identity is inconsistent."; return false; }
            IAiRecruitOrderHandler handler = null;
            if (order.Kind != AiOrderKind.Idle)
                foreach (var candidate in handlers) if (candidate.Handles(order.Kind)) { handler = candidate; break; }
            if (order.Kind != AiOrderKind.Idle && handler == null)
            { reason = "No verified initializer is installed for the current AI order."; return false; }
            int oldId = unit->r_TribeId;
            AiNativeGroup oldGroup = default(AiNativeGroup);
            if (oldId != 0 && (!TryGetGroup(oldId, group.Owner, out oldGroup) ||
                unit->r_AITribeRole != UnmanagedRole || *(short*)(Tribe(oldGroup) + 0x5C) != 1))
            { reason = "Only a new, unmanaged unit can be admitted."; return false; }
            var snapshot = new AiRecruitSnapshot(Module, unitId, unit);
            byte[] oldPrivate = null;
            if (oldId != 0)
            {
                oldPrivate = new byte[TribeStride];
                Marshal.Copy((IntPtr)Tribe(oldGroup) + 0x28, oldPrivate, 0, oldPrivate.Length);
            }
            bool previousAdmitting = admitting; long previousAdmissionGeneration = admittingGeneration;
            admitting = true; admittingGeneration = operationWorld;
            bool added = false;
            var expectedType = unit->r_UnitChimp;
            using (var mutation = AiRecruitMutationReceipts.Enter(() => worldGeneration == operationWorld, unitId, group.Id,
                () => TryUnit(unitId, unitGlobalId, out var currentUnit, out _) && currentUnit->r_ControllableForPlayerId == group.Owner &&
                    currentUnit->r_UnitChimp == expectedType && IsCurrent(group),
                () => unit->r_TribeId == 0 && !HasMember(group, unitId)))
            {
            try
            {
                if (oldId != 0 && !TryDetach(unitId, unitGlobalId, out reason)) return false;
                if (!IsWorldCurrent(operationWorld)) { reason = "The world changed before AI assignment."; return false; }
                added = true; assign(TribeManager, unitId, group.Id);
                if (!mutation.WasAssigned || !IsWorldCurrent(operationWorld))
                { reason = "The actual native membership call was vetoed, redirected or crossed a world transition."; return false; }
                if (!HasMember(group, unitId) || unit->r_TribeId != group.Id)
                { reason = "Native assignment did not publish membership."; return false; }
                if (handler != null && !handler.TryInitialize(unitId, unitGlobalId, group, order, out reason)) return false;
                if (!IsWorldCurrent(operationWorld) || !IsCurrent(group) || unit->r_GlobalId != unitGlobalId || unit->r_TribeId != group.Id)
                { reason = "Identity changed during AI admission."; return false; }
                unit->r_AITribeRole = unitRole;
                if (unitRole == 5) ((GameTribe*)(Tribe(group) + 0x2A))->r_TribeStance = TribeStance.Aggressive;
                snapshot.Commit();
                return true;
            }
            catch (Exception error) { reason = "AI admission failed: " + error.Message; return false; }
            finally
            {
                try
                {
                if (IsWorldCurrent(operationWorld) && !snapshot.Committed && unit->r_GlobalId == unitGlobalId)
                {
                    if (added && unit->r_TribeId == group.Id && IsCurrent(group) && HasMember(group, unitId))
                    {
                        if (!TryDetach(unitId, unitGlobalId, out var rollbackReason))
                            throw new InvalidOperationException("AI admission rollback failed: " + rollbackReason);
                    }
                    // Removing its sole member marks the private tribe for deletion. No native
                    // tick can delete it in this scope; restore this owned private record only.
                    bool restoredPrivate = oldId == 0;
                    if (oldPrivate != null)
                    {
                        byte* old = Tribe(oldGroup);
                        if (*(uint*)(old + 0x34) == oldGroup.GlobalId && *(int*)(old + 0x2C) == oldGroup.Owner)
                        { Marshal.Copy(oldPrivate, 0, (IntPtr)old + 0x28, oldPrivate.Length); restoredPrivate = true; }
                    }
                    snapshot.Restore(unit);
                    if (!restoredPrivate)
                    { unit->r_TribeId = 0; unit->r_TribeLeaderUnitId = 0; unit->UnknownAIFlag = 0; }
                }
                }
                finally { admitting = previousAdmitting; admittingGeneration = previousAdmissionGeneration; }
            }
            }
        }

        private int DefensiveClass(GamePlayerResources* resources, int unitId) => classify(LordManager, unitId,
            resources->r_AIConstructionPauseTriggerIndex == 1 && resources->r_AIConstructionPauseTriggers[1] > 0 ? 1 : 0);

        internal bool HasMember(AiNativeGroup group, int unitId) => IsCurrent(group) && RawHasMember(group, unitId);
        private bool RawHasMember(AiNativeGroup group, int unitId) => unitId > 0 && unitId < 10000 &&
            ((*(ushort*)(Tribe(group) + 0x60 + (unitId >> 4) * 2) >> (unitId & 15)) & 1) != 0;

        internal bool TryUnit(int id, uint global, out GameUnit* unit, out string reason)
        {
            reason = string.Empty;
            if (!UnitAccess.TryGetById(id, out unit, out _) || !UnitAccess.IsReallyAlive(unit) ||
                (global != 0 && unit->r_GlobalId != global))
            { unit = null; reason = "The unit identity is absent, dying or not initialized."; return false; }
            return true;
        }

        private static void ValidateLayout()
        {
            if (Marshal.SizeOf(typeof(GameUnit)) != UnitStride || Marshal.SizeOf(typeof(GameTribe)) != TribeStride ||
                Marshal.OffsetOf(typeof(GameUnit), nameof(GameUnit.r_AITribeRole)).ToInt32() != 0x426 ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_AITribeIdsByStorageRole)).ToInt32() != 0x48A0 ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_AITribeGlobalIdsByStorageRole)).ToInt32() != 0x4AF8 ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_AILordMinusOne)).ToInt32() != 0x2300 ||
                Marshal.SizeOf(typeof(InternalAIC)) != 0x5E4 ||
                Marshal.OffsetOf(typeof(GameUnitManager), nameof(GameUnitManager.GameUnitArray)).ToInt32() != 0x65C ||
                Marshal.OffsetOf(typeof(GameTribeManager), nameof(GameTribeManager.GameTribeArray)).ToInt32() != 0x2A)
                throw new InvalidOperationException("AI military Extender layouts differ from the audited native contracts.");
        }
    }
}

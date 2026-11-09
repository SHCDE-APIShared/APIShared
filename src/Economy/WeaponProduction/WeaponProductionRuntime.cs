using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Networking;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Process-owned state. The admission owner holds the engine monitor for world
    // changes. Chores and the native producer adapters mutate only in simulation.
    internal static unsafe partial class WeaponProductionRuntime
    {
        private static readonly object RegistrationGate = new object();
        private static WeaponProductionRegistration[] registrations = Array.Empty<WeaponProductionRegistration>();
        private static WeaponProductionRegistration[] preparedRegistrations;
        private static bool preparedBackend;
        private static bool preparedTannerIngredients;
        private static WeaponProductionCatalog prepared = WeaponProductionCatalog.Empty;
        private static WeaponProductionCatalog frozen = WeaponProductionCatalog.Empty;
        private static WeaponProductionCatalog active = WeaponProductionCatalog.Empty;
        private static readonly Dictionary<int, BuildingState> buildings = new Dictionary<int, BuildingState>();
        private static readonly Dictionary<int, Batch> workers = new Dictionary<int, Batch>();
        private static readonly List<int> retiredWorkerIds = new List<int>();
        private static readonly List<int> retiredBuildingIds = new List<int>();
        private static AuthenticatedChoreChannel<WeaponProductionPacket> channel;
        private static object engine;
        private static int currentOffset, nextOffset;
        private static int* availability;
        private static bool backendReady;
        private static bool cleanupReady;
        private static IDisposable vanillaSwitch;
        internal static bool HasHudDemand => Volatile.Read(ref active).HasOptions;
        internal static bool HasTannerIngredientOverrides => Volatile.Read(ref active).HasTannerIngredientOverrides;

        private sealed class BuildingState
        {
            internal uint GlobalId;
            internal eStructs Type;
            internal WeaponProductionOption Next;
            internal Batch Current;
        }

        private sealed class Batch
        {
            internal int BuildingId, UnitId;
            internal uint BuildingGlobalId, UnitGlobalId;
            internal WeaponProductionOption Option;
            internal int Elapsed, LastTick;
            internal bool HasTick;
            internal BatchPhase Phase;
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            engine = EngineMonitor.Current;
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterAuthenticatedChore<WeaponProductionPacket>(
                "weapon-workshop/selection", Execute, new AuthenticatedChoreChannelOptions<WeaponProductionPacket>
                { IsEnabled = () => HasHudDemand, MaximumPayloadBytes = 128, ValidatePayload = packet => packet.IsValid });

            if (engine == null) throw new InvalidOperationException("Weapon workshops need the engine monitor.");
            InitializeSave();
            if (!BuildingProductionFieldLayout.TryResolve(hash, out currentOffset, out nextOffset, out string reason))
                throw new InvalidOperationException(reason);
            // The current-image D66B0 permission readers bind this six-DWORD table.
            // Its values are live map rules and may be set by a scenario.
            const int ruleTableRva = 0x3666030;
            if (context.ModuleHandle == IntPtr.Zero || context.Memory.Length < ruleTableRva + 6 * sizeof(int))
                throw new InvalidOperationException("The audited weapon availability table is outside the loaded image.");
            availability = (int*)(context.ModuleHandle + ruleTableRva);
            if (!WeaponProductionNativeRuntime.TryInitialize(context, hash, log, out reason))
                throw new InvalidOperationException(reason);
            WeaponProductionTransferNativeRuntime.Initialize(context, hash, log);
            if (!WeaponProductionTransferNativeRuntime.IsAvailable)
                throw new InvalidOperationException("Weapon-workshop transfer and checksum protection is unavailable.");
            if (!WeaponProductionHud.TryInitialize(out reason)) throw new InvalidOperationException(reason);
            vanillaSwitch = BuildingR3EventHooks.OnSwitchProductionGood.Observable.Subscribe(OnVanillaSwitch);
            backendReady = true;
            InitializeCleanup();
        }

        private static BuildingState FindState(int id, uint gid, eStructs type) =>
            buildings.TryGetValue(id, out var state) && state.GlobalId == gid && state.Type == type ? state : null;

        private static Batch FindBatch(int id, uint gid, int unitId, uint unitGid) =>
            workers.TryGetValue(unitId, out var batch) && batch.UnitGlobalId == unitGid &&
            batch.BuildingId == id && batch.BuildingGlobalId == gid ? batch : null;

        private static void UpdateDemand(int id, BuildingState state)
        {
            bool needed = state != null && (state.Next != null || state.Current != null);
            if (!needed) buildings.Remove(id);
            PublishBuilding(id, needed ? state : null);
            WeaponProductionNativeRuntime.SetBuildingDemand(id, needed);
        }

        private static bool TryBuilding(int id, uint gid, out GameBuilding* building)
        {
            building = null;
            var manager = GameBuildingManagerAPI.Instance;
            return id > 0 && id <= MaxBuildingId && gid != 0 && manager != null && manager.IsValidId(id) &&
                manager.TryGetBuildingById(id, out building) && building != null &&
                building->r_GlobalId == gid && building->r_AliveState == AliveState.IsAlive;
        }

        private static eGoods ReadGood(GameBuilding* building, int offset) => (eGoods)(*(short*)((byte*)building + offset));
        private static WeaponProductionOption ReadVanilla(GameBuilding* building, int offset)
        {
            var type = building->r_BuildingType;
            if (type == eStructs.STRUCT_TANNERS_WORKSHOP) return WeaponProductionCatalog.FindVanilla(type, eGoods.STORED_LEATHER_ARMOUR);
            if (type == eStructs.STRUCT_ARMOURERS_WORKSHOP) return WeaponProductionCatalog.FindVanilla(type, eGoods.STORED_METAL_ARMOUR);
            return WeaponProductionCatalog.FindVanilla(type, ReadGood(building, offset));
        }

        private static bool HasNativeSelection(eStructs type) => type == eStructs.STRUCT_FLETCHERS_WORKSHOP ||
            type == eStructs.STRUCT_POLETURNERS_WORKSHOP || type == eStructs.STRUCT_BLACKSMITHS_WORKSHOP;
    }
}

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
    internal static unsafe partial class WeaponProductionRuntime
    {
        private static void InitializeCleanup()
        {
            if (!backendReady || cleanupReady || registrations.Length == 0) return;
            cleanupReady = SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "weapon-workshop/retire-state",
                RetireLostActors, out _, int.MinValue);
        }

        // After a Vanilla desync repair the host keeps the row of a lost worker until the next retire
        // pass, while the clients already hold the cleared row from the transfer. Vanilla compares
        // checksums only on map ticks divisible by 200 (0xD1AD0), after this tick callback has run, so
        // the difference is never compared as long as every such tick is a retire tick.
        private const int RetireIntervalTicks = 8, VanillaChecksumIntervalTicks = 200;
        // Does not compile when the retire interval stops dividing Vanilla's checksum interval.
        private const byte RetireIntervalDividesChecksumInterval =
            VanillaChecksumIntervalTicks % RetireIntervalTicks == 0 ? 0 : 256;

        private static void RetireLostActors(int tick)
        {
            // The Extender has kill events for individual damage sources but no
            // universal worker retirement notification. Only visit custom rows.
            if (!HasHudDemand || tick % RetireIntervalTicks != 0 || (buildings.Count == 0 && workers.Count == 0)) return;
            foreach (var pair in workers)
                if (!IsBatchActorCurrent(pair.Value)) retiredWorkerIds.Add(pair.Key);
            foreach (int id in retiredWorkerIds)
            {
                workers.Remove(id);
                WorkerWire(id).Clear();
                ((ushort*)WorkerWireKindsAddress)[id] = 0;
            }
            retiredWorkerIds.Clear();

            foreach (var pair in buildings)
            {
                var state = pair.Value;
                if (!TryBuilding(pair.Key, state.GlobalId, out _) ||
                    state.Next == null && !HasCurrentBatch(state))
                    retiredBuildingIds.Add(pair.Key);
                else if (!HasCurrentBatch(state) && state.Current != null)
                {
                    state.Current = null;
                    PublishBuilding(pair.Key, state);
                }
            }
            foreach (int id in retiredBuildingIds) UpdateDemand(id, null);
            retiredBuildingIds.Clear();
        }

        private static bool HasCurrentBatch(BuildingState state) => state?.Current != null &&
            (state.Current.Phase == BatchPhase.LocalProduct || IsBatchActorCurrent(state.Current));

        private static bool IsBatchActorCurrent(Batch batch) =>
            UnitAccess.TryGetById(batch.UnitId, out GameUnit* unit, out _) && unit->r_GlobalId == batch.UnitGlobalId &&
            MatchesWorkerRole(batch.Option.BuildingType, unit->r_UnitChimp) &&
            (UnitAccess.IsReallyAlive(unit) || unit->r_AliveState == AliveState.Paused && unit->r_IsKilledByProjectile == 0);

        internal static void BeginWorld(WeaponProductionCatalog catalog)
        {
            EndWorld();
            frozen = catalog;
            PublishStoredOutputOptions(catalog);
            if (catalog.HasOptions) Marshal.WriteByte(TransferDemandAddress, 1);
        }

        internal static bool Activate(bool allowed, out string reason)
        {
            reason = null;
            if (!allowed && (buildings.Count != 0 || workers.Count != 0))
            { reason = "This mission mode does not allow the saved weapon-workshop recipes."; return false; }
            var catalog = allowed ? frozen : WeaponProductionCatalog.Empty;
            Volatile.Write(ref active, catalog);
            WeaponProductionNativeRuntime.SetDemand(catalog.HasOptions);
            return true;
        }

        internal static void EndWorld()
        {
            Volatile.Write(ref active, WeaponProductionCatalog.Empty);
            WeaponProductionNativeRuntime.SetDemand(false);
            WeaponProductionNativeRuntime.ClearBuildingDemand();
            frozen = WeaponProductionCatalog.Empty;
            ClearStoredOutputOptions();
            ClearWire();
            ResetSaveLoad();
            buildings.Clear();
            workers.Clear();
            retiredBuildingIds.Clear();
            retiredWorkerIds.Clear();
        }
    }
}

using System;
using System.Threading;
using APIShared.Goods;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageRuntime
    {
        internal static bool IsRegistrationReady(NativeStoredGood good)
        {
            var runtime = Instance;
            if (good == null || !runtime.ready) return false;
            // Catalogue preparation can read while Unity publishes another startup good.
            // Never race Dictionary resize or acquire the engine lock under RegistrationGate.
            NativeStoredGood[] snapshot = Volatile.Read(ref runtime.registeredHandles);
            for (int i = 0; i < snapshot.Length; i++)
                if (ReferenceEquals(snapshot[i], good)) return true;
            return false;
        }

        internal static bool TryPrepareRegisteredDelivery(NativeStoredGood good, out string reason)
        {
            if (!IsRegistrationReady(good)) { reason = "The registered custom output is unavailable."; return false; }
            try
            {
                // A requested startup recipe is actual demand. Prime reflection/layout and
                // the scratch-only publication aperture here, before the first native arrival.
                NativeStorageDeliveryEngine.WarmCommitPath();
                reason = string.Empty; return true;
            }
            catch (Exception error) { reason = "Custom output delivery preparation failed: " + error.Message; return false; }
        }

        // Called only by the existing qualified weapon producer owner. There is no public cargo
        // callback or external replay token; its branch decides the accepted/refused continuation.
        internal bool TryDeliver(NativeStoredGood good, int workerId, uint workerGid, int homeId, uint homeGid,
            int destinationId, uint destinationGid, GameUnit* expectedWorker, out string reason)
        {
            if (productionSave != null) { reason = "A production save checkpoint is frozen."; return false; }
            if (!IsRegistrationReady(good) || good.Kind != StoragePool.Armory)
            { reason = "The exact registered Armory output is unavailable."; return false; }
            if (!Context(true, out object world, out reason)) return false;
            var clock = MissionLifecycleService.NativeClock;
            if (clock == null || clock.Context.Mode.IsRealMultiplayer)
            { reason = "Custom production delivery is not qualified for real multiplayer repair."; return false; }
            if (!EnsureWorld(out world, out reason)) return false;
            if (!Reconcile(out reason)) return false;
            if (!TryResolveActor(workerId, workerGid, homeId, homeGid, expectedWorker, out NativeStorageDeliveryActor actor, out reason)) return false;
            if (!NativeStorageNativeRuntime.TryRead(destinationId, out NativeStorageProbe destination) ||
                !destination.IsLive || destination.GlobalId != destinationGid || destination.Kind != good.Kind)
            { reason = "The selected destination is not this exact live storage."; return false; }
            bool accepted = deliveries.TryDeliver(state, good.Key, world, destination, actor, out _, out reason);
            if (accepted) NativeStorageNativeRuntime.UpdateDemand(state.HasRows);
            return accepted;
        }

        // Authorization for the separately qualified producer's refusal/search-state writes.
        // A bad destination is precisely why a retry may be needed; it is not an authorization
        // to change an actor during blocked restoration, another world or critical saving.
        internal bool CanMutateProductionActor(NativeStoredGood good, int workerId, uint workerGid, int homeId, uint homeGid,
            GameUnit* expectedWorker, out string reason)
        {
            if (productionSave != null) { reason = "A production save checkpoint is frozen."; return false; }
            if (!IsRegistrationReady(good) || good.Kind != StoragePool.Armory)
            { reason = "The exact registered Armory output is unavailable."; return false; }
            if (!Context(true, out object world, out reason)) return false;
            var clock = MissionLifecycleService.NativeClock;
            if (clock == null || clock.Context.Mode.IsRealMultiplayer)
            { reason = "Custom production mutation is not qualified for real multiplayer repair."; return false; }
            if (!EnsureWorld(out world, out reason) ||
                !TryResolveActor(workerId, workerGid, homeId, homeGid, expectedWorker, out NativeStorageDeliveryActor actor, out reason)) return false;
            NativeStorageDeliveryEngine.WarmCommitPath();
            var target = new NativeStorageProbe(true, *(ushort*)((byte*)actor.Worker + NativeStorageDeliveryEngine.DestinationSlotOffset),
                *(uint*)((byte*)actor.Worker + NativeStorageDeliveryEngine.DestinationGlobalOffset),
                actor.Worker->r_ControllableForPlayerId, StoragePool.Armory, 0, 50);
            if (!actor.TryRead(target, out _) || !ReferenceEquals(world, MissionLifecycleService.NativeWorld))
            { reason = "The production actor or native world changed."; return false; }
            reason = string.Empty; return true;
        }

        private static bool TryResolveActor(int workerId, uint workerGid, int homeId, uint homeGid, GameUnit* expectedWorker,
            out NativeStorageDeliveryActor actor, out string reason)
        {
            actor = default;
            if (expectedWorker == null || workerId < 1 || workerId > 9999 || workerGid == 0 ||
                !UnitAccess.TryGetById(workerId, out GameUnit* worker, out _) || worker != expectedWorker || worker->r_GlobalId != workerGid)
            { reason = "The native worker slot or pointer was replaced."; return false; }
            var buildings = GameBuildingManagerAPI.Instance;
            if (homeId < 1 || homeId > 3999 || homeGid == 0 || buildings == null || !buildings.IsValidId(homeId) ||
                !buildings.TryGetBuildingById(homeId, out GameBuilding* home) || home == null || home->r_GlobalId != homeGid)
            { reason = "The native workshop was replaced."; return false; }
            actor = new NativeStorageDeliveryActor(worker, workerId, workerGid, home, homeId, homeGid);
            reason = string.Empty; return true;
        }
    }
}

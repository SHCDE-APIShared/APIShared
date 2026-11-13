using System;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageRuntime
    {
        private bool passiveCargoRecord;
        private readonly NativeStorageCargoEngine cargoEngine = new NativeStorageCargoEngine(GoodsWalletNative.Instance);
        // Bound/staged cargo history and unclassified passive records need the critical save
        // owner, even without native production demand. A provable legacy schema-1 failure
        // retains the existing manual-storage save policy; it does not imply unknown cargo.
        internal bool HasExternalCargoSaveDemand => CargoSaveCheckpointRequired(ready, blocked && passiveCargoRecord, state, staged);
        internal static bool CargoSaveCheckpointRequired(bool initialized, bool passive,
            NativeStorageState current, NativeStorageState pending) => initialized &&
            (passive || current != null && current.HasCargoHistory || pending != null && pending.HasCargoHistory);

        internal static bool CargoEntryAllowed(bool saveFrozen, int nativeAddDepth, bool criticalSaveAvailable, out string reason)
        {
            if (saveFrozen) { reason = "A critical storage/cargo save checkpoint is frozen."; return false; }
            if (nativeAddDepth != 0) { reason = "Cargo cannot change within a native add-goods chain."; return false; }
            if (!criticalSaveAvailable) { reason = "The critical native-storage/cargo save owner is unavailable."; return false; }
            reason = string.Empty; return true;
        }
        internal static bool CargoWorldAllowed(bool knownClock, bool realMultiplayer, bool exactWorld, bool passive) =>
            knownClock && !realMultiplayer && exactWorld && !passive;

        private bool CargoContext(out object world, out string reason)
        {
            world = null;
            if (!CargoEntryAllowed(productionSave != null, NativeStorageNativeRuntime.B6100Depth,
                WorkerProductionCriticalSaveRuntime.IsAvailable, out reason)) return false;
            if (!Context(true, out world, out reason) || !EnsureWorld(out world, out reason)) return false;
            var clock = MissionLifecycleService.NativeClock;
            if (!CargoWorldAllowed(clock != null && clock.Context.Mode.Kind != GameModes.GameModeKind.Unknown,
                clock?.Context.Mode.IsRealMultiplayer == true,
                clock != null && ReferenceEquals(clock.World, world) && ReferenceEquals(world, MissionLifecycleService.NativeWorld), blocked))
            { reason = "External production cargo currently supports only a confirmed single-player/editor world."; return false; }
            return true;
        }
        internal bool TryGetLastCargoSequence(string owner, out long sequence, out string reason)
        {
            sequence = 0;
            if (!CargoContext(out _, out reason)) return false;
            sequence = state.GetLastCargoSequence(owner); reason = string.Empty; return true;
        }
        internal bool TryGetCargo(string owner, long sequence, out NativeCargoSnapshot snapshot, out string reason)
        {
            snapshot = default;
            if (!CargoContext(out object world, out reason)) return false;
            if (!state.TryGetCargo(owner, sequence, out NativeStorageState.CargoLot lot))
            { reason = "This cargo creation receipt is not retained; a cached quantity cannot recreate it."; return false; }
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "The native world changed during cargo observation."; return false; }
            snapshot = new NativeCargoSnapshot(world, lot); reason = string.Empty; return true;
        }
        internal bool TryCreateCargo(string owner, long sequence, NativeStoredGood good, int player, int quantity,
            out NativeCargoSnapshot snapshot, out GoodsTransactionStatus status, out string reason)
        {
            snapshot = default; status = GoodsTransactionStatus.InvalidRequest;
            if (!CargoContext(out object world, out reason)) return false;
            if (!IsRegistrationReady(good) || !StringComparer.Ordinal.Equals(owner, good.OwnerGuid))
            { reason = "The exact registered output must belong to this owner."; return false; }
            if (!cargoEngine.TryCreate(state, owner, good.Key, world, true, sequence, player, quantity, out var lot, out status, out reason)) return false;
            snapshot = new NativeCargoSnapshot(world, lot); return true;
        }
        internal bool TryDepositCargo(string owner, NativeCargoSnapshot expected, NativeStorageSnapshot destination, int quantity,
            out NativeCargoSnapshot snapshot, out GoodsTransactionStatus status, out string reason)
        {
            snapshot = default; status = GoodsTransactionStatus.InvalidRequest;
            if (!CargoContext(out object world, out reason)) return false;
            if (!ReferenceEquals(world, expected.World) || !ReferenceEquals(world, destination.World))
            { reason = "Cargo and storage snapshots must belong to this exact native world."; return false; }
            if (!Reconcile(out reason)) return false;
            NativeStorageNativeRuntime.TryRead(destination.BuildingId, out NativeStorageProbe current);
            if (!cargoEngine.TryDeposit(state, owner, expected.Value, world, true, destination.Probe, current, quantity, out var lot, out status, out reason)) return false;
            snapshot = new NativeCargoSnapshot(world, lot); NativeStorageNativeRuntime.UpdateDemand(state.HasRows); return true;
        }
        internal bool TryDiscardCargo(string owner, NativeCargoSnapshot expected, out NativeCargoSnapshot snapshot,
            out GoodsTransactionStatus status, out string reason)
        {
            snapshot = default; status = GoodsTransactionStatus.InvalidRequest;
            if (!CargoContext(out object world, out reason)) return false;
            if (!ReferenceEquals(world, expected.World)) { reason = "The cargo snapshot belongs to another native world."; return false; }
            if (!cargoEngine.TryDiscard(state, owner, expected.Value, world, true, out var lot, out status, out reason)) return false;
            snapshot = new NativeCargoSnapshot(world, lot); return true;
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Goods;

namespace APIShared.Economy
{
    // The closed backend is trusted framework code, never a producer-supplied callback.
    internal sealed class NativeStorageCargoEngine
    {
        private readonly IGoodsWalletBackend backend;
        private static readonly object warmGate = new object();
        private static bool warmed;
        internal NativeStorageCargoEngine(IGoodsWalletBackend backend) { this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }
        private bool Context(object expectedWorld, bool offline, out string reason)
        {
            object gate = backend.EngineMonitor;
            if (!offline || !backend.IsReady || gate == null || !Monitor.IsEntered(gate) || !backend.HasAuthorityContext)
            { reason = "Cargo requires a ready, engine-protected single-player/editor world."; return false; }
            object world = backend.World;
            if (world == null || !ReferenceEquals(world, expectedWorld)) { reason = "The cargo belongs to another native world."; return false; }
            reason = string.Empty; return true;
        }
        internal bool TryCreate(NativeStorageState state, string owner, CustomGoodId good, object world, bool offline,
            long sequence, int player, int quantity, out NativeStorageState.CargoLot result, out GoodsTransactionStatus status, out string reason)
        {
            result = default; status = GoodsTransactionStatus.InvalidRequest; reason = string.Empty;
            NativeStorageState.PreparedCargoChange plan = null;
            NativeStorageState.CargoLot next;
            try
            {
                if (state == null || !StringComparer.Ordinal.Equals(owner, good.OwnerGuid)) { reason = "The output belongs to another owner."; return false; }
                if (!Context(world, offline, out reason)) return false;
                WarmCommitPath();
                status = state.PrepareCreateCargo(good, sequence, player, quantity, out plan, out next, out reason);
            }
            catch (Exception error) { plan?.Dispose(); reason = "Cargo preparation failed: " + error.Message; status = GoodsTransactionStatus.InvalidRequest; return false; }
            return Finish(world, offline, plan, next, out result, ref status, ref reason);
        }
        internal bool TryDeposit(NativeStorageState state, string owner, NativeStorageState.CargoLot expected,
            object world, bool offline, NativeStorageProbe requested, NativeStorageProbe current, int quantity,
            out NativeStorageState.CargoLot result, out GoodsTransactionStatus status, out string reason)
        {
            result = default; status = GoodsTransactionStatus.InvalidRequest; reason = string.Empty;
            NativeStorageState.PreparedCargoChange plan = null;
            NativeStorageState.CargoLot next;
            try
            {
                if (state == null || !StringComparer.Ordinal.Equals(owner, expected.Good.OwnerGuid) || !requested.IsLive)
                { reason = "A retained owner-bound lot and previously live storage snapshot are required."; return false; }
                if (!Context(world, offline, out reason)) return false;
                WarmCommitPath();
                bool live = current.IsLive && current.Slot == requested.Slot && current.GlobalId == requested.GlobalId &&
                    current.PlayerId == requested.PlayerId && current.Kind == requested.Kind;
                // Retained delivery intent uses the original identity even when its building
                // disappeared. A genuinely new application still requires this live flag.
                var destination = new NativeStorageProbe(live, requested.Slot, requested.GlobalId, requested.PlayerId,
                    requested.Kind, live ? current.NativeUsed : 0, live ? current.Capacity : requested.Capacity);
                status = state.PrepareDepositCargo(expected, destination, quantity, out plan, out next, out reason);
            }
            catch (Exception error) { plan?.Dispose(); reason = "Cargo preparation failed: " + error.Message; status = GoodsTransactionStatus.InvalidRequest; return false; }
            return Finish(world, offline, plan, next, out result, ref status, ref reason);
        }
        internal bool TryDiscard(NativeStorageState state, string owner, NativeStorageState.CargoLot expected, object world, bool offline,
            out NativeStorageState.CargoLot result, out GoodsTransactionStatus status, out string reason)
        {
            result = default; status = GoodsTransactionStatus.InvalidRequest; reason = string.Empty;
            NativeStorageState.PreparedCargoChange plan = null;
            NativeStorageState.CargoLot next;
            try
            {
                if (state == null || !StringComparer.Ordinal.Equals(owner, expected.Good.OwnerGuid)) { reason = "The lot belongs to another owner."; return false; }
                if (!Context(world, offline, out reason)) return false;
                WarmCommitPath();
                status = state.PrepareDiscardCargo(expected, out plan, out next, out reason);
            }
            catch (Exception error) { plan?.Dispose(); reason = "Cargo preparation failed: " + error.Message; status = GoodsTransactionStatus.InvalidRequest; return false; }
            return Finish(world, offline, plan, next, out result, ref status, ref reason);
        }
        private bool Finish(object world, bool offline, NativeStorageState.PreparedCargoChange plan, NativeStorageState.CargoLot next,
            out NativeStorageState.CargoLot result, ref GoodsTransactionStatus status, ref string reason)
        {
            result = default;
            if (status != GoodsTransactionStatus.AlreadyApplied && (status != GoodsTransactionStatus.Applied || plan == null))
            { plan?.Dispose(); if (status == GoodsTransactionStatus.Applied) status = GoodsTransactionStatus.InvalidRequest; return false; }
            bool admitted = false;
            try { admitted = Context(world, offline, out reason) && (plan == null || plan.IsPrepared); }
            catch (Exception error) { reason = "Cargo final admission failed: " + error.Message; }
            if (!admitted)
            { plan?.Dispose(); status = GoodsTransactionStatus.InvalidRequest; if (reason.Length == 0) reason = "The cargo publication changed before commit."; return false; }
            // No backend, producer callback or allocation follows the first published effect.
            // Unexpected invariant failures propagate rather than pretending a partial effect
            // was an ordinary refusal. Process faults/asynchronous termination are excluded.
            if (plan != null) using (plan) Publish(plan);
            result = next; reason = string.Empty; return true;
        }
        internal static void WarmCommitPath()
        {
            lock (warmGate)
            {
                if (warmed) return;
                var state = new NativeStorageState(); var good = new CustomGoodId("APIShared.Cargo.Warmup", "scratch");
                if (!state.TryRegister(good, StoragePool.Granary, out string reason)) throw new InvalidOperationException(reason);
                if (state.PrepareCreateCargo(good, 1, 1, 2, out var creation, out var lot, out reason) != GoodsTransactionStatus.Applied)
                    throw new InvalidOperationException(reason);
                using (creation) Publish(creation);
                var destination = new NativeStorageProbe(true, 1, 1, 1, StoragePool.Granary, 0, 250);
                if (state.PrepareDepositCargo(lot, destination, 1, out var deposit, out var remainder, out reason) != GoodsTransactionStatus.Applied)
                    throw new InvalidOperationException(reason);
                using (deposit) Publish(deposit);
                if (state.PrepareDiscardCargo(remainder, out var discard, out _, out reason) != GoodsTransactionStatus.Applied)
                    throw new InvalidOperationException(reason);
                using (discard) Publish(discard);
                warmed = true;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void Publish(NativeStorageState.PreparedCargoChange plan) { plan.Publish(); }
    }
}

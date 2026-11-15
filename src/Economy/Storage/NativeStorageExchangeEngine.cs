using System;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Goods;

namespace APIShared.Economy
{
    // The runtime binds/reconciles this private state under the engine monitor before entering.
    // This seam owns preparation and the callback-free payment/publication aperture, not hooks.
    internal sealed unsafe class NativeStorageExchangeEngine
    {
        private static readonly object warmSync = new object();
        private static bool warmed;
        private readonly IGoodsWalletBackend backend;

        internal NativeStorageExchangeEngine(IGoodsWalletBackend backend)
        { this.backend = backend ?? throw new ArgumentNullException(nameof(backend)); }

        private bool Context(out object world, out string reason)
        {
            world = null;
            if (!backend.IsReady) { reason = "The verified native wallet/storage boundary is unavailable."; return false; }
            object gate = backend.EngineMonitor;
            if (gate == null || !Monitor.IsEntered(gate) || !backend.HasAuthorityContext)
            { reason = "The engine monitor and a known offline/editor or native-tick/authenticated-Chore multiplayer context are required."; return false; }
            world = backend.World;
            reason = world == null ? "No confirmed native world is available." : string.Empty;
            return world != null;
        }

        internal bool TryExchange(NativeStorageState state, CustomGoodId good, NativeStorageSnapshot expected,
            NativeStorageProbe current, long sequence, int quantityDelta, int goldDelta,
            out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            reason = string.Empty;
            NativeStorageState.PreparedExchange prepared = null;
            int* cell = null;
            int target = 0;
            bool commit = false;
            try
            {
                if (state == null || !expected.Probe.IsLive)
                { reason = "A private storage state and a previously live building snapshot are required."; return false; }
                if (!Context(out object world, out reason)) return false;
                if (!ReferenceEquals(expected.World, world))
                { reason = "The storage observation belongs to another native world."; return false; }
                WarmCommitPath();

                NativeStorageProbe requested = expected.Probe;
                bool live = current.IsLive && current.Slot == requested.Slot && current.GlobalId == requested.GlobalId &&
                    current.PlayerId == requested.PlayerId && current.Kind == requested.Kind;
                // Keep the original semantic identity even after destruction/capture/reuse so the
                // ledger can recognize a retained retry. New applications still require live=true.
                var probe = new NativeStorageProbe(live, requested.Slot, requested.GlobalId, requested.PlayerId,
                    requested.Kind, live ? current.NativeUsed : 0, live ? current.Capacity : requested.Capacity);
                status = state.PrepareExchange(good, sequence, probe, quantityDelta, goldDelta, out prepared, out reason);
                if (status == GoodsTransactionStatus.AlreadyApplied)
                {
                    if (!Context(out object replayWorld, out reason) || !ReferenceEquals(world, replayWorld))
                    { status = GoodsTransactionStatus.InvalidRequest; reason = "The native world/context changed while checking the retained receipt."; return false; }
                    reason = string.Empty;
                    return true;
                }
                if (status != GoodsTransactionStatus.Applied || prepared == null)
                {
                    if (status == GoodsTransactionStatus.Applied) status = GoodsTransactionStatus.InvalidRequest;
                    return false;
                }

                // Free deliveries/consumption have no wallet dependency. Existing signed debt
                // and a temporarily unavailable player-resources pointer cannot prevent them.
                int before = 0;
                if (goldDelta != 0)
                {
                    if (!backend.TryGetGoldCell(requested.PlayerId, out cell) || cell == null)
                    { status = GoodsTransactionStatus.InvalidRequest; reason = "The checked native wallet pointer is unavailable."; return false; }
                    before = *cell;
                    long after = (long)before + goldDelta;
                    if (after < 0)
                    { status = GoodsTransactionStatus.InsufficientStock; reason = "The exchange would leave negative native gold."; return false; }
                    if (after > int.MaxValue)
                    { status = GoodsTransactionStatus.CapacityExceeded; reason = "The exchange would overflow native signed gold."; return false; }
                    target = (int)after;
                }
                if (!prepared.IsPrepared || !Context(out object finalWorld, out reason) || !ReferenceEquals(world, finalWorld) ||
                    (cell != null && *cell != before))
                { status = GoodsTransactionStatus.InvalidRequest; reason = "The prepared storage/world/wallet context changed before publication."; return false; }
                reason = string.Empty;
                commit = true;
            }
            catch (Exception error)
            {
                status = GoodsTransactionStatus.InvalidRequest;
                reason = "Storage exchange preparation failed: " + error.Message;
            }
            finally
            {
                if (!commit) prepared?.Dispose();
            }
            if (!commit) return false;
            // Publication is outside the preparation catch: an unexpected invariant violation
            // after a native write must never be reported as an ordinary rejected transaction.
            // Normal allocation/JIT/backend failures are exhausted above. Asynchronous thread
            // termination and process/native failure are outside this in-process atomic contract.
            using (prepared) PublishGoldAndStorage(cell, target, prepared);
            return true;
        }

        // Exercise the actual helper, managed publication and disposal on isolated scratch
        // state, including both the optional-wallet and no-wallet paths, before real payment.
        internal static void WarmCommitPath()
        {
            lock (warmSync)
            {
                if (warmed) return;
                var scratch = new NativeStorageState();
                var good = new CustomGoodId("APIShared.Storage.ExchangeWarmup", "scratch");
                if (!scratch.TryRegister(good, StoragePool.Armory, out string reason)) throw new InvalidOperationException(reason);
                var probe = new NativeStorageProbe(true, 1, 1, 1, StoragePool.Armory, 0, 50);
                GoodsTransactionStatus status = scratch.PrepareExchange(good, 1, probe, 1, 1, out var first, out reason);
                if (status != GoodsTransactionStatus.Applied || first == null) throw new InvalidOperationException(reason);
                int scratchGold = 0;
                using (first) PublishGoldAndStorage(&scratchGold, 1, first);
                status = scratch.PrepareExchange(good, 2, probe, -1, 0, out var second, out reason);
                if (status != GoodsTransactionStatus.Applied || second == null) throw new InvalidOperationException(reason);
                using (second) PublishGoldAndStorage(null, 0, second);
                warmed = true;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PublishGoldAndStorage(int* cell, int target, NativeStorageState.PreparedExchange prepared)
        {
            if (cell != null) *cell = target;
            prepared.Publish();
        }
    }
}

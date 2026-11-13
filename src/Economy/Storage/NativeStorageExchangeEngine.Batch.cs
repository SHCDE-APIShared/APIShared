using System;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageExchangeEngine
    {
        private static bool batchWarmed;

        // True means a definitive receipt/admission decision. A new operation's cold
        // probe is always disposed unpublished, including affordability/journal failures;
        // the runtime must reconcile before its real preparation. No native store or
        // wallet is resolved here, so retained retries survive destruction and capture.
        internal bool ProbeBatchReceipt(NativeStorageState state, NativeStorageBatch batch, long sequence,
            out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            reason = string.Empty;
            try
            {
                if (state == null || batch == null)
                { reason = "A private storage state and owned batch are required."; return true; }
                if (!Context(out object world, out reason)) return true;
                if (!ReferenceEquals(batch.World, world))
                { reason = "The storage batch belongs to another native world."; return true; }
                status = state.ProbeBatchReceipt(batch, sequence, out bool final, out reason);
                if (!Context(out object after, out string contextReason) || !ReferenceEquals(world, after))
                {
                    status = GoodsTransactionStatus.InvalidRequest;
                    reason = string.IsNullOrEmpty(contextReason) ? "The native world changed while checking the retained batch." : contextReason;
                    return true;
                }
                return final;
            }
            catch (Exception error)
            {
                status = GoodsTransactionStatus.InvalidRequest;
                reason = "Storage batch receipt preparation failed: " + error.Message;
                return true;
            }
        }

        // A runtime-owned reader is used only before the first native write. It is not
        // consumer code or a commit callback. Each distinct store is resolved once per
        // pass; canonical rows may refer to several goods in the same pool.
        internal static NativeStorageProbe[] ReadBatchProbes(NativeStorageBatch batch, Func<int, NativeStorageProbe> read)
        {
            if (batch == null || read == null) throw new ArgumentNullException(batch == null ? nameof(batch) : nameof(read));
            var result = new NativeStorageProbe[batch.Count];
            for (int i = 0; i < result.Length; i++)
            {
                int prior = -1;
                for (int j = 0; j < i; j++)
                    if (batch[j].Expected.Slot == batch[i].Expected.Slot) { prior = j; break; }
                result[i] = prior >= 0 ? result[prior] : read(batch[i].Expected.Slot);
            }
            return result;
        }

        private static bool SameProbe(NativeStorageProbe left, NativeStorageProbe right) =>
            left.IsLive && right.IsLive && left.Slot == right.Slot && left.GlobalId == right.GlobalId &&
            left.PlayerId == right.PlayerId && left.Kind == right.Kind && left.NativeUsed == right.NativeUsed && left.Capacity == right.Capacity;

        internal bool TryExchangeBatch(NativeStorageState state, NativeStorageBatch batch, NativeStorageProbe[] currentByRow,
            long sequence, Func<int, NativeStorageProbe> readFinal, out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            reason = string.Empty;
            NativeStorageState.PreparedExchange prepared = null;
            int* cell = null;
            int target = 0;
            bool commit = false;
            try
            {
                if (state == null || batch == null || currentByRow == null || currentByRow.Length != batch.Count || readFinal == null)
                { reason = "A private state, owned batch and matching native probe pass are required."; return false; }
                if (!Context(out object world, out reason)) return false;
                if (!ReferenceEquals(batch.World, world))
                { reason = "The storage batch belongs to another native world."; return false; }
                WarmBatchCommitPath();
                status = state.PrepareBatchExchange(batch, sequence, currentByRow, out prepared, out reason);
                if (status == GoodsTransactionStatus.AlreadyApplied)
                {
                    if (!Context(out object replayWorld, out reason) || !ReferenceEquals(world, replayWorld))
                    { status = GoodsTransactionStatus.InvalidRequest; reason = "The native world/context changed while checking the retained batch."; return false; }
                    reason = string.Empty;
                    return true;
                }
                if (status != GoodsTransactionStatus.Applied || prepared == null)
                {
                    if (status == GoodsTransactionStatus.Applied) status = GoodsTransactionStatus.InvalidRequest;
                    return false;
                }

                int before = 0;
                if (batch.GoldDelta != 0)
                {
                    if (!backend.TryGetGoldCell(batch.PlayerId, out cell) || cell == null)
                    { status = GoodsTransactionStatus.InvalidRequest; reason = "The checked native wallet pointer is unavailable."; return false; }
                    before = *cell;
                    long after = (long)before + batch.GoldDelta;
                    if (after < 0)
                    { status = GoodsTransactionStatus.InsufficientStock; reason = "The batch would leave negative native gold."; return false; }
                    if (after > int.MaxValue)
                    { status = GoodsTransactionStatus.CapacityExceeded; reason = "The batch would overflow native signed gold."; return false; }
                    target = (int)after;
                }
                if (!prepared.IsPrepared || !Context(out object checkedWorld, out reason) || !ReferenceEquals(world, checkedWorld))
                { status = GoodsTransactionStatus.InvalidRequest; reason = "The prepared storage/world context changed before publication."; return false; }
                // Native occupancy may have changed during preparation or checked wallet
                // resolution. Refuse the entire requested batch rather than publish a prefix.
                for (int i = 0; i < batch.Count; i++)
                {
                    bool repeated = false;
                    for (int j = 0; j < i; j++)
                        if (batch[j].Expected.Slot == batch[i].Expected.Slot) { repeated = true; break; }
                    if (!repeated && !SameProbe(currentByRow[i], readFinal(batch[i].Expected.Slot)))
                    { status = GoodsTransactionStatus.InvalidRequest; reason = "A storage identity or native occupancy changed before batch publication."; return false; }
                }
                if (!prepared.IsPrepared || !Context(out object finalWorld, out reason) || !ReferenceEquals(world, finalWorld) ||
                    (cell != null && *cell != before))
                { status = GoodsTransactionStatus.InvalidRequest; reason = "The prepared batch/world/wallet context changed before publication."; return false; }
                reason = string.Empty;
                commit = true;
            }
            catch (Exception error)
            {
                status = GoodsTransactionStatus.InvalidRequest;
                reason = "Storage batch preparation failed: " + error.Message;
            }
            finally
            {
                if (!commit) prepared?.Dispose();
            }
            if (!commit) return false;
            // Use the existing optional raw gold store followed by preallocated ledger/rows
            // publication. Rejection catches and every backend/reader call end above.
            using (prepared) PublishGoldAndStorage(cell, target, prepared);
            return true;
        }

        internal static void WarmBatchCommitPath()
        {
            lock (warmSync)
            {
                if (batchWarmed) return;
                WarmCommitPath();
                var scratch = new NativeStorageState();
                var good = new CustomGoodId("APIShared.Storage.BatchWarmup", "scratch");
                if (!scratch.TryRegister(good, StoragePool.Armory, out string reason)) throw new InvalidOperationException(reason);
                object world = new object();
                var left = new NativeStorageProbe(true, 1, 1, 1, StoragePool.Armory, 0, 50);
                var right = new NativeStorageProbe(true, 2, 2, 1, StoragePool.Armory, 0, 50);
                var probes = new[] { left, right };
                var rows = new[] { new NativeStorageBatchRow(good, left, 1), new NativeStorageBatchRow(good, right, 1) };
                if (!NativeStorageBatch.TryCreate(good.OwnerGuid, world, rows, 1, out var batch, out reason)) throw new InvalidOperationException(reason);
                var status = scratch.PrepareBatchExchange(batch, 1, probes, out var first, out reason);
                if (status != GoodsTransactionStatus.Applied || first == null) throw new InvalidOperationException(reason);
                int scratchGold = 0;
                using (first) PublishGoldAndStorage(&scratchGold, 1, first);
                rows[0] = new NativeStorageBatchRow(good, left, -1); rows[1] = new NativeStorageBatchRow(good, right, -1);
                if (!NativeStorageBatch.TryCreate(good.OwnerGuid, world, rows, 0, out batch, out reason)) throw new InvalidOperationException(reason);
                status = scratch.PrepareBatchExchange(batch, 2, probes, out var second, out reason);
                if (status != GoodsTransactionStatus.Applied || second == null) throw new InvalidOperationException(reason);
                using (second) PublishGoldAndStorage(null, 0, second);
                batchWarmed = true;
            }
        }
    }
}

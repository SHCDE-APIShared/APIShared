using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Goods;
using APIShared.Internal;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageRuntime
    {
        internal static bool BatchEntryAllowed(bool saveFrozen, int nativeAddDepth, out string reason)
        {
            if (saveFrozen) { reason = "A production save checkpoint is frozen."; return false; }
            if (nativeAddDepth != 0)
            { reason = "Storage batches cannot change capacity inside a native add-goods Pre/Original/Post chain."; return false; }
            reason = string.Empty; return true;
        }

        // Caller collections are untrusted: Count/indexers may throw or reenter the
        // framework. Read each once into bounded owned values, with no lease held.
        // The real entry supplies the immutable process registration snapshot, then
        // rechecks its native world, authority and save/add-goods boundaries afterward.
        internal static bool TryCopyBatch(string owner, object world, IReadOnlyList<NativeStorageChange> changes,
            NativeStoredGood[] registrations, int goldDelta, out NativeStorageBatch batch, out string reason)
        {
            batch = null;
            try
            {
                if (world == null || changes == null || registrations == null)
                { reason = "An exact native world, registered goods and a bounded batch are required."; return false; }
                int count = changes.Count;
                if (count < 1 || count > NativeStorageBatch.MaximumChanges)
                { reason = "A storage batch must contain one through sixty-four physical rows."; return false; }
                var rows = new NativeStorageBatchRow[count];
                for (int i = 0; i < count; i++)
                {
                    NativeStorageChange change = changes[i];
                    NativeStoredGood good = change.Good;
                    bool registeredHandle = false;
                    if (good != null)
                        for (int handle = 0; handle < registrations.Length; handle++)
                            if (ReferenceEquals(registrations[handle], good)) { registeredHandle = true; break; }
                    if (!registeredHandle || !StringComparer.Ordinal.Equals(owner, good.OwnerGuid))
                    { reason = "Every row must use this client's exact registered good handle."; return false; }
                    NativeStorageSnapshot snapshot = change.Storage;
                    if (!ReferenceEquals(snapshot.World, world) || !snapshot.Probe.IsLive || snapshot.Probe.Kind != good.Kind)
                    { reason = "Every row must observe a live matching storage pool in this exact native world."; return false; }
                    rows[i] = new NativeStorageBatchRow(good.Key, snapshot.Probe, change.QuantityDelta);
                }
                return NativeStorageBatch.TryCreate(owner, world, rows, goldDelta, out batch, out reason);
            }
            catch (Exception)
            {
                batch = null;
                reason = "The storage batch could not be copied and validated.";
                return false;
            }
        }

        internal bool TryExchangeBatch(string owner, long sequence, IReadOnlyList<NativeStorageChange> changes, int goldDelta,
            out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            NativeStorageBatch batch;
            NativeStorageProbe[] current;
            try
            {
                if (!BatchEntryAllowed(productionSave != null, NativeStorageNativeRuntime.B6100Depth, out reason) ||
                    !Context(true, out object world, out reason)) return false;
                NativeStoredGood[] handles = Volatile.Read(ref registeredHandles);
                if (!TryCopyBatch(owner, world, changes, handles, goldDelta, out batch, out reason)) return false;
                // No caller getter is invoked beyond this point. In particular, a getter
                // that synchronously loads a replacement cannot bind this old payload there.
                if (!BatchEntryAllowed(productionSave != null, NativeStorageNativeRuntime.B6100Depth, out reason) ||
                    !Context(true, out object afterCopy, out reason) || !ReferenceEquals(world, afterCopy))
                { reason = "The native world/context changed while copying the storage batch."; return false; }
                if (!EnsureWorld(out object bound, out reason) || !ReferenceEquals(world, bound)) return false;

                // Already-bound retained receipts take precedence over maintenance, which
                // can consume sequences and evict terminal operations. Pending activation
                // still follows the existing matching-world reconciliation requirement.
                if (exchanges.ProbeBatchReceipt(state, batch, sequence, out status, out reason))
                    return status == GoodsTransactionStatus.AlreadyApplied;
                if (!Reconcile(out reason)) { status = GoodsTransactionStatus.InvalidRequest; return false; }
                current = NativeStorageExchangeEngine.ReadBatchProbes(batch, readProbe);
                if (!BatchEntryAllowed(productionSave != null, NativeStorageNativeRuntime.B6100Depth, out reason) ||
                    !Context(true, out object beforePrepare, out reason) || !ReferenceEquals(world, beforePrepare) || blocked)
                { status = GoodsTransactionStatus.InvalidRequest; reason = "The native world/context changed before storage batch preparation."; return false; }
            }
            catch (Exception error)
            {
                status = GoodsTransactionStatus.InvalidRequest;
                reason = "Storage batch admission failed: " + error.Message;
                return false;
            }
            // Do not put publication under an ordinary rejection catch. The engine owns
            // the optional direct gold store and the one preallocated physical/ledger commit.
            bool applied = exchanges.TryExchangeBatch(state, batch, current, sequence, readProbe, out status, out reason);
            if (applied) NativeStorageNativeRuntime.UpdateDemand(state.HasRows);
            return applied;
        }
    }
}

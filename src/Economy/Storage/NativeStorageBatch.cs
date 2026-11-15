using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal readonly struct NativeStorageBatchRow
    {
        internal readonly CustomGoodId Good;
        internal readonly NativeStorageProbe Expected;
        internal readonly int QuantityDelta;
        internal NativeStorageBatchRow(CustomGoodId good, NativeStorageProbe expected, int quantityDelta)
        { Good = good; Expected = expected; QuantityDelta = quantityDelta; }
    }

    // Closed immutable input to the existing exchange owner. No caller array, collection,
    // native pointer or callback survives canonicalization. Content receives only owned lines.
    internal sealed class NativeStorageBatch
    {
        internal const int MaximumChanges = 64, MaximumAccounts = 32;
        private readonly NativeStorageBatchRow[] rows;
        private readonly GoodsChange[] changes;
        private readonly byte[] intent;
        internal string Owner { get; }
        internal object World { get; }
        internal int PlayerId { get; }
        internal int GoldDelta { get; }
        internal int Count => rows.Length;
        internal NativeStorageBatchRow this[int index] => rows[index];

        private NativeStorageBatch(string owner, object world, int player, int goldDelta,
            NativeStorageBatchRow[] rows, GoodsChange[] changes, byte[] intent)
        { Owner = owner; World = world; PlayerId = player; GoldDelta = goldDelta; this.rows = rows; this.changes = changes; this.intent = intent; }

        internal static bool TryCreate(string owner, object world, NativeStorageBatchRow[] proposed, int goldDelta,
            out NativeStorageBatch batch, out string reason)
        {
            batch = null;
            if (world == null || proposed == null || proposed.Length < 1 || proposed.Length > MaximumChanges)
            { reason = "A current world and 1–64 physical changes are required."; return false; }
            try { _ = new CustomGoodId(owner, "batch"); }
            catch (ArgumentException error) { reason = error.Message; return false; }
            var owned = (NativeStorageBatchRow[])proposed.Clone();
            int player = owned[0].Expected.PlayerId;
            var stores = new Dictionary<int, NativeStorageProbe>();
            var totals = new SortedDictionary<CustomGoodId, long>(GoodOrder.Instance);
            for (int i = 0; i < owned.Length; i++)
            {
                NativeStorageBatchRow row = owned[i];
                try { _ = new CustomGoodId(row.Good.OwnerGuid, row.Good.LocalId); }
                catch (ArgumentException error) { reason = error.Message; return false; }
                if (!StringComparer.Ordinal.Equals(row.Good.OwnerGuid, owner) || !row.Expected.IsLive ||
                    row.Expected.PlayerId != player || row.QuantityDelta == 0)
                { reason = "Changes require one owner/player, original live snapshots and nonzero deltas."; return false; }
                if (stores.TryGetValue(row.Expected.Slot, out NativeStorageProbe prior) && !SameIdentity(prior, row.Expected))
                { reason = "One building slot names conflicting storage identities."; return false; }
                stores[row.Expected.Slot] = row.Expected;
                totals.TryGetValue(row.Good, out long total);
                totals[row.Good] = total + row.QuantityDelta;
            }
            Array.Sort(owned, RowOrder.Instance);
            for (int i = 1; i < owned.Length; i++)
                if (RowOrder.Instance.Compare(owned[i - 1], owned[i]) == 0)
                { reason = "A physical good/store row appears more than once."; return false; }
            var normalized = new List<GoodsChange>(Math.Min(totals.Count, MaximumAccounts));
            foreach (var entry in totals)
            {
                if (entry.Value < int.MinValue || entry.Value > int.MaxValue)
                { reason = "An aggregated goods change exceeds Int32."; return false; }
                if (entry.Value != 0) normalized.Add(new GoodsChange(entry.Key, player, (int)entry.Value));
            }
            if (normalized.Count < 1 || normalized.Count > MaximumAccounts)
            { reason = "A batch needs 1–32 nonzero aggregated good/player accounts."; return false; }
            batch = new NativeStorageBatch(owner, world, player, goldDelta, owned, normalized.ToArray(), Fingerprint(owner, player, goldDelta, owned));
            reason = string.Empty; return true;
        }

        // The fixed kind/version and domain distinguish this from all existing native intents.
        // Hash all physical rows, not just ledger totals: equal balances can name different stores.
        private static byte[] Fingerprint(string owner, int player, int gold, NativeStorageBatchRow[] rows)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("APIShared.NativeStorage.Batch/v1")); writer.Write((byte)0);
                byte[] ownerBytes = Encoding.ASCII.GetBytes(owner);
                writer.Write((byte)ownerBytes.Length); writer.Write(ownerBytes);
                writer.Write((byte)player); writer.Write(gold); writer.Write((byte)rows.Length);
                foreach (NativeStorageBatchRow row in rows)
                {
                    byte[] local = Encoding.ASCII.GetBytes(row.Good.LocalId);
                    writer.Write((byte)local.Length); writer.Write(local);
                    writer.Write(row.Expected.Slot); writer.Write(row.Expected.GlobalId);
                    writer.Write((byte)row.Expected.Kind); writer.Write(row.QuantityDelta);
                }
                writer.Flush();
                using (SHA256 sha = SHA256.Create())
                {
                    byte[] digest = sha.ComputeHash(stream.ToArray());
                    var result = new byte[34]; result[0] = 5; result[1] = 1;
                    Buffer.BlockCopy(digest, 0, result, 2, digest.Length); return result;
                }
            }
        }

        internal GoodsTransactionStatus PrepareLedger(CustomGoodsInventory inventory, long sequence,
            out CustomGoodsInventory.PreparedGoodsCommit lease) =>
            inventory.ForOwner(Owner).PrepareNative(sequence, changes, intent, out lease);

        internal static bool SameIdentity(NativeStorageProbe left, NativeStorageProbe right) =>
            left.Slot == right.Slot && left.GlobalId == right.GlobalId && left.PlayerId == right.PlayerId && left.Kind == right.Kind;

        private sealed class GoodOrder : IComparer<CustomGoodId>
        {
            internal static readonly GoodOrder Instance = new GoodOrder();
            public int Compare(CustomGoodId left, CustomGoodId right)
            { int order = StringComparer.Ordinal.Compare(left.OwnerGuid, right.OwnerGuid); return order != 0 ? order : StringComparer.Ordinal.Compare(left.LocalId, right.LocalId); }
        }
        private sealed class RowOrder : IComparer<NativeStorageBatchRow>
        {
            internal static readonly RowOrder Instance = new RowOrder();
            public int Compare(NativeStorageBatchRow left, NativeStorageBatchRow right)
            {
                int order = GoodOrder.Instance.Compare(left.Good, right.Good);
                if (order == 0) order = left.Expected.Slot.CompareTo(right.Expected.Slot);
                if (order == 0) order = left.Expected.GlobalId.CompareTo(right.Expected.GlobalId);
                if (order == 0) order = left.Expected.PlayerId.CompareTo(right.Expected.PlayerId);
                if (order == 0) order = left.Expected.Kind.CompareTo(right.Expected.Kind);
                return order;
            }
        }
    }
}

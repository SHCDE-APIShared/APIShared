using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace APIShared.Goods
{
    public sealed partial class CustomGoodsInventory
    {
        /// <summary>Maximum accepted/emitted canonical snapshot size, matching one
        /// VersionedSaveData entry. This codec owns no archive/save registration.</summary>
        public const int MaximumSnapshotBytes = 1024 * 1024;

        /// <summary>Returns a private canonical byte array (schema 1 until a native-intent transaction is published; schema 2 thereafter) containing the catalog,
        /// balances, active holds and terminal replay receipts, in ordinal identity order.
        /// Capture invokes no callbacks. Wrap these bytes in existing VersionedSaveData;
        /// use SimulationRelevant for gameplay state. That agreement does not synchronize changes.</summary>
        public byte[] Capture()
        {
            lock (sync)
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(0x31494743); writer.Write(state.NativeFormat ? 2 : 1);
                writer.Write(catalog.Count);
                var indices = new Dictionary<CustomGoodId, int>();
                foreach (var entry in catalog)
                {
                    indices.Add(entry.Key, indices.Count);
                    writer.Write(entry.Key.OwnerGuid); writer.Write(entry.Key.LocalId); writer.Write(entry.Value);
                    for (int player = 1; player <= 8; player++)
                    {
                        state.Stock.TryGetValue(new Account(entry.Key, player), out Stock stock);
                        writer.Write(stock.Quantity);
                    }
                }
                var watermarks = new SortedDictionary<string, long>(state.Watermarks, StringComparer.Ordinal);
                writer.Write(watermarks.Count);
                foreach (var entry in watermarks) { writer.Write(entry.Key); writer.Write(entry.Value); }
                var ordered = new List<KeyValuePair<Operation, Receipt>>(state.Journal);
                ordered.Sort((left, right) => CompareOperation(left.Key, right.Key));
                writer.Write(ordered.Count);
                foreach (var entry in ordered)
                {
                    writer.Write(entry.Key.Owner); writer.Write(entry.Key.Id);
                    writer.Write(entry.Value.Reservation); writer.Write((byte)entry.Value.Status);
                    writer.Write(entry.Value.Changes.Length);
                    foreach (GoodsChange change in entry.Value.Changes)
                    {
                        writer.Write(indices[change.Good]); writer.Write((byte)change.PlayerId); writer.Write(change.Delta);
                    }
                    if (state.NativeFormat)
                    {
                        byte[] intent = entry.Value.Intent;
                        writer.Write(intent?.Length ?? 0);
                        if (intent != null) writer.Write(intent);
                    }
                }
                writer.Flush();
                if (stream.Length > MaximumSnapshotBytes) throw new InvalidOperationException("Inventory snapshot exceeds its bounded format.");
                return stream.ToArray();
            }
        }

        /// <summary>Atomically restores a schema-1 or schema-2 snapshot. Older readers reject schema 2; preserve such loaded bytes rather than replacing them. Register all its goods first with
        /// matching capacities; additional registered goods start empty. Rejects unknown goods,
        /// mismatched capacities, malformed/noncanonical records, overbooked holds and local bounds.
        /// Failure returns false/reason and preserves state AND existing handle validity.
        /// Success replaces stock/receipts and invalidates old handles. To resume a saved hold,
        /// Reserve its original sequence and payload again; that does not hold stock twice.
        /// The caller must reconcile outstanding native work before resuming it. No native units,
        /// buildings, gold, orders or network authority are verified by this method.</summary>
        public bool TryRestore(byte[] data, out string reason)
        {
            if (data == null || data.Length < 16 || data.Length > MaximumSnapshotBytes)
            { reason = "Invalid snapshot size."; return false; }
            byte[] owned = (byte[])data.Clone();
            lock (sync)
            {
                try
                {
                    if (nativeLeaseActive) { reason = "A native exchange is being prepared."; return false; }
                    State next = Decode(owned);
                    var nextEpoch = new object();
                    state = next; epoch = nextEpoch;
                    reason = string.Empty; return true;
                }
                catch (Exception error) when (error is InvalidDataException || error is IOException || error is ArgumentException ||
                    error is OverflowException || error is FormatException)
                { reason = "Invalid goods snapshot: " + error.Message; return false; }
            }
        }

        // The catalog table is sorted by full persisted identity, not registration order. References
        // are indices into that explicit table. Parse into a private state and recompute active holds;
        // trusting a separate serialized Reserved total would allow orphan or overbooked holds.
        private State Decode(byte[] data)
        {
            using (var stream = new MemoryStream(data, false))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                RequireSnapshot(reader.ReadInt32() == 0x31494743, "Unknown snapshot signature.");
                int schema = reader.ReadInt32();
                RequireSnapshot(schema == 1 || schema == 2, "Unknown schema.");
                int count = ReadCount(reader, maximumGoods);
                var goods = new CustomGoodId[count];
                var next = new State { NativeFormat = schema == 2 };
                for (int i = 0; i < count; i++)
                {
                    var good = new CustomGoodId(ReadId(reader, 128), ReadId(reader, 64));
                    int capacity = reader.ReadInt32();
                    RequireSnapshot(i == 0 || GoodComparer.Instance.Compare(goods[i - 1], good) < 0, "Catalog order or duplicate.");
                    RequireSnapshot(catalog.TryGetValue(good, out int registered) && registered == capacity, "Unknown good or changed capacity.");
                    goods[i] = good;
                    for (int player = 1; player <= 8; player++)
                    {
                        int quantity = reader.ReadInt32();
                        RequireSnapshot(quantity >= 0 && quantity <= capacity, "Balance exceeds capacity.");
                        if (quantity != 0) next.Stock.Add(new Account(good, player), new Stock(quantity, 0));
                    }
                }
                int watermarkCount = ReadCount(reader, maximumGoods);
                string previousOwner = null;
                var owners = new HashSet<string>(StringComparer.Ordinal);
                foreach (CustomGoodId good in goods) owners.Add(good.OwnerGuid);
                for (int i = 0; i < watermarkCount; i++)
                {
                    string owner = ReadId(reader, 128); long sequence = reader.ReadInt64();
                    RequireSnapshot(owners.Contains(owner) && sequence > 0 &&
                        (i == 0 || StringComparer.Ordinal.Compare(previousOwner, owner) < 0), "Invalid owner watermark.");
                    next.Watermarks.Add(owner, sequence); previousOwner = owner;
                }
                int operations = ReadCount(reader, maximumOperations);
                Operation previous = default;
                for (int i = 0; i < operations; i++)
                {
                    var operation = new Operation(ReadId(reader, 128), reader.ReadInt64());
                    RequireSnapshot(operation.Id > 0 && next.Watermarks.TryGetValue(operation.Owner, out long watermark) &&
                        operation.Id <= watermark, "Invalid operation sequence.");
                    RequireSnapshot(i == 0 || CompareOperation(previous, operation) < 0, "Journal order or duplicate.");
                    previous = operation;
                    byte reservationFlag = reader.ReadByte();
                    RequireSnapshot(reservationFlag <= 1, "Invalid reservation flag.");
                    bool reservation = reservationFlag == 1;
                    var status = (GoodsTransactionStatus)reader.ReadByte();
                    RequireSnapshot(reservation ? status == GoodsTransactionStatus.Reserved || status == GoodsTransactionStatus.Committed ||
                        status == GoodsTransactionStatus.Released : status == GoodsTransactionStatus.Applied, "Invalid receipt state.");
                    int lines = ReadCount(reader, 32);
                    RequireSnapshot(!reservation || lines != 0, "Empty reservation.");
                    var changes = new GoodsChange[lines];
                    Account previousAccount = default;
                    for (int line = 0; line < lines; line++)
                    {
                        int index = reader.ReadInt32(); int player = reader.ReadByte(); int delta = reader.ReadInt32();
                        RequireSnapshot(index >= 0 && index < goods.Length && player >= 1 && player <= 8 && delta != 0 &&
                            (!reservation || delta < 0), "Invalid change.");
                        var account = new Account(goods[index], player);
                        RequireSnapshot(account.Good.OwnerGuid == operation.Owner, "Foreign good.");
                        RequireSnapshot(line == 0 || AccountComparer.Instance.Compare(previousAccount, account) < 0, "Change order or duplicate.");
                        previousAccount = account;
                        changes[line] = new GoodsChange(account.Good, player, delta);
                        if (status == GoodsTransactionStatus.Reserved)
                        {
                            next.Stock.TryGetValue(account, out Stock stock);
                            long held = (long)stock.Reserved - delta;
                            RequireSnapshot(held <= stock.Quantity, "Overbooked reservation.");
                            next.Stock[account] = new Stock(stock.Quantity, (int)held);
                        }
                    }
                    byte[] intent = null;
                    if (schema == 2)
                    {
                        int length = ReadCount(reader, MaximumNativeIntentBytes);
                        if (length != 0)
                        {
                            RequireSnapshot(!reservation && status == GoodsTransactionStatus.Applied, "Invalid native receipt.");
                            intent = reader.ReadBytes(length);
                            RequireSnapshot(intent.Length == length, "Truncated native intent.");
                        }
                    }
                    next.Journal.Add(operation, new Receipt(reservation, status, changes, intent));
                }
                RequireSnapshot(stream.Position == stream.Length, "Trailing bytes.");
                return next;
            }
        }
        private static int ReadCount(BinaryReader reader, int maximum)
        { int count = reader.ReadInt32(); RequireSnapshot(count >= 0 && count <= maximum, "Count limit."); return count; }
        private static string ReadId(BinaryReader reader, int maximum)
        {
            // BinaryWriter's canonical length prefix needs one byte for 1–127 ASCII bytes,
            // exactly 80 01 for 128. Reject alternate encodings and lengths before allocating.
            int length = reader.ReadByte();
            if (length >= 128)
            {
                RequireSnapshot(length == 128 && reader.ReadByte() == 1, "Noncanonical ID length.");
                length = 128;
            }
            RequireSnapshot(length >= 1 && length <= maximum, "ID length limit.");
            byte[] bytes = reader.ReadBytes(length);
            RequireSnapshot(bytes.Length == length, "Truncated ID.");
            for (int i = 0; i < bytes.Length; i++) RequireSnapshot(bytes[i] >= 33 && bytes[i] <= 126, "Invalid ID character.");
            return Encoding.ASCII.GetString(bytes);
        }
        private static void RequireSnapshot(bool condition, string message)
        { if (!condition) throw new InvalidDataException(message); }
        private static int CompareOperation(Operation left, Operation right)
        { int order = StringComparer.Ordinal.Compare(left.Owner, right.Owner); return order != 0 ? order : left.Id.CompareTo(right.Id); }
    }
}

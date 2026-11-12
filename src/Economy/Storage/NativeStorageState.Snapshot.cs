using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed partial class NativeStorageState
    {
        private const int StoresMagic = 0x3153474E;
        internal const int MaximumSnapshotBytes = 1024 * 1024;

        internal void Capture(out byte[] goodsData, out byte[] storesData)
        {
            if (activePlan != null) throw new InvalidOperationException("Cannot capture an unpublished exchange.");
            goodsData = ledger.Capture();
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write(StoresMagic); writer.Write(1); writer.Write(catalog.Count);
                var indices = new Dictionary<CustomGoodId, int>();
                foreach (var entry in catalog)
                {
                    indices.Add(entry.Key, indices.Count);
                    writer.Write(entry.Key.OwnerGuid); writer.Write(entry.Key.LocalId); writer.Write((byte)entry.Value.Kind);
                }
                Row[] rows = published.Rows;
                writer.Write(rows.Length);
                foreach (Row row in rows)
                {
                    writer.Write(indices[row.Good]); writer.Write(row.Slot); writer.Write(row.GlobalId);
                    writer.Write((byte)row.Player); writer.Write((byte)row.Kind); writer.Write(row.Quantity);
                }
                writer.Flush();
                if (stream.Length > MaximumSnapshotBytes) throw new InvalidOperationException("Storage allocations exceed their bounded snapshot format.");
                storesData = stream.ToArray();
            }
        }

        // Both save entries are validated in a private owner before the runtime swaps a world.
        // Saved catalog entries without a current registration remain dormant and still consume
        // their original building's shared capacity. Registering their owner later reactivates them.
        internal static bool TryRestore(IReadOnlyList<NativeStorageGoodRegistration> registrations,
            byte[] goodsData, byte[] storesData, out NativeStorageState candidate, out string reason)
        {
            candidate = null; reason = string.Empty;
            if (registrations == null || registrations.Count > MaximumGoods || goodsData == null || storesData == null ||
                goodsData.Length > MaximumSnapshotBytes || storesData.Length < 16 || storesData.Length > MaximumSnapshotBytes)
            { reason = "Invalid storage snapshot size or registration count."; return false; }
            byte[] ownedGoods = (byte[])goodsData.Clone(), ownedStores = (byte[])storesData.Clone();
            try
            {
                var next = new NativeStorageState();
                Row[] rows;
                using (var stream = new MemoryStream(ownedStores, false))
                using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
                {
                    Require(reader.ReadInt32() == StoresMagic && reader.ReadInt32() == 1, "Unknown storage schema.");
                    int count = ReadCount(reader, MaximumGoods);
                    var goods = new CustomGoodId[count];
                    for (int i = 0; i < count; i++)
                    {
                        var good = new CustomGoodId(ReadId(reader, 128), ReadId(reader, 64));
                        var kind = (StoragePool)reader.ReadByte();
                        Require(ValidKind(kind) && (i == 0 || GoodOrder.Instance.Compare(goods[i - 1], good) < 0), "Invalid storage catalog order or pool.");
                        goods[i] = good;
                        Require(next.ledger.ForOwner(good.OwnerGuid).TryRegister(good.LocalId, int.MaxValue, out _, out _), "Invalid ledger registration.");
                        next.catalog.Add(good, new CatalogEntry(kind, false));
                    }
                    rows = new Row[ReadCount(reader, MaximumRows)];
                    for (int i = 0; i < rows.Length; i++)
                    {
                        int goodIndex = reader.ReadInt32(), slot = reader.ReadInt32(); uint globalId = reader.ReadUInt32();
                        int player = reader.ReadByte(); var kind = (StoragePool)reader.ReadByte(); int quantity = reader.ReadInt32();
                        Require(goodIndex >= 0 && goodIndex < goods.Length && next.catalog[goods[goodIndex]].Kind == kind,
                            "Unknown good or changed allocation pool.");
                        rows[i] = new Row(goods[goodIndex], slot, globalId, player, kind, quantity);
                    }
                    Require(stream.Position == stream.Length, "Trailing storage bytes.");
                }
                for (int i = 0; i < registrations.Count; i++)
                    Require(next.TryRegister(registrations[i].Good, registrations[i].Kind, out _), "Current and saved storage catalogs are incompatible.");
                Require(next.ledger.TryRestore(ownedGoods, out string ledgerReason), "Invalid private goods ledger: " + ledgerReason);
                next.published = BuildPublished(rows);
                next.ValidateLedgerRows();
                candidate = next; return true;
            }
            catch (Exception error) when (error is InvalidDataException || error is IOException || error is ArgumentException ||
                error is OverflowException || error is FormatException)
            { reason = "Invalid native storage snapshot: " + error.Message; return false; }
        }

        // The common no-change check only walks marked slots and returns this owner. Keep the
        // first allocation and all snapshot work in a separate cold method, after a real mismatch.
        internal bool Reconcile(Func<int, NativeStorageProbe> probe, out NativeStorageState candidate, out string reason)
        {
            candidate = this; reason = string.Empty;
            if (activePlan != null) { reason = "An exchange is prepared."; return false; }
            if (probe == null) { reason = "A native building probe is required."; return false; }
            Published before = published;
            if (before.Rows.Length == 0) return true;
            try
            {
                for (int slot = 1; slot < before.Index.Length; slot++)
                {
                    BuildingIndex stored = before.Index[slot];
                    if (stored.Quantity == 0) continue;
                    NativeStorageProbe current = probe(slot);
                    if (!current.IsLive || current.Slot != slot || !stored.Matches(current))
                        return ReconcileChanged(probe, slot, out candidate, out reason);
                }
                return true;
            }
            catch (Exception error)
            { candidate = this; reason = "Native storage reconciliation failed: " + error.Message; return false; }
        }

        private bool ReconcileChanged(Func<int, NativeStorageProbe> probe, int firstLostSlot,
            out NativeStorageState candidate, out string reason)
        {
            candidate = this; reason = string.Empty;
            Published before = published;
            var retained = new bool[BuildingSlots];
            for (int slot = 1; slot < firstLostSlot; slot++) retained[slot] = true;
            // firstLostSlot was already probed by the warm path. Do not query it again and
            // accidentally retain a different result from a stateful/throwing probe.
            for (int slot = firstLostSlot + 1; slot < BuildingSlots; slot++)
            {
                BuildingIndex stored = before.Index[slot];
                if (stored.Quantity == 0) continue;
                NativeStorageProbe current = probe(slot);
                retained[slot] = current.IsLive && current.Slot == slot && stored.Matches(current);
            }
            var remaining = new List<Row>(before.Rows.Length);
            var losses = new SortedDictionary<GoodPlayer, long>(GoodPlayerOrder.Instance);
            foreach (Row row in before.Rows)
            {
                if (retained[row.Slot]) { remaining.Add(row); continue; }
                var key = new GoodPlayer(row.Good, row.Player);
                losses.TryGetValue(key, out long quantity); losses[key] = quantity + row.Quantity;
            }
            Capture(out byte[] goods, out byte[] stores);
            if (!TryRestore(GetRegistrations(), goods, stores, out NativeStorageState next, out reason)) return false;
            next.published = BuildPublished(remaining.ToArray());
            string owner = null;
            var batch = new List<GoodsChange>(32);
            foreach (var loss in losses)
            {
                if (batch.Count != 0 && (owner != loss.Key.Good.OwnerGuid || batch.Count == 32))
                {
                    if (!next.ApplyLoss(owner, batch, out reason)) return false;
                    batch.Clear();
                }
                owner = loss.Key.Good.OwnerGuid;
                Require(loss.Value > 0 && loss.Value <= int.MaxValue, "Invalid reconciled stock loss.");
                batch.Add(new GoodsChange(loss.Key.Good, loss.Key.Player, -(int)loss.Value));
            }
            if (batch.Count != 0 && !next.ApplyLoss(owner, batch, out reason)) return false;
            next.ValidateLedgerRows();
            candidate = next; return true;
        }

        private bool ApplyLoss(string owner, List<GoodsChange> changes, out string reason)
        {
            CustomGoodsClient client = ledger.ForOwner(owner);
            long sequence = client.LastAcceptedSequence;
            if (sequence == long.MaxValue) { reason = "Storage reconciliation exhausted this owner's operation sequence."; return false; }
            GoodsTransactionStatus status = client.Apply(sequence + 1, changes.ToArray());
            if (status != GoodsTransactionStatus.Applied) { reason = "The private ledger rejected storage reconciliation: " + status; return false; }
            reason = string.Empty; return true;
        }

        private void ValidateLedgerRows()
        {
            var totals = new Dictionary<GoodPlayer, long>();
            foreach (Row row in published.Rows)
            {
                Require(catalog.TryGetValue(row.Good, out CatalogEntry entry) && entry.Kind == row.Kind, "Unknown allocation good.");
                var key = new GoodPlayer(row.Good, row.Player);
                totals.TryGetValue(key, out long prior); long total = prior + row.Quantity;
                Require(total <= int.MaxValue, "Allocated goods exceed the private ledger's bound.");
                totals[key] = total;
            }
            foreach (var entry in catalog)
            {
                CustomGoodsClient client = ledger.ForOwner(entry.Key.OwnerGuid);
                for (int player = 1; player <= 8; player++)
                {
                    totals.TryGetValue(new GoodPlayer(entry.Key, player), out long expected);
                    Require(client.TryGetBalance(entry.Key.LocalId, player, out GoodsBalance balance) && balance.Reserved == 0 && balance.Quantity == expected,
                        "The private ledger and physical allocations disagree.");
                }
            }
        }

        private static int ReadCount(BinaryReader reader, int maximum)
        { int count = reader.ReadInt32(); Require(count >= 0 && count <= maximum, "Storage record count exceeds its bound."); return count; }
        private static string ReadId(BinaryReader reader, int maximum)
        {
            int length = 0;
            for (int index = 0; index < 5; index++)
            {
                byte current = reader.ReadByte();
                Require(index != 4 || (current & 0xF0) == 0, "Invalid identity length.");
                length |= (current & 0x7F) << (7 * index);
                if ((current & 0x80) != 0) continue;
                Require((index == 0 || current != 0) && length >= 1 && length <= maximum, "Identity length exceeds its bound.");
                var characters = new char[length];
                for (int i = 0; i < characters.Length; i++)
                { byte value = reader.ReadByte(); Require(value >= '!' && value <= '~', "Invalid storage identity."); characters[i] = (char)value; }
                return new string(characters);
            }
            throw new InvalidDataException("Invalid identity length.");
        }
        private static void Require(bool condition, string reason) { if (!condition) throw new InvalidDataException(reason); }
        private readonly struct GoodPlayer : IEquatable<GoodPlayer>
        {
            internal readonly CustomGoodId Good;
            internal readonly int Player;
            internal GoodPlayer(CustomGoodId good, int player) { Good = good; Player = player; }
            public bool Equals(GoodPlayer other) => Good.Equals(other.Good) && Player == other.Player;
            public override bool Equals(object obj) => obj is GoodPlayer other && Equals(other);
            public override int GetHashCode() => unchecked(Good.GetHashCode() * 397 ^ Player);
        }
        private sealed class GoodPlayerOrder : IComparer<GoodPlayer>
        {
            internal static readonly GoodPlayerOrder Instance = new GoodPlayerOrder();
            public int Compare(GoodPlayer left, GoodPlayer right)
            { int order = GoodOrder.Instance.Compare(left.Good, right.Good); return order != 0 ? order : left.Player.CompareTo(right.Player); }
        }
    }
}

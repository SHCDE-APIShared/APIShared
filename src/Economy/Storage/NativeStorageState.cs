using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Goods;

namespace APIShared.Economy
{
    // Pure values shared with the engine adapter. They carry no native pointer or game enum.
    internal enum StoragePool { Armory = 1, Granary = 2 }

    internal readonly struct NativeStorageGoodRegistration
    {
        internal readonly CustomGoodId Good;
        internal readonly StoragePool Kind;
        internal NativeStorageGoodRegistration(CustomGoodId good, StoragePool kind) { Good = good; Kind = kind; }
    }

    internal readonly struct NativeStorageProbe
    {
        internal readonly bool Alive;
        internal readonly int Slot, PlayerId, NativeUsed, Capacity;
        internal readonly uint GlobalId;
        internal readonly StoragePool Kind;
        internal NativeStorageProbe(bool alive, int slot, uint globalId, int playerId, StoragePool kind, int nativeUsed, int capacity)
        { Alive = alive; Slot = slot; GlobalId = globalId; PlayerId = playerId; Kind = kind; NativeUsed = nativeUsed; Capacity = capacity; }
        internal bool IsLive => Alive && Slot > 0 && Slot < NativeStorageState.BuildingSlots && GlobalId != 0 &&
            PlayerId >= 1 && PlayerId <= 8 && NativeStorageState.ValidKind(Kind) && NativeUsed >= 0 &&
            Capacity == NativeStorageState.PoolCapacity(Kind);
    }

    // One private ledger and its physical allocations belong to exactly one world. The runtime
    // serializes these methods with the engine monitor; only GetOccupied is a lock-free hook read.
    // Neither the ledger nor a mutable catalog/row collection escapes this owner.
    internal sealed partial class NativeStorageState
    {
        internal const int MaximumGoods = 128, MaximumRows = 16384, BuildingSlots = 4000;
        private readonly CustomGoodsInventory ledger;
        private readonly SortedDictionary<CustomGoodId, CatalogEntry> catalog =
            new SortedDictionary<CustomGoodId, CatalogEntry>(GoodOrder.Instance);
        private volatile Published published = new Published(Array.Empty<Row>(), new BuildingIndex[BuildingSlots]);
        private PreparedExchange activePlan;

        internal NativeStorageState() : this(new CustomGoodsInventory()) { }
        private NativeStorageState(CustomGoodsInventory ledger) { this.ledger = ledger; }
        internal static bool ValidKind(StoragePool kind) => kind == StoragePool.Armory || kind == StoragePool.Granary;
        // These are the supported pools' physical limits, independently of current native use.
        // Foreign native writes may overfill a pool; valid existing virtual claims can still drain.
        internal static int PoolCapacity(StoragePool kind) => kind == StoragePool.Armory ? 50 : kind == StoragePool.Granary ? 250 : 0;
        internal bool HasRows => published.Rows.Length != 0;
        internal int RowCount => published.Rows.Length;
        internal object PublicationIdentity => published;

        internal NativeStorageGoodRegistration[] GetRegistrations(bool activeOnly = true)
        {
            int count = 0;
            foreach (CatalogEntry entry in catalog.Values) if (!activeOnly || entry.Active) count++;
            var result = new NativeStorageGoodRegistration[count];
            int index = 0;
            foreach (var entry in catalog)
                if (!activeOnly || entry.Value.Active) result[index++] = new NativeStorageGoodRegistration(entry.Key, entry.Value.Kind);
            return result;
        }

        // Called during adapter initialization, before any live native payment. This exercises
        // the actual nongeneric publication method on wholly private, disposable scratch state.
        internal static void WarmPublication()
        {
            var scratch = new NativeStorageState();
            var good = new CustomGoodId("APIShared.Storage.Warmup", "scratch");
            if (!scratch.TryRegister(good, StoragePool.Armory, out string reason)) throw new InvalidOperationException(reason);
            var probe = new NativeStorageProbe(true, 1, 1, 1, StoragePool.Armory, 0, 50);
            GoodsTransactionStatus status = scratch.PrepareExchange(good, 1, probe, 1, 0, out PreparedExchange prepared, out reason);
            if (status != GoodsTransactionStatus.Applied) throw new InvalidOperationException(reason);
            using (prepared) prepared.Publish();
        }

        internal bool TryRegister(CustomGoodId good, StoragePool kind, out string reason)
        {
            if (activePlan != null || activeCargoPlan != null) { reason = "An exchange is prepared."; return false; }
            if (!ValidKind(kind) || !ValidGood(good)) { reason = "Invalid storage good or pool."; return false; }
            if (catalog.TryGetValue(good, out CatalogEntry prior))
            {
                if (prior.Kind != kind) { reason = "The storage pool for this good changed."; return false; }
                catalog[good] = new CatalogEntry(kind, true); reason = string.Empty; return true;
            }
            if (catalog.Count >= MaximumGoods) { reason = "The storage-good catalog is full."; return false; }
            if (!ledger.ForOwner(good.OwnerGuid).TryRegister(good.LocalId, int.MaxValue, out _, out reason)) return false;
            catalog.Add(good, new CatalogEntry(kind, true)); reason = string.Empty; return true;
        }

        internal long GetLastSequence(string owner) => ledger.ForOwner(owner).LastAcceptedSequence;

        internal bool TryGetBalance(CustomGoodId good, int player, out GoodsBalance balance)
        {
            balance = default;
            return catalog.ContainsKey(good) && ledger.ForOwner(good.OwnerGuid).TryGetBalance(good.LocalId, player, out balance);
        }

        internal int GetQuantity(CustomGoodId good, NativeStorageProbe building)
        {
            Row[] rows = published.Rows;
            var key = new Row(good, building.Slot, building.GlobalId, building.PlayerId, building.Kind, 0);
            int position = Array.BinarySearch(rows, key, RowOrder.Instance);
            return position >= 0 ? rows[position].Quantity : 0;
        }

        // Observation uses the existing exact row key. It never edits another building's
        // claims, the player ledger, owner sequences or the immutable publication tables.
        internal bool TryGetQuantity(CustomGoodId good, NativeStorageProbe building, out int quantity, out string reason)
        {
            quantity = 0;
            if (!catalog.TryGetValue(good, out CatalogEntry registration) || !registration.Active)
            { reason = "The output good is not currently registered."; return false; }
            if (!building.IsLive || building.Kind != registration.Kind)
            { reason = "The live storage is not the registered good's pool."; return false; }
            quantity = GetQuantity(good, building); reason = string.Empty; return true;
        }
        // Only a marked exact identity contributes. The published 4000-slot table is immutable,
        // so dormant/unmarked calls need no dictionary, callback, allocation or native lookup.
        internal int GetOccupied(int slot, uint globalId, int player, StoragePool kind)
        {
            if (slot <= 0 || slot >= BuildingSlots) return 0;
            BuildingIndex entry = published.Index[slot];
            return entry.GlobalId == globalId && entry.Player == player && entry.Kind == kind ? entry.Quantity : 0;
        }

        internal GoodsTransactionStatus PrepareExchange(CustomGoodId good, long ownerSequence, NativeStorageProbe building,
            int quantityDelta, int goldDelta, out PreparedExchange prepared, out string reason)
            => PreparePhysicalChange(good, ownerSequence, building, quantityDelta, ExchangeIntent(building, goldDelta), out prepared, out reason);

        // A trusted native producer supplies its closed, versioned actor intent. No consumer
        // callback, arbitrary ledger or external replay handle participates in this boundary.
        internal GoodsTransactionStatus PrepareDelivery(CustomGoodId good, long ownerSequence, NativeStorageProbe building,
            byte[] canonicalIntent, out PreparedExchange prepared, out string reason)
            => PreparePhysicalChange(good, ownerSequence, building, 1, canonicalIntent, out prepared, out reason);

        private GoodsTransactionStatus PreparePhysicalChange(CustomGoodId good, long ownerSequence, NativeStorageProbe building,
            int quantityDelta, byte[] canonicalIntent, out PreparedExchange prepared, out string reason)
        {
            prepared = null; reason = string.Empty;
            if (activePlan != null || activeCargoPlan != null) return Reject(GoodsTransactionStatus.InvalidRequest, "An exchange is already prepared.", out reason);
            if (!catalog.TryGetValue(good, out CatalogEntry registration) || !registration.Active)
                return Reject(GoodsTransactionStatus.UnknownGood, "This storage good is not currently registered.", out reason);
            if (quantityDelta == 0)
                return Reject(GoodsTransactionStatus.InvalidRequest, "An exchange must change its goods quantity.", out reason);

            // The ledger decides retained retry/conflict/expiry before live affordability and
            // binding. A completed exchange remains a retry after its original building vanished.
            var changes = new[] { new GoodsChange(good, building.PlayerId, quantityDelta) };
            GoodsTransactionStatus status = ledger.ForOwner(good.OwnerGuid).PrepareNative(ownerSequence, changes, canonicalIntent,
                out CustomGoodsInventory.PreparedGoodsCommit lease);
            if (status != GoodsTransactionStatus.Applied)
            {
                lease?.Dispose();
                return status;
            }
            try
            {
                if (lease == null || !lease.IsPrepared)
                    return Reject(GoodsTransactionStatus.InvalidRequest, "The ledger did not prepare an exchange.", out reason);
                if (!building.IsLive || building.Kind != registration.Kind)
                    return Reject(GoodsTransactionStatus.InvalidRequest, "The building is not a live instance of the registered storage pool.", out reason);
                Published before = published;
                BuildingIndex occupied = before.Index[building.Slot];
                if (occupied.Quantity != 0 && !occupied.Matches(building))
                    return Reject(GoodsTransactionStatus.InvalidRequest, "The reused building slot requires reconciliation.", out reason);
                var key = new Row(good, building.Slot, building.GlobalId, building.PlayerId, building.Kind, 0);
                int position = Array.BinarySearch(before.Rows, key, RowOrder.Instance);
                int currentQuantity = position >= 0 ? before.Rows[position].Quantity : 0;
                long nextQuantity = (long)currentQuantity + quantityDelta;
                if (nextQuantity < 0)
                    return Reject(GoodsTransactionStatus.InsufficientStock, "This building does not contain enough of the good.", out reason);
                if (nextQuantity > int.MaxValue)
                    return Reject(GoodsTransactionStatus.CapacityExceeded, "The building allocation exceeds its quantity limit.", out reason);
                long nextOccupied = (long)occupied.Quantity + quantityDelta;
                if (quantityDelta > 0 && (long)building.NativeUsed + nextOccupied > building.Capacity)
                    return Reject(GoodsTransactionStatus.CapacityExceeded, "Native stock and all virtual allocations fill the storage pool.", out reason);
                int nextCount = before.Rows.Length + (position < 0 ? 1 : 0) - (nextQuantity == 0 ? 1 : 0);
                if (nextCount > MaximumRows)
                    return Reject(GoodsTransactionStatus.LimitReached, "The physical allocation table is full.", out reason);
                var nextRows = new Row[nextCount];
                int output = 0;
                for (int i = 0; i < before.Rows.Length; i++)
                {
                    if (i == position)
                    {
                        if (nextQuantity != 0) nextRows[output++] = key.WithQuantity((int)nextQuantity);
                    }
                    else nextRows[output++] = before.Rows[i];
                }
                if (position < 0) nextRows[output] = key.WithQuantity((int)nextQuantity);
                Array.Sort(nextRows, RowOrder.Instance);
                Published next = BuildPublished(nextRows);
                prepared = new PreparedExchange(this, lease, next);
                activePlan = prepared;
                lease = null;
                return GoodsTransactionStatus.Applied;
            }
            finally { lease?.Dispose(); }
        }

        internal sealed class PreparedExchange : IDisposable
        {
            private readonly NativeStorageState owner;
            private readonly Published next;
            private CustomGoodsInventory.PreparedGoodsCommit lease;
            private bool completed;
            internal PreparedExchange(NativeStorageState owner, CustomGoodsInventory.PreparedGoodsCommit lease, Published next)
            { this.owner = owner; this.lease = lease; this.next = next; }
            internal GoodsTransactionStatus Status => GoodsTransactionStatus.Applied;
            internal CustomGoodsInventory.PreparedGoodsCommit LedgerCommit => lease;
            internal bool IsPrepared => !completed && lease != null && lease.IsPrepared;

            // The runtime must do its final world/native checks before the gold write. Everything
            // needed for this publication was allocated in PrepareExchange; there is no callback.
            internal void Publish()
            {
                if (!IsPrepared) throw new InvalidOperationException("This exchange is not prepared.");
                lease.Publish();
                owner.published = next;
                owner.activePlan = null;
                completed = true;
            }
            public void Dispose()
            {
                if (ReferenceEquals(owner.activePlan, this)) owner.activePlan = null;
                CustomGoodsInventory.PreparedGoodsCommit prior = lease;
                lease = null; completed = true;
                prior?.Dispose();
            }
        }

        private static byte[] ExchangeIntent(NativeStorageProbe building, int goldDelta)
        {
            using (var stream = new MemoryStream(22))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)2); writer.Write((byte)1);
                writer.Write(building.Slot); writer.Write(building.GlobalId);
                writer.Write(building.PlayerId); writer.Write((int)building.Kind); writer.Write(goldDelta);
                return stream.ToArray();
            }
        }

        private static GoodsTransactionStatus Reject(GoodsTransactionStatus status, string detail, out string reason)
        { reason = detail; return status; }
        private static bool ValidGood(CustomGoodId good)
        {
            try { _ = new CustomGoodId(good.OwnerGuid, good.LocalId); return true; }
            catch (ArgumentException) { return false; }
        }

        private readonly struct CatalogEntry
        {
            internal readonly StoragePool Kind;
            internal readonly bool Active;
            internal CatalogEntry(StoragePool kind, bool active) { Kind = kind; Active = active; }
        }
        internal readonly struct Row
        {
            internal readonly CustomGoodId Good;
            internal readonly int Slot, Player, Quantity;
            internal readonly uint GlobalId;
            internal readonly StoragePool Kind;
            internal Row(CustomGoodId good, int slot, uint globalId, int player, StoragePool kind, int quantity)
            { Good = good; Slot = slot; GlobalId = globalId; Player = player; Kind = kind; Quantity = quantity; }
            internal Row WithQuantity(int quantity) => new Row(Good, Slot, GlobalId, Player, Kind, quantity);
        }
        internal readonly struct BuildingIndex
        {
            internal readonly uint GlobalId;
            internal readonly int Player, Quantity;
            internal readonly StoragePool Kind;
            internal BuildingIndex(uint globalId, int player, StoragePool kind, int quantity)
            { GlobalId = globalId; Player = player; Kind = kind; Quantity = quantity; }
            internal bool Matches(NativeStorageProbe probe) => GlobalId == probe.GlobalId && Player == probe.PlayerId && Kind == probe.Kind;
        }
        internal sealed class Published
        {
            internal readonly Row[] Rows;
            internal readonly BuildingIndex[] Index;
            internal Published(Row[] rows, BuildingIndex[] index) { Rows = rows; Index = index; }
        }
        private static Published BuildPublished(Row[] rows)
        {
            var index = new BuildingIndex[BuildingSlots];
            Row previous = default;
            for (int i = 0; i < rows.Length; i++)
            {
                Row row = rows[i];
                if (row.Slot <= 0 || row.Slot >= BuildingSlots || row.GlobalId == 0 || row.Player < 1 || row.Player > 8 ||
                    !ValidKind(row.Kind) || row.Quantity <= 0 || (i != 0 && RowOrder.Instance.Compare(previous, row) >= 0))
                    throw new InvalidDataException("Invalid or noncanonical storage allocation.");
                previous = row;
                BuildingIndex existing = index[row.Slot];
                if (existing.Quantity != 0 && (existing.GlobalId != row.GlobalId || existing.Player != row.Player || existing.Kind != row.Kind))
                    throw new InvalidDataException("A storage slot contains several building identities.");
                long quantity = (long)existing.Quantity + row.Quantity;
                if (quantity > PoolCapacity(row.Kind))
                    throw new InvalidDataException("Virtual allocations exceed the fixed storage-pool capacity.");
                index[row.Slot] = new BuildingIndex(row.GlobalId, row.Player, row.Kind, (int)quantity);
            }
            return new Published(rows, index);
        }
        private sealed class GoodOrder : IComparer<CustomGoodId>
        {
            internal static readonly GoodOrder Instance = new GoodOrder();
            public int Compare(CustomGoodId left, CustomGoodId right)
            { int order = StringComparer.Ordinal.Compare(left.OwnerGuid, right.OwnerGuid); return order != 0 ? order : StringComparer.Ordinal.Compare(left.LocalId, right.LocalId); }
        }
        private sealed class RowOrder : IComparer<Row>
        {
            internal static readonly RowOrder Instance = new RowOrder();
            public int Compare(Row left, Row right)
            {
                int order = GoodOrder.Instance.Compare(left.Good, right.Good);
                if (order == 0) order = left.Slot.CompareTo(right.Slot);
                if (order == 0) order = left.GlobalId.CompareTo(right.GlobalId);
                if (order == 0) order = left.Player.CompareTo(right.Player);
                if (order == 0) order = left.Kind.CompareTo(right.Kind);
                return order;
            }
        }
    }
}

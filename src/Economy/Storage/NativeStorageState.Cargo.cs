using System;
using System.Collections.Generic;
using System.IO;
using APIShared.Goods;

namespace APIShared.Economy
{
    internal sealed partial class NativeStorageState
    {
        internal const int MaximumCargoRecords = 1024;
        private CargoPublished cargo = CargoPublished.Empty;
        private PreparedCargoChange activeCargoPlan;
        internal bool HasCargoHistory => cargo.Used;
        internal object CargoPublicationIdentity => cargo;
        internal int StoresSchema => cargo.Used ? 2 : 1;
        internal int CargoRecordCount => cargo.Lots.Count;

        internal readonly struct CargoKey
        {
            internal readonly string Owner;
            internal readonly long Sequence;
            internal CargoKey(string owner, long sequence) { Owner = owner; Sequence = sequence; }
        }
        private sealed class CargoKeyOrder : IComparer<CargoKey>
        {
            internal static readonly CargoKeyOrder Instance = new CargoKeyOrder();
            public int Compare(CargoKey left, CargoKey right)
            { int order = StringComparer.Ordinal.Compare(left.Owner, right.Owner); return order != 0 ? order : left.Sequence.CompareTo(right.Sequence); }
        }
        internal enum CargoReceiptKind : byte { None, Deposit, Discard }
        internal readonly struct CargoReceipt
        {
            internal readonly CargoReceiptKind Kind;
            internal readonly int Before, Quantity;
            internal readonly NativeStorageProbe Destination;
            internal CargoReceipt(CargoReceiptKind kind, int before, int quantity, NativeStorageProbe destination)
            { Kind = kind; Before = before; Quantity = quantity; Destination = destination; }
            internal bool Matches(CargoReceiptKind kind, int before, int quantity, NativeStorageProbe destination) =>
                Kind == kind && Before == before && Quantity == quantity && Destination.Slot == destination.Slot &&
                Destination.GlobalId == destination.GlobalId && Destination.PlayerId == destination.PlayerId && Destination.Kind == destination.Kind;
        }
        internal readonly struct CargoLot
        {
            internal readonly CustomGoodId Good;
            internal readonly long Sequence;
            internal readonly int Player, Issued, Remaining;
            internal readonly StoragePool Kind;
            internal readonly CargoReceipt Receipt;
            internal CargoLot(CustomGoodId good, long sequence, int player, StoragePool kind, int issued, int remaining, CargoReceipt receipt)
            { Good = good; Sequence = sequence; Player = player; Kind = kind; Issued = issued; Remaining = remaining; Receipt = receipt; }
            internal CargoLot WithRemaining(int remaining, CargoReceipt receipt) => new CargoLot(Good, Sequence, Player, Kind, Issued, remaining, receipt);
            internal bool SameIdentity(CargoLot other) => Good.Equals(other.Good) && Sequence == other.Sequence &&
                Player == other.Player && Kind == other.Kind && Issued == other.Issued;
        }
        internal sealed class CargoPublished
        {
            internal static readonly CargoPublished Empty = new CargoPublished(false,
                new SortedDictionary<CargoKey, CargoLot>(CargoKeyOrder.Instance), new SortedDictionary<string, long>(StringComparer.Ordinal));
            internal readonly bool Used;
            internal readonly SortedDictionary<CargoKey, CargoLot> Lots;
            internal readonly SortedDictionary<string, long> Watermarks;
            internal CargoPublished(bool used, SortedDictionary<CargoKey, CargoLot> lots, SortedDictionary<string, long> watermarks)
            { Used = used; Lots = lots; Watermarks = watermarks; }
        }
        private CargoPublished CopyCargo() => new CargoPublished(true,
            new SortedDictionary<CargoKey, CargoLot>(cargo.Lots, CargoKeyOrder.Instance),
            new SortedDictionary<string, long>(cargo.Watermarks, StringComparer.Ordinal));
        internal long GetLastCargoSequence(string owner) => owner != null && cargo.Watermarks.TryGetValue(owner, out long sequence) ? sequence : 0;
        internal bool TryGetCargo(string owner, long sequence, out CargoLot lot)
        {
            lot = default;
            return !string.IsNullOrEmpty(owner) && sequence > 0 && cargo.Lots.TryGetValue(new CargoKey(owner, sequence), out lot);
        }
        private bool CargoGoodActive(CustomGoodId good, out StoragePool kind)
        { bool found = catalog.TryGetValue(good, out CatalogEntry entry) && entry.Active; kind = entry.Kind; return found; }
        private bool CargoOwnerKnown(string owner)
        { foreach (CustomGoodId good in catalog.Keys) if (StringComparer.Ordinal.Equals(owner, good.OwnerGuid)) return true; return false; }
        private bool CargoCanPrepare(out string reason)
        { reason = activePlan != null || activeCargoPlan != null ? "A storage/cargo publication is already prepared." : string.Empty; return reason.Length == 0; }

        internal GoodsTransactionStatus PrepareCreateCargo(CustomGoodId good, long sequence, int player, int quantity,
            out PreparedCargoChange prepared, out CargoLot result, out string reason)
        {
            prepared = null; result = default;
            if (!CargoCanPrepare(out reason)) return GoodsTransactionStatus.InvalidRequest;
            if (!ValidGood(good) || sequence <= 0 || player < 1 || player > 8 || quantity <= 0)
                return Reject(GoodsTransactionStatus.InvalidRequest, "A valid good, positive creation sequence/quantity and player 1–8 are required.", out reason);
            if (!CargoGoodActive(good, out StoragePool kind)) return Reject(GoodsTransactionStatus.UnknownGood, "The output good is not currently registered.", out reason);
            var key = new CargoKey(good.OwnerGuid, sequence);
            if (cargo.Lots.TryGetValue(key, out CargoLot prior))
            {
                if (!prior.Good.Equals(good) || prior.Player != player || prior.Issued != quantity)
                    return Reject(GoodsTransactionStatus.Conflict, "This cargo creation sequence names another production lot.", out reason);
                result = prior; reason = string.Empty; return GoodsTransactionStatus.AlreadyApplied;
            }
            if (sequence <= GetLastCargoSequence(good.OwnerGuid))
                return Reject(GoodsTransactionStatus.ExpiredOperation, "The cargo creation receipt expired or its lower sequence gap was closed.", out reason);
            CargoPublished next = CopyCargo();
            if (next.Lots.Count == MaximumCargoRecords)
            {
                // Deterministic eviction of the lowest creation sequence, then ordinal owner.
                // Active cargo is never evicted, even when its sequence is very old.
                CargoKey? victim = null;
                foreach (var item in next.Lots)
                    if (item.Value.Remaining == 0 && (!victim.HasValue || item.Key.Sequence < victim.Value.Sequence ||
                        item.Key.Sequence == victim.Value.Sequence && StringComparer.Ordinal.Compare(item.Key.Owner, victim.Value.Owner) < 0)) victim = item.Key;
                if (!victim.HasValue) return Reject(GoodsTransactionStatus.LimitReached, "All bounded cargo slots contain active quantity.", out reason);
                next.Lots.Remove(victim.Value);
            }
            if (!next.Watermarks.ContainsKey(good.OwnerGuid) && next.Watermarks.Count >= MaximumGoods)
                return Reject(GoodsTransactionStatus.LimitReached, "The cargo owner watermark table is full.", out reason);
            result = new CargoLot(good, sequence, player, kind, quantity, quantity, default);
            next.Lots.Add(key, result); next.Watermarks[good.OwnerGuid] = sequence;
            prepared = new PreparedCargoChange(this, cargo, next, null); activeCargoPlan = prepared;
            reason = string.Empty; return GoodsTransactionStatus.Applied;
        }

        private GoodsTransactionStatus CheckCargoCheckpoint(CargoLot expected, CargoReceiptKind kind, int quantity,
            NativeStorageProbe destination, out CargoLot current, out string reason)
        {
            current = default;
            if (!ValidGood(expected.Good) || expected.Sequence <= 0 || expected.Remaining <= 0 || quantity <= 0 || quantity > expected.Remaining)
                return Reject(GoodsTransactionStatus.InvalidRequest, "A positive retained cargo checkpoint and bounded quantity are required.", out reason);
            if (!TryGetCargo(expected.Good.OwnerGuid, expected.Sequence, out current))
                return Reject(GoodsTransactionStatus.ExpiredOperation, "This cargo creation receipt is not retained.", out reason);
            if (!current.SameIdentity(expected)) return Reject(GoodsTransactionStatus.Conflict, "The cargo identity differs.", out reason);
            if (!CargoGoodActive(current.Good, out _)) return Reject(GoodsTransactionStatus.UnknownGood, "The saved cargo owner/output is dormant.", out reason);
            if (current.Receipt.Kind != CargoReceiptKind.None && current.Receipt.Before == expected.Remaining)
            {
                if (current.Receipt.Matches(kind, expected.Remaining, quantity, destination)) { reason = string.Empty; return GoodsTransactionStatus.AlreadyApplied; }
                return Reject(GoodsTransactionStatus.Conflict, "This cargo checkpoint already accepted a different delivery or discard.", out reason);
            }
            if (current.Remaining != expected.Remaining)
                return Reject(expected.Remaining > current.Remaining ? GoodsTransactionStatus.ExpiredOperation : GoodsTransactionStatus.InvalidRequest,
                    "The cargo checkpoint is stale, unretained or ahead of current quantity.", out reason);
            reason = string.Empty; return GoodsTransactionStatus.Applied;
        }
        internal GoodsTransactionStatus PrepareDepositCargo(CargoLot expected, NativeStorageProbe destination, int quantity,
            out PreparedCargoChange prepared, out CargoLot result, out string reason)
        {
            prepared = null; result = default;
            if (!CargoCanPrepare(out reason)) return GoodsTransactionStatus.InvalidRequest;
            GoodsTransactionStatus status = CheckCargoCheckpoint(expected, CargoReceiptKind.Deposit, quantity, destination, out CargoLot current, out reason);
            if (status == GoodsTransactionStatus.AlreadyApplied) { result = current; return status; }
            if (status != GoodsTransactionStatus.Applied) return status;
            if (!destination.IsLive || destination.Kind != current.Kind || destination.PlayerId != current.Player)
                return Reject(GoodsTransactionStatus.InvalidRequest, "The live storage pool/player does not match this production lot.", out reason);
            long last = GetLastSequence(current.Good.OwnerGuid);
            if (last == long.MaxValue) return Reject(GoodsTransactionStatus.LimitReached, "The owner's storage command sequence is exhausted.", out reason);
            CargoPublished next = CopyCargo();
            result = current.WithRemaining(current.Remaining - quantity,
                new CargoReceipt(CargoReceiptKind.Deposit, current.Remaining, quantity, destination));
            next.Lots[new CargoKey(current.Good.OwnerGuid, current.Sequence)] = result;
            byte[] intent = CargoIntent(current, destination, quantity);
            status = PreparePhysicalChange(current.Good, last + 1, destination, quantity, intent, out PreparedExchange physical, out reason);
            if (status != GoodsTransactionStatus.Applied || physical == null) { physical?.Dispose(); result = default; return status; }
            try { prepared = new PreparedCargoChange(this, cargo, next, physical); activeCargoPlan = prepared; physical = null; }
            finally { physical?.Dispose(); }
            reason = string.Empty; return GoodsTransactionStatus.Applied;
        }
        internal GoodsTransactionStatus PrepareDiscardCargo(CargoLot expected, out PreparedCargoChange prepared, out CargoLot result, out string reason)
        {
            prepared = null; result = default;
            if (!CargoCanPrepare(out reason)) return GoodsTransactionStatus.InvalidRequest;
            GoodsTransactionStatus status = CheckCargoCheckpoint(expected, CargoReceiptKind.Discard, expected.Remaining, default, out CargoLot current, out reason);
            if (status == GoodsTransactionStatus.AlreadyApplied) { result = current; return status; }
            if (status != GoodsTransactionStatus.Applied) return status;
            CargoPublished next = CopyCargo();
            result = current.WithRemaining(0, new CargoReceipt(CargoReceiptKind.Discard, current.Remaining, current.Remaining, default));
            next.Lots[new CargoKey(current.Good.OwnerGuid, current.Sequence)] = result;
            prepared = new PreparedCargoChange(this, cargo, next, null); activeCargoPlan = prepared;
            reason = string.Empty; return GoodsTransactionStatus.Applied;
        }
        internal sealed class PreparedCargoChange : IDisposable
        {
            private readonly NativeStorageState owner;
            private readonly CargoPublished before, next;
            private PreparedExchange physical;
            private bool completed;
            internal PreparedCargoChange(NativeStorageState owner, CargoPublished before, CargoPublished next, PreparedExchange physical)
            { this.owner = owner; this.before = before; this.next = next; this.physical = physical; }
            internal bool IsPrepared => !completed && ReferenceEquals(owner.activeCargoPlan, this) &&
                ReferenceEquals(owner.cargo, before) && (physical == null || physical.IsPrepared);
            internal void Publish()
            {
                if (!IsPrepared) throw new InvalidOperationException("The cargo publication is no longer prepared.");
                physical?.Publish();
                owner.cargo = next; owner.activeCargoPlan = null; completed = true;
            }
            public void Dispose()
            {
                if (ReferenceEquals(owner.activeCargoPlan, this)) owner.activeCargoPlan = null;
                PreparedExchange prior = physical; physical = null; completed = true; prior?.Dispose();
            }
        }
        private static byte[] CargoIntent(CargoLot lot, NativeStorageProbe destination, int quantity)
        {
            using (var stream = new MemoryStream(40))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)4); writer.Write((byte)1); writer.Write(lot.Sequence); writer.Write(lot.Remaining);
                writer.Write(quantity); writer.Write(destination.Slot); writer.Write(destination.GlobalId);
                writer.Write(destination.PlayerId); writer.Write((int)destination.Kind);
                return stream.ToArray();
            }
        }

        // Snapshot.cs calls these only in stores schema 2, after its existing physical rows.
        // 1024 fixed-size records plus at most 128 bounded owner IDs add under 64 KiB. Together
        // with the existing maximum catalogue/rows this remains below the 1 MiB stores bound.
        private void WriteCargo(BinaryWriter writer, Dictionary<CustomGoodId, int> goodIndices)
        {
            writer.Write(cargo.Watermarks.Count);
            foreach (var watermark in cargo.Watermarks) { writer.Write(watermark.Key); writer.Write(watermark.Value); }
            writer.Write(cargo.Lots.Count);
            foreach (CargoLot lot in cargo.Lots.Values)
            {
                writer.Write(goodIndices[lot.Good]); writer.Write(lot.Sequence); writer.Write((byte)lot.Player);
                writer.Write(lot.Issued); writer.Write(lot.Remaining); writer.Write((byte)lot.Receipt.Kind);
                writer.Write(lot.Receipt.Before); writer.Write(lot.Receipt.Quantity);
                writer.Write(lot.Receipt.Destination.Slot); writer.Write(lot.Receipt.Destination.GlobalId);
                writer.Write((byte)lot.Receipt.Destination.PlayerId); writer.Write((byte)lot.Receipt.Destination.Kind);
            }
        }
        private void ReadCargo(BinaryReader reader, CustomGoodId[] goods)
        {
            var watermarks = new SortedDictionary<string, long>(StringComparer.Ordinal);
            string priorOwner = null;
            int owners = ReadCount(reader, MaximumGoods);
            Require(owners > 0, "Cargo schema 2 must retain its creation high-watermarks.");
            for (int i = 0; i < owners; i++)
            {
                string owner = ReadId(reader, 128); long sequence = reader.ReadInt64();
                Require(sequence > 0 && CargoOwnerKnown(owner) && (i == 0 || StringComparer.Ordinal.Compare(priorOwner, owner) < 0), "Invalid cargo watermark.");
                watermarks.Add(owner, sequence); priorOwner = owner;
            }
            var lots = new SortedDictionary<CargoKey, CargoLot>(CargoKeyOrder.Instance);
            CargoKey previous = default;
            int count = ReadCount(reader, MaximumCargoRecords);
            Require(count > 0, "Cargo schema 2 must retain at least one lot receipt.");
            for (int i = 0; i < count; i++)
            {
                int index = reader.ReadInt32(); long sequence = reader.ReadInt64(); int player = reader.ReadByte();
                int issued = reader.ReadInt32(), remaining = reader.ReadInt32(); var receiptKind = (CargoReceiptKind)reader.ReadByte();
                int before = reader.ReadInt32(), quantity = reader.ReadInt32();
                int slot = reader.ReadInt32(); uint global = reader.ReadUInt32(); int destinationPlayer = reader.ReadByte(); var pool = (StoragePool)reader.ReadByte();
                Require(index >= 0 && index < goods.Length, "Unknown cargo good.");
                CustomGoodId good = goods[index]; StoragePool kind = catalog[good].Kind;
                var key = new CargoKey(good.OwnerGuid, sequence);
                Require(sequence > 0 && watermarks.TryGetValue(good.OwnerGuid, out long high) && sequence <= high &&
                    player >= 1 && player <= 8 && issued > 0 && remaining >= 0 && remaining <= issued &&
                    (i == 0 || CargoKeyOrder.Instance.Compare(previous, key) < 0), "Invalid cargo identity, quantity or order.");
                previous = key;
                bool zeroDestination = slot == 0 && global == 0 && destinationPlayer == 0 && (int)pool == 0;
                if (receiptKind == CargoReceiptKind.None)
                    Require(remaining == issued && before == 0 && quantity == 0 && zeroDestination, "Invalid untouched cargo receipt.");
                else
                {
                    Require(before > remaining && before <= issued && quantity > 0 && before - quantity == remaining, "Invalid cargo checkpoint arithmetic.");
                    if (receiptKind == CargoReceiptKind.Discard)
                        Require(remaining == 0 && zeroDestination, "Invalid discarded cargo receipt.");
                    else
                        Require(receiptKind == CargoReceiptKind.Deposit && slot > 0 && slot < BuildingSlots && global != 0 &&
                            destinationPlayer == player && pool == kind, "Invalid cargo delivery receipt.");
                }
                var destination = new NativeStorageProbe(false, slot, global, destinationPlayer, pool, 0, 0);
                lots.Add(key, new CargoLot(good, sequence, player, kind, issued, remaining, new CargoReceipt(receiptKind, before, quantity, destination)));
            }
            cargo = new CargoPublished(true, lots, watermarks);
        }
    }
}

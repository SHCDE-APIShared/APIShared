using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    // Actual payment history, independent of the next settings snapshot. No native lookups belong here.
    internal sealed class HumanConstructionLedger
    {
        internal const int MaximumReceipts = 32768;
        internal const int ChunkSize = 1000;
        private readonly Dictionary<ConstructionObjectIdentity, Receipt> records = new Dictionary<ConstructionObjectIdentity, Receipt>();
        internal int Count => records.Count;
        internal bool Contains(ConstructionObjectIdentity identity) => records.ContainsKey(identity);
        internal bool CanRecord(ConstructionObjectIdentity identity) => identity.IsValid && records.Count < MaximumReceipts && !records.ContainsKey(identity);
        internal void Add(ConstructionObjectIdentity identity, int payer, IReadOnlyDictionary<eGoods, int> paid)
        {
            if (!CanRecord(identity) || payer <= 0) throw new InvalidOperationException("Construction payment identity cannot be recorded.");
            records.Add(identity, new Receipt(identity, payer, HumanConstructionGoods.Snapshot(paid)));
        }
        internal void Retire(ConstructionObjectIdentity identity) => records.Remove(identity);
        internal void Clear() => records.Clear();
        internal void Prune(Func<ConstructionObjectIdentity, int?> currentOwner)
        {
            var identities = new List<ConstructionObjectIdentity>(records.Keys);
            foreach (var identity in identities)
            {
                if (!records.TryGetValue(identity, out Receipt receipt)) continue;
                int? owner = currentOwner(identity);
                if (!owner.HasValue || owner.Value != receipt.Payer) records.Remove(identity);
            }
        }
        internal bool TryConsume(ConstructionObjectIdentity identity, int actor, int currentOwner, bool isHuman,
            Func<eGoods, int, int> quoteAmount, out IReadOnlyDictionary<eGoods, int> refund)
        {
            if (!TryQuote(identity, actor, currentOwner, isHuman, quoteAmount, out refund)) return false;
            records.Remove(identity);
            return true;
        }
        internal bool TryQuote(ConstructionObjectIdentity identity, int actor, int currentOwner, bool isHuman,
            Func<eGoods, int, int> quoteAmount, out IReadOnlyDictionary<eGoods, int> refund)
        {
            refund = HumanConstructionGoods.Empty;
            if (!records.TryGetValue(identity, out Receipt record)) return false;
            // An ownership transfer permanently retires the old payer's claim, even if transferred back later.
            if (currentOwner != record.Payer) { records.Remove(identity); return false; }
            if (!isHuman || actor != currentOwner || quoteAmount == null) return false;
            var result = new Dictionary<eGoods, int>();
            foreach (var item in record.Paid)
            {
                // The adapter owns its native arithmetic order; a dimensionless double factor
                // cannot preserve both SE float-material and native integer-unit refunds.
                int amount = quoteAmount(item.Key, item.Value);
                if (amount < Math.Min(0, item.Value) || amount > Math.Max(0, item.Value))
                    throw new ArgumentOutOfRangeException(nameof(quoteAmount), "Refund amounts must preserve sign and cannot exceed the actual payment.");
                if (amount != 0) result[item.Key] = amount;
            }
            refund = new ReadOnlyDictionary<eGoods, int>(result);
            return true;
        }
        internal IReadOnlyList<byte[]> Encode()
        {
            var sorted = new List<Receipt>(records.Values);
            sorted.Sort((a, b) => { int n = a.Identity.Kind.CompareTo(b.Identity.Kind); if (n == 0) n = a.Identity.Slot.CompareTo(b.Identity.Slot); return n != 0 ? n : a.Identity.GlobalId.CompareTo(b.Identity.GlobalId); });
            var chunks = new List<byte[]>();
            for (int start = 0; start < sorted.Count; start += ChunkSize)
            {
                using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
                {
                    int count = Math.Min(ChunkSize, sorted.Count - start); writer.Write(count);
                    for (int i = start; i < start + count; i++)
                    {
                        Receipt receipt = sorted[i]; writer.Write((byte)receipt.Identity.Kind); writer.Write(receipt.Identity.Slot);
                        writer.Write(receipt.Identity.GlobalId); writer.Write(receipt.Payer); writer.Write(receipt.Paid.Count);
                        // Stable goods ordering makes multiplayer save comparison independent of dictionary insertion.
                        foreach (eGoods good in HumanConstructionGoods.All) if (receipt.Paid.TryGetValue(good, out int amount))
                        { writer.Write((int)good); writer.Write(amount); }
                    }
                    writer.Flush(); chunks.Add(stream.ToArray());
                }
            }
            return chunks;
        }
        internal void Restore(IEnumerable<byte[]> chunks)
        {
            // Parse into a separate candidate: a malformed tail must never leave a partial refund ledger.
            var candidate = new HumanConstructionLedger();
            foreach (byte[] bytes in chunks)
            {
                if (bytes == null || bytes.Length > 1024 * 1024) throw new InvalidDataException("Invalid payment chunk.");
                using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
                {
                    int count = reader.ReadInt32(); if (count < 0 || count > ChunkSize) throw new InvalidDataException("Invalid payment count.");
                    for (int i = 0; i < count; i++)
                    {
                        var identity = new ConstructionObjectIdentity((ConstructionObjectKind)reader.ReadByte(), reader.ReadInt32(), reader.ReadUInt32());
                        int payer = reader.ReadInt32(), goods = reader.ReadInt32();
                        if (goods < 1 || goods > HumanConstructionGoods.All.Length) throw new InvalidDataException("Invalid payment goods.");
                        var paid = new Dictionary<eGoods, int>();
                        for (int j = 0; j < goods; j++)
                        {
                            eGoods good = (eGoods)reader.ReadInt32(); int amount = reader.ReadInt32();
                            if (amount == 0 || !HumanConstructionGoods.IsValidAmount(good, amount) || paid.ContainsKey(good)) throw new InvalidDataException("Invalid payment amount.");
                            paid.Add(good, amount);
                        }
                        candidate.Add(identity, payer, paid);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException("Unexpected payment bytes.");
                }
            }
            records.Clear(); foreach (var item in candidate.records) records.Add(item.Key, item.Value);
        }
        private sealed class Receipt
        {
            internal Receipt(ConstructionObjectIdentity identity, int payer, IReadOnlyDictionary<eGoods, int> paid) { Identity = identity; Payer = payer; Paid = paid; }
            internal readonly ConstructionObjectIdentity Identity;
            internal readonly int Payer;
            internal readonly IReadOnlyDictionary<eGoods, int> Paid;
        }
    }
}

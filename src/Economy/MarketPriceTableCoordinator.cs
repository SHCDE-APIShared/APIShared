using System;
using System.IO;

namespace APIShared.Economy
{
    internal readonly struct MarketPriceResolution
    {
        internal readonly int Price;
        internal readonly bool Own;
        internal MarketPriceResolution(int price, bool own) { Price = price; Own = own; }
    }

    internal readonly struct MarketPriceSavedCell
    {
        internal readonly int Baseline, Foreign, Applied;
        internal readonly bool HasForeign, Owned;
        internal MarketPriceSavedCell(int baseline, int foreign, int applied, bool hasForeign, bool owned)
        { Baseline = baseline; Foreign = foreign; Applied = applied; HasForeign = hasForeign; Owned = owned; }
    }

    // Independent from the game bridge so ownership, resaving and failed setters use the
    // same production algorithm in deterministic tests. No periodic observation occurs.
    internal sealed class MarketPriceTableCoordinator
    {
        private sealed class Cell
        {
            internal int Baseline, Foreign, Applied, AI;
            internal bool HasForeign, Owned;
        }
        private readonly int goods;
        private readonly Func<int, bool, int> read, defaults;
        private readonly Action<int, bool, int> write;
        private readonly Cell[] cells;
        internal bool HasContextualPrices { get; private set; }
        internal MarketPriceTableCoordinator(int goods, Func<int, bool, int> read,
            Action<int, bool, int> write, Func<int, bool, int> defaults)
        {
            this.goods = goods; this.read = read; this.write = write; this.defaults = defaults;
            cells = new Cell[goods * 2];
        }
        private static int Index(int good, bool buying) => good * 2 + (buying ? 0 : 1);
        internal void Begin(MarketPriceSavedCell[] saved)
        {
            HasContextualPrices = false;
            for (int good = 0; good < goods; good++) for (int side = 0; side < 2; side++)
            {
                bool buying = side == 0; int index = Index(good, buying);
                int current = read(good, buying), baseline = defaults(good, buying);
                var cell = new Cell { Baseline = baseline, Applied = current, AI = current };
                if (saved != null)
                {
                    var old = saved[index];
                    // A known saved output or the native default/reset is not a new foreign write.
                    if (current == old.Applied || current == baseline)
                    {
                        cell.Baseline = old.Baseline; cell.HasForeign = old.HasForeign; cell.Foreign = old.Foreign;
                        cell.Owned = old.Owned && current == old.Applied;
                    }
                    else { cell.HasForeign = true; cell.Foreign = current; }
                }
                else if (current != baseline) { cell.HasForeign = true; cell.Foreign = current; }
                cells[index] = cell;
            }
        }

        internal void Refresh(Func<int, bool, bool, int, int?, MarketPriceResolution> resolve, Action<string> error)
        {
            HasContextualPrices = false;
            for (int good = 0; good < goods; good++) for (int side = 0; side < 2; side++)
            {
                bool buying = side == 0; var cell = cells[Index(good, buying)];
                if (cell == null) continue;
                try
                {
                    int current = Observe(good, buying, cell);
                    int? foreign = cell.HasForeign ? cell.Foreign : (int?)null;
                    MarketPriceResolution result = resolve(good, buying, false, cell.Baseline, foreign);
                    MarketPriceResolution ai = resolve(good, buying, true, cell.Baseline, foreign);
                    cell.AI = ai.Price;
                    if (result.Price != current)
                    {
                        // Record before invoking a setter that could mutate and then throw.
                        cell.Applied = result.Price; cell.Owned = true;
                        write(good, buying, result.Price);
                    }
                    else if (!result.Own) cell.Owned = false;
                    cell.Applied = result.Price;
                    HasContextualPrices |= cell.AI != result.Price;
                }
                catch (Exception ex) { error?.Invoke("Market-price cell " + good + "/" + side + " failed: " + ex.Message); }
            }
        }

        private int Observe(int good, bool buying, Cell cell)
        {
            int current = read(good, buying);
            if (current != cell.Applied)
            { cell.HasForeign = true; cell.Foreign = current; cell.Owned = false; cell.Applied = current; }
            return current;
        }
        internal int AppliedPrice(int good, bool buying) => cells[Index(good, buying)].Applied;
        internal int AIPrice(int good, bool buying) => cells[Index(good, buying)].AI;

        internal void End(Action<string> error)
        {
            for (int good = 0; good < goods; good++) for (int side = 0; side < 2; side++)
            {
                bool buying = side == 0; int index = Index(good, buying); var cell = cells[index];
                if (cell == null) continue;
                try { if (cell.Owned && read(good, buying) == cell.Applied) write(good, buying, cell.HasForeign ? cell.Foreign : cell.Baseline); }
                catch (Exception ex) { error?.Invoke("Market-price restoration failed: " + ex.Message); }
                finally { cells[index] = null; }
            }
            HasContextualPrices = false;
        }

        internal byte[] Save()
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(1); writer.Write(cells.Length);
                for (int i = 0; i < cells.Length; i++)
                {
                    var cell = cells[i] ?? throw new InvalidOperationException("Market-price session is not initialized.");
                    Observe(i / 2, i % 2 == 0, cell);
                    writer.Write(cell.Baseline); writer.Write(cell.Foreign); writer.Write(cell.Applied);
                    writer.Write(cell.HasForeign); writer.Write(cell.Owned);
                }
                return stream.ToArray();
            }
        }

        internal static MarketPriceSavedCell[] Decode(byte[] bytes, int goods)
        {
            int expected = checked(8 + goods * 2 * 14);
            if (bytes == null || bytes.Length != expected) throw new InvalidOperationException("Invalid market-price payload length.");
            using (var stream = new MemoryStream(bytes, false)) using (var reader = new BinaryReader(stream))
            {
                if (reader.ReadInt32() != 1 || reader.ReadInt32() != goods * 2)
                    throw new InvalidOperationException("Unsupported market-price payload.");
                var result = new MarketPriceSavedCell[goods * 2];
                for (int i = 0; i < result.Length; i++)
                {
                    int baseline = reader.ReadInt32(), foreign = reader.ReadInt32(), applied = reader.ReadInt32();
                    byte hasForeign = reader.ReadByte(), owned = reader.ReadByte();
                    if (baseline < 0 || hasForeign > 1 || owned > 1) throw new InvalidOperationException("Invalid market-price cell.");
                    result[i] = new MarketPriceSavedCell(baseline, foreign, applied, hasForeign != 0, owned != 0);
                }
                return result;
            }
        }
    }
}

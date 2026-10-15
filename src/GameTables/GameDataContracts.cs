using System;
using System.Threading;

namespace APIShared.GameTables
{
    /// <summary>A Script Extender data table that <see cref="GameDataLayers"/> composes.</summary>
    public enum GameDataTable
    {
        /// <summary>Current trade base prices (<c>GamePlayerManagerAPI.GetTradeBasePrice</c>): one row per
        /// <c>eGoods</c> value, column 0 the buy price, column 1 the sell price. The game's own value of each
        /// cell is its default price (<c>GetDefaultTradeBasePrice</c>), the table every map load starts from.</summary>
        TradeBasePrices = 0,
    }

    /// <summary>Composition layers, applied after the cell's Base (the game's own value) in this order.
    /// Within a layer, contributions apply in ascending order, then ordinal owner GUID, then ordinal ID.</summary>
    public enum GameDataLayer
    {
        /// <summary>Adds a signed amount. All contributions add up.</summary>
        Additive = 1,
        /// <summary>Multiplies by a ratio, each in turn, rounding half away from zero after each step.</summary>
        Multiplier = 2,
        /// <summary>Replaces the result so far. The last contribution wins.</summary>
        Override = 3,
        /// <summary>Limits the result to a range, each range in turn.</summary>
        Clamp = 4,
    }

    /// <summary>The cell a layer is asked about. A value copy.</summary>
    public readonly struct GameDataCell
    {
        /// <summary>Creates a cell, for example for consumer tests.</summary>
        public GameDataCell(GameDataTable table, int row, int column, int baseValue)
        { Table = table; Row = row; Column = column; BaseValue = baseValue; }
        /// <summary>The table.</summary>
        public GameDataTable Table { get; }
        /// <summary>Row index; for <see cref="GameDataTable.TradeBasePrices"/> the <c>eGoods</c> value.</summary>
        public int Row { get; }
        /// <summary>Column index; for <see cref="GameDataTable.TradeBasePrices"/> 0 is buy, 1 is sell.</summary>
        public int Column { get; }
        /// <summary>The game's own value of the cell, captured before APIShared first wrote it.</summary>
        public int BaseValue { get; }
    }

    /// <summary>One layer's contribution for one cell. Create it with the factory that matches the
    /// registration's layer; a contribution of another kind is ignored and counted as a failure.</summary>
    public readonly struct GameDataContribution
    {
        private GameDataContribution(GameDataLayer layer, int value, int second) { Layer = layer; Value = value; Second = second; }
        internal GameDataLayer Layer { get; }
        internal int Value { get; }
        internal int Second { get; }
        /// <summary>For <see cref="GameDataLayer.Additive"/>: add <paramref name="amount"/> (may be negative).</summary>
        public static GameDataContribution Add(int amount) => new GameDataContribution(GameDataLayer.Additive, amount, 0);
        /// <summary>For <see cref="GameDataLayer.Multiplier"/>: multiply by <paramref name="numerator"/> /
        /// <paramref name="denominator"/>; numerator at least 0, denominator at least 1.</summary>
        public static GameDataContribution Scale(int numerator, int denominator) => new GameDataContribution(GameDataLayer.Multiplier, numerator, denominator);
        /// <summary>For <see cref="GameDataLayer.Multiplier"/>: multiply by <paramref name="percent"/> / 100.</summary>
        public static GameDataContribution Percent(int percent) => new GameDataContribution(GameDataLayer.Multiplier, percent, 100);
        /// <summary>For <see cref="GameDataLayer.Override"/>: use <paramref name="value"/>.</summary>
        public static GameDataContribution Set(int value) => new GameDataContribution(GameDataLayer.Override, value, 0);
        /// <summary>For <see cref="GameDataLayer.Clamp"/>: limit to [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
        public static GameDataContribution Range(int minimum, int maximum) => new GameDataContribution(GameDataLayer.Clamp, minimum, maximum);
    }

    /// <summary>Decides one layer's contribution for one cell. Return false to leave the cell alone.</summary>
    /// <remarks>Runs inside a composition (see <see cref="GameDataLayers.TryRegister"/>) for every cell of
    /// the table and must be deterministic: the same inputs give the same answer on every multiplayer peer.</remarks>
    public delegate bool GameDataLayerHandler(GameDataCell cell, out GameDataContribution contribution);

    /// <summary>Handle of one layer registration. Dispose it to unregister; the next composition then
    /// computes the table without it (back to the game's value when no layer is left).</summary>
    public sealed class GameDataRegistration : IDisposable
    {
        internal int ActiveValue = 1;
        private readonly Action<GameDataRegistration> remove;
        internal GameDataRegistration(string ownerGuid, string registrationId, GameDataTable table, GameDataLayer layer, int order,
            Action<GameDataRegistration> remove)
        { OwnerGuid = ownerGuid; RegistrationId = registrationId; Table = table; Layer = layer; Order = order; this.remove = remove; }
        /// <summary>Owning plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Registration ID, unique per owner and table.</summary>
        public string RegistrationId { get; }
        /// <summary>The composed table.</summary>
        public GameDataTable Table { get; }
        /// <summary>The layer of every contribution.</summary>
        public GameDataLayer Layer { get; }
        /// <summary>Ascending order within the layer.</summary>
        public int Order { get; }
        /// <summary>False once <see cref="Dispose"/> has been called.</summary>
        public bool IsActive => Volatile.Read(ref ActiveValue) != 0;
        /// <summary>Unregisters. Idempotent and thread-safe.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ActiveValue, 0) != 0)
                remove(this);
        }
        /// <inheritdoc/>
        public override string ToString() => OwnerGuid + "/" + RegistrationId + " (" + Table + " " + Layer + ", order " + Order + ")";
    }
}

using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>An immutable copy of one player's five native construction material stocks.</summary>
    public readonly struct ConstructionMaterialStock
    {
        internal ConstructionMaterialStock(uint wood, uint stone, uint iron, uint pitch, int gold)
        {
            Wood = wood;
            Stone = stone;
            Iron = iron;
            Pitch = pitch;
            Gold = gold;
        }

        /// <summary>Wood planks in stock.</summary>
        public uint Wood { get; }

        /// <summary>Stone blocks in stock.</summary>
        public uint Stone { get; }

        /// <summary>Iron ingots in stock.</summary>
        public uint Iron { get; }

        /// <summary>Raw pitch in stock.</summary>
        public uint Pitch { get; }

        /// <summary>Gold balance, which can be negative.</summary>
        public int Gold { get; }

        /// <summary>Live stock at the time of the snapshot, or zero for another good.</summary>
        public long GetAmount(eGoods good)
        {
            switch (ConstructionCostValues.MaterialIndex(good))
            {
                case 0: return Wood;
                case 1: return Stone;
                case 2: return Iron;
                case 3: return Pitch;
                case 4: return Gold;
                default: return 0;
            }
        }
    }
}

using System;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>An immutable snapshot of the five native construction materials.</summary>
    public readonly struct ConstructionCostValues : IEquatable<ConstructionCostValues>
    {
        /// <summary>Creates a snapshot in native wood, stone, iron, pitch, gold order.</summary>
        public ConstructionCostValues(int wood, int stone, int iron, int pitch, int gold)
        { Wood = wood; Stone = stone; Iron = iron; Pitch = pitch; Gold = gold; }
        /// <summary>Wood planks required.</summary>
        public int Wood { get; }
        /// <summary>Stone blocks required.</summary>
        public int Stone { get; }
        /// <summary>Iron ingots required.</summary>
        public int Iron { get; }
        /// <summary>Raw pitch required.</summary>
        public int Pitch { get; }
        /// <summary>Gold required.</summary>
        public int Gold { get; }
        /// <summary>Amount of a native material; zero for a good outside the five native construction materials.</summary>
        public int GetAmount(eGoods good)
        {
            switch (good)
            {
                case eGoods.STORED_WOOD_PLANKS: return Wood;
                case eGoods.STORED_STONE_BLOCKS: return Stone;
                case eGoods.STORED_IRON_INGOTS: return Iron;
                case eGoods.STORED_PITCH_RAW: return Pitch;
                case eGoods.STORED_GOLD: return Gold;
                default: return 0;
            }
        }
        internal static int MaterialIndex(eGoods good)
        {
            switch (good)
            {
                case eGoods.STORED_WOOD_PLANKS: return 0;
                case eGoods.STORED_STONE_BLOCKS: return 1;
                case eGoods.STORED_IRON_INGOTS: return 2;
                case eGoods.STORED_PITCH_RAW: return 3;
                case eGoods.STORED_GOLD: return 4;
                default: return -1;
            }
        }
        /// <summary>Value comparison.</summary>
        public bool Equals(ConstructionCostValues other) => Wood == other.Wood && Stone == other.Stone && Iron == other.Iron && Pitch == other.Pitch && Gold == other.Gold;
        /// <summary>Value comparison.</summary>
        public override bool Equals(object obj) => obj is ConstructionCostValues other && Equals(other);
        /// <summary>Value hash.</summary>
        public override int GetHashCode() { unchecked { return ((((Wood * 397 ^ Stone) * 397 ^ Iron) * 397 ^ Pitch) * 397) ^ Gold; } }
    }
}

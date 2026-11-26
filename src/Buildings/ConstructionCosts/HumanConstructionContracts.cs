using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>The native identity domain of a paid construction object; domains never share slots.</summary>
    public enum ConstructionObjectKind : byte
    {
        /// <summary>A one-based building game ID.</summary>
        Building = 1,
        /// <summary>A one-based unit game ID, for stationary tower weapons.</summary>
        Unit = 2,
        /// <summary>A direct projectile-array slot, for static decorations.</summary>
        Decoration = 3
    }

    /// <summary>Exact mission-local object identity. GlobalId is a generation, not a slot or array index.</summary>
    public readonly struct ConstructionObjectIdentity : IEquatable<ConstructionObjectIdentity>
    {
        /// <summary>Creates an identity; callers must corroborate it against the current live record.</summary>
        public ConstructionObjectIdentity(ConstructionObjectKind kind, int slot, uint globalId)
        { Kind = kind; Slot = slot; GlobalId = globalId; }
        /// <summary>The record domain.</summary>
        public ConstructionObjectKind Kind { get; }
        /// <summary>Building/unit game ID or direct decoration slot, according to Kind.</summary>
        public int Slot { get; }
        /// <summary>The native generation. Zero is permitted because it is not a proven invalid decoration generation.</summary>
        public uint GlobalId { get; }
        /// <summary>Whether the domain and slot can describe a construction object.</summary>
        public bool IsValid => Slot > 0 && (Kind == ConstructionObjectKind.Building || Kind == ConstructionObjectKind.Unit || Kind == ConstructionObjectKind.Decoration);
        /// <summary>Compares the full identity, not merely a reused native slot.</summary>
        public bool Equals(ConstructionObjectIdentity other) => Kind == other.Kind && Slot == other.Slot && GlobalId == other.GlobalId;
        /// <summary>Compares a boxed identity.</summary>
        public override bool Equals(object value) => value is ConstructionObjectIdentity other && Equals(other);
        /// <summary>Hashes the full identity.</summary>
        public override int GetHashCode() => unchecked(((int)Kind * 397 ^ Slot) * 397 ^ (int)GlobalId);
    }

    internal static class HumanConstructionRefundMath
    {
        internal static int Materials(int paid, float multiplier, int percentage)
        {
            if (float.IsNaN(multiplier) || float.IsInfinity(multiplier))
                throw new ArgumentOutOfRangeException(nameof(multiplier), "Refund multipliers must be finite.");
            // Match SE's exact expression and unsigned conversion before applying bounds.
            // Mono may retain wider floating-stack precision until conv.u8; intermediate
            // float stores or Math.Min/Max(float) calls can change the truncated result.
            int boundedPercentage = Math.Max(0, Math.Min(100, percentage));
            if (boundedPercentage == 0 || paid == 0 || multiplier <= 0) return 0;
            int magnitude = Math.Abs(paid);
            float percentageFactor = boundedPercentage / 100f;
            ulong raw = (ulong)(magnitude * multiplier * percentageFactor);
            int bounded = (int)Math.Min((ulong)magnitude, raw);
            return paid < 0 ? -bounded : bounded;
        }

        internal static int StationaryUnit(int paid, int percentage) =>
            (int)((long)paid * Math.Max(0, Math.Min(100, percentage)) / 100);
    }

    internal static class HumanConstructionGoods
    {
        internal static readonly eGoods[] All = {
            eGoods.STORED_GOLD, eGoods.STORED_BOWS, eGoods.STORED_CROSSBOWS, eGoods.STORED_SPEARS,
            eGoods.STORED_PIKES, eGoods.STORED_MACES, eGoods.STORED_SWORDS, eGoods.STORED_LEATHER_ARMOUR,
            eGoods.STORED_METAL_ARMOUR, eGoods.STORED_WOOD_PLANKS, eGoods.STORED_RAW_HOPS,
            eGoods.STORED_STONE_BLOCKS, eGoods.STORED_IRON_INGOTS, eGoods.STORED_PITCH_RAW,
            eGoods.STORED_RAW_WHEAT, eGoods.STORED_FLOUR, eGoods.STORED_FOOD_BREAD,
            eGoods.STORED_FOOD_CHEESE, eGoods.STORED_FOOD_MEAT, eGoods.STORED_FOOD_FRUIT, eGoods.STORED_FOOD_ALE
        };
        internal static readonly IReadOnlyDictionary<eGoods, int> Empty = new ReadOnlyDictionary<eGoods, int>(new Dictionary<eGoods, int>());
        internal static bool IsSupported(eGoods good) => Array.IndexOf(All, good) >= 0;
        internal static bool IsValidAmount(eGoods good, int amount) => IsSupported(good) &&
            (good == eGoods.STORED_GOLD ? amount >= -10000 && amount <= 10000 : amount >= 0 && amount <= 100);
        internal static IReadOnlyDictionary<eGoods, int> Snapshot(IReadOnlyDictionary<eGoods, int> values)
        {
            var copy = new Dictionary<eGoods, int>();
            if (values != null) foreach (var item in values)
            {
                if (!IsValidAmount(item.Key, item.Value)) throw new ArgumentOutOfRangeException(nameof(values), "Unsupported construction extra good or amount.");
                if (item.Value != 0) copy.Add(item.Key, item.Value);
            }
            return new ReadOnlyDictionary<eGoods, int>(copy);
        }
    }
}

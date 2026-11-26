using System;
using SHCDESE.Extensions;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>The enum domain from which a construction target was supplied.</summary>
    public enum ConstructionCostTargetKind : byte
    {
        /// <summary>No target.</summary>
        None,
        /// <summary>A structure or building-tooltip identifier.</summary>
        Structure,
        /// <summary>A build-menu mapper.</summary>
        Mapper
    }

    /// <summary>A typed construction identity; menu aliases never become unchecked table indexes.</summary>
    /// <remarks>Factory methods do not access the game. Equality coalesces known aliases of the same
    /// cost entry, including a mapper and its corresponding structure. Invalid targets compare equal
    /// to default. Enum domains are interpreted explicitly, never guessed from numeric values.</remarks>
    public readonly struct ConstructionCostTarget : IEquatable<ConstructionCostTarget>
    {
        // Canonical identity is 1 + native row; -1 denotes the decoration backend, zero is invalid.
        private readonly int identity;
        private readonly int source;
        private ConstructionCostTarget(ConstructionCostTargetKind kind, int sourceValue, int canonical)
        { Kind = kind; source = sourceValue; identity = canonical; }

        /// <summary>The original enum domain; None for default.</summary>
        public ConstructionCostTargetKind Kind { get; }
        /// <summary>The supplied structure identifier, or STRUCT_NULL for another domain.</summary>
        public eStructs Structure => Kind == ConstructionCostTargetKind.Structure ? (eStructs)source : eStructs.STRUCT_NULL;
        /// <summary>The supplied mapper, or its zero value for another domain.</summary>
        public eMappers Mapper => Kind == ConstructionCostTargetKind.Mapper ? (eMappers)source : default;
        /// <summary>Whether this target has a supported construction identity.</summary>
        public bool IsValid => identity != 0;
        /// <summary>Whether the target has a native construction-cost row. Writes also require an existing visual row.</summary>
        public bool SupportsVanillaCosts => identity > 0;
        /// <summary>Whether the target can identify a human construction operation.</summary>
        public bool SupportsHumanExtraCosts => identity != 0;
        /// <summary>Canonical symbolic key; independent of the source alias. Empty for an invalid target.</summary>
        public string Key => identity == -1 ? "Mapper:" + nameof(eMappers.MAPPER_BRAZIER) :
            identity > 0 ? "Structure:" + ((eStructs)(identity - 1)).ToString() : string.Empty;

        internal int Row => identity - 1;
        internal bool IsDecoration => identity == -1;

        /// <summary>Interprets an eStructs building or tooltip identifier, resolving known menu aliases.</summary>
        public static ConstructionCostTarget FromStructure(eStructs structure)
        {
            if (!Enum.IsDefined(typeof(eStructs), structure)) return default;
            if (structure == eStructs.STRUCT_BRAZIER)
                return new ConstructionCostTarget(ConstructionCostTargetKind.Structure, (int)structure, -1);
            eStructs resolved = ResolveStructure(structure);
            int row = (int)resolved;
            if (row <= (int)eStructs.STRUCT_NULL || row >= (int)eStructs.STRUCT_MAX) return default;
            return new ConstructionCostTarget(ConstructionCostTargetKind.Structure, (int)structure, row + 1);
        }

        /// <summary>Interprets an eMappers build-menu identity; numeric equality with an eStructs value is irrelevant.</summary>
        public static ConstructionCostTarget FromMapper(eMappers mapper)
        {
            if (!Enum.IsDefined(typeof(eMappers), mapper)) return default;
            if (mapper == eMappers.MAPPER_BRAZIER)
                return new ConstructionCostTarget(ConstructionCostTargetKind.Mapper, (int)mapper, -1);
            // The SE native conversion omits this abstract menu identity, although its oriented aliases exist.
            eStructs structure = mapper == eMappers.MAPPER_GATE_WOOD ? eStructs.STRUCT_GATE_WOOD : mapper.ConvertToEStructs();
            var target = FromStructure(structure);
            return target.IsValid ? new ConstructionCostTarget(ConstructionCostTargetKind.Mapper, (int)mapper, target.identity) : default;
        }

        /// <summary>Reads a canonical symbolic key produced by Key; never accepts numeric enum strings.</summary>
        public static bool TryParseKey(string key, out ConstructionCostTarget target)
        {
            target = default;
            if (key == null) return false;
            const string structures = "Structure:";
            const string mappers = "Mapper:";
            if (key.StartsWith(structures, StringComparison.Ordinal) &&
                key.Substring(structures.Length).StartsWith("STRUCT_", StringComparison.Ordinal) &&
                Enum.TryParse(key.Substring(structures.Length), out eStructs structure) &&
                Enum.GetName(typeof(eStructs), structure) == key.Substring(structures.Length)) target = FromStructure(structure);
            else if (key.StartsWith(mappers, StringComparison.Ordinal) &&
                key.Substring(mappers.Length).StartsWith("MAPPER_", StringComparison.Ordinal) &&
                Enum.TryParse(key.Substring(mappers.Length), out eMappers mapper) &&
                Enum.GetName(typeof(eMappers), mapper) == key.Substring(mappers.Length)) target = FromMapper(mapper);
            return target.IsValid;
        }

        private static eStructs ResolveStructure(eStructs structure)
        {
            switch (structure)
            {
                // These native rows are also named destroyed towers in eStructs. Vanilla's tooltip
                // and successful stationary-engine construction both use these exact cost entries.
                case eStructs.STRUCT_MANGONEL: return eStructs.STRUCT_TOWER1_DESTROYED;
                case eStructs.STRUCT_BALLISTA: return eStructs.STRUCT_TOWER2_DESTROYED;
                case eStructs.STRUCT_GATE_WOOD1A:
                case eStructs.STRUCT_GATE_WOOD1B:
                case eStructs.STRUCT_GATE_WOOD1C:
                case eStructs.STRUCT_GATE_WOOD1D: return eStructs.STRUCT_GATE_WOOD;
                case eStructs.STRUCT_GATE_STONE1A:
                case eStructs.STRUCT_GATE_STONE1B: return eStructs.STRUCT_GATE_INNER;
                case eStructs.STRUCT_GATE_STONE2A:
                case eStructs.STRUCT_GATE_STONE2B: return eStructs.STRUCT_GATE_MAIN;
                case eStructs.STRUCT_GARDEN_SMALL:
                case eStructs.STRUCT_GARDEN_MED:
                case eStructs.STRUCT_GARDEN_LARGE: return eStructs.STRUCT_GARDEN;
                case eStructs.STRUCT_POND_SMALL:
                case eStructs.STRUCT_POND_LARGE:
                case eStructs.STRUCT_POND5:
                case eStructs.STRUCT_POND6:
                case eStructs.STRUCT_POND7:
                case eStructs.STRUCT_POND8: return eStructs.STRUCT_POND;
                default: return structure;
            }
        }

        /// <summary>Canonical identity comparison, including across source domains.</summary>
        public bool Equals(ConstructionCostTarget other) => identity == other.identity;
        /// <summary>Canonical identity comparison.</summary>
        public override bool Equals(object obj) => obj is ConstructionCostTarget other && Equals(other);
        /// <summary>Hash of the canonical identity.</summary>
        public override int GetHashCode() => identity;
        /// <summary>Canonical symbolic identity.</summary>
        public override string ToString() => Key;
        /// <summary>Canonical identity comparison.</summary>
        public static bool operator ==(ConstructionCostTarget left, ConstructionCostTarget right) => left.Equals(right);
        /// <summary>Canonical identity comparison.</summary>
        public static bool operator !=(ConstructionCostTarget left, ConstructionCostTarget right) => !left.Equals(right);
    }
}

using System;
using SHCDESE.Interop;

namespace APIShared.Health
{
    /// <summary>The canonical native object whose maximum health is initialized.</summary>
    public enum HealthObjectKind
    {
        /// <summary>A one-based unit record, including mobile siege equipment.</summary>
        Unit = 0,
        /// <summary>An ordinary building record; siege-tower proxies are coordinated with their unit.</summary>
        Building = 1
    }

    /// <summary>Why the service is assigning maximum health once.</summary>
    public enum HealthInitializationReason
    {
        /// <summary>Initial objects after completed new-game initialization.</summary>
        MissionStart,
        /// <summary>A newly spawned object.</summary>
        Created,
        /// <summary>A completed native unit type initialization, including recruitment and worker changes.</summary>
        TypeInitialized,
        /// <summary>Restoration after native save-load normalization.</summary>
        SaveLoaded
    }

    /// <summary>Immutable, pointer-free snapshot at an object's completed initialization.</summary>
    /// <remarks>HP are raw native units, not displayed HP. The owner is sampled at this initialization;
    /// subsequent owner changes do not reapply policies. Do not retain IDs as live object references.</remarks>
    public readonly struct HealthModifierContext
    {
        /// <summary>Constructs an immutable context, also usable by consumer tests.</summary>
        public HealthModifierContext(HealthObjectKind kind, int objectId, uint globalId, int playerId,
            bool isAIPlayer, eChimps unitType, eStructs buildingType, uint originalMaximumHealth,
            HealthInitializationReason reason)
        {
            Kind = kind; ObjectId = objectId; GlobalId = globalId; PlayerId = playerId;
            IsAIPlayer = isAIPlayer; UnitType = unitType; BuildingType = buildingType;
            OriginalMaximumHealth = originalMaximumHealth; Reason = reason;
        }
        /// <summary>Unit or ordinary building.</summary>
        public HealthObjectKind Kind { get; }
        /// <summary>One-based unit/building game ID.</summary>
        public int ObjectId { get; }
        /// <summary>Identity of the native record at initialization.</summary>
        public uint GlobalId { get; }
        /// <summary>One-based controlling player at initialization.</summary>
        public int PlayerId { get; }
        /// <summary>Whether that player has an AI Lord lineup at initialization.</summary>
        public bool IsAIPlayer { get; }
        /// <summary>Unit type, meaningful only for units.</summary>
        public eChimps UnitType { get; }
        /// <summary>Building type, meaningful only for buildings.</summary>
        public eStructs BuildingType { get; }
        /// <summary>Completed native maximum before coordinated modifiers, retained across saves.</summary>
        public uint OriginalMaximumHealth { get; }
        /// <summary>The initialization boundary being handled.</summary>
        public HealthInitializationReason Reason { get; }
    }

    /// <summary>A validated unscaled unit basis supplied by a consumer's older save format.</summary>
    public readonly struct HealthLegacyBasis
    {
        /// <summary>Creates a migration record. The service validates the live ID/global identity.</summary>
        public HealthLegacyBasis(int unitId, uint globalId, uint maximum)
        { UnitId = unitId; GlobalId = globalId; Maximum = maximum; }
        /// <summary>One-based unit ID.</summary>
        public int UnitId { get; }
        /// <summary>Expected nonzero global identity.</summary>
        public uint GlobalId { get; }
        /// <summary>Original positive, unscaled maximum.</summary>
        public uint Maximum { get; }
    }
}

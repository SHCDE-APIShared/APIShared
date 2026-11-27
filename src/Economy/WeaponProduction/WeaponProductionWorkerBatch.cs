namespace APIShared.Economy
{
    /// <summary>The framework's latched batch phase, rather than the worker's native AI or animation state.</summary>
    public enum WeaponProductionBatchPhase
    {
        /// <summary>The tracked recipe is in its manufacturing phase.</summary>
        Manufacturing = 0,
        /// <summary>The tracked recipe has produced a local product awaiting carriage.</summary>
        LocalProduct = 1,
        /// <summary>The worker is carrying the tracked recipe's product.</summary>
        Cargo = 2
    }

    /// <summary>Immutable, detached observation of one framework-latched additional-recipe worker batch.</summary>
    /// <remarks>The option and phase are recorded by the existing batch owner. This is not a quantity,
    /// delivery-destination, ownership-history or native-animation observation. It may be retained as history,
    /// but it does not stay current across worker changes, a load or world retirement. It grants no mutation
    /// authority. Workers without a framework-latched additional recipe have no such snapshot.
    /// The default value represents no observation; consume fields only after a successful read.</remarks>
    public readonly struct WeaponProductionWorkerBatch
    {
        internal WeaponProductionWorkerBatch(int unitId, uint unitGlobalId, int homeBuildingId,
            uint homeBuildingGlobalId, WeaponProductionOption option, WeaponProductionBatchPhase phase)
        {
            UnitId = unitId; UnitGlobalId = unitGlobalId; HomeBuildingId = homeBuildingId;
            HomeBuildingGlobalId = homeBuildingGlobalId; Option = option; Phase = phase;
        }

        /// <summary>The one-based unit slot validated for this observation.</summary>
        public int UnitId { get; }
        /// <summary>The exact unsigned unit Global ID paired with UnitId.</summary>
        public uint UnitGlobalId { get; }
        /// <summary>The one-based workshop slot recorded by the batch and validated against the live worker link.</summary>
        public int HomeBuildingId { get; }
        /// <summary>The exact unsigned workshop Global ID paired with HomeBuildingId.</summary>
        public uint HomeBuildingGlobalId { get; }
        /// <summary>The immutable admitted recipe latched by this worker, independent of the next selection.</summary>
        public WeaponProductionOption Option { get; }
        /// <summary>The recorded batch phase; it does not predict a native animation or delivery result.</summary>
        public WeaponProductionBatchPhase Phase { get; }
    }
}

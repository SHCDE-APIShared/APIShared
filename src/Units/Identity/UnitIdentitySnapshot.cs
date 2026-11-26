namespace APIShared.Units
{
    /// <summary>Immutable exact-unit variant identity. NativeType is the existing simulation carrier, not a new native unit type.</summary>
    public readonly struct UnitIdentitySnapshot
    {
        internal UnitIdentitySnapshot(string ownerGuid, string variantId, int nativeType, int unitId, uint globalId, bool pending)
        { OwnerGuid = ownerGuid; VariantId = variantId; NativeType = nativeType; UnitId = unitId; GlobalId = globalId; IsPendingRecruitment = pending; }
        /// <summary>Registering plugin GUID, compared ordinally.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local variant ID, persisted as text.</summary>
        public string VariantId { get; }
        /// <summary>Existing native carrier type expected after recruitment completes.</summary>
        public int NativeType { get; }
        /// <summary>One-based unit slot; slots can be reused.</summary>
        public int UnitId { get; }
        /// <summary>Exact nonzero unsigned native GlobalId.</summary>
        public uint GlobalId { get; }
        /// <summary>True while this exact peasant's native transformation targets NativeType. This is identity evidence, not payment acknowledgement.</summary>
        public bool IsPendingRecruitment { get; }
    }

}

using System;

namespace APIShared.Savegames
{
    /// <summary>Save modes supported by an explicitly enrolled critical participant.</summary>
    public enum CriticalSaveModes
    {
        /// <summary>No save mode is selected.</summary>
        None = 0,
        /// <summary>Protected single-player and editor saves; real multiplayer is not supported.</summary>
        SinglePlayerAndEditor = 1
    }

    /// <summary>Prepares one participant's private checkpoint for the current protected save attempt.</summary>
    /// <param name="context">The owned logical save flags and nullable path, not a native temporary filename.</param>
    /// <param name="state">Private state for this attempt, passed to release even when null or preparation fails.</param>
    /// <returns>True when the participant is ready; false refuses destination replacement for this save attempt.</returns>
    /// <remarks>Runs synchronously on the protected save caller. Exceptions also refuse replacement.
    /// Every entered preparation has one release, including false, throwing and null-state outcomes.
    /// This callback grants no new native access or authority to import a consumer ledger.</remarks>
    public delegate bool CriticalSavePrepare(VersionedSaveContext context, ref object state);

    /// <summary>Releases one entered participant checkpoint on the save caller's thread.</summary>
    /// <param name="context">The same logical context supplied to preparation.</param>
    /// <param name="state">The participant's private preparation state, including null.</param>
    /// <remarks>Release runs once for every entered preparation, in reverse participant order and independently
    /// of other releases. Exceptions are recorded without masking the first failure or an already committed result.
    /// Callbacks must complete synchronously; there is no asynchronous cancellation or disposal registration.</remarks>
    public delegate void CriticalSaveRelease(VersionedSaveContext context, object state);

    /// <summary>Describes one explicit protected-save enrollment; the client validates and snapshots it at entry.</summary>
    public sealed class CriticalSaveOptions
    {
        /// <summary>Creates an enrollment description without registering or activating anything.</summary>
        /// <param name="reservedMarkerEntry">A known entry reserved for framework marker injection; capture must not write it.</param>
        /// <param name="minimumReaderVersion">The enrolled protection floor, supported by the current reader and excluding
        /// every older consumer reader which cannot restore the coupled protected state.</param>
        /// <param name="prepare">Synchronous participant preparation; false or an exception refuses the current replacement.</param>
        /// <param name="release">Independent release for every entered preparation, including null state and failed preparation.</param>
        public CriticalSaveOptions(string reservedMarkerEntry, int minimumReaderVersion,
            CriticalSavePrepare prepare, CriticalSaveRelease release)
        {
            ReservedMarkerEntry = reservedMarkerEntry; MinimumReaderVersion = minimumReaderVersion;
            Prepare = prepare; Release = release;
        }
        /// <summary>The declared known entry owned by the framework's protection marker.</summary>
        public string ReservedMarkerEntry { get; }
        /// <summary>The minimum original record/reader version required for protected-record confirmation.
        /// The consumer must raise its format floor when needed: APIShared cannot infer which historical consumer builds
        /// understand the coupled record. An old unprotected reader must reject these records as TooNew.</summary>
        public int MinimumReaderVersion { get; }
        /// <summary>The synchronous callback which prepares this participant's private checkpoint.</summary>
        public CriticalSavePrepare Prepare { get; }
        /// <summary>The independent callback which releases every entered checkpoint.</summary>
        public CriticalSaveRelease Release { get; }
        /// <summary>Supported save modes; defaults to single-player/editor. Snapshotted by enrollment.</summary>
        public CriticalSaveModes Modes { get; set; } = CriticalSaveModes.SinglePlayerAndEditor;
        /// <summary>Whether this participant requires the shared NativeStorage checkpoint; defaults false.
        /// Set before enrollment and before the first NativeStorage registration when that request is needed.</summary>
        public bool RequiresNativeStorage { get; set; }
    }

    /// <summary>A process-lived enrollment for one exact owner-matching versioned record.</summary>
    /// <remarks>There is no unregistration or Dispose. This token exposes metadata and diagnostics, not native
    /// state, legacy-import permission, a live commit certificate or authority to retire a private ledger.</remarks>
    public sealed class CriticalSaveParticipant
    {
        internal CriticalSaveParticipant(VersionedSavePreparedSource preparedSource,
            VersionedSaveCriticalSource criticalSource, VersionedSaveDataHandle handle, bool requiresStorage)
        {
            PreparedSource = preparedSource ?? throw new ArgumentNullException(nameof(preparedSource));
            CriticalSource = criticalSource ?? throw new ArgumentNullException(nameof(criticalSource));
            Handle = handle ?? throw new ArgumentNullException(nameof(handle));
            RequiresNativeStorage = requiresStorage;
        }
        /// <summary>The exact GUID which registered the underlying record and enrolled this participant.</summary>
        public string OwnerGuid => Handle.OwnerGuid;
        /// <summary>The underlying Script Extender record identifier.</summary>
        public string SaveDataIdentifier => Handle.SaveDataIdentifier;
        /// <summary>The existing process-lived versioned-record diagnostic handle.</summary>
        public VersionedSaveDataHandle Handle { get; }
        /// <summary>Whether enrollment requested the shared NativeStorage checkpoint.</summary>
        public bool RequiresNativeStorage { get; }
        internal VersionedSavePreparedSource PreparedSource { get; }
        internal VersionedSaveCriticalSource CriticalSource { get; }
        internal VersionedSaveDataRegistry Registry => PreparedSource.Registry;

        internal bool TryGetPreparedOrigin(string owner, VersionedSaveLoad load,
            out VersionedSavePreparedOrigin origin, out string reason)
        {
            origin = null;
            if (!string.Equals(owner, OwnerGuid, StringComparison.Ordinal))
            { reason = "The critical participant belongs to another owner."; return false; }
            return PreparedSource.TryGetCurrent(load, out origin, out reason);
        }
        internal bool TryValidateCurrentWorld(VersionedSavePreparedOrigin origin, object world,
            long session, out string reason)
        {
            if (!PreparedSource.TryValidateCompleted(origin, out reason)) return false;
            return Registry.TryValidateCriticalWorld(PreparedSource, world, session, out reason);
        }
        /// <summary>Reads whether this exact active Prepared delivery has an original protected record.</summary>
        /// <param name="load">The receiver supplied to this record's current Prepared callback.</param>
        /// <param name="protectedRecord">True when the original envelope contains this participant's marker at its reader floor.</param>
        /// <param name="reason">Diagnostic text when the exact delivery or original record cannot be validated.</param>
        /// <returns>True when the observation is valid, including an unprotected or absent record.</returns>
        /// <remarks>Call synchronously inside this participant's Prepared callback on its delivering thread.
        /// Retaining the receiver or calling from Loaded, another callback or another thread does not grant access.
        /// Marker presence alone does not confirm the complete cohort. This observes original-envelope provenance only;
        /// it does not publish native state or authorize import/cutover.</remarks>
        public bool TryReadPreparedProtection(VersionedSaveLoad load, out bool protectedRecord, out string reason)
        {
            protectedRecord = false;
            if (!TryGetPreparedOrigin(OwnerGuid, load, out var origin, out reason)) return false;
            return Registry.TryReadCriticalPreparedMarker(CriticalSource, origin, out _, out protectedRecord, out reason);
        }
    }
}

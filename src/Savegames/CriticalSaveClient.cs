namespace APIShared.Savegames
{
    /// <summary>Owner-bound access to explicit protected-save participant enrollment.</summary>
    /// <remarks>Obtaining this client performs no registration, hook installation, writer request or save operation.</remarks>
    public sealed class CriticalSaveClient
    {
        private readonly string owner;
        internal CriticalSaveClient(string owner) { this.owner = owner; }

        /// <summary>Enrolls one exact versioned record into the process-lived protected save cohort.</summary>
        /// <param name="handle">This owner's already registered versioned handle, with Prepared, SaveGamesAndMaps and SimulationRelevant.</param>
        /// <param name="options">Marker, reader-floor, aggregate ceiling and synchronous callbacks, snapshotted during this call.</param>
        /// <param name="participant">The process-lived participant on success; null on refusal.</param>
        /// <param name="reason">Diagnostic text on refusal.</param>
        /// <returns>True when enrollment succeeds; false when prerequisites or requested protection are unavailable.</returns>
        /// <remarks>Call on initialized Unity startup before any world or archive load, never from capture/load callbacks.
        /// A NativeStorage requirement must be enrolled before its first registration; a pending requirement refuses
        /// saving until the actual shared storage join is ready. Protected saving is single-player/editor only and
        /// needs the qualified writer and owned context. Ordinary registrations keep their defaults and multiplayer path.
        /// Any enrolled participant makes this process's real-multiplayer saves refuse, including state-only participants.
        /// Every archived participant needs a complete readable origin positively associated with the current native mission;
        /// zero or mismatched source session IDs refuse saving, including state-only cohorts.
        /// Participants are process-lived: no Dispose, unregister or asynchronous cancellation is provided.
        /// Preparation runs in ordinal owner GUID, then identifier order, on the owning save caller under the
        /// actual engine monitor and outside registry locks. Each entered callback receives a private ref State;
        /// all entered releases run in reverse order, including false, throwing and null-state preparations.
        /// False publishes no enrollment or native mode request. A successful storage reservation is permanent;
        /// later shared-source startup failure keeps the cohort blocked and requires restart/reconfiguration.
        /// An enrolled source reading its original protected marker requires the exact ordered owner/identifier,
        /// marker and reader-floor configuration. Adding/removing an enrollment or changing a marker/floor makes that
        /// original passive and refuses saving, including adding a participant with NoRecord. No membership migration is supplied.
        /// This check requires a registered owner to read an original protected record; absent APIShared/all original
        /// providers, no enforcement is promised. New NoRecord-only owners cannot discover removed unregistered records.
        /// AggregateBudget is a snapshotted ceiling: the least permissive joined source sets the total encoded
        /// byte limit. Existing/default and internal sources keep 8 MiB; all sources must accept 16 MiB to raise it.
        /// This resource option changes no original marker identity or record format and cannot raise per-record
        /// or per-entry limits. A later lower ceiling refuses an oversized replacement rather than dropping data.
        /// Enrollment is not legacy import, a state snapshot or permission to discard an existing consumer ledger.</remarks>
        public bool TryEnroll(VersionedSaveDataHandle handle, CriticalSaveOptions options,
            out CriticalSaveParticipant participant, out string reason) =>
            CriticalSaveRuntime.TryEnroll(owner, handle, options, out participant, out reason);

        /// <summary>Acknowledges this participant for the exact current newly created editor world.</summary>
        /// <param name="participant">A participant enrolled by this client owner.</param>
        /// <param name="context">The actual current EditorCreated mission context.</param>
        /// <param name="reason">Diagnostic text on refusal.</param>
        /// <returns>True only when the runtime accepts the exact created-world metadata association.</returns>
        /// <remarks>Requires the Unity caller to hold the actual engine monitor at the current EditorCreated world/session boundary.
        /// This is metadata acknowledgement only; it neither restores nor initializes native/consumer state,
        /// synthesizes an archive marker, imports a ledger, or grants save/cutover authority by itself.</remarks>
        public bool TryConfirmCreatedWorld(CriticalSaveParticipant participant, MissionContext context, out string reason) =>
            CriticalSaveRuntime.TryConfirmCreatedWorld(owner, participant, context, out reason);
    }
}

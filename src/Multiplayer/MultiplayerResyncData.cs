using System;

namespace APIShared.Multiplayer
{
    /// <summary>Registers simulation records that travel alongside an executed Vanilla multiplayer repair.</summary>
    public static class MultiplayerResyncData
    {
        /// <summary>Whether the shared channel and native repair boundaries are available.</summary>
        public static bool IsAvailable => MultiplayerResyncDataRuntime.IsAvailable;

        /// <summary>Checks the live shared hooks for startup or multiplayer admission.</summary>
        public static bool CheckIntegrity() => IsAvailable && MultiplayerResyncNativeRuntime.CheckDataIntegrity();

        /// <summary>Registers one required, versioned record on the Unity thread after native initialization and before
        /// the first multiplayer lobby roster of this process resolves; registration then closes for the process.</summary>
        /// <remarks>
        /// The owner GUID and record ID must each contain 1 to 96 ASCII letters, digits, periods,
        /// underscores or hyphens. For example, use <c>auto-send-rules</c> for a feature record.
        /// Register the same records on all peers. Capture returns a private canonical byte array, including an
        /// empty array for inactive data. Prepare validates host bytes against the repaired world and returns
        /// an owned, fully prepared replacement. It must not mutate simulation or retain native pointers.
        /// Commit only publishes that replacement. It must not allocate, block or throw.
        /// Callbacks run under the engine monitor on the native caller thread; never dispatch Unity work from
        /// them or enter a nested load, save or engine run. APIShared retains registrations for the process.
        /// </remarks>
        public static bool TryRegister<TPrepared>(string ownerGuid, string recordId, int schemaVersion,
            int maximumBytes, Func<byte[]> capture, Func<ReadOnlyMemory<byte>, TPrepared> prepare,
            Action<TPrepared> commit, out string reason) =>
            Register(ownerGuid, recordId, schemaVersion, maximumBytes, capture, prepare, commit, false, out reason);

        /// <summary>Registers a record whose authoritative bytes are already frozen at executed repair start.</summary>
        /// <remarks>
        /// Use this on all peers only if capture reads a private snapshot that remains identical through native
        /// repair, such as metadata whose writers are suspended by the resync Started callback. Use
        /// <see cref="TryRegister{TPrepared}"/> when capture depends on repaired tiles, units or other changing state.
        /// Host capture runs after Started and before Vanilla executes Chore54, under the engine monitor. Client
        /// capture has the same timing as ordinary registration. Each peer prepares against its repaired world and
        /// publishes only at the existing completion gate. Capture, prepare and commit follow the threading,
        /// ownership and failure contract of ordinary registration, including the same owner GUID and record ID rules.
        /// The host can advertise the manifest early when every registered record uses early capture.
        /// </remarks>
        public static bool TryRegisterEarlyCapture<TPrepared>(string ownerGuid, string recordId, int schemaVersion,
            int maximumBytes, Func<byte[]> capture, Func<ReadOnlyMemory<byte>, TPrepared> prepare,
            Action<TPrepared> commit, out string reason) =>
            Register(ownerGuid, recordId, schemaVersion, maximumBytes, capture, prepare, commit, true, out reason);

        private static bool Register<TPrepared>(string ownerGuid, string recordId, int schemaVersion,
            int maximumBytes, Func<byte[]> capture, Func<ReadOnlyMemory<byte>, TPrepared> prepare,
            Action<TPrepared> commit, bool captureAtStart, out string reason)
        {
            if (capture == null || prepare == null || commit == null)
            { reason = "Capture, prepare and commit callbacks are required."; return false; }
            return MultiplayerResyncDataRuntime.TryRegister(new MultiplayerResyncDataRuntime.Record(
                ownerGuid, recordId, schemaVersion, maximumBytes, capture, bytes => prepare(bytes),
                value => commit((TPrepared)value), captureAtStart), out reason);
        }
    }
}

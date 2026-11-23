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

        /// <summary>Registers one required, versioned record on the Unity thread after native initialization and before multiplayer admission.</summary>
        /// <remarks>
        /// Register the same records on all peers. Capture returns a private canonical byte array, including an
        /// empty array for inactive data. Prepare validates host bytes against the repaired world and returns
        /// an owned, fully prepared replacement. It must not mutate simulation or retain native pointers.
        /// Commit only publishes that replacement. It must not allocate, block or throw.
        /// Callbacks run under the engine monitor on the native caller thread; never dispatch Unity work from
        /// them or enter a nested load, save or engine run. APIShared retains registrations for the process.
        /// </remarks>
        public static bool TryRegister<TPrepared>(string ownerGuid, string recordId, int schemaVersion,
            int maximumBytes, Func<byte[]> capture, Func<ReadOnlyMemory<byte>, TPrepared> prepare,
            Action<TPrepared> commit, out string reason)
        {
            if (capture == null || prepare == null || commit == null)
            { reason = "Capture, prepare and commit callbacks are required."; return false; }
            return MultiplayerResyncDataRuntime.TryRegister(new MultiplayerResyncDataRuntime.Record(
                ownerGuid, recordId, schemaVersion, maximumBytes, capture, bytes => prepare(bytes),
                value => commit((TPrepared)value)), out reason);
        }
    }
}

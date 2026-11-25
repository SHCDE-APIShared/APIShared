using System;

namespace APIShared.WorldMutations
{
    /// <summary>Process-owned effective world-mutation observations shared by independent consumers.</summary>
    /// <remarks>Register during startup after APIShared initialization. Calls are synchronous on the original
    /// publisher thread, never dispatched to Unity. Registrations remain rooted for the process lifetime;
    /// use the enabled predicate for logical activation. Observers cannot veto or replace originals.
    /// Callback and diagnostic failures are isolated, including failures during exception formatting.
    /// No event is replayed. Registering does not authorize native access from another thread.
    /// Existing source-specific APIs retain their original behavior and availability.</remarks>
    public static class WorldMutationEvents
    {
        /// <summary>Whether initialization has completed for at least one audited adapter.</summary>
        public static bool IsAvailable => WorldMutationRuntime.Registry.AvailableSources != WorldMutationSource.None;
        /// <summary>Sources whose complete outer/final-original adapters initialized successfully.</summary>
        public static WorldMutationSource AvailableSources => WorldMutationRuntime.Registry.AvailableSources;
        /// <summary>Registers an owner-local observer in order, owner GUID and ID ordinal order.</summary>
        /// <remarks>The enabled predicate is evaluated dynamically, must be cheap and must not mutate game state.
        /// The source mask selects invocation callbacks; outer batch completion can include nested sources.
        /// A successful registration does not claim that every declared source adapter is available.
        /// Missing sources remain absent; inspect AvailableSources and retain source-specific feature gates.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, IWorldMutationObserver observer,
            out string reason, int order = 0, WorldMutationSource sources = WorldMutationSource.All,
            Func<bool> enabled = null)
        {
            if (!IsAvailable) { reason = "World-mutation adapters are unavailable."; return false; }
            return WorldMutationRuntime.Registry.Register(ownerGuid, registrationId, observer, sources, enabled, order, out reason);
        }
        /// <summary>Reads the current identified native world. False during loading, retirement or repair.</summary>
        /// <remarks>Generation is local to this process and includes native-world-token changes within a single
        /// mission attempt. Read in a qualified synchronous publisher context. This takes no engine lock and
        /// does not authorize mutation. Ignore output values on false. No peer may use these local numbers as
        /// synchronized payload identities.</remarks>
        public static bool TryGetCurrentWorld(out long sessionId, out long worldGeneration)
        {
            try { return WorldMutationRuntime.Registry.TryGetWorld(out sessionId, out worldGeneration); }
            catch { sessionId = worldGeneration = 0; return false; }
        }
    }
}

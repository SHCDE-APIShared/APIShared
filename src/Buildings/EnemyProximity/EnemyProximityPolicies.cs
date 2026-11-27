using System;
using System.Threading;

namespace APIShared.Buildings
{
    /// <summary>Coordinates optional category radii through the existing Script Extender proximity event.</summary>
    /// <remarks>The Script Extender retains the live native hook. This service owns only a private,
    /// read-only native predicate and one event subscription. Without active contributions Vanilla runs.
    /// It does not change enemy building, wall, Keep, terrain or occupancy checks.</remarks>
    public static partial class EnemyProximityPolicies
    {
        internal static readonly EnemyProximityRegistry Registry = new EnemyProximityRegistry();

        /// <summary>Register once after APIShared native initialization, initially inactive.</summary>
        /// <remarks>Invalid/duplicate identity or unavailable event integration returns false with a reason.
        /// Registrations survive the process, including inactive ones. Callbacks execute synchronously on
        /// the native caller's thread, outside locks, ordered by phase, order and ordinal owner/ID.
        /// Each immutable result commits atomically; a throwing or invalid participant is discarded and
        /// later participants continue. Block is sticky. Recursive questions bypass policy callbacks.
        /// Do not touch UI, send network messages or mutate game state in a callback. There is no replay
        /// or implicit thread dispatch. Existing earlier SE skips are respected; unrelated later raw-SE
        /// subscribers are outside this broker's control. Query-backend failure leaves original radii
        /// active but does not prevent a valid block decision. Check QueryDiagnostic separately.</remarks>
        public static bool TryRegister(string ownerGuid, string registrationId, EnemyProximityPhase phase,
            Func<EnemyProximityContext, EnemyProximityDecision> callback,
            out EnemyProximityRegistration registration, out string error, int order = 0)
        {
            registration = null;
            if (Volatile.Read(ref integrationReady) == 0)
            { error = "Enemy proximity event integration is not initialized."; return false; }
            return Registry.TryRegister(ownerGuid, registrationId, phase, callback, out registration, out error, order);
        }

        /// <summary>Whether this exact identity registered successfully, even if logically inactive.</summary>
        public static bool HasRegistration(string ownerGuid, string registrationId) => Registry.HasRegistration(ownerGuid, registrationId);

        /// <summary>Independent availability of the read-only native category predicate.
        /// A non-available predicate does not disable registered block decisions.</summary>
        public static NativeCapabilityDiagnostic QueryDiagnostic => Volatile.Read(ref queryDiagnostic);
    }
}

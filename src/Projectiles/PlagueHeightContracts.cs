using System;

namespace APIShared
{
    /// <summary>Standing surfaces whose original plague immunity may be relaxed.</summary>
    [Flags]
    public enum PlagueSurfaceCategory
    {
        /// <summary>No surface override; a sole inactive/empty owner preserves Vanilla.</summary>
        None = 0,
        /// <summary>Low and high walls, including their stairs.</summary>
        Walls = 1,
        /// <summary>Walkable gatehouses.</summary>
        Gates = 2,
        /// <summary>Defensive towers; not mobile siege towers.</summary>
        Towers = 4,
        /// <summary>Keep platforms.</summary>
        Keeps = 8,
        /// <summary>All supported fortification surfaces.</summary>
        All = Walls | Gates | Towers | Keeps
    }

    /// <summary>Owner-bound, opt-in height-aware plague eligibility. Acquire through
    /// <see cref="ModApiClient.TryGetPlagueHeight"/> after API readiness.</summary>
    /// <remarks>Configure on the Unity/main thread before/after simulation, identically on all peers.
    /// No networking or mission authorization is provided. No callbacks or replay occur. Each owner
    /// starts inactive; repeated acquisition shares its policy. Active owners are unioned, with the
    /// greatest tolerance per category and greatest overall tolerance for ordinary ground. An owner
    /// cannot veto another owner. With no active category the complete Vanilla path is restored.
    /// Hooks and registrations persist to process exit; deactivation changes data only. Damage amount,
    /// resistance, unit-type immunity, timing and treatment remain Vanilla. Tolerance compares standing
    /// planes, inclusive, excluding the visual cloud offset. Unknown surfaces retain Vanilla exclusions.
    /// Invalid policy leaves the previous policy intact. Unsupported native builds affect only this service.</remarks>
    public interface IPlagueHeightCapability
    {
        /// <summary>Atomically replaces this owner's activation and policy. Tolerance is 0..65534
        /// native height units; invalid flags/range return ValidationFailed without changing state.</summary>
        bool TrySetPolicy(bool active, PlagueSurfaceCategory categories, int maximumHeightDifference,
            out NativeCapabilityDiagnostic diagnostic);
    }
}

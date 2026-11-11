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

    /// <summary>The effective plague standing-height policy the native damage path applies: the union of
    /// all active owners (<see cref="IPlagueHeightCapability"/>). Read it through
    /// <see cref="ModApiClient.TryGetPlagueHeightPolicy"/>, for example to predict where a cloud would damage
    /// a unit. A tolerance of -1 means no active owner enables that category, which keeps Vanilla's
    /// immunity there; with no category enabled the complete Vanilla path applies.</summary>
    /// <remarks>Ordinary ground uses <see cref="GroundTolerance"/> (the greatest category tolerance) while the
    /// policy is active. Tolerances are inclusive differences of standing planes in native height units.</remarks>
    public readonly struct PlagueHeightPolicy
    {
        /// <summary>Creates a policy view; each tolerance is -1 (category off) or 0..65534.</summary>
        public PlagueHeightPolicy(int walls, int gates, int towers, int keeps)
        {
            WallsTolerance = walls; GatesTolerance = gates; TowersTolerance = towers; KeepsTolerance = keeps;
        }
        /// <summary>Tolerance for walls and their stairs, or -1.</summary>
        public int WallsTolerance { get; }
        /// <summary>Tolerance for walkable gatehouses, or -1.</summary>
        public int GatesTolerance { get; }
        /// <summary>Tolerance for defensive towers, or -1.</summary>
        public int TowersTolerance { get; }
        /// <summary>Tolerance for keep platforms, or -1.</summary>
        public int KeepsTolerance { get; }
        /// <summary>Whether any category is enabled; otherwise the native path is Vanilla.</summary>
        public bool IsActive => WallsTolerance >= 0 || GatesTolerance >= 0 || TowersTolerance >= 0 || KeepsTolerance >= 0;
        /// <summary>Tolerance for ordinary ground while the policy is active (greatest category), or -1.</summary>
        public int GroundTolerance => Math.Max(Math.Max(WallsTolerance, GatesTolerance), Math.Max(TowersTolerance, KeepsTolerance));

        // Packed lanes of PlagueHeightPolicies: tolerance+1 per category, 0 for no owner.
        internal static PlagueHeightPolicy FromPacked(ulong packed) => new PlagueHeightPolicy(
            (int)(packed & 0xFFFF) - 1, (int)((packed >> 16) & 0xFFFF) - 1, (int)((packed >> 32) & 0xFFFF) - 1, (int)((packed >> 48) & 0xFFFF) - 1);
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

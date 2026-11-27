using System;
using System.Threading;
using APIShared.Modifiers;

namespace APIShared.Wildlife
{
    /// <summary>Immutable input of one chicken target decision. Requires APIShared 0.54.0 or later.</summary>
    public readonly struct ChickenPopulationContext
    {
        /// <summary>Creates a context, also usable by consumer tests.</summary>
        public ChickenPopulationContext(int playerId, int vanillaTarget, int currentChickens)
        { PlayerId = playerId; VanillaTarget = vanillaTarget; CurrentChickens = currentChickens; }

        /// <summary>One-based player, 1 through 8.</summary>
        public int PlayerId { get; }
        /// <summary>Vanilla's target for this player: 0 to 10 from a player stock (probably food), or 100 while
        /// Vanilla's own "many chickens" switch is set.</summary>
        public int VanillaTarget { get; }
        /// <summary>Vanilla's count of this player's chickens, the value the final target is compared with.</summary>
        public int CurrentChickens { get; }
    }

    /// <summary>Retained owner-bound chicken target policy. Keep it for the process lifetime.</summary>
    public sealed class ChickenPopulationModifierHandle
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<ChickenPopulationContext, ValueModifierContribution> Provider;
        internal int FailureReported;
        private int enabled;

        internal ChickenPopulationModifierHandle(string owner, string id, int order, Func<ChickenPopulationContext, ValueModifierContribution> provider)
        { Owner = owner; Id = id; Order = order; Provider = provider; }

        /// <summary>Registering plugin GUID.</summary>
        public string OwnerGuid => Owner;
        /// <summary>Stable ID within the owner.</summary>
        public string RegistrationId => Id;
        /// <summary>Whether the provider takes part in future decisions. Initially false.</summary>
        public bool IsEnabled => Volatile.Read(ref enabled) != 0;

        /// <summary>Enables or disables the provider. Any thread; takes effect at Vanilla's next decision.</summary>
        /// <remarks>Change it identically on every multiplayer peer, normally from synchronized settings before
        /// or after a mission, or inside a simulation tick callback; this does not synchronize anything.</remarks>
        public void SetEnabled(bool value) => ChickenPopulationRuntime.SetEnabled(this, value);

        internal bool SetEnabledFlag(bool value) => Interlocked.Exchange(ref enabled, value ? 1 : 0) != (value ? 1 : 0);
    }

    /// <summary>Staged modifiers of the number of chickens Vanilla keeps per player. Requires APIShared 0.54.0 or later.</summary>
    /// <remarks>
    /// <para>Vanilla's chicken routine (RVA 0xD29E0, a scheduled step of the simulation tick) computes a target
    /// per active player from a player stock, probably food (0 to 10; 100 with Vanilla's own switch), and compares it with the
    /// player's chicken count: below the target it spawns one chicken next to a random bakery, granary or mill of
    /// that player (none without one), above it it lets the first of the player's chickens fade out. Not in the map
    /// or siege editor (native game types 1 and 6).</para>
    /// <para>Providers see <see cref="ChickenPopulationContext"/> and return a
    /// <see cref="ValueModifierContribution"/> composed like <see cref="ValueModifierCalculator"/> from Vanilla's
    /// target, in ascending order, then ordinal owner GUID and ID; the result is rounded once and clamped to 0
    /// through <see cref="MaximumTarget"/>. Only how many chickens Vanilla aims for changes; when and where it
    /// spawns or removes one stays Vanilla's.</para>
    /// <para>Providers run on the simulation thread for every active player of each chicken step, without
    /// registry locks. They must be fast, deterministic on every multiplayer peer (simulation data and
    /// synchronized settings only), must not block, write native records or touch UI. An exception disables that
    /// provider's contribution for that decision and is reported once. Contributions of fixed values only are
    /// composed without allocation.</para>
    /// <para>Register on the initialized Unity thread during startup, before the first mission of the process:
    /// the native bridge installs on the first registration and only then. Handles are never removed; switch
    /// them with <see cref="ChickenPopulationModifierHandle.SetEnabled"/>. Without an enabled handle the bridge
    /// does not leave native code. Bound to the audited native image.</para>
    /// </remarks>
    public static class ChickenPopulationModifiers
    {
        /// <summary>The largest target a composition can produce.</summary>
        public const int MaximumTarget = 1000;

        /// <summary>Whether providers can be registered: the native contract validated and the bridge is installed
        /// or can still be installed.</summary>
        public static bool IsAvailable => ChickenPopulationRuntime.IsAvailable;

        /// <summary>Why the service is unavailable; empty when it is available.</summary>
        public static string UnavailableReason => ChickenPopulationRuntime.UnavailableReason;

        /// <summary>Registers an initially disabled provider.</summary>
        /// <param name="ownerGuid">Your stable plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within the owner.</param>
        /// <param name="provider">Returns this owner's contribution; <see cref="ValueModifierContribution.None"/> for none.</param>
        /// <param name="handle">The process-held handle on success.</param>
        /// <param name="reason">Empty on success; otherwise why nothing was registered.</param>
        /// <param name="order">Ascending composition order.</param>
        /// <returns>False for invalid arguments, a duplicate identity, the wrong thread, a closed installation
        /// window or an unavailable service.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<ChickenPopulationContext, ValueModifierContribution> provider,
            out ChickenPopulationModifierHandle handle, out string reason, int order = 0) =>
            ChickenPopulationRuntime.TryRegister(ownerGuid, registrationId, provider, order, out handle, out reason);
    }
}

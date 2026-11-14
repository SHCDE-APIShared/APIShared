using System;
using System.Threading;

namespace APIShared.Buildings
{
    /// <summary>A rule's decision for one build-menu mapper. Requires APIShared 0.54.0 or later.</summary>
    public enum BuildMenuAvailabilityDecision : byte
    {
        /// <summary>No opinion: Vanilla decides.</summary>
        Default = 0,
        /// <summary>The mapper is available although a Vanilla game rule refuses it, for example the Hunter's hut on
        /// a skirmish or multiplayer map that started without deer and goats. A mapper whose entry in the mission's
        /// own availability table is off stays unavailable, as do mappers Vanilla already allows unchanged. That table
        /// covers mappers below 0x1B8 only; a higher mapper becomes available unconditionally.</summary>
        Allow = BuildMenuAvailabilityAssembly.Allow,
        /// <summary>The mapper is unavailable, whatever Vanilla and other rules decide. Any veto wins.</summary>
        Veto = BuildMenuAvailabilityAssembly.Veto
    }

    /// <summary>Process-held rule of one owner. Keep it for the process lifetime.</summary>
    public sealed class BuildMenuAvailabilityRule
    {
        internal readonly byte[] Decisions = new byte[BuildMenuAvailabilityAssembly.MapperCapacity];
        private int enabled = 1;

        internal BuildMenuAvailabilityRule(string ownerGuid, string registrationId)
        { OwnerGuid = ownerGuid; RegistrationId = registrationId; }

        /// <summary>Registering plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable ID within the owner.</summary>
        public string RegistrationId { get; }
        /// <summary>Whether the rule's decisions count. Initially true.</summary>
        public bool IsEnabled => Volatile.Read(ref enabled) != 0;

        /// <summary>Sets this rule's decision for one mapper. Any thread; takes effect at the next native query.</summary>
        /// <param name="mapper">The mapper ID (<c>eMappers</c>), from 1 through <see cref="BuildMenuAvailability.MaximumMapper"/>.</param>
        /// <param name="decision">The decision; <see cref="BuildMenuAvailabilityDecision.Default"/> withdraws it.</param>
        /// <param name="reason">Empty on success; otherwise why nothing changed.</param>
        public bool TrySetDecision(int mapper, BuildMenuAvailabilityDecision decision, out string reason) =>
            BuildMenuAvailabilityRuntime.TrySet(this, mapper, decision, out reason);

        /// <summary>This rule's current decision for a mapper; Default for an ID outside the table.</summary>
        public BuildMenuAvailabilityDecision GetDecision(int mapper) =>
            mapper < 1 || mapper > BuildMenuAvailability.MaximumMapper ? BuildMenuAvailabilityDecision.Default
                : (BuildMenuAvailabilityDecision)Volatile.Read(ref Decisions[mapper]);

        /// <summary>Withdraws or restores all of this rule's decisions logically; the decisions are kept.</summary>
        public void SetEnabled(bool value) => BuildMenuAvailabilityRuntime.SetEnabled(this, value);

        /// <summary>Withdraws all of this rule's decisions.</summary>
        public void ClearAll() => BuildMenuAvailabilityRuntime.Clear(this);

        internal bool SetEnabledFlag(bool value) => Interlocked.Exchange(ref enabled, value ? 1 : 0) != (value ? 1 : 0);
    }

    /// <summary>Owner-bound rules that make build-menu mappers (buildings and other placement tools) available
    /// or unavailable for the local player, relative to Vanilla's decision. Requires APIShared 0.54.0 or later.</summary>
    /// <remarks>
    /// <para>Vanilla asks one native function (RVA 0xFFC70) whether a mapper may be used: the build menu, its
    /// hotkeys and the placement check of the managed UI, and the local game state the UI shows. It has no
    /// player argument; it decides for the player this game shows. The simulation, the AI and other peers never
    /// ask it, so a rule changes no simulation state by itself. In multiplayer each peer decides its own menu:
    /// register identical rules and drive them from synchronized settings, so that every player has the same
    /// options. A rule may decide per player by setting its decisions from the local player's ID.</para>
    /// <para>Composition per mapper: any enabled <see cref="BuildMenuAvailabilityDecision.Veto"/> makes it
    /// unavailable; otherwise any enabled <see cref="BuildMenuAvailabilityDecision.Allow"/> applies; otherwise
    /// Vanilla decides. Order and owner do not matter.</para>
    /// <para>Register on the initialized Unity thread during startup, before the first mission of the process:
    /// the native wrapper installs on the first registration and only then, because no thread runs the function
    /// while the Unity thread registers outside a mission. A later first registration fails. Decisions may change
    /// at any time and from any thread. Rules are never removed; switch them with
    /// <see cref="BuildMenuAvailabilityRule.SetEnabled"/>. Bound to the audited native image.</para>
    /// </remarks>
    public static class BuildMenuAvailability
    {
        /// <summary>The highest mapper ID a rule can decide.</summary>
        public const int MaximumMapper = BuildMenuAvailabilityAssembly.MapperCapacity - 1;

        /// <summary>Whether rules can be registered: the native contract validated and the wrapper is installed
        /// or can still be installed.</summary>
        public static bool IsAvailable => BuildMenuAvailabilityRuntime.IsAvailable;

        /// <summary>Why the service is unavailable; empty when it is available.</summary>
        public static string UnavailableReason => BuildMenuAvailabilityRuntime.UnavailableReason;

        /// <summary>Registers a rule without decisions.</summary>
        /// <param name="ownerGuid">Your stable plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within the owner.</param>
        /// <param name="rule">The process-held rule on success.</param>
        /// <param name="reason">Empty on success; otherwise why nothing was registered.</param>
        /// <returns>False for invalid arguments, a duplicate identity, the wrong thread, a closed installation
        /// window or an unavailable service.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, out BuildMenuAvailabilityRule rule, out string reason) =>
            BuildMenuAvailabilityRuntime.TryRegister(ownerGuid, registrationId, out rule, out reason);
    }
}

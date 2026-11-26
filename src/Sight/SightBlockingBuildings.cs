using SHCDESE.Interop;

namespace APIShared.Sight
{
    /// <summary>
    /// Lets mods make building types that Vanilla's sight test looks through block sight like ordinary buildings.
    /// </summary>
    /// <remarks>
    /// <para>Vanilla's sight test (<see cref="LineOfSight"/>, and every unit sight check of the game) asks the shared
    /// obstacle-height helper (RVA 0x6B990) for the height of each building it crosses. That helper sends a few
    /// building types to a special case that adds nothing to the terrain height when it is asked for sight; on the
    /// audited image this is only <see cref="eStructs.STRUCT_HUNTERS_HUT"/>, so Hunters and archers see through
    /// Hunter's huts. While an enabled registration exists for such a type, the helper uses the ordinary building
    /// case for it instead: the type's own value of Vanilla's building-height table (40 for the Hunter's hut, the
    /// same as the Woodcutter's hut). Arrows and other projectiles ask the helper in its other mode, where Vanilla
    /// already uses that ordinary value, so their flight does not change.</para>
    /// <para>Only types for which <see cref="IsExemptInVanilla"/> is true can be registered; other types already block
    /// with their own rule. Registrations of several owners for one type combine: the type blocks while at least one
    /// of them is enabled. Without an enabled registration Vanilla's behaviour is unchanged.</para>
    /// <para>Registration: on the initialized Unity thread, during startup before the first mission, because the
    /// first registration installs a native reader hook (RVA 0x6BA0C) that must not be written while the simulation
    /// can run it. The hook stays for the process lifetime; enabling only flips a byte the hook reads. Registrations
    /// are permanent and start disabled.</para>
    /// <para>Multiplayer: the rule changes which targets Vanilla's units see, which is gameplay. Every peer must hold
    /// the same enabled state at the same simulation tick: change it outside a running mission (for example in a
    /// mission-initialization callback) or inside a <see cref="Simulation.SimulationTickEvents"/> callback, from
    /// synchronized settings only.</para>
    /// <para>Availability: bound to the audited native image; see <see cref="UnavailableReason"/>.</para>
    /// </remarks>
    public static class SightBlockingBuildings
    {
        /// <summary>Most registrations accepted in one process.</summary>
        public const int MaximumRegistrations = 64;

        /// <summary>Whether the native contract validated, so that registrations can install the hook.</summary>
        public static bool IsAvailable => SightBlockingBuildingsNative.IsAvailable;

        /// <summary>Empty while available; otherwise why registrations fail.</summary>
        public static string UnavailableReason => SightBlockingBuildingsNative.UnavailableReason;

        /// <summary>Whether Vanilla's sight test ignores this building type (the helper's special case).</summary>
        /// <param name="buildingType">Building type.</param>
        /// <returns>False for every other type and while the service is unavailable.</returns>
        public static bool IsExemptInVanilla(eStructs buildingType) => SightBlockingBuildingsNative.IsExemptInVanilla(buildingType);

        /// <summary>Whether an enabled registration currently makes this type block sight.</summary>
        /// <param name="buildingType">Building type.</param>
        public static bool IsBlocking(eStructs buildingType) => SightBlockingBuildingsNative.IsBlocking(buildingType);

        /// <summary>Registers a sight-blocking rule for one exempt building type for the process lifetime.</summary>
        /// <param name="ownerGuid">Your stable mod GUID.</param>
        /// <param name="registrationId">Stable ID, unique within this owner.</param>
        /// <param name="buildingType">A type for which <see cref="IsExemptInVanilla"/> is true.</param>
        /// <param name="rule">The disabled registration on success.</param>
        /// <param name="reason">Empty on success; otherwise why registration failed.</param>
        /// <returns>Whether the rule was registered and the hook is installed.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, eStructs buildingType,
            out SightBlockingBuildingRule rule, out string reason) =>
            SightBlockingBuildingsNative.TryRegister(ownerGuid, registrationId, buildingType, out rule, out reason);
    }

    /// <summary>One mod's sight-blocking rule for one building type; see <see cref="SightBlockingBuildings"/>.</summary>
    public sealed class SightBlockingBuildingRule
    {
        internal SightBlockingBuildingRule(string ownerGuid, string registrationId, eStructs buildingType)
        {
            OwnerGuid = ownerGuid;
            RegistrationId = registrationId;
            BuildingType = buildingType;
        }

        /// <summary>The registering mod's GUID.</summary>
        public string OwnerGuid { get; }

        /// <summary>The owner-local registration ID.</summary>
        public string RegistrationId { get; }

        /// <summary>The building type this rule makes block sight.</summary>
        public eStructs BuildingType { get; }

        /// <summary>Whether this rule is currently enabled.</summary>
        public bool IsEnabled => SightBlockingBuildingsNative.IsEnabled(this);

        /// <summary>Enables or disables this rule; takes effect for the next sight test.</summary>
        /// <remarks>Any thread; serialized internally. Follow the multiplayer timing rule of
        /// <see cref="SightBlockingBuildings"/>. Repeating the current state changes nothing.</remarks>
        /// <param name="enabled">True: the type blocks sight while this or another rule for it is enabled.</param>
        public void SetEnabled(bool enabled) => SightBlockingBuildingsNative.SetEnabled(this, enabled);
    }
}

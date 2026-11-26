using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Buildings
{
    /// <summary>
    /// Named actions on a player's building that other mods can run for that player, for example the switch behind a
    /// mod's button in a building panel. The registering mod keeps the meaning, its activation rules and its own control;
    /// APIShared validates the building and runs the executor. A mod that shows another player's building panel (a
    /// <see cref="Presentation.BuildingPanelSections.TryRegisterPresenter">building panel presenter</see>) receives the
    /// clicks on such controls and runs them through its own authorized path, which ends in <see cref="TryExecute"/>.
    /// </summary>
    /// <remarks>
    /// <para>APIShared only dispatches: it adds no transport, authorization or persistence. In multiplayer the caller of
    /// <see cref="TryExecute"/> runs it on every peer in the same tick (an authenticated Chore it owns); the executor must
    /// be deterministic. Registrations are process-rooted, ordered by ordinal owner GUID and action ID, and cannot be
    /// removed; an executor refuses while its feature is inactive.</para>
    /// </remarks>
    public static unsafe class BuildingActions
    {
        internal static readonly BuildingActionRegistry Registry = new BuildingActionRegistry(Events.InterceptionDiagnostics.Report);

        /// <summary>Registers an action. Any thread; register once at startup, identically on every peer.</summary>
        /// <returns>False for invalid options or a duplicate owner/action ID pair.</returns>
        public static bool TryRegister(BuildingActionOptions options, out string reason) => Registry.TryRegister(options, out reason);

        /// <summary>Whether an action with this owner GUID and ID is registered.</summary>
        public static bool IsRegistered(string ownerGuid, string actionId) => Registry.Find(ownerGuid, actionId) != null;

        /// <summary>A stable 32-bit key of an action identity for compact transports such as a Chore field: FNV-1a (32 bit)
        /// over the UTF-16 code units of <paramref name="ownerGuid"/>, '/', <paramref name="actionId"/>. The same in every
        /// process and APIShared version; resolve it with <see cref="TryGetActionByKey"/>.</summary>
        public static int GetActionKey(string ownerGuid, string actionId) => BuildingActionRegistry.KeyOf(ownerGuid, actionId);

        /// <summary>Resolves a key from <see cref="GetActionKey"/>, for example one received from a peer, to the registered action.
        /// False when no registered action, or more than one, has this key.</summary>
        public static bool TryGetActionByKey(int key, out string ownerGuid, out string actionId)
        {
            BuildingActionRegistry.Entry entry = Registry.FindByKey(key);
            ownerGuid = entry?.Owner;
            actionId = entry?.Id;
            return entry != null;
        }

        /// <summary>Whether the action is registered and applies to buildings of this type.</summary>
        public static bool AppliesTo(string ownerGuid, string actionId, eStructs buildingType) =>
            Registry.Find(ownerGuid, actionId)?.Types.Contains(buildingType) == true;

        /// <summary>Validates the building and runs the action's executor synchronously.</summary>
        /// <remarks>Call only from deterministic simulation code holding EngineInterface's engine monitor: an authenticated
        /// Chore handler running on every peer in the same tick, or a single-player engine action. The building must be
        /// alive, still carry the request's GlobalId, have one of the action's types and belong to the acting player.
        /// Executor exceptions are logged and reported as a refusal.</remarks>
        /// <param name="request">The request.</param>
        /// <param name="reason">Why nothing was applied, or the executor's optional success text (an empty string without one).</param>
        public static bool TryExecute(BuildingActionRequest request, out string reason)
        {
            if (request == null) { reason = "A request is required."; return false; }
            BuildingActionRegistry.Entry entry = Registry.Find(request.ActionOwnerGuid, request.ActionId);
            if (entry == null) { reason = "No building action " + request.ActionOwnerGuid + "/" + request.ActionId + " is registered."; return false; }
            if (request.BuildingId < 1 || !BuildingAccess.TryGetById(request.BuildingId, out GameBuilding* building, out _) ||
                building == null || building->r_AliveState != AliveState.IsAlive || building->r_GlobalId != request.BuildingGlobalId)
            { reason = "Building " + request.BuildingId + " no longer exists."; return false; }
            if (request.ActingPlayerId < 1 || building->r_PlayerIdOwner != request.ActingPlayerId)
            { reason = "Building " + request.BuildingId + " belongs to player " + building->r_PlayerIdOwner + "."; return false; }
            if (!entry.Types.Contains(building->r_BuildingType))
            { reason = "The action does not apply to " + building->r_BuildingType + "."; return false; }
            return Registry.TryExecute(entry, request, out reason);
        }
    }
}

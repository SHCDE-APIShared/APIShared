using System;
using System.Collections.Generic;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    /// <summary>One request to run a registered building action for a player, as built by a building panel presenter
    /// (<see cref="Presentation.BuildingPanelSections.TrySubmitAction"/>) or by any mod that triggers the action itself.
    /// Immutable; it carries no authorization: <see cref="BuildingActions.TryExecute"/> checks the building again.</summary>
    public sealed class BuildingActionRequest
    {
        /// <summary>Creates a request.</summary>
        /// <param name="actionOwnerGuid">GUID of the mod that registered the action.</param>
        /// <param name="actionId">The action's owner-local ID.</param>
        /// <param name="actingPlayerId">1-based player the action runs for; must own the building at execution.</param>
        /// <param name="buildingId">1-based building ID.</param>
        /// <param name="buildingGlobalId">The building's GlobalId when the request was made.</param>
        /// <param name="argument">Action-defined value, for example a target state; the action documents its meaning.</param>
        public BuildingActionRequest(string actionOwnerGuid, string actionId, int actingPlayerId, int buildingId,
            uint buildingGlobalId, int argument)
        {
            ActionOwnerGuid = actionOwnerGuid; ActionId = actionId; ActingPlayerId = actingPlayerId;
            BuildingId = buildingId; BuildingGlobalId = buildingGlobalId; Argument = argument;
        }

        /// <summary>GUID of the mod that registered the action.</summary>
        public string ActionOwnerGuid { get; }
        /// <summary>The action's owner-local ID.</summary>
        public string ActionId { get; }
        /// <summary>1-based player the action runs for.</summary>
        public int ActingPlayerId { get; }
        /// <summary>1-based building ID.</summary>
        public int BuildingId { get; }
        /// <summary>The building's GlobalId when the request was made; ID plus GlobalId identify the building.</summary>
        public uint BuildingGlobalId { get; }
        /// <summary>Action-defined value.</summary>
        public int Argument { get; }

        /// <inheritdoc />
        public override string ToString() =>
            ActionOwnerGuid + "/" + ActionId + " player=" + ActingPlayerId + " building=" + BuildingId + "/" + BuildingGlobalId +
            " argument=" + Argument;
    }

    /// <summary>Runs a building action in deterministic simulation code; see <see cref="BuildingActions.TryExecute"/>.</summary>
    /// <param name="request">The validated request: the building is alive, carries the request's GlobalId, has one of the
    /// action's types and is owned by <see cref="BuildingActionRequest.ActingPlayerId"/>.</param>
    /// <param name="reason">Why the action refused; on success an optional short result text for the caller's log, or null.</param>
    /// <returns>Whether the action changed the game as the owner's own control would.</returns>
    public delegate bool BuildingActionExecutor(BuildingActionRequest request, out string reason);

    /// <summary>Describes an action for <see cref="BuildingActions.TryRegister"/>. Read once during registration.</summary>
    public sealed class BuildingActionOptions
    {
        /// <summary>Creates options.</summary>
        /// <param name="ownerGuid">The registering plugin's GUID.</param>
        /// <param name="actionId">Stable owner-local action ID; other mods address the action with both values.</param>
        /// <param name="buildingTypes">Building types the action applies to; at least one.</param>
        /// <param name="execute">Executor; see <see cref="BuildingActionExecutor"/>.</param>
        public BuildingActionOptions(string ownerGuid, string actionId, IEnumerable<eStructs> buildingTypes, BuildingActionExecutor execute)
        {
            OwnerGuid = ownerGuid; ActionId = actionId; Execute = execute;
            BuildingTypes = buildingTypes == null ? null : new List<eStructs>(buildingTypes).AsReadOnly();
        }

        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local action ID.</summary>
        public string ActionId { get; }
        /// <summary>Building types the action applies to.</summary>
        public IReadOnlyList<eStructs> BuildingTypes { get; }
        /// <summary>The executor.</summary>
        public BuildingActionExecutor Execute { get; }
    }
}

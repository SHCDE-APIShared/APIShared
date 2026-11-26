using System;
using System.Collections.Generic;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    /// <summary>Immutable description of the building whose Vanilla panel is open. Presentation data only:
    /// it grants no command rights; commands must authorize themselves again where they execute.</summary>
    public sealed class BuildingPanelContext
    {
        internal BuildingPanelContext(int buildingId, uint globalId, eStructs type, int owner, int local, int viewed, long generation)
            : this(buildingId, globalId, type, owner, local, viewed, generation, local, null, null) { }

        internal BuildingPanelContext(int buildingId, uint globalId, eStructs type, int owner, int local, int viewed, long generation,
            int acting, string presenterOwner, string presenterId)
        {
            BuildingId = buildingId; GlobalId = globalId; BuildingType = type; OwnerPlayerId = owner;
            LocalPlayerId = local; ViewedPlayerId = viewed; PanelGeneration = generation;
            ActingPlayerId = acting; PresenterOwnerGuid = presenterOwner; PresenterRegistrationId = presenterId;
        }
        /// <summary>One-based building game ID of the open panel.</summary>
        public int BuildingId { get; }
        /// <summary>The building's GlobalId when the panel was read; never zero. ID plus GlobalId identify the building.</summary>
        public uint GlobalId { get; }
        /// <summary>The building's type from its native record.</summary>
        public eStructs BuildingType { get; }
        /// <summary>One-based owning player from the native record.</summary>
        public int OwnerPlayerId { get; }
        /// <summary>Locally controlled player, or a value of zero or less when unavailable (for example spectating).</summary>
        public int LocalPlayerId { get; }
        /// <summary>Player whose reports and view are shown, independent of local control.</summary>
        public int ViewedPlayerId { get; }
        /// <summary>Whether the locally controlled player owns this building.</summary>
        public bool IsOwnedByLocalPlayer => LocalPlayerId > 0 && OwnerPlayerId == LocalPlayerId;
        /// <summary>Changes whenever the building HUD instance is replaced; not a mission or network identity.</summary>
        public long PanelGeneration { get; }
        /// <summary>Since 0.73.0: the player the panel's controls act for. For Vanilla's own selection this is
        /// <see cref="LocalPlayerId"/>; for a presented panel it is the player the presenter acts for.</summary>
        public int ActingPlayerId { get; }
        /// <summary>Since 0.73.0: GUID of the presenter that shows this building, or null for Vanilla's own selection.</summary>
        public string PresenterOwnerGuid { get; }
        /// <summary>Since 0.73.0: the presenter's owner-local registration ID, or null for Vanilla's own selection.</summary>
        public string PresenterRegistrationId { get; }
        /// <summary>Since 0.73.0: whether a presenter shows this building on another mod's behalf
        /// (see <see cref="BuildingPanelSections.TryRegisterPresenter"/>).</summary>
        public bool IsPresented => PresenterOwnerGuid != null;
        /// <summary>Since 0.73.0: whether the acting player owns this building, so the panel may offer the owner's
        /// controls. For a presented panel, route them with <see cref="BuildingPanelSections.TrySubmitAction"/>.</summary>
        public bool CanActAsOwner => ActingPlayerId > 0 && OwnerPlayerId == ActingPlayerId;

        internal bool Same(int buildingId, uint globalId, eStructs type, int owner, int local, int viewed, long generation,
            int acting, string presenterOwner, string presenterId) =>
            BuildingId == buildingId && GlobalId == globalId && BuildingType == type && OwnerPlayerId == owner &&
            LocalPlayerId == local && ViewedPlayerId == viewed && PanelGeneration == generation &&
            ActingPlayerId == acting && PresenterOwnerGuid == presenterOwner && PresenterRegistrationId == presenterId;
    }

    /// <summary>Since 0.73.0: Unity thread, on every GUI check while sections exist: the building the presenter's
    /// building panel shows right now.</summary>
    /// <param name="buildingId">1-based building ID of the shown building.</param>
    /// <param name="actingPlayerId">1-based player the presenter acts for (normally the building's owner).</param>
    /// <returns>False while the presenter shows no building panel. Keep it cheap and allocation-free.</returns>
    public delegate bool BuildingPanelPresentationQuery(out int buildingId, out int actingPlayerId);

    /// <summary>Since 0.73.0: Unity thread: a section's control on the presenter's panel asks to run a building action.
    /// The presenter starts its own authorized execution (an authenticated Chore on every peer, or a single-player engine
    /// action) that ends in <see cref="Buildings.BuildingActions.TryExecute"/>. It must not apply anything locally ahead
    /// of a multiplayer Chore.</summary>
    /// <param name="request">The action, acting player and building of the presented panel.</param>
    /// <param name="reason">Why the presenter refused, or null.</param>
    /// <returns>Whether the request was accepted for execution (not that it was applied already).</returns>
    public delegate bool BuildingPanelActionSubmit(Buildings.BuildingActionRequest request, out string reason);

    /// <summary>Since 0.73.0: describes a building panel presenter for <see cref="BuildingPanelSections.TryRegisterPresenter"/>:
    /// a mod that shows Vanilla's building panel for a building other than the local player's own selection, for
    /// example a spectator tool or a foreign-control mod. Read once during registration.</summary>
    public sealed class BuildingPanelPresenterOptions
    {
        /// <summary>Creates options.</summary>
        /// <param name="ownerGuid">The presenter plugin's GUID.</param>
        /// <param name="registrationId">Stable owner-local ID.</param>
        /// <param name="query">The building its panel shows; see <see cref="BuildingPanelPresentationQuery"/>.</param>
        /// <param name="submit">Runs actions for the shown building; see <see cref="BuildingPanelActionSubmit"/>.</param>
        public BuildingPanelPresenterOptions(string ownerGuid, string registrationId, BuildingPanelPresentationQuery query,
            BuildingPanelActionSubmit submit)
        {
            OwnerGuid = ownerGuid; RegistrationId = registrationId; Query = query; Submit = submit;
        }

        /// <summary>The presenter plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local ID.</summary>
        public string RegistrationId { get; }
        /// <summary>The shown-building query.</summary>
        public BuildingPanelPresentationQuery Query { get; }
        /// <summary>The action submit callback.</summary>
        public BuildingPanelActionSubmit Submit { get; }
        /// <summary>Order among presenters, ascending, then ordinal owner GUID and ID: the first presenter that reports a
        /// building owns the panel.</summary>
        public int Order { get; set; }
    }

    /// <summary>Unity-thread factory input. Create and return a fresh, unattached control; APIShared adds it to
    /// <see cref="Host"/>. Do not retain the host or the control across HUD recreation.</summary>
    public sealed class BuildingPanelSectionFactoryContext
    {
        internal BuildingPanelSectionFactoryContext(FrameworkElement host, long generation) { Host = host; PanelGeneration = generation; }
        /// <summary>APIShared's section host inside Vanilla's building HUD, for resource lookups such as
        /// <c>Host.TryFindResource("UISprites I039")</c>. Do not change its layout, children or DataContext.</summary>
        public FrameworkElement Host { get; }
        /// <summary>Generation of the HUD instance the control is created for.</summary>
        public long PanelGeneration { get; }
    }

    /// <summary>Immutable callback payload, reused from Opened through Closed of one showing.</summary>
    public sealed class BuildingPanelSectionEventArgs
    {
        internal BuildingPanelSectionEventArgs(BuildingPanelContext context, FrameworkElement content)
        { Context = context; Content = content; }
        /// <summary>The building this showing belongs to.</summary>
        public BuildingPanelContext Context { get; }
        /// <summary>The control the section's factory created for the current HUD instance.</summary>
        public FrameworkElement Content { get; }
    }

    /// <summary>Describes a building panel section for <see cref="BuildingPanelSections.TryRegister"/>.
    /// Read once during registration; later changes have no effect.</summary>
    public sealed class BuildingPanelSectionOptions
    {
        /// <summary>Creates options for an owner-local section.</summary>
        /// <param name="ownerGuid">The registering plugin's GUID.</param>
        /// <param name="registrationId">Stable owner-local section ID.</param>
        /// <param name="buildingTypes">Building types whose panel can show the section; at least one.</param>
        /// <param name="factory">Creates the section's control once per HUD instance on Unity; see
        /// <see cref="BuildingPanelSectionFactoryContext"/>. Null results and exceptions disable the section
        /// for that HUD instance and are logged once.</param>
        public BuildingPanelSectionOptions(string ownerGuid, string registrationId, IEnumerable<eStructs> buildingTypes,
            Func<BuildingPanelSectionFactoryContext, FrameworkElement> factory)
        {
            OwnerGuid = ownerGuid; RegistrationId = registrationId; Factory = factory;
            BuildingTypes = buildingTypes == null ? null : new List<eStructs>(buildingTypes).AsReadOnly();
        }
        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local section ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Building types whose panel can show the section.</summary>
        public IReadOnlyList<eStructs> BuildingTypes { get; }
        /// <summary>Control factory.</summary>
        public Func<BuildingPanelSectionFactoryContext, FrameworkElement> Factory { get; }
        /// <summary>Callback order among sections, ascending, then ordinal owner GUID and ID. Sections position
        /// themselves and should not overlap; their drawing order is unspecified.</summary>
        public int Order { get; set; }
        /// <summary>Optional dynamic condition, evaluated on every GUI check while a matching panel is open,
        /// for example local ownership or a feature switch. Null shows the section for every matching panel.
        /// Exceptions count as false and are logged once.</summary>
        public Func<BuildingPanelContext, bool> ShowWhen { get; set; }
        /// <summary>Optional: the section becomes visible for a building; initialize the control here.</summary>
        public Action<BuildingPanelSectionEventArgs> Opened { get; set; }
        /// <summary>Optional: every later GUI check while the section stays visible for the same building,
        /// including while paused. Keep it cheap; update the control only on real changes.</summary>
        public Action<BuildingPanelSectionEventArgs> Updated { get; set; }
        /// <summary>Optional: the section was hidden (panel closed, another building, condition false, HUD
        /// replaced). Receives the arguments of the ending showing.</summary>
        public Action<BuildingPanelSectionEventArgs> Closed { get; set; }
    }

    /// <summary>Process-lifetime handle of a registered section. Read on the initialized Unity thread.</summary>
    public sealed class BuildingPanelSectionRegistration
    {
        private readonly BuildingPanelSectionEntry entry;
        internal BuildingPanelSectionRegistration(BuildingPanelSectionEntry entry) { this.entry = entry; }
        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid => entry.Owner;
        /// <summary>Owner-local section ID.</summary>
        public string RegistrationId => entry.Id;
        /// <summary>Arguments of the current showing, or null while the section is hidden.</summary>
        public BuildingPanelSectionEventArgs Current => entry.Shown;
    }
}

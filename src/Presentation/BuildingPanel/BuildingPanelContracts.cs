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
        {
            BuildingId = buildingId; GlobalId = globalId; BuildingType = type; OwnerPlayerId = owner;
            LocalPlayerId = local; ViewedPlayerId = viewed; PanelGeneration = generation;
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

        internal bool Same(int buildingId, uint globalId, eStructs type, int owner, int local, int viewed, long generation) =>
            BuildingId == buildingId && GlobalId == globalId && BuildingType == type && OwnerPlayerId == owner &&
            LocalPlayerId == local && ViewedPlayerId == viewed && PanelGeneration == generation;
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

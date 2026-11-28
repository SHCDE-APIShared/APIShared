using System;
using System.Collections.Generic;
using Noesis;
using SHCDESE.Interop;

namespace APIShared.Presentation
{
    /// <summary>Describes a replacement of Vanilla's recruitment area for one building variant, for
    /// <see cref="BuildingPanelSections.TryRegisterRecruitmentArea"/>: the buildings of some native types that
    /// <see cref="Selects"/> picks, for example a custom barracks built on the native stone barracks.
    /// Read once during registration; later changes have no effect.</summary>
    /// <remarks>Any number of owners can register variants on the same native type. Two replacements compete
    /// only when both select the same open building; the fixed precedence never depends on registration order
    /// (see <see cref="BuildingPanelSections.TryRegisterRecruitmentArea"/>). A variant is identified by owner GUID
    /// and variant ID, not by a native ID.</remarks>
    public sealed class BuildingPanelRecruitmentAreaOptions
    {
        /// <summary>Creates options for an owner-local building variant.</summary>
        /// <param name="ownerGuid">The registering plugin's GUID.</param>
        /// <param name="variantId">Stable owner-local ID of the building variant this replacement serves.</param>
        /// <param name="buildingTypes">Native types the variant is built on; at least one. A cheap prefilter
        /// before <see cref="Selects"/>.</param>
        /// <param name="factory">Creates the replacement control once per HUD instance on Unity, like a section
        /// factory: APIShared adds it collapsed to the section host (an 800×170 Grid aligned like Vanilla's
        /// building panels), so the control positions itself as Vanilla's recruitment area would. Null results
        /// and exceptions disable the replacement for that HUD instance and are logged once.</param>
        public BuildingPanelRecruitmentAreaOptions(string ownerGuid, string variantId, IEnumerable<eStructs> buildingTypes,
            Func<BuildingPanelSectionFactoryContext, FrameworkElement> factory)
        {
            OwnerGuid = ownerGuid; VariantId = variantId; Factory = factory;
            BuildingTypes = buildingTypes == null ? null : new List<eStructs>(buildingTypes).AsReadOnly();
        }
        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local variant ID.</summary>
        public string VariantId { get; }
        /// <summary>Native types the variant is built on.</summary>
        public IReadOnlyList<eStructs> BuildingTypes { get; }
        /// <summary>Control factory.</summary>
        public Func<BuildingPanelSectionFactoryContext, FrameworkElement> Factory { get; }
        /// <summary>Optional variant selector among buildings of <see cref="BuildingTypes"/>, evaluated on every
        /// GUI check while such a panel is open; keep it cheap, allocation-free and free of side effects. Null
        /// selects every building of the types. Exceptions count as false and are logged once; the replacement
        /// then stays out of that context until the panel shows another one.</summary>
        public Func<BuildingPanelContext, bool> Selects { get; set; }
        /// <summary>Optional: the replacement became visible for a building; initialize the control here.</summary>
        public Action<BuildingPanelSectionEventArgs> Opened { get; set; }
        /// <summary>Optional: every later GUI check while it stays visible for the same building, including while
        /// paused. Keep it cheap.</summary>
        public Action<BuildingPanelSectionEventArgs> Updated { get; set; }
        /// <summary>Optional: the replacement was hidden (another building, panel closed, disabled, a higher
        /// precedence replacement now matches, HUD replaced). Receives the arguments of the ending showing.</summary>
        public Action<BuildingPanelSectionEventArgs> Closed { get; set; }
    }

    /// <summary>Process-lifetime handle of a registered recruitment-area replacement. Unity thread only.</summary>
    public sealed class BuildingPanelRecruitmentAreaRegistration
    {
        private readonly BuildingPanelRecruitmentAreaEntry entry;
        internal BuildingPanelRecruitmentAreaRegistration(BuildingPanelRecruitmentAreaEntry entry) { this.entry = entry; }
        /// <summary>The registering plugin's GUID.</summary>
        public string OwnerGuid => entry.Owner;
        /// <summary>Owner-local variant ID.</summary>
        public string VariantId => entry.Id;
        /// <summary>Whether the replacement takes part in matching; true after registration.</summary>
        public bool IsEnabled => entry.Enabled;
        /// <summary>Arguments of the current showing, or null while Vanilla or another replacement shows the area.</summary>
        public BuildingPanelSectionEventArgs Current => entry.Shown;
        /// <summary>Logical activation, for example from the owner's settings; no hook is removed. A disabled
        /// replacement matches no building and hands a shown area back at the next GUI check. Unity thread.</summary>
        public void SetEnabled(bool enabled) => entry.Enabled = enabled;
        /// <summary>Reads this variant's competition status for settings presentation; see
        /// <see cref="BuildingPanelSections.TryGetRecruitmentAreaStatus"/>. Reading changes nothing.</summary>
        public bool TryGetStatus(out BuildingPanelRecruitmentAreaStatus status, out string reason) =>
            BuildingPanelSections.TryGetRecruitmentAreaStatus(entry.Owner, entry.Id, out status, out reason);
    }

    /// <summary>Immutable explanation of how one recruitment-area variant competes with the others registered in
    /// this process, for a consumer's own settings presentation (no popup is shown by APIShared). The effective
    /// owner depends on the open building, so the status describes the rule inputs, every rival that can compete
    /// and what this process has observed, rather than one final value.</summary>
    /// <remarks>Precedence where several replacements select the same building: a replacement with a selector before
    /// one for every building of its types, then ordinal owner GUID, then ordinal variant ID. Peers compute it from
    /// their own registrations; nothing here is synchronized or persisted.</remarks>
    public sealed class BuildingPanelRecruitmentAreaStatus
    {
        internal BuildingPanelRecruitmentAreaStatus(string owner, string variantId, bool hasSelector, bool enabled,
            IReadOnlyList<eStructs> types, bool shownNow, IReadOnlyList<BuildingPanelRecruitmentAreaRival> rivals)
        {
            OwnerGuid = owner; VariantId = variantId; HasSelector = hasSelector; IsEnabled = enabled;
            BuildingTypes = types; IsShownNow = shownNow; Rivals = rivals;
        }
        /// <summary>The variant's owner GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>The variant's owner-local ID.</summary>
        public string VariantId { get; }
        /// <summary>Whether the variant registered a selector (it then precedes whole-type replacements).</summary>
        public bool HasSelector { get; }
        /// <summary>Whether the variant currently takes part in matching.</summary>
        public bool IsEnabled { get; }
        /// <summary>Native types the variant is built on.</summary>
        public IReadOnlyList<eStructs> BuildingTypes { get; }
        /// <summary>Whether this variant's replacement is the one shown in the open building panel right now.</summary>
        public bool IsShownNow { get; }
        /// <summary>Every other registered variant sharing at least one native type, in precedence order.</summary>
        public IReadOnlyList<BuildingPanelRecruitmentAreaRival> Rivals { get; }
    }

    /// <summary>One other variant that can compete with the variant a status describes.</summary>
    public sealed class BuildingPanelRecruitmentAreaRival
    {
        internal BuildingPanelRecruitmentAreaRival(string owner, string variantId, bool hasSelector, bool enabled,
            IReadOnlyList<eStructs> sharedTypes, bool precedes, bool observedOverlap)
        {
            OwnerGuid = owner; VariantId = variantId; HasSelector = hasSelector; IsEnabled = enabled;
            SharedBuildingTypes = sharedTypes; Precedes = precedes; ObservedOverlap = observedOverlap;
        }
        /// <summary>The rival's owner GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>The rival's owner-local variant ID.</summary>
        public string VariantId { get; }
        /// <summary>Whether the rival registered a selector.</summary>
        public bool HasSelector { get; }
        /// <summary>Whether the rival currently takes part in matching.</summary>
        public bool IsEnabled { get; }
        /// <summary>Native types both variants are built on.</summary>
        public IReadOnlyList<eStructs> SharedBuildingTypes { get; }
        /// <summary>True when the rival's replacement is shown instead of the described variant on a building both
        /// select; false when the described variant wins there.</summary>
        public bool Precedes { get; }
        /// <summary>Whether this process has seen both variants select the same open building. False means only that
        /// the overlap has not been observed yet, not that the selectors are disjoint.</summary>
        public bool ObservedOverlap { get; }
    }
}

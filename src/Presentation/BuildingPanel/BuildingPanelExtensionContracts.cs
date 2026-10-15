using Noesis;
using SHCDESE.Interop;
using System;

namespace APIShared
{
    /// <summary>The building shown in the in-building panel during one refresh. A value copy: read it inside the
    /// callback; it does not follow later selection changes.</summary>
    public readonly struct BuildingPanelSelection
    {
        internal BuildingPanelSelection(int buildingId, int globalId, int ownerPlayerId, int localPlayerId,
            eStructs buildingType, int panelMode)
        {
            BuildingId = buildingId; GlobalId = globalId; OwnerPlayerId = ownerPlayerId;
            LocalPlayerId = localPlayerId; BuildingType = buildingType; PanelMode = panelMode;
        }
        /// <summary>One-based native building ID (slot); reused after the building is removed.</summary>
        public int BuildingId { get; }
        /// <summary>Non-zero native global ID; together with BuildingId it identifies this building instance.</summary>
        public int GlobalId { get; }
        /// <summary>Owning player ID.</summary>
        public int OwnerPlayerId { get; }
        /// <summary>Local player ID at this refresh; zero when unknown.</summary>
        public int LocalPlayerId { get; }
        /// <summary>Whether the local player owns the building.</summary>
        public bool IsLocallyOwned => OwnerPlayerId > 0 && OwnerPlayerId == LocalPlayerId;
        /// <summary>Native building type.</summary>
        public eStructs BuildingType { get; }
        /// <summary>Native in-building sub-mode (the panel variant Vanilla shows), as read from the game state.</summary>
        public int PanelMode { get; }
    }

    /// <summary>Unity-thread context for creating an extension element.</summary>
    public sealed class BuildingPanelExtensionContext
    {
        internal BuildingPanelExtensionContext(FrameworkElement hud) { Hud = hud; }
        /// <summary>The current building panel and its inherited Vanilla resources; do not retain it across panel recreation.</summary>
        public FrameworkElement Hud { get; }
    }

    /// <summary>A permanent row, button or text contribution to the selected building's panel.</summary>
    public sealed class BuildingPanelExtensionDefinition
    {
        /// <summary>Creates a definition. All callbacks run on the Unity thread outside registry locks; each is isolated.</summary>
        /// <param name="extensionId">Stable owner-local ID; duplicates are rejected.</param>
        /// <param name="factory">Creates a fresh, unattached element once per panel instance (and again after
        /// RequestContentRefresh). APIShared places it in the shared extension stack and owns its visibility.</param>
        /// <param name="appliesTo">Selects the buildings that show the element; null shows it for every building.
        /// Runs on every refresh while the panel is open: keep it cheap and allocation-free.</param>
        /// <param name="update">Optional per-refresh update of the shown element, for example changing text.
        /// Runs on every refresh while the element is shown: keep it cheap and allocation-free.</param>
        /// <param name="order">Ascending stack position, followed by ordinal owner GUID and extension ID.</param>
        public BuildingPanelExtensionDefinition(string extensionId,
            Func<BuildingPanelExtensionContext, FrameworkElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo = null,
            Action<BuildingPanelSelection, FrameworkElement> update = null, int order = 0)
        {
            ExtensionId = extensionId; Factory = factory; AppliesTo = appliesTo; Update = update; Order = order;
        }
        /// <summary>Stable owner-local ID.</summary>
        public string ExtensionId { get; }
        /// <summary>Element factory.</summary>
        public Func<BuildingPanelExtensionContext, FrameworkElement> Factory { get; }
        /// <summary>Building filter; null for every building.</summary>
        public Func<BuildingPanelSelection, bool> AppliesTo { get; }
        /// <summary>Optional per-refresh update.</summary>
        public Action<BuildingPanelSelection, FrameworkElement> Update { get; }
        /// <summary>Stack order.</summary>
        public int Order { get; }
    }

    /// <summary>Logical control of a process-lived extension; no hook disposal or unregistration is exposed.</summary>
    public interface IBuildingPanelExtensionRegistration
    {
        /// <summary>Hidden extensions are never shown and their callbacks do not run. Defaults to true.</summary>
        void SetVisible(bool visible);
        /// <summary>Recreates the element at the next refresh, including after a failed callback disabled it.</summary>
        void RequestContentRefresh();
    }

    /// <summary>An exclusive replacement of Vanilla's recruitment area for selected buildings of some types.</summary>
    public sealed class BuildingPanelRecruitmentAreaDefinition
    {
        /// <summary>Creates a definition. All callbacks run on the Unity thread outside registry locks; each is isolated.</summary>
        /// <param name="claimId">Stable owner-local ID.</param>
        /// <param name="buildingTypes">Building types this claim owns. While the claim exists no other claim can
        /// own any of them.</param>
        /// <param name="factory">Creates a fresh, unattached element once per panel instance (and again after
        /// RequestContentRefresh). APIShared adds it to the building panel's root grid and owns its visibility;
        /// the element owns its own size and placement.</param>
        /// <param name="appliesTo">Selects the buildings of these types that show the replacement; null selects all.
        /// Others keep Vanilla's recruitment area. Runs on every refresh: keep it cheap and allocation-free.</param>
        /// <param name="update">Optional per-refresh update while the replacement is shown; keep it allocation-free.</param>
        public BuildingPanelRecruitmentAreaDefinition(string claimId, eStructs[] buildingTypes,
            Func<BuildingPanelExtensionContext, FrameworkElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo = null,
            Action<BuildingPanelSelection, FrameworkElement> update = null)
        {
            ClaimId = claimId; BuildingTypes = buildingTypes == null ? null : (eStructs[])buildingTypes.Clone();
            Factory = factory; AppliesTo = appliesTo; Update = update;
        }
        /// <summary>Stable owner-local ID.</summary>
        public string ClaimId { get; }
        /// <summary>Claimed building types (a copy).</summary>
        public eStructs[] BuildingTypes { get; }
        /// <summary>Replacement factory.</summary>
        public Func<BuildingPanelExtensionContext, FrameworkElement> Factory { get; }
        /// <summary>Building filter within the claimed types; null for all.</summary>
        public Func<BuildingPanelSelection, bool> AppliesTo { get; }
        /// <summary>Optional per-refresh update.</summary>
        public Action<BuildingPanelSelection, FrameworkElement> Update { get; }
    }

    /// <summary>Handle of an exclusive recruitment-area claim.</summary>
    public interface IBuildingPanelRecruitmentAreaClaim
    {
        /// <summary>Whether Release was called.</summary>
        bool IsReleased { get; }
        /// <summary>Gives up the claim and its building types. Vanilla's recruitment panels are restored at the next
        /// refresh on the Unity thread; another owner can claim the types immediately. Idempotent.</summary>
        void Release();
        /// <summary>Recreates the replacement at the next refresh, including after a failed callback disabled it.</summary>
        void RequestContentRefresh();
    }

    /// <summary>Owner-bound access to the shared building panel extension stack.</summary>
    /// <remarks>Extensions refresh after Vanilla's MainViewModel.setUpInbuilding and after the in-game GUI checks
    /// (PresentationOperation.GuiChecks), on the Unity thread. A callback that throws disables only its extension,
    /// logs once and leaves the others running; RequestContentRefresh re-enables it. No hook is installed and
    /// nothing runs per frame until the first extension registers. Your callbacks own any gameplay authorization.</remarks>
    public interface IBuildingPanelExtensionsCapability
    {
        /// <summary>Registers a process-lived owner-local extension. Returns false with a null registration and a
        /// ValidationFailed diagnostic for a missing ID or factory or a duplicate owner-local ID.</summary>
        bool TryRegisterExtension(BuildingPanelExtensionDefinition definition,
            out IBuildingPanelExtensionRegistration registration, out NativeCapabilityDiagnostic diagnostic);
        /// <summary>Logically activates all of this owner's extensions and recruitment-area claims without
        /// uninstalling callbacks. Defaults to true. An inactive owner's claim keeps its building types.</summary>
        void SetOwnerActive(bool active);
        /// <summary>Claims Vanilla's recruitment area for the selected buildings of some types. While the claim is
        /// shown, Vanilla's recruitment panels (barracks, mercenary post, Bedouin stockade, engineers' and
        /// tunnellers' guilds, cathedral) and the full-panel click mask are hidden; their exact visibility is
        /// restored when another building is selected, on Release, when the claim's callback fails, when the panel
        /// is recreated and at mission end. Returns false with a Conflict diagnostic naming the current owner when
        /// any type is already claimed (a second claimant is refused, never silently overrides), and with
        /// ValidationFailed for a missing ID, type list or factory.</summary>
        bool TryClaimRecruitmentArea(BuildingPanelRecruitmentAreaDefinition definition,
            out IBuildingPanelRecruitmentAreaClaim claim, out NativeCapabilityDiagnostic diagnostic);
    }
}

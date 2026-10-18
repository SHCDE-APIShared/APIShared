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
            eStructs buildingType, int panelMode, bool isMapEditor = false)
        {
            BuildingId = buildingId; GlobalId = globalId; OwnerPlayerId = ownerPlayerId;
            LocalPlayerId = localPlayerId; BuildingType = buildingType; PanelMode = panelMode; IsMapEditor = isMapEditor;
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
        /// <summary>Whether the map editor is open. Only extensions created with showInMapEditor see such a selection;
        /// recruitment-area replacements never do.</summary>
        public bool IsMapEditor { get; }
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
        /// <param name="showInMapEditor">Whether the extension also shows in the map editor. Off by default: opt in only
        /// for an extension with an editor use; BuildingPanelSelection.IsMapEditor tells the two apart.</param>
        public BuildingPanelExtensionDefinition(string extensionId,
            Func<BuildingPanelExtensionContext, FrameworkElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo = null,
            Action<BuildingPanelSelection, FrameworkElement> update = null, int order = 0, bool showInMapEditor = false)
        {
            ExtensionId = extensionId; Factory = factory; AppliesTo = appliesTo; Update = update; Order = order;
            ShowInMapEditor = showInMapEditor;
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
        /// <summary>Whether the extension also shows in the map editor.</summary>
        public bool ShowInMapEditor { get; }
    }

    /// <summary>Logical control of a process-lived extension; no hook disposal or unregistration is exposed.</summary>
    public interface IBuildingPanelExtensionRegistration
    {
        /// <summary>Hidden extensions are never shown and their callbacks do not run. Defaults to true.</summary>
        void SetVisible(bool visible);
        /// <summary>Recreates the element at the next refresh, including after a failed callback disabled it.</summary>
        void RequestContentRefresh();
    }

    /// <summary>A replacement of Vanilla's recruitment area for one building variant: the buildings of some native
    /// types that the selector picks, for example a custom barracks built on the native stone barracks.</summary>
    /// <remarks>Any number of owners can register variants on the same native type. Two replacements conflict only
    /// when both match the same selected building; see IBuildingPanelExtensionsCapability.TryRegisterRecruitmentArea.
    /// A variant is identified by owner GUID and variant ID, not by a native ID, so a later shared registry of
    /// building variants (issue #50) can supply the selector without changing this contract.</remarks>
    public sealed class BuildingPanelRecruitmentAreaDefinition
    {
        /// <summary>Creates a definition. All callbacks run on the Unity thread outside registry locks; each is isolated.</summary>
        /// <param name="variantId">Stable owner-local ID of the building variant this replacement serves.</param>
        /// <param name="buildingTypes">Native types the variant is built on; a cheap prefilter before the selector.</param>
        /// <param name="factory">Creates a fresh, unattached element once per panel instance (and again after
        /// RequestContentRefresh). APIShared adds it to the building panel's root grid and owns its visibility;
        /// the element owns its own size and placement.</param>
        /// <param name="appliesTo">Selects the buildings of these types that are the variant; null selects every
        /// building of the types. Others keep Vanilla's recruitment area or another variant's. Runs on every refresh
        /// for a building of these types: keep it cheap and allocation-free.</param>
        /// <param name="update">Optional per-refresh update while the replacement is shown; keep it allocation-free.</param>
        public BuildingPanelRecruitmentAreaDefinition(string variantId, eStructs[] buildingTypes,
            Func<BuildingPanelExtensionContext, FrameworkElement> factory,
            Func<BuildingPanelSelection, bool> appliesTo = null,
            Action<BuildingPanelSelection, FrameworkElement> update = null)
        {
            VariantId = variantId; BuildingTypes = buildingTypes == null ? null : (eStructs[])buildingTypes.Clone();
            Factory = factory; AppliesTo = appliesTo; Update = update;
        }
        /// <summary>Stable owner-local variant ID.</summary>
        public string VariantId { get; }
        /// <summary>Native types the variant is built on (a copy).</summary>
        public eStructs[] BuildingTypes { get; }
        /// <summary>Replacement factory.</summary>
        public Func<BuildingPanelExtensionContext, FrameworkElement> Factory { get; }
        /// <summary>Variant selector within the types; null for every building of the types.</summary>
        public Func<BuildingPanelSelection, bool> AppliesTo { get; }
        /// <summary>Optional per-refresh update.</summary>
        public Action<BuildingPanelSelection, FrameworkElement> Update { get; }
    }

    /// <summary>Handle of a recruitment-area replacement.</summary>
    public interface IBuildingPanelRecruitmentAreaRegistration
    {
        /// <summary>Whether Release was called.</summary>
        bool IsReleased { get; }
        /// <summary>Removes the replacement. Vanilla's recruitment panels, or the next matching variant's replacement,
        /// return at the next refresh on the Unity thread. The variant ID can be registered again at once. Idempotent.</summary>
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
        /// <summary>Logically activates all of this owner's extensions and recruitment-area replacements without
        /// uninstalling callbacks. Defaults to true. An inactive owner's replacements match no building.</summary>
        void SetOwnerActive(bool active);
        /// <summary>Registers a replacement of Vanilla's recruitment area for one building variant. While it is shown,
        /// Vanilla's recruitment panels (barracks, mercenary post, Bedouin stockade, engineers' and tunnellers'
        /// guilds, cathedral) and the full-panel click mask are hidden; their exact visibility is restored when
        /// another building is selected, on Release, when the replacement's callback fails, when the panel is
        /// recreated and at mission end. Never shown in the map editor, where nothing is recruited.
        /// Any number of owners can register variants on the same native type. When several replacements match the
        /// same selected building, one is shown by a fixed precedence that never depends on registration order: a
        /// replacement with a selector before one for every building of its types, then ordinal owner GUID, then
        /// ordinal variant ID. Each such conflict is logged once per pair, naming both mods and the one shown.
        /// Returns false with ValidationFailed for a missing ID, type list or factory, or a variant ID this owner
        /// already registered.</summary>
        bool TryRegisterRecruitmentArea(BuildingPanelRecruitmentAreaDefinition definition,
            out IBuildingPanelRecruitmentAreaRegistration registration, out NativeCapabilityDiagnostic diagnostic);
    }
}

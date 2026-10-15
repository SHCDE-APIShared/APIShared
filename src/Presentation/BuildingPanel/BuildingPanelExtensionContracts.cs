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
        /// <summary>Logically activates all of this owner's extensions without uninstalling callbacks. Defaults to true.</summary>
        void SetOwnerActive(bool active);
    }
}

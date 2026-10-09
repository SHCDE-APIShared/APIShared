namespace APIShared
{
    /// <summary>Immutable repair costs and resource amounts for the selected owned building.</summary>
    public sealed class BuildingRepairQuote
    {
        /// <summary>Creates a repair quote from a single simulation snapshot.</summary>
        public BuildingRepairQuote(int buildingId, int buildingGlobalId, int panel, bool canRepair,
            int currentHealth, int maxHealth, int wood, int stone, int iron, int pitch, int gold,
            int availableWood, int availableStone, int availableIron, int availablePitch, int availableGold)
        {
            BuildingId = buildingId;
            BuildingGlobalId = buildingGlobalId;
            Panel = panel;
            CanRepair = canRepair;
            CurrentHealth = currentHealth;
            MaxHealth = maxHealth;
            Wood = wood;
            Stone = stone;
            Iron = iron;
            Pitch = pitch;
            Gold = gold;
            AvailableWood = availableWood;
            AvailableStone = availableStone;
            AvailableIron = availableIron;
            AvailablePitch = availablePitch;
            AvailableGold = availableGold;
        }

        /// <summary>One-based game building ID.</summary>
        public int BuildingId { get; }
        /// <summary>Stable identity of the selected building.</summary>
        public int BuildingGlobalId { get; }
        /// <summary>Vanilla in-building HUD panel.</summary>
        public int Panel { get; }
        /// <summary>Vanilla repair proximity and state permission.</summary>
        public bool CanRepair { get; }
        /// <summary>Current building health.</summary>
        public int CurrentHealth { get; }
        /// <summary>Maximum building health.</summary>
        public int MaxHealth { get; }
        /// <summary>Required wood.</summary>
        public int Wood { get; }
        /// <summary>Required stone.</summary>
        public int Stone { get; }
        /// <summary>Required iron.</summary>
        public int Iron { get; }
        /// <summary>Required pitch.</summary>
        public int Pitch { get; }
        /// <summary>Required gold.</summary>
        public int Gold { get; }
        /// <summary>Available wood.</summary>
        public int AvailableWood { get; }
        /// <summary>Available stone.</summary>
        public int AvailableStone { get; }
        /// <summary>Available iron.</summary>
        public int AvailableIron { get; }
        /// <summary>Available pitch.</summary>
        public int AvailablePitch { get; }
        /// <summary>Available gold.</summary>
        public int AvailableGold { get; }
        /// <summary>Whether every required resource is available in this snapshot.</summary>
        public bool HasResources => AvailableWood >= Wood && AvailableStone >= Stone &&
            AvailableIron >= Iron && AvailablePitch >= Pitch && AvailableGold >= Gold;
    }

    /// <summary>Owner-bound repair quotes and HUD presentation; these operations do not issue a repair command.</summary>
    /// <remarks>Use on the Unity/UI thread. The shared service and its hooks persist to process exit; activation is logical. Quotes are immutable snapshots, not guarantees that selection, resources or permission remain unchanged. Hover presentation has one shared current button identity; use a stable mod-qualified ID to avoid collisions.</remarks>
    public interface IBuildingRepairCapability
    {
        /// <summary>Enables or disables this owner's use without removing process-wide hooks.</summary>
        void SetActive(bool active);
        /// <summary>Reads the captured quote matching the latest selected building and panel. Returns false with null quote when this owner is inactive or a matching building-HUD snapshot is absent. A returned quote may still deny repair or lack resources.</summary>
        bool TryGetSelectedQuote(out BuildingRepairQuote quote);
        /// <summary>Begins the shared tooltip for a stable button identity; empty IDs or an inactive owner are ignored. Hover errors are logged and clear the tooltip. A later hover may replace this one.</summary>
        void BeginHover(string buttonId);
        /// <summary>Clears hover only if the ID still matches the shared current button, including after owner deactivation. Errors are logged.</summary>
        void EndHover(string buttonId);
    }
}

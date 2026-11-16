namespace APIShared.Buildings
{
    // Hash-bound phase identities for the supported CrusaderDE image. C4290 marks a
    // record for removal; B8310 later releases and clears its slot. These are distinct
    // boundaries even though the current SE callback names suggest the reverse order.
    internal static class BuildingRemovalNativeContracts
    {
        internal const int MarkBuildingForRemovalRva = 0xC4290;
        internal const int ReleaseBuildingSlotRva = 0xB8310;

        // Keep the misleading external names at this compatibility boundary only.
        internal static class ExtenderAdapter
        {
            internal const string OwnerTypeName = "BulkBuildingDetours";
            internal const string MarkBuildingForRemovalCallbackName = "c_game_building_delete_hook_impl";
            internal const string MarkBuildingForRemovalDelegateName = "c_game_building_delete_delegate";
            internal const string ReleaseBuildingSlotCallbackName = "c_game_building_bulldoze_hook_impl";
            internal const string ReleaseBuildingSlotDelegateName = "c_game_building_bulldoze_delegate";
        }
    }
}

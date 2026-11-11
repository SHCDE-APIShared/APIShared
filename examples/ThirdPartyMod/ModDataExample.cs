using APIShared.ModData;

namespace ThirdPartyMod
{
    // Call from a matching successful mission/selection context on the Unity thread.
    // No ExtendedData assembly is required for this consumer.
    internal static class ModDataExample
    {
        internal static ModDataReadResult ReadOwnMapData() =>
            ModDataAccess.ReadCurrentMapNamespace(ExamplePlugin.Guid);

        internal static ModDataReadResult ReadConfirmedLordData(int playerId) =>
            ModDataAccess.ReadSelectedLordNamespace(playerId, ExamplePlugin.Guid);

        // Explicitly local provenance, independent of host confirmation.
        internal static ModDataReadResult InspectLocalLordData(string lordJsonPath) =>
            ModDataAccess.ReadLocalLordNamespace(lordJsonPath, ExamplePlugin.Guid);
    }
}

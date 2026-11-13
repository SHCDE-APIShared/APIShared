using System;
using APIShared.Localization;

namespace APIShared.Economy
{
    internal static unsafe partial class WeaponProductionRuntime
    {
        // Process-lived lookup for the existing worker wire option byte. Catalogue/world
        // transitions own every change, while the native demand DWORD is still zero.
        internal static readonly IntPtr StoredOutputOptionsAddress = AllocateWire(OptionMask, 1);
        internal static bool HasStoredOutputDemand => active.HasStoredOutputs;

        private static void PublishStoredOutputOptions(WeaponProductionCatalog catalog)
        {
            var flags = new Span<byte>((void*)StoredOutputOptionsAddress, OptionMask + 1);
            for (int i = 0; i < flags.Length; i++)
                flags[i] = catalog.At(i)?.StoredOutput != null ? (byte)1 : (byte)0;
        }

        private static void ClearStoredOutputOptions() =>
            new Span<byte>((void*)StoredOutputOptionsAddress, OptionMask + 1).Clear();

        // The existing native repair protocol can restore a worker's carrier and recipe,
        // but it does not restore the private pooled-storage ledger in that same repair.
        // New stored-output recipes therefore require an offline/editor world until that
        // joint contract is qualified. Native-only recipes keep their existing admission.
        internal static string StoredOutputModeDiagnostic(WeaponProductionCatalog catalog, bool realMultiplayer) =>
            realMultiplayer && catalog?.HasStoredOutputs == true
                ? UiText.Get("WeaponProduction.StoredOutputsOfflineOnly") : null;

        // Reader 2 understands the custom output identity in the frozen catalogue.
        // Preserve both the legacy payload and its outer 1/1 envelope for native-only worlds.
        internal static int SaveReaderVersion(WeaponProductionCatalog catalog) => catalog?.HasStoredOutputs == true ? 2 : 1;
    }
}

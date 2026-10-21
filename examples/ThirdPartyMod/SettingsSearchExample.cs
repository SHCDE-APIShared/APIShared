using APIShared.ModSettings;
using BepInEx;
using BepInEx.Logging;
using SHCDESE.API;
using System;
using System.IO;

namespace ThirdPartyMod
{
    // Alternative for a mod that owns its settings page without the preset base class.
    // Call on the Unity thread after the Extender's asset manager is ready. The normal
    // ExamplePlugin already gets this support from its shared preset registration.
    internal static class SettingsSearchExample
    {
        internal static void RegisterOwnPage(BaseUnityPlugin plugin, object settings,
            string modName, string relativeXamlPath, ManualLogSource log,
            Func<string, string, bool> applyLocalFilter = null)
        {
            if (!GameAssetManagerAPI.Instance.GetModifiedFilePath(relativeXamlPath, out string path))
                throw new FileNotFoundException("Settings XAML was not resolved.", relativeXamlPath);

            // The same model and source belong to the catalog and the registered tab.
            // Optional navigation updates only the mod's own filter properties.
            ModSettingsSearchCatalog.RegisterSource(settings, path, log, modName, applyLocalFilter);
            GameXAMLManagerAPI.Instance.RegisterLobbyModSettings(plugin, modName, settings, relativeXamlPath);
        }
    }
}

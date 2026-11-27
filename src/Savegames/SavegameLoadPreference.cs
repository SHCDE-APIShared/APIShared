using System;
using BepInEx.Configuration;

namespace APIShared
{
    internal sealed class SavegameLoadPreference
    {
        private readonly ConfigFile config;
        private readonly ConfigEntry<bool> entry;
        private readonly Action<Exception> report;
        private bool value;
        internal SavegameLoadPreference(ConfigFile config, Action<Exception> report)
        {
            this.config = config; this.report = report;
            entry = config.Bind("Savegames", "UseCurrentModSettings", false,
                "Last local choice in the load dialog. Forced save permissions never change this preference.");
            value = entry.Value;
        }
        internal bool Value => value;
        internal void Set(bool next)
        {
            value = next; // Keep the user's choice even if publishing the config fails.
            try { entry.Value = next; if (!config.SaveOnConfigSet) config.Save(); }
            catch (Exception error) { report?.Invoke(error); }
        }
    }
}

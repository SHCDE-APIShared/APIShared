using System;
using BepInEx.Configuration;

namespace APIShared.Presentation
{
    // UI preference only: never included in mission settings, savegames or network state.
    internal sealed class TroopActionMenuPreference
    {
        private readonly ConfigFile config;
        private readonly ConfigEntry<bool> entry;
        private readonly Action<Exception> report;
        private bool value = true;
        internal TroopActionMenuPreference(ConfigFile config, Action<Exception> report)
        {
            this.config = config; this.report = report;
            try { entry = config.Bind("TroopActions", "AutoCloseMenu", true, "Close the local troop action overflow menu after invoking an action."); value = entry.Value; }
            catch (Exception error) { report?.Invoke(error); }
        }
        internal bool Value => value;
        internal void Set(bool next)
        {
            if (value == next) return;
            value = next;
            try { if (entry != null) { entry.Value = next; if (!config.SaveOnConfigSet) config.Save(); } }
            catch (Exception error) { report?.Invoke(error); }
        }
    }
}

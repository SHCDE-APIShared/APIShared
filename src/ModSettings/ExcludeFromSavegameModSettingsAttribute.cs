using System;

namespace APIShared.ModSettings
{
    /// <summary>Excludes a plugin's registered settings from APIShared's automatic savegame settings capture and restoration.</summary>
    /// <remarks>Apply to the plugin class when it owns a separate persistence protocol or does not represent mission settings. Inherited by derived plugin classes. This does not disable presets or other providers' persistence.</remarks>
    [AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
    public sealed class ExcludeFromSavegameModSettingsAttribute : Attribute
    {
    }

    internal static class SavegameSettingsParticipation
    {
        internal static bool IsExcluded(Type pluginType) => pluginType != null &&
            Attribute.IsDefined(pluginType, typeof(ExcludeFromSavegameModSettingsAttribute), inherit: true);
    }
}

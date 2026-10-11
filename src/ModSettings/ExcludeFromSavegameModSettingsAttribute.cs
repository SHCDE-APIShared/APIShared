using System;
using System.Collections.Generic;

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
        private static readonly HashSet<Type> ExplicitExclusions = new HashSet<Type>();

        internal static void Exclude(Type pluginType)
        {
            if (pluginType == null) throw new ArgumentNullException(nameof(pluginType));
            lock (ExplicitExclusions) ExplicitExclusions.Add(pluginType);
        }

        internal static bool IsExcluded(Type pluginType)
        {
            if (pluginType == null) return false;
            lock (ExplicitExclusions)
                for (Type type = pluginType; type != null; type = type.BaseType)
                    if (ExplicitExclusions.Contains(type)) return true;
            return Attribute.IsDefined(pluginType, typeof(ExcludeFromSavegameModSettingsAttribute), inherit: true);
        }
    }
}

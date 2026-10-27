using System;
using System.IO;
using System.Reflection;
using System.Threading;
using SHCDESE.API;

namespace APIShared.Localization
{
    internal static class GameLocale
    {
        private static string steamLanguage;
        private static bool manifestAttempted;
        private static int mainThread;
        private static string published = "en-US";
        private static BepInEx.Configuration.ConfigEntry<string> configuredLanguage;
        internal static void Initialize()
        {
            mainThread = Thread.CurrentThread.ManagedThreadId;
            // Retain the managed entry, not the early Unity component's apparent null state after cleanup.
            var plugin = SHCDESE.BepInEx.Bootstrap.Plugin.Instance;
            if (!ReferenceEquals(plugin, null)) configuredLanguage = plugin.LanguageProvider;
            Current();
        }
        internal static string Current()
        {
            if (Thread.CurrentThread.ManagedThreadId != mainThread) return Volatile.Read(ref published);
            string configured = null, extender = null;
            try {
                if (configuredLanguage != null) configured = configuredLanguage.Value;
            } catch { }
            // Explicit overrides do not need Steam/asset access from recurring UI bindings.
            if (string.IsNullOrWhiteSpace(configured) || string.Equals(configured, "detect", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(steamLanguage)) steamLanguage = ReadSteamLanguage();
                try { extender = GameAssetManagerAPI.Instance.CurrentLanguage; } catch { }
            }
            string locale = LocaleSelection.Select(configured, steamLanguage, extender);
            Volatile.Write(ref published, locale);
            return locale;
        }
        private static string ReadSteamLanguage()
        {
            try
            {
                var type = Type.GetType("Steamworks.SteamApps, com.rlabrecque.steamworks.net", false);
                var method = type == null ? null : type.GetMethod("GetCurrentGameLanguage", BindingFlags.Static | BindingFlags.Public);
                string value = method == null ? null : method.Invoke(null, null) as string;
                if (!string.IsNullOrWhiteSpace(value)) return value;
            }
            catch { } // Steam may not be initialized while the plugin starts.
            if (manifestAttempted) return null;
            manifestAttempted = true;
            try
            {
                var common = new DirectoryInfo(BepInEx.Paths.GameRootPath);
                while (common != null && !string.Equals(common.Name, "common", StringComparison.OrdinalIgnoreCase)) common = common.Parent;
                string path = common == null || common.Parent == null ? null : Path.Combine(common.Parent.FullName, "appmanifest_3024040.acf");
                if (path == null || !File.Exists(path)) return null;
                bool userConfig = false;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line == "\"UserConfig\"") { userConfig = true; continue; }
                    if (!userConfig) continue;
                    if (line == "}") break;
                    string[] parts = line.Split('"');
                    if (parts.Length >= 4 && string.Equals(parts[1], "language", StringComparison.OrdinalIgnoreCase)) return parts[3];
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException || error is UnauthorizedAccessException || error is ArgumentException)) throw;
            }
            return null;
        }
    }
}

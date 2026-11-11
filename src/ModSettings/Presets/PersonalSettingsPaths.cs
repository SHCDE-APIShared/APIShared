using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace APIShared.ModSettings
{
    /// <summary>Resolves user-owned storage independently of the plugin installation directory.</summary>
    internal sealed class PersonalSettingsPaths
    {
        // Internal fixture seam; production always resolves the platform's Documents folder.
        internal static Func<string, string> TestDocumentsDirectory;
        // Internal fixture seam for the per-user folder candidates, in order of preference.
        internal static Func<IEnumerable<string>> PlatformFolders = DefaultFolders;

        internal PersonalSettingsPaths(string assemblyPath, string targetGuid)
        {
            string documents = TestDocumentsDirectory != null
                ? TestDocumentsDirectory(assemblyPath)
                : PlatformFolders().FirstOrDefault(IsUsable);
            if (!IsUsable(documents))
                throw new IOException("Cannot resolve an absolute per-user directory for APIShared personal settings.");
            DocumentsDirectory = Path.GetFullPath(documents);
            Root = Path.Combine(DocumentsDirectory, "SHCDE", "APIShared", targetGuid);
            SettingsFile = Path.Combine(Root, "settings.msgpack");
            Presets = Path.Combine(Root, "Presets");
        }

        internal string DocumentsDirectory { get; }
        internal string Root { get; }
        internal string SettingsFile { get; }
        internal string Presets { get; }

        private static bool IsUsable(string folder) => !string.IsNullOrWhiteSpace(folder) && Path.IsPathRooted(folder);

        // Some Wine/Proton prefixes report no Documents folder. The settings then use the
        // next per-user folder in this fixed order, never the game installation, so the
        // chosen location stays the same on every start of that prefix.
        private static IEnumerable<string> DefaultFolders()
        {
            yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (IsUsable(profile)) yield return Path.Combine(profile, "Documents");
            yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
    }
}

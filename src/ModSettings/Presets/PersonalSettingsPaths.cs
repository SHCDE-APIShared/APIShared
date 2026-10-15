using System;
using System.IO;

namespace APIShared.ModSettings
{
    /// <summary>Resolves user-owned storage independently of the plugin installation directory.</summary>
    internal sealed class PersonalSettingsPaths
    {
        // Internal fixture seam; production always resolves the platform's Documents folder.
        internal static Func<string, string> TestDocumentsDirectory;

        internal PersonalSettingsPaths(string assemblyPath, string targetGuid)
        {
            string documents = TestDocumentsDirectory != null
                ? TestDocumentsDirectory(assemblyPath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents) || !Path.IsPathRooted(documents))
                throw new IOException("Cannot resolve an absolute Documents directory for APIShared personal settings.");
            DocumentsDirectory = Path.GetFullPath(documents);
            Root = Path.Combine(DocumentsDirectory, "SHCDE", "APIShared", targetGuid);
            SettingsFile = Path.Combine(Root, "settings.msgpack");
            Presets = Path.Combine(Root, "Presets");
        }

        internal string DocumentsDirectory { get; }
        internal string Root { get; }
        internal string SettingsFile { get; }
        internal string Presets { get; }
    }
}

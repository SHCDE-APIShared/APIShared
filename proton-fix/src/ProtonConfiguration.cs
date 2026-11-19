using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.Workshop.Proton
{
    // Only the Proton fix reads this file since APIShared 0.57.0 dropped its external updater
    // (the namespace is kept so the source-linked helpers stay unchanged).
    internal static class ProtonConfiguration
    {
        private static readonly Dictionary<string, ProtonPathOptions> options = new Dictionary<string, ProtonPathOptions>(StringComparer.OrdinalIgnoreCase);
        // Read once per process. Bind must not create the file or rewrite existing user
        // settings during startup.
        internal static ProtonPathOptions Load(string configDirectory)
        {
            if (!WinePathInterop.Instance.IsWine) return new ProtonPathOptions();
            string path = Path.Combine(configDirectory, "APIShared.ProtonPaths.cfg");
            lock (options) {
                ProtonPathOptions cached;
                if (!options.TryGetValue(path, out cached)) {
                    cached = Read(path); options.Add(path, cached);
                }
                return new ProtonPathOptions { HostGameDirectory = cached.HostGameDirectory, WineBashPath = cached.WineBashPath };
            }
        }
        private static ProtonPathOptions Read(string path)
        {
            ProtonPaths.CheckUnlinked(path);
            var config = new ConfigFile(path, false) { SaveOnConfigSet = false };
            return new ProtonPathOptions {
                HostGameDirectory = config.Bind("ProtonPaths", "HostGameDirectory", "", "Optional absolute Linux path to the active game directory. Restart required.").Value,
                WineBashPath = config.Bind("ProtonPaths", "WineBashPath", "", "Optional Windows/Wine path to host Bash. Restart required.").Value
            };
        }
    }
}

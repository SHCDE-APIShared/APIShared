using System;
using System.IO;

namespace APISharedTests
{
    internal static class InstalledTestEnvironment
    {
        internal static string GameDirectory()
        {
            string game = Environment.GetEnvironmentVariable("SHCDE_GAME_DIR");
            string recorded = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GameDirectory.txt");
            if (string.IsNullOrWhiteSpace(game) && File.Exists(recorded)) game = File.ReadAllText(recorded).Trim().Trim('\uFEFF');
            if (string.IsNullOrWhiteSpace(game)) throw new InvalidOperationException("Set SHCDE_GAME_DIR or build tests with -p:GameDir.");
            return Path.GetFullPath(game);
        }
    }
}

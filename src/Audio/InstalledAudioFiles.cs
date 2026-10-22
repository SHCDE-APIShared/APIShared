using System;
using System.IO;

namespace APIShared.Audio
{
    // Unity can load Assembly-CSharp from bytes, leaving Assembly.Location empty.
    // Resolve the installed files exclusively from BepInEx's game root.
    internal sealed class InstalledAudioFiles
    {
        internal readonly string Native, Managed, Backend;
        internal InstalledAudioFiles(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("BepInEx game root is unavailable.", nameof(gameRoot));
            string root = Path.GetFullPath(gameRoot);
            Native = Path.Combine(root, "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            Managed = Path.Combine(root, "Stronghold Crusader Definitive Edition_Data", "Managed", "Assembly-CSharp.dll");
            Backend = Path.Combine(root, "BepInEx", "plugins", "000shcdese", "RedBird.Backends.NativeX64.dll");
        }
        // The managed playback hook is gated by its IL contract (SoundIlContract), not by a file hash.
        // The native hash is computed once at startup and passed in instead of hashing the DLL again.
        internal void Validate(string nativeHash)
        {
            if (!File.Exists(Native)) throw new FileNotFoundException("Required installed audio binary is missing.", Native);
            if (!string.Equals(nativeHash, NativeContract.NativeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Binary identity changed; re-audit native layout, IL and backend: " + Native);
            ValidateFile(Backend, NativeContract.BackendHash);
        }
        internal void Validate() => Validate(NativeContract.HashFile(Native));
        internal static void ValidateFile(string path, string expectedHash)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Required installed audio binary is missing.", path);
            if (NativeContract.HashFile(path) != expectedHash)
                throw new InvalidOperationException("Binary identity changed; re-audit native layout, IL and backend: " + path);
        }
    }
}

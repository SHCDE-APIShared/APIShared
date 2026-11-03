using RedBird.Backends.NativeX64;
using System;
using System.IO;

namespace APIShared.Audio
{
    // Unity can load Assembly-CSharp from bytes, leaving Assembly.Location empty.
    // Resolve the installed files exclusively from BepInEx's game root.
    internal sealed class InstalledAudioFiles
    {
        internal readonly string Native, Managed;
        internal InstalledAudioFiles(string gameRoot)
        {
            if (string.IsNullOrWhiteSpace(gameRoot)) throw new ArgumentException("BepInEx game root is unavailable.", nameof(gameRoot));
            string root = Path.GetFullPath(gameRoot);
            Native = Path.Combine(root, "Stronghold Crusader Definitive Edition_Data", "Plugins", "x86_64", "CrusaderDE.dll");
            Managed = Path.Combine(root, "Stronghold Crusader Definitive Edition_Data", "Managed", "Assembly-CSharp.dll");
        }
        // The managed playback hook is gated by its IL contract (SoundIlContract), not by a file hash.
        // The native hash is computed once at startup and passed in instead of hashing the DLL again.
        // The RedBird backend is the loaded assembly (minimum version plus runtime hook contract).
        internal void Validate(string nativeHash)
        {
            if (!File.Exists(Native)) throw new FileNotFoundException("Required installed audio binary is missing.", Native);
            if (!string.Equals(nativeHash, NativeContract.NativeHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Binary identity changed; re-audit native layout and IL: " + Native);
            NativeContract.ValidateBackendVersion(typeof(NativeDetourBackend).Assembly.GetName().Version);
        }
        internal void Validate() => Validate(NativeContract.HashFile(Native));
    }
}

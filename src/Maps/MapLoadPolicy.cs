using System;
using System.IO;
using BepInEx.Logging;

namespace APIShared.Maps
{
    /// <summary>Decision for a local file used in an ordinary map start.</summary>
    public readonly struct MapLoadDecision
    {
        internal readonly string NativeFileName, Rejection;
        private MapLoadDecision(string name, string rejection) { NativeFileName = name; Rejection = rejection; }

        /// <summary>Keep the original path and native filename.</summary>
        public static MapLoadDecision Allow() => default;

        /// <summary>Replace only the native filename argument. Include the .map extension.</summary>
        public static MapLoadDecision UseNativeFileName(string name) =>
            new MapLoadDecision(name ?? throw new ArgumentNullException(nameof(name)), null);

        /// <summary>Reject this start with a player-readable reason.</summary>
        public static MapLoadDecision Reject(string reason) =>
            new MapLoadDecision(null, string.IsNullOrWhiteSpace(reason)
                ? throw new ArgumentException("A rejection reason is required.", nameof(reason)) : reason);
    }

    /// <summary>Optional policy for ordinary local and Steam multiplayer map starts.</summary>
    public static partial class MapLoadPolicy
    {
        /// <summary>Maximum length of the native filename argument, including its extension, in UTF-16 code units.</summary>
        public const int NativeFileNameLimit = 63;
        private const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        private static Func<string, bool, MapLoadDecision> provider;
        private static ManualLogSource log;
        private static string hash;
        internal static bool HasProvider => provider != null;

        internal static void Initialize(string nativeHash, ManualLogSource logger)
        { hash = nativeHash; log = logger; }

        /// <summary>
        /// Registers one process-lifetime provider per process, on the Unity thread after APIShared's
        /// LibraryLoaded initialization. The callback receives the original local path and current alias
        /// support. It runs on the Unity thread, can run during preparation and admission, and must not
        /// change game state or files. Registration still enables rejection on unsupported game builds.
        /// </summary>
        public static bool TryRegister(string ownerGuid, Func<string, bool, MapLoadDecision> decide, out string diagnostic)
        {
            if (!Internal.UnityMainThreadDispatch.IsMainThread || string.IsNullOrWhiteSpace(ownerGuid) ||
                decide == null || provider != null || log == null)
            {
                diagnostic = "Map load policy requires Unity startup and one valid provider.";
                return false;
            }

            TryInstallNativeAlias(out diagnostic);
            provider = decide;
            return true;
        }

        internal static MapLoadDecision Evaluate(string path)
        {
            if (provider == null || string.IsNullOrEmpty(path) ||
                !path.EndsWith(".map", StringComparison.OrdinalIgnoreCase))
                return default;
            try
            {
                var decision = provider(path, CanAlias);
                string name = decision.NativeFileName;
                if (name == null) return decision;
                if (!CanAlias || name.Length > NativeFileNameLimit || name.Length <= 4 ||
                    !name.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
                    name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0)
                    return MapLoadDecision.Reject("The map load policy supplied an unusable native filename for " + path);
                return decision;
            }
            catch (Exception error)
            { return MapLoadDecision.Reject("Map load policy failed for " + path + ": " + error.Message); }
        }

    }
}

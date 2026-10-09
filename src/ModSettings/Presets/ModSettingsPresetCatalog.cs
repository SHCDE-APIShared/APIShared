using APIShared.Internal;
using BepInEx.Logging;
using SHCDESE.API.Components.ModManager;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace APIShared.ModSettings
{
    internal static class ModSettingsPresetCatalog
    {
        internal const long MaximumFileBytes = 8 * 1024 * 1024;
        private const int MaximumFilesPerProvider = 512;
        private const int MaximumPresetsPerTarget = 2048;

        internal static string ValidateTargetGuid(string targetGuid) =>
            RequireSafePathSegment(targetGuid, "target plugin GUID");

        internal static void ValidatePersonalWritePath(
            string providerRoot,
            string personalDirectory,
            string path)
        {
            string root = Path.GetFullPath(providerRoot);
            string directory = Path.GetFullPath(personalDirectory);
            string fullPath = Path.GetFullPath(path);
            if (!IsAtOrBelow(root, directory) || !IsBelow(directory, fullPath))
                throw new InvalidDataException("Personal preset path leaves its target directory.");
            EnsureExistingPathSegmentsNoReparse(root, directory);
            if (File.Exists(fullPath) &&
                (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Personal preset paths may not use reparse points.");
            }
        }

        public static IReadOnlyList<PublishedModSettingsPreset> Discover(
            string targetGuid,
            Version targetVersion,
            string targetPluginDirectory,
            ManualLogSource log) => Discover(
                targetGuid,
                targetVersion,
                targetPluginDirectory,
                Path.Combine(targetPluginDirectory, "LobbyModSettings", "Presets", "Override", targetGuid),
                log);

        public static IReadOnlyList<PublishedModSettingsPreset> Discover(
            string targetGuid,
            Version targetVersion,
            string targetPluginDirectory,
            string personalPresetRoot,
            ManualLogSource log)
        {
            targetGuid = RequireSafePathSegment(targetGuid, "target plugin GUID");
            var providers = new List<PresetProvider>();
            var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddProvider(
                providers,
                seenDirectories,
                "personal:" + targetGuid,
                "Personal",
                personalPresetRoot,
                ModSettingsPresetSourceKind.Personal,
                directoryIsTarget: true);
            AddProvider(
                providers,
                seenDirectories,
                targetGuid,
                targetGuid,
                targetPluginDirectory,
                ModSettingsPresetSourceKind.Bundled,
                directoryIsTarget: false);
            foreach (KeyValuePair<ModInfo, string> provider in GameAssetModManager.Instance.GetRegisteredAssetDirectories())
            {
                if (!Directory.Exists(provider.Value))
                {
                    if (string.Equals(Path.GetExtension(provider.Value), ".semod", StringComparison.OrdinalIgnoreCase))
                        DebugLogHelper.LogWarning(log, "Preset provider [" + provider.Key.Name + "] is a .semod archive and is skipped; published presets currently require a loose folder.");
                    continue;
                }
                AddProvider(
                    providers,
                    seenDirectories,
                    provider.Key.GUID,
                    provider.Key.Name,
                    provider.Value,
                    string.Equals(provider.Key.GUID, targetGuid, StringComparison.OrdinalIgnoreCase)
                        ? ModSettingsPresetSourceKind.Bundled
                        : ModSettingsPresetSourceKind.External,
                    directoryIsTarget: false);
            }

            var result = new List<PublishedModSettingsPreset>();
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PresetProvider provider in providers)
            {
                try
                {
                var providerPresets = new List<PublishedModSettingsPreset>();
                string root = Path.GetFullPath(provider.Root);
                string presetDirectory = provider.DirectoryIsTarget
                    ? root
                    : Path.GetFullPath(Path.Combine(root, "Override", targetGuid));
                if (!IsAtOrBelow(root, presetDirectory) || !Directory.Exists(presetDirectory)) continue;
                EnsureNoReparsePoints(root, presetDirectory);
                string[] files = Directory.GetFiles(presetDirectory, "preset_*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Take(MaximumFilesPerProvider + 1).ToArray();
                if (files.Length > MaximumFilesPerProvider)
                {
                    DebugLogHelper.LogError(log, "Preset provider [" + provider.Name + "] exceeds the per-target file limit and is ignored.");
                    continue;
                }
                foreach (string file in files)
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(file);
                        if (!IsBelow(presetDirectory, fullPath)) throw new InvalidDataException("Preset path leaves its provider directory.");
                        EnsureNoReparsePoints(root, fullPath);
                        var info = new FileInfo(fullPath);
                        if (info.Length <= 0 || info.Length > MaximumFileBytes) throw new InvalidDataException("Preset file size is outside the supported range.");
                        PublishedModSettingsPreset preset = ModSettingsPresetJson.Parse(
                            ReadPresetText(fullPath), provider.Guid, provider.Name, targetGuid, fullPath);
                        preset.SourceKind = provider.SourceKind;
                        if (!MatchesVersion(targetVersion, preset.MinimumTargetVersion, preset.MaximumTargetVersion)) continue;
                        providerPresets.Add(preset);
                    }
                    catch (Exception exception)
                    {
                        DebugLogHelper.LogError(log, "Published preset [" + file + "] was rejected: " + exception.Message);
                    }
                }
                var duplicatedIds = new HashSet<string>(
                    providerPresets
                        .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                        .Where(group => group.Skip(1).Any())
                        .Select(group => group.Key),
                    StringComparer.OrdinalIgnoreCase);
                foreach (string duplicateId in duplicatedIds)
                {
                    DebugLogHelper.LogError(
                        log,
                        "Preset provider [" + provider.Name + "] contains duplicate id [" + duplicateId + "] for target [" + targetGuid + "]; all duplicates are ignored.");
                }
                foreach (PublishedModSettingsPreset preset in providerPresets.Where(item => !duplicatedIds.Contains(item.Id)))
                {
                    if (result.Count >= MaximumPresetsPerTarget)
                    {
                        DebugLogHelper.LogError(log, "Published presets for target [" + targetGuid + "] exceed the global limit; remaining presets are ignored.");
                        break;
                    }
                    if (!identities.Add(preset.StableId))
                    {
                        DebugLogHelper.LogError(log, "Duplicate provider/target/preset identity [" + preset.StableId + "] was ignored.");
                        continue;
                    }
                    result.Add(preset);
                }
                }
                catch (Exception exception)
                {
                    DebugLogHelper.LogError(
                        log,
                        "Preset provider [" + provider.Name + "] was rejected: " + exception.Message);
                }
            }

            return result.OrderBy(item => item.SourceKind)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ProviderName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }

        private static void AddProvider(
            List<PresetProvider> providers,
            HashSet<string> seen,
            string guid,
            string name,
            string path,
            ModSettingsPresetSourceKind sourceKind,
            bool directoryIsTarget)
        {
            if (string.IsNullOrWhiteSpace(guid) || guid.Any(char.IsControl) ||
                string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
            string full = Path.GetFullPath(path);
            if (seen.Add(full))
            {
                providers.Add(new PresetProvider
                {
                    Guid = guid,
                    Name = name ?? guid,
                    Root = full,
                    SourceKind = sourceKind,
                    DirectoryIsTarget = directoryIsTarget,
                });
            }
        }

        private sealed class PresetProvider
        {
            public string Guid;
            public string Name;
            public string Root;
            public ModSettingsPresetSourceKind SourceKind;
            public bool DirectoryIsTarget;
        }

        private static string ReadPresetText(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length <= 0 || stream.Length > MaximumFileBytes)
                    throw new InvalidDataException("Preset file size is outside the supported range.");
                using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true))
                {
                    string text = reader.ReadToEnd();
                    if (stream.Length > MaximumFileBytes || Encoding.UTF8.GetByteCount(text) > MaximumFileBytes)
                        throw new InvalidDataException("Preset file size is outside the supported range.");
                    return text;
                }
            }
        }
        private static bool IsBelow(string root, string path)
        {
            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(path);
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAtOrBelow(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(canonicalRoot, canonicalPath, StringComparison.OrdinalIgnoreCase) ||
                IsBelow(canonicalRoot, canonicalPath);
        }

        private static void EnsureNoReparsePoints(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalPath = Path.GetFullPath(path);
            if (!IsAtOrBelow(canonicalRoot, canonicalPath))
                throw new InvalidDataException("Preset path leaves its provider directory.");
            string relative = canonicalPath.Substring(canonicalRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = canonicalRoot;
            foreach (string segment in relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Preset paths may not traverse reparse points.");
            }
        }

        private static void EnsureExistingPathSegmentsNoReparse(string root, string path)
        {
            string canonicalRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string canonicalPath = Path.GetFullPath(path);
            if (!IsAtOrBelow(canonicalRoot, canonicalPath))
                throw new InvalidDataException("Preset path leaves its provider directory.");
            string relative = canonicalPath.Substring(canonicalRoot.Length)
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string current = canonicalRoot;
            foreach (string segment in relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && !File.Exists(current))
                    break;
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Preset paths may not traverse reparse points.");
            }
        }
        private static bool MatchesVersion(Version actual, string minimum, string maximum)
        {
            if (actual == null) return string.IsNullOrWhiteSpace(minimum) && string.IsNullOrWhiteSpace(maximum);
            Version parsedMinimum = null;
            Version parsedMaximum = null;
            if (!string.IsNullOrWhiteSpace(minimum))
            {
                if (!TryVersion(minimum, out parsedMinimum))
                    throw new InvalidDataException("Invalid minimumTargetVersion [" + minimum + "].");
            }
            if (!string.IsNullOrWhiteSpace(maximum))
            {
                if (!TryVersion(maximum, out parsedMaximum))
                    throw new InvalidDataException("Invalid maximumTargetVersion [" + maximum + "].");
            }
            if (parsedMinimum != null && parsedMaximum != null && parsedMinimum > parsedMaximum)
                throw new InvalidDataException("minimumTargetVersion exceeds maximumTargetVersion.");
            if (parsedMinimum != null && actual < parsedMinimum) return false;
            if (parsedMaximum != null && actual > parsedMaximum) return false;
            return true;
        }
        private static bool TryVersion(string text, out Version version)
        {
            string numeric = (text ?? string.Empty).Split('-', '+', ' ')[0];
            return Version.TryParse(numeric, out version);
        }

        private static string RequireSafePathSegment(string value, string label)
        {
            string segment = (value ?? string.Empty).Trim();
            if (segment.Length == 0 || segment == "." || segment == ".." ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                segment.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                throw new InvalidDataException("Invalid " + label + ".");
            }
            return segment;
        }
    }
}

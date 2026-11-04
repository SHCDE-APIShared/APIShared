using APIShared.Internal;
using BepInEx.Logging;
using MessagePack;
using System;
using System.Collections.Generic;
using System.IO;

namespace APIShared.ModSettings
{
    /// <summary>Owns the binary personal settings file, atomic publication and corrupt-data backups. It has no UI, host or mission state.</summary>
    internal sealed class LobbyPresetStorage
    {
        private readonly string filePath;
        private readonly string legacyFilePath;
        private readonly string validationRoot;
        private readonly ManualLogSource log;
        private readonly string modName;
        internal string FilePath => filePath;
        internal long SuccessfulWrites { get; private set; }
        internal long FailedWrites { get; private set; }
        internal string LastWriteResult { get; private set; } = "not-attempted";

        internal LobbyPresetStorage(string filePath, ManualLogSource log, string modName,
            string legacyFilePath = null, string validationRoot = null)
        {
            this.filePath = filePath;
            this.legacyFilePath = legacyFilePath;
            this.validationRoot = validationRoot;
            this.log = log;
            this.modName = modName;
        }

        // A corrupt or inaccessible new file must never revive stale installation settings.
        private string ReadPath => ExistsAt(filePath) ? filePath : legacyFilePath ?? filePath;
        internal bool Exists => ExistsAt(ReadPath);
        internal bool NeedsRelocation => !ExistsAt(filePath) && legacyFilePath != null && ExistsAt(legacyFilePath);
        internal string DiagnosticPaths => $"workingSettingsPath={filePath}, legacySettingsPath={legacyFilePath ?? "none"}";

        private static bool ExistsAt(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                    throw new IOException("Settings file path is a directory: " + path);
                return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
        }
#if API_SHARED_PRESET_TESTS
        internal int TestWriteCount { get; private set; }
#endif

        internal void Write(Dictionary<string, byte[]> payload)
        {
            string directory = Path.GetDirectoryName(filePath);
            string temporaryPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            bool ownsTemporaryFile = false;
            int publishAttempts = 0;
            try
            {
                ValidateWritePath();
                Directory.CreateDirectory(directory);
                ValidateWritePath();
                byte[] bytes = MessagePackSerializer.Serialize(payload);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    ownsTemporaryFile = true;
                    stream.Write(bytes, 0, bytes.Length);
                }
                PresetAtomicPublishResult result =
                    PresetAtomicFilePublisher.Publish(temporaryPath, filePath);
                publishAttempts = result.Attempts;
                if (!result.Succeeded)
                {
                    FailedWrites++;
                    LastWriteResult = "atomic-publish-failed";
                    DebugLogHelper.LogError(
                        log,
                        $"[{modName}] Could not atomically publish lobby-settings presets to [{filePath}] " +
                        $"after {result.Attempts} attempts; hresult=0x{result.Error.HResult:X8}: {result.Error}");
                }
                else
                {
                    bool report = SuccessfulWrites == 0 || LastWriteResult != "published";
                    SuccessfulWrites++;
                    LastWriteResult = "published";
                    if (report)
                        DebugLogHelper.LogInfo(log,
                            $"SETTINGS_STORAGE: mod={modName}, result=published, path={filePath}, " +
                            $"bytes={bytes.Length}, attempts={result.Attempts}, successfulWrites={SuccessfulWrites}, " +
                            $"failedWrites={FailedWrites}; scope=personal. Subsequent successes are counted in the mission settings snapshot.");
#if API_SHARED_PRESET_TESTS
                    TestWriteCount++;
#endif
                }
            }
            catch (Exception exception)
            {
                FailedWrites++;
                LastWriteResult = "write-failed:" + exception.GetType().FullName;
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Could not save lobby-settings presets to [{filePath}]; " +
                    $"publishAttempts={publishAttempts}, hresult=0x{exception.HResult:X8}: {exception}");
            }
            finally
            {
                try
                {
                    if (ownsTemporaryFile && File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (Exception exception)
                {
                    DebugLogHelper.LogWarning(
                        log,
                        $"[{modName}] Could not remove temporary preset file [{temporaryPath}]: {exception.Message}");
                }
            }
        }

        private void ValidateWritePath()
        {
            if (validationRoot != null)
                ModSettingsPresetCatalog.ValidatePersonalWritePath(validationRoot, Path.GetDirectoryName(filePath), filePath);
        }

        internal bool TryRead(out Dictionary<string, byte[]> payload)
        {
            string readPath = ReadPath;
            payload = null;
            if (!ExistsAt(readPath))
                return false;

            try
            {
                payload = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(
                    File.ReadAllBytes(readPath));
                return payload != null;
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Could not read lobby-settings presets from [{readPath}]: {exception}");
                return false;
            }
        }

        internal void BackupCorruptFile()
        {
            string readPath = ReadPath;
            if (!ExistsAt(readPath))
                return;

            string backupPath = filePath + ".corrupt-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            try
            {
                ValidateWritePath();
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                ValidateWritePath();
                File.Copy(readPath, backupPath, false);
                DebugLogHelper.LogWarning(
                    log,
                    $"[{modName}] Preserved invalid preset data at [{backupPath}].");
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Could not preserve invalid preset data: {exception}");
            }
        }

    }
}

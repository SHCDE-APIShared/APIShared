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
        private readonly ManualLogSource log;
        private readonly string modName;

        internal LobbyPresetStorage(string filePath, ManualLogSource log, string modName)
        {
            this.filePath = filePath;
            this.log = log;
            this.modName = modName;
        }

        internal bool Exists => File.Exists(filePath);
#if API_SHARED_PRESET_TESTS
        internal int TestWriteCount { get; private set; }
#endif

        internal void Write(Dictionary<string, byte[]> payload)
        {
            string directory = Path.GetDirectoryName(filePath);
            string temporaryPath = filePath + ".tmp-" + Guid.NewGuid().ToString("N");
            int publishAttempts = 0;
            try
            {
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(temporaryPath, MessagePackSerializer.Serialize(payload));
                PresetAtomicPublishResult result =
                    PresetAtomicFilePublisher.Publish(temporaryPath, filePath);
                publishAttempts = result.Attempts;
                if (!result.Succeeded)
                {
                    DebugLogHelper.LogError(
                        log,
                        $"[{modName}] Could not atomically publish lobby-settings presets to [{filePath}] " +
                        $"after {result.Attempts} attempts; hresult=0x{result.Error.HResult:X8}: {result.Error}");
                }
#if API_SHARED_PRESET_TESTS
                else
                    TestWriteCount++;
#endif
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Could not save lobby-settings presets to [{filePath}]; " +
                    $"publishAttempts={publishAttempts}, hresult=0x{exception.HResult:X8}: {exception}");
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
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

        internal bool TryRead(out Dictionary<string, byte[]> payload)
        {
            payload = null;
            if (!File.Exists(filePath))
                return false;

            try
            {
                payload = MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(
                    File.ReadAllBytes(filePath));
                return payload != null;
            }
            catch (Exception exception)
            {
                DebugLogHelper.LogError(
                    log,
                    $"[{modName}] Could not read lobby-settings presets from [{filePath}]: {exception}");
                return false;
            }
        }

        internal void BackupCorruptFile()
        {
            if (!File.Exists(filePath))
                return;

            string backupPath = filePath + ".corrupt-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmssfff");
            try
            {
                File.Copy(filePath, backupPath, false);
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

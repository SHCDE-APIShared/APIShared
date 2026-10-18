// APIShared-owned implementation; independent of workspace Shared helpers.
// Initial provenance: a7888900e, Shared/DebugLogHelper.cs. No automatic synchronization.
using BepInEx.Logging;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

namespace APIShared.Internal
{
    internal static class DebugLogHelper
    {
        public const string CurrentNativeSha256 =
            "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";

        private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(1);
        private static DateTime debugEnabledCacheExpiresAtUtc;
        private static bool debugEnabledCache;
        // API_SHARED_DIAGNOSTICS: optional logs only. Public observer registration is
        // separate explicit consumer demand and is never disabled by this switch.
        private static bool diagnosticsInitialized;
        private static volatile bool diagnosticsEnabled;
        private static readonly Dictionary<ManualLogSource, ManualLogSource> serviceLogs =
            new Dictionary<ManualLogSource, ManualLogSource>();
        internal static bool DiagnosticsEnabled => diagnosticsEnabled;

        internal static void InitializeDiagnostics(ConfigFile config, ManualLogSource log)
        {
            if (diagnosticsInitialized) return;
            diagnosticsInitialized = true;
            try
            {
                diagnosticsEnabled = config.Bind("Diagnostics", "Enabled", false,
                    "API_SHARED_DIAGNOSTICS: enable APIShared routine logs and optional diagnostic presentation. " +
                    "Keep disabled for normal releases. Restart required; Debug output also needs a BepInEx Debug listener. " +
                    "Consumer-registered observers and warning/error reports are independent.").Value;
                if (diagnosticsEnabled)
                    LogWarning(log, "API_SHARED_DIAGNOSTICS_ENABLED: diagnostics are explicitly enabled for this process. " +
                        "This persisted setting will also enable them on later starts; normal release default is disabled.");
            }
            catch (Exception ex)
            {
                diagnosticsEnabled = false;
                LogError(log, "API_SHARED_DIAGNOSTICS_CONFIG_FAILED: diagnostics remain disabled: " + ex);
            }
        }

        // API_SHARED_DIAGNOSTICS: an unregistered source filters logs from services
        // retaining a logger, including direct Info/Debug calls. Failures still forward.
        internal static ManualLogSource ForExternalCode(ManualLogSource log)
        {
            if (log == null) return null;
            lock (serviceLogs)
            {
                if (serviceLogs.TryGetValue(log, out ManualLogSource proxy)) return proxy;
                proxy = new ManualLogSource(log.SourceName);
                proxy.LogEvent += (_, args) =>
                {
                    if (DiagnosticsEnabled || (args.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0)
                        log.Log(args.Level, WithTimestamp(Convert.ToString(args.Data, CultureInfo.InvariantCulture)));
                };
                serviceLogs.Add(log, proxy);
                return proxy;
            }
        }

        public static bool IsDebugEnabled()
        {
            if (!DiagnosticsEnabled) return false;
            DateTime now = DateTime.UtcNow;
            if (now < debugEnabledCacheExpiresAtUtc)
                return debugEnabledCache;

            debugEnabledCache = ComputeDebugEnabled();
            debugEnabledCacheExpiresAtUtc = now + CacheDuration;
            return debugEnabledCache;
        }

        public static bool IsDiskDebugEnabled()
        {
            if (!DiagnosticsEnabled) return false;
            try
            {
                foreach (ILogListener listener in Logger.Listeners)
                {
                    if (listener is DiskLogListener diskLogListener &&
                        HasDebugFlag(diskLogListener.DisplayedLogLevel))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public static void LogDebug(ManualLogSource log, params object[] parts)
        {
            if (log == null || !IsDebugEnabled())
                return;

            log.LogDebug(WithTimestamp(string.Join(" ", parts)));
        }

        public static void LogDebug(ManualLogSource log, Func<string> messageFactory)
        {
            if (log == null || messageFactory == null || !IsDebugEnabled())
                return;

            log.LogDebug(WithTimestamp(messageFactory()));
        }

        public static void LogInfo(ManualLogSource log, string message)
        {
            if (!DiagnosticsEnabled) return;
            log?.LogInfo(WithTimestamp(message));
        }

        public static void LogWarning(ManualLogSource log, string message)
        {
            log?.LogWarning(WithTimestamp(message));
        }

        public static void LogError(ManualLogSource log, string message)
        {
            log?.LogError(WithTimestamp(message));
        }

        public static bool ReportNativeLibraryVersion(
            ManualLogSource log,
            string componentName,
            bool requireCurrentVersion = false,
            bool logSuccess = true)
        {
            string label = string.IsNullOrWhiteSpace(componentName)
                ? "Mod"
                : componentName;

            try
            {
                string path = Path.Combine(
                    BepInEx.Paths.GameRootPath,
                    "Stronghold Crusader Definitive Edition_Data",
                    "Plugins",
                    "x86_64",
                    "CrusaderDE.dll");
                if (!File.Exists(path))
                {
                    LogError(log, $"{label} cannot verify CrusaderDE.dll because the installed file was not found: path={path}.");
                    return false;
                }

                string actualHash;
                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha256 = SHA256.Create())
                {
                    actualHash = BitConverter.ToString(sha256.ComputeHash(stream))
                        .Replace("-", string.Empty);
                }

                long fileSize = new FileInfo(path).Length;
                if (string.Equals(actualHash, CurrentNativeSha256, StringComparison.OrdinalIgnoreCase))
                {
                    if (logSuccess)
                    {
                        LogDebug(
                            log,
                            $"{label} verified the installed CrusaderDE.dll: sha256={actualHash}, size={fileSize}, path={path}.");
                    }
                    return true;
                }

                string message =
                    $"{label} detected a changed CrusaderDE.dll: expectedSha256={CurrentNativeSha256}, " +
                    $"actualSha256={actualHash}, size={fileSize}, path={path}.";
                if (requireCurrentVersion)
                {
                    LogError(log, message + " Version-sensitive native code remains inactive.");
                }
                else
                {
                    LogWarning(
                        log,
                        message +
                        " Signature-validated code may continue; any failed validation is logged and the affected feature remains inactive.");
                }

                return false;
            }
            catch (Exception ex)
            {
                if (requireCurrentVersion)
                {
                    LogError(log, $"{label} could not verify the installed CrusaderDE.dll; version-sensitive native code must remain inactive: {ex}");
                }
                else
                {
                    LogWarning(
                        log,
                        $"{label} could not verify the installed CrusaderDE.dll hash. " +
                        $"Signature-validated code may continue; any failed validation is logged and the affected feature remains inactive. Reason: {ex}");
                }
                return false;
            }
        }

        public static bool IsCurrentNativeLibraryVersion()
        {
            try
            {
                string path = Path.Combine(
                    BepInEx.Paths.GameRootPath,
                    "Stronghold Crusader Definitive Edition_Data",
                    "Plugins",
                    "x86_64",
                    "CrusaderDE.dll");
                if (!File.Exists(path))
                    return false;

                using (FileStream stream = File.OpenRead(path))
                using (SHA256 sha256 = SHA256.Create())
                {
                    string actualHash = BitConverter.ToString(sha256.ComputeHash(stream))
                        .Replace("-", string.Empty);
                    return string.Equals(
                        actualHash,
                        CurrentNativeSha256,
                        StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                // Callers use false to keep only fixed-layout native code inactive.
                return false;
            }
        }

        private static string WithTimestamp(string message)
        {
            return $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message ?? string.Empty}";
        }

        private static bool ComputeDebugEnabled()
        {
            try
            {
                foreach (ILogListener listener in Logger.Listeners)
                {
                    if (IsDebugEnabled(listener))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool IsDebugEnabled(ILogListener listener)
        {
            if (listener == null)
                return false;

            if (listener is DiskLogListener diskLogListener)
                return HasDebugFlag(diskLogListener.DisplayedLogLevel);

            object displayedLogLevel = TryGetPropertyValue(listener, "DisplayedLogLevel");
            if (displayedLogLevel is LogLevel listenerLogLevel)
                return HasDebugFlag(listenerLogLevel);

            object value = TryGetConfigEntryValue(listener.GetType(), "ConfigConsoleDisplayedLevel");
            return value is LogLevel logLevel && HasDebugFlag(logLevel);
        }

        private static object TryGetPropertyValue(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            return property?.GetValue(instance);
        }

        private static object TryGetConfigEntryValue(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            object configEntry = field?.GetValue(null);
            if (configEntry == null)
                return null;

            PropertyInfo valueProperty = configEntry.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public);
            return valueProperty?.GetValue(configEntry);
        }

        private static bool HasDebugFlag(LogLevel logLevel)
        {
            return (logLevel & LogLevel.Debug) != LogLevel.None;
        }
    }
}

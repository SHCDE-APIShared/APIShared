// APIShared-owned implementation; independent of workspace Shared helpers.
// Initial provenance: a7888900e, Shared/DebugLogHelper.cs. No automatic synchronization.
using BepInEx.Logging;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace APIShared.Internal
{
    internal static class DebugLogHelper
    {
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
            RuntimeHealth.Initialize(log);
            try
            {
                diagnosticsEnabled = config.Bind("Diagnostics", "Enabled", false,
                    "API_SHARED_DIAGNOSTICS: enable APIShared Debug logs and optional diagnostic presentation. " +
                    "Keep disabled for normal releases. Restart required; Debug output also needs a BepInEx Debug listener. " +
                    "Info, warning and error logs, and consumer-registered observers, are independent of this switch.").Value;
                // A deliberate user choice, not a failure: bypass the failure ledger and its tag.
                if (diagnosticsEnabled)
                    try
                    {
                        log?.LogWarning(WithTimestamp("API_SHARED_DIAGNOSTICS_ENABLED: diagnostics are explicitly enabled for this process. " +
                            "This persisted setting will also enable them on later starts; normal release default is disabled."));
                    }
                    catch { }
            }
            catch (Exception ex)
            {
                diagnosticsEnabled = false;
                LogError(log, "API_SHARED_DIAGNOSTICS_CONFIG_FAILED: diagnostics remain disabled: " + ex);
            }
        }

        // An unregistered source for services retaining a logger: failures forward through
        // RuntimeHealth, Info always passes, and only Debug needs API_SHARED_DIAGNOSTICS.
        // Info volume is kept low at the call sites (one-time markers), not by this filter.
        internal static ManualLogSource ForExternalCode(ManualLogSource log)
        {
            if (log == null) return null;
            lock (serviceLogs)
            {
                if (serviceLogs.TryGetValue(log, out ManualLogSource proxy)) return proxy;
                proxy = new ManualLogSource(log.SourceName);
                proxy.LogEvent += (_, args) =>
                {
                    string text = Convert.ToString(args.Data, CultureInfo.InvariantCulture);
                    if ((args.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal)) != 0)
                        RuntimeHealth.Forward(log, args.Level, text);
                    else if ((args.Level & LogLevel.Debug) == 0 || IsDebugEnabled())
                        try { log.Log(args.Level, HasTimestamp(text) ? text : WithTimestamp(text)); } catch { }
                };
                serviceLogs.Add(log, proxy);
                return proxy;
            }
        }

        // Explicit launch rejections are individual events, even when their text is identical.
        // Unwrap only service loggers created here; foreign logger behavior stays with its owner.
        internal static ManualLogSource UnwrapServiceLog(ManualLogSource source)
        {
            if (source == null) return null;
            lock (serviceLogs)
                foreach (var entry in serviceLogs)
                    if (ReferenceEquals(entry.Value, source)) return entry.Key;
            return source;
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

        public static void LogDebug(ManualLogSource log, string message)
        {
            if (log == null || !IsDebugEnabled())
                return;

            try { log.LogDebug(WithTimestamp(message)); } catch { }
        }

        public static void LogDebug(ManualLogSource log, Func<string> messageFactory)
        {
            if (log == null || messageFactory == null || !IsDebugEnabled())
                return;

            try { log.LogDebug(WithTimestamp(messageFactory())); } catch { }
        }

        public static void LogInfo(ManualLogSource log, string message)
        {
            try { log?.LogInfo(WithTimestamp(message)); } catch { }
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void LogWarning(ManualLogSource log, string message)
        {
            RuntimeHealth.Failure(log, RuntimeHealth.CallerFeature(), false, message);
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        public static void LogError(ManualLogSource log, string message)
        {
            RuntimeHealth.Failure(log, RuntimeHealth.CallerFeature(), true, message);
        }

        private static string WithTimestamp(string message)
        {
            return $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}] {message ?? string.Empty}";
        }

        // Services that stamp their own lines ("[yyyy-MM-dd HH:mm:ss.fff]" or without brackets)
        // must not receive a second timestamp from the forwarding proxy.
        private static bool HasTimestamp(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int start = text[0] == '[' ? 1 : 0;
            if (text.Length < start + 23) return false;
            for (int i = 0; i < 23; i++)
            {
                char c = text[start + i];
                bool separator = i == 4 || i == 7 || i == 10 || i == 13 || i == 16 || i == 19;
                if (separator ? !(c == '-' || c == ' ' || c == ':' || c == '.') : (c < '0' || c > '9')) return false;
            }
            return true;
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

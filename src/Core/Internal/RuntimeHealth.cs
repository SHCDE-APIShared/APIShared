// APIShared-owned failure reporting; no workspace source dependency.
using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Logging;

namespace APIShared.Internal
{
    // Runs only at a failure boundary. Cache eviction never prevents a new report.
    internal sealed class RuntimeHealthLedger
    {
        internal const string FailureTag = "MOD_FAILURE", WarningTag = "MOD_WARNING";
        private sealed class Entry
        {
            internal LinkedListNode<string> Node;
            internal long EmittedAt, Suppressed;
        }
        private readonly object sync = new object();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly LinkedList<string> recent = new LinkedList<string>();
        private readonly string owner;
        private readonly Action<LogLevel, string> write;
        private readonly Func<long> clock;
        private readonly long interval;
        private readonly int capacity;
        internal RuntimeHealthLedger(string owner, Action<LogLevel, string> write)
            : this(owner, write, System.Diagnostics.Stopwatch.GetTimestamp, System.Diagnostics.Stopwatch.Frequency, 4096) { }
        internal RuntimeHealthLedger(string owner, Action<LogLevel, string> write, Func<long> clock, long frequency, int capacity = 4096)
        {
            if (clock == null || frequency <= 0 || frequency > long.MaxValue / 30 || capacity <= 0)
                throw new ArgumentException("A monotonic clock, valid frequency and positive capacity are required.");
            this.owner = owner ?? "unknown"; this.write = write; this.clock = clock;
            interval = frequency * 30; this.capacity = capacity;
        }
        internal int Count { get { lock (sync) return entries.Count; } }
        internal void Report(string feature, bool error, string detail)
        {
            try
            {
                feature = Clean(feature);
                // Hash the complete normalized cause: truncating display text must not merge different failures.
                string normalized = System.Text.RegularExpressions.Regex.Replace(detail ?? "",
                    @"\[\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d{3}\]", "").Replace('\r', ' ').Replace('\n', ' ');
                string cause;
                using (var hash = System.Security.Cryptography.SHA256.Create())
                    cause = BitConverter.ToString(hash.ComputeHash(System.Text.Encoding.UTF8.GetBytes(normalized))).Replace("-", "");
                string key = feature + (error ? "\nerror\n" : "\nwarning\n") + cause;
                long suppressed = 0;
                lock (sync)
                {
                    long now = clock();
                    if (entries.TryGetValue(key, out var entry))
                    {
                        recent.Remove(entry.Node); recent.AddLast(entry.Node);
                        if (now - entry.EmittedAt < interval)
                        {
                            if (entry.Suppressed < long.MaxValue) entry.Suppressed++;
                            return;
                        }
                        suppressed = entry.Suppressed; entry.Suppressed = 0; entry.EmittedAt = now;
                    }
                    else
                    {
                        if (entries.Count >= capacity) { entries.Remove(recent.First.Value); recent.RemoveFirst(); }
                        entries.Add(key, new Entry { Node = recent.AddLast(key), EmittedAt = now });
                    }
                }
                // Warnings can be expected degradations; only errors are labelled as failures.
                // The full hash stays the identity key; a 12-digit prefix suffices to correlate lines.
                string message = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) +
                    "] " + (error ? FailureTag : WarningTag) + " mod=" + owner + "; feature=" + feature +
                    "; cause=" + cause.Substring(0, 12) + "; detail=" + Clean(detail) +
                    (suppressed == 0 ? "" : "; suppressed repeats=" + suppressed);
                try { write?.Invoke(error ? LogLevel.Error : LogLevel.Warning, message); } catch { }
            }
            catch { /* Logging cannot interrupt gameplay or cleanup. */ }
        }
        private static string Clean(string value)
        {
            value = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
            return value.Length <= 4096 ? value : value.Substring(0, 4096);
        }
    }

    internal static class RuntimeHealth
    {
        private static RuntimeHealthLedger ledger;
        private static ManualLogSource logger;
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ManualLogSource, RuntimeHealthLedger> fallbackLedgers =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ManualLogSource, RuntimeHealthLedger>();
        internal static void Initialize(ManualLogSource log)
        {
            if (ledger != null || log == null) return;
            logger = log;
            ledger = new RuntimeHealthLedger(log.SourceName, (level, text) => Emit(logger, level, text));
        }
        internal static void Failure(ManualLogSource fallback, string feature, bool error, string detail)
        {
            if (fallback == null) return;
            if (ReferenceEquals(fallback, logger)) { ledger?.Report(feature, error, detail); return; }
            // Libraries/offline consumers may use several independent log sources.
            // Do not capture their first logger as the process plugin's logger.
            try
            {
                fallbackLedgers.GetValue(fallback, source => new RuntimeHealthLedger(source.SourceName,
                    (level, text) => Emit(source, level, text))).Report(feature, error, detail);
            }
            catch { /* Logging cannot interrupt gameplay or cleanup. */ }
        }
        // Existing two-argument logging APIs remain binary compatible. The stack is
        // inspected only when a failure is reported, never during successful gameplay.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        internal static string CallerFeature()
        {
            try
            {
                foreach (var frame in new System.Diagnostics.StackTrace(false).GetFrames())
                {
                    var method = frame.GetMethod(); var type = method?.DeclaringType;
                    string name = type?.FullName;
                    if (name == null || name.StartsWith("System.", StringComparison.Ordinal) ||
                        name.StartsWith("BepInEx.Logging.", StringComparison.Ordinal) ||
                        name.StartsWith(typeof(DebugLogHelper).FullName, StringComparison.Ordinal) ||
                        name.StartsWith(typeof(RuntimeHealth).FullName, StringComparison.Ordinal) ||
                        name == "APIShared.NativeApiLog") continue;
                    return name + "." + method.Name;
                }
            }
            catch { }
            return "unknown-caller";
        }
        internal static void Forward(ManualLogSource target, LogLevel level, string text)
        {
            // Already reported failures passed through a service proxy retain their
            // identity and repeat count. A proxy must not throttle them a second time.
            if (text != null && (text.IndexOf("] " + RuntimeHealthLedger.FailureTag + " mod=", StringComparison.Ordinal) >= 0 ||
                text.IndexOf("] " + RuntimeHealthLedger.WarningTag + " mod=", StringComparison.Ordinal) >= 0))
            { Emit(target, level, text); return; }
            Failure(target, CallerFeature(), (level & (LogLevel.Error | LogLevel.Fatal)) != 0, text);
        }
        // Written on the reporting thread: BepInEx log sources are thread-safe, and a
        // main-thread handoff would lose exactly the reports of a hanging or crashing frame.
        private static void Emit(ManualLogSource target, LogLevel level, string text)
        { try { target?.Log(level, text); } catch { } }
    }
}

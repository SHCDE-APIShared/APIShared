using System;
using APIShared.Internal;
using BepInEx.Logging;

namespace APIShared.Statistics
{
    /// <summary>Shared registration broker; owns no table, permissions, game data or polling.
    /// All operations after APIShared initialization on Unity's main thread. Registrations are permanent;
    /// providers run only when a renderer explicitly requests a selected column. Independent of any specific renderer mod.</summary>
    public static class LordStatistics
    {
        private static readonly ManualLogSource log = Logger.CreateLogSource("APIShared.Statistics");
        private static int errors;
        private static readonly StatisticRegistry registry = new StatisticRegistry(() => UnityMainThreadDispatch.IsMainThread, Report);
        private static void Report(string source, Exception error)
        { if (++errors <= 12) log.LogWarning("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + source + ": " + error); }
        /// <summary>Registers a unique owner/local-ID pair. False leaves the existing registration untouched;
        /// reason diagnoses invalid input, a duplicate or incorrect thread/initialization.</summary>
        public static bool TryRegister(string ownerGuid, LordStatisticColumnDefinition definition,
            out LordStatisticColumn column, out string reason) => registry.Register(ownerGuid, definition, out column, out reason);
        /// <summary>Gets current catalog without invoking metadata or values providers.</summary>
        public static LordStatisticCatalog GetCatalog() => registry.Snapshot();
        /// <summary>Ordered synchronous catalog notifications, outside registry locks. No automatic replay;
        /// subscribe then GetCatalog on the same Unity thread. Exceptions are isolated. UI dispatch belongs
        /// to consumers. Reentrant mutations queue later revisions until the current revision is delivered.</summary>
        public static event Action<LordStatisticCatalog> CatalogChanged
        { add { registry.RequireThread(); registry.Changed += value; } remove { registry.RequireThread(); registry.Changed -= value; } }
    }
}

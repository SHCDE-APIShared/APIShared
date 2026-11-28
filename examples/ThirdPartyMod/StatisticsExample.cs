using System;
using System.Collections.Generic;
using APIShared.Statistics;
using Noesis;

namespace ThirdPartyMod
{
    // Invoke on Unity's main thread after APIShared initialization, e.g. inside the
    // posted OnReady callback. No renderer assembly reference is necessary.
    internal static class StatisticsExample
    {
        internal static LordStatisticColumn Register(string ownerGuid, Func<ImageSource> icon,
            Func<int, decimal?> readCurrentValue, Action<string> report)
        {
            var definition = new LordStatisticColumnDefinition("current-score",
                () => "Current example score", () => "Current score in points; unknown if unavailable.", icon,
                request => {
                    // Request IDs are authorized for display or ordering. Sample all requested
                    // players together; native providers acquire their normal read lock here.
                    var snapshot = new Dictionary<int, decimal?>();
                    foreach (int playerId in request.PlayerIds) snapshot[playerId] = readCurrentValue(playerId);
                    return snapshot;
                }, LordStatisticTheme.OtherMods,
                value => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " pt",
                canShowValue: view => view.IsOwn || view.IsSpectator, sortHiddenValues: false);
            if (LordStatistics.TryRegister(ownerGuid, definition, out var column, out string reason)) return column;
            report(reason); return null;
            // Keep the returned handle process-wide. Use SetActive(false/true) for feature settings,
            // RefreshMetadata for language or visibility changes.
        }
    }
}

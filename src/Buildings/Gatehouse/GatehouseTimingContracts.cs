using BepInEx.Logging;
using RedBird.Core.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    /// <summary>Documented Vanilla values and supported UI ranges for gatehouse timing.</summary>
    public static class GatehouseTimingValues
    {
        /// <summary>Minimum supported human reopening delay in seconds.</summary>
        public const double MinimumHumanDelaySeconds = 0.0;
        /// <summary>Maximum supported human reopening delay in seconds.</summary>
        public const double MaximumHumanDelaySeconds = 30.0;
        /// <summary>Minimum supported AI reopening delay in seconds.</summary>
        public const double MinimumAiDelaySeconds = 0.0;
        /// <summary>Maximum supported AI reopening delay in seconds.</summary>
        public const double MaximumAiDelaySeconds = 120.0;
        /// <summary>Minimum supported closing distance in tiles.</summary>
        public const double MinimumDistanceTiles = 5.0;
        /// <summary>Maximum supported closing distance in tiles.</summary>
        public const double MaximumDistanceTiles = 50.0;
        /// <summary>Vanilla human reopening delay in seconds.</summary>
        public const double VanillaHumanDelaySeconds = 2.5;
        /// <summary>Vanilla AI reopening delay in seconds.</summary>
        public const double VanillaAiDelaySeconds = 30.0;
        /// <summary>Vanilla human closing distance in tiles.</summary>
        public const double VanillaHumanDistanceTiles = 17.5;
        /// <summary>Vanilla AI closing distance in tiles.</summary>
        public const double VanillaAiDistanceTiles = 25.0;
    }

    /// <summary>Immutable desired gatehouse timing and enemy-proximity settings.</summary>
    public readonly struct GatehouseTimingSettings
    {
        /// <summary>Creates gatehouse settings expressed in seconds and map tiles.</summary>
        public GatehouseTimingSettings(
            bool enabled,
            double humanReopenDelaySeconds,
            double aiReopenDelaySeconds,
            double humanCloseDistanceTiles,
            double aiCloseDistanceTiles)
        {
            Enabled = enabled;
            HumanReopenDelaySeconds = humanReopenDelaySeconds;
            AiReopenDelaySeconds = aiReopenDelaySeconds;
            HumanCloseDistanceTiles = humanCloseDistanceTiles;
            AiCloseDistanceTiles = aiCloseDistanceTiles;
        }

        /// <summary>
        /// Gets whether custom timing values should be applied. False restores only the four
        /// Vanilla timing values and does not change the independently owned distance origin.
        /// </summary>
        public bool Enabled { get; }
        /// <summary>Gets the human gate reopening delay in seconds.</summary>
        public double HumanReopenDelaySeconds { get; }
        /// <summary>Gets the AI gate reopening delay in seconds.</summary>
        public double AiReopenDelaySeconds { get; }
        /// <summary>Gets the human gate closing distance in tiles.</summary>
        public double HumanCloseDistanceTiles { get; }
        /// <summary>Gets the AI gate closing distance in tiles.</summary>
        public double AiCloseDistanceTiles { get; }
    }

    /// <summary>
    /// Applies validated, transactional gatehouse timing values.
    /// </summary>
    public interface IGatehouseTimingCapability
    {
        /// <summary>Attempts to apply and verify all four gatehouse timing values as one transaction.</summary>
        bool TryApply(GatehouseTimingSettings settings, out NativeCapabilityDiagnostic diagnostic);
    }
}

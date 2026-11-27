using BepInEx.Logging;
using RedBird.Core.Memory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace APIShared
{
    internal sealed class GatehouseTimingService
    {
        private const int TicksPerSecond = 40;
        private const int UnitsPerTile = Buildings.BuildingGeometry.NativeUnitsPerTile;
        private readonly string binaryHash;
        private readonly GatehouseTimingTarget target;
        private readonly GatehousePermanentRuntimeState runtimeState;
        private readonly NativeOwnershipRegistry ownership;
        private readonly object mutationSync;
        private readonly ManualLogSource log;
        private int expectedAiDistance = GatehouseTimingTarget.VanillaAiDistance;
        private int expectedAiDelay = GatehouseTimingTarget.VanillaAiDelay;
        private int expectedHumanDistance = GatehouseTimingTarget.VanillaHumanDistance;
        private int expectedHumanDelay = GatehouseTimingTarget.VanillaHumanDelay;
        private string automationOwner;

        public GatehouseTimingService(
            string binaryHash,
            GatehouseTimingTarget target,
            GatehousePermanentRuntimeState runtimeState,
            NativeOwnershipRegistry ownership,
            object mutationSync,
            ManualLogSource log)
        {
            this.binaryHash = binaryHash;
            this.target = target ?? throw new ArgumentNullException(nameof(target));
            this.runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
            this.ownership = ownership ?? throw new ArgumentNullException(nameof(ownership));
            this.mutationSync = mutationSync ?? throw new ArgumentNullException(nameof(mutationSync));
            this.log = log;
            VerifyInitialLayoutAndOwnedValues();
        }

        public IGatehouseTimingCapability Bind(string ownerGuid) => new OwnerCapability(this, ownerGuid);

        internal void ReadClosingRange(out GatehouseDistanceOrigin origin, out int maximumNativeDistance) =>
            runtimeState.ReadClosingRange(out origin, out maximumNativeDistance);

        private bool TrySetManualOnlyResolver(string ownerGuid, Func<int, bool> resolver,
            out NativeCapabilityDiagnostic diagnostic)
        {
            lock (mutationSync)
            {
                bool available = runtimeState.Automation != null;
                string failure = available ? null : "The audited gate/bridge automation hooks are unavailable.";
                NativeCapabilityState state = NativeCapabilityState.ValidationFailed;
                if (available && automationOwner != null && automationOwner != ownerGuid)
                {
                    available = false;
                    failure = "The per-building automation policy already belongs to another owner.";
                    state = NativeCapabilityState.Conflict;
                }
                if (available)
                {
                    available = runtimeState.Automation.TryPublish(resolver, out failure);
                    if (available) automationOwner = ownerGuid;
                }
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.GatehouseAutomation,
                    available ? NativeCapabilityState.Available : state, binaryHash,
                    available ? "Per-building automation policy published; native hooks remain installed." : failure);
                return available;
            }
        }

        private bool TryApply(string ownerGuid, GatehouseTimingSettings settings, out NativeCapabilityDiagnostic diagnostic)
        {
            int aiDistance;
            int aiDelay;
            int humanDistance;
            int humanDelay;
            try
            {
                if (settings.Enabled)
                {
                    ValidateRange(settings.HumanReopenDelaySeconds, GatehouseTimingValues.MinimumHumanDelaySeconds, GatehouseTimingValues.MaximumHumanDelaySeconds, nameof(settings.HumanReopenDelaySeconds));
                    ValidateRange(settings.AiReopenDelaySeconds, GatehouseTimingValues.MinimumAiDelaySeconds, GatehouseTimingValues.MaximumAiDelaySeconds, nameof(settings.AiReopenDelaySeconds));
                    ValidateRange(settings.HumanCloseDistanceTiles, GatehouseTimingValues.MinimumDistanceTiles, GatehouseTimingValues.MaximumDistanceTiles, nameof(settings.HumanCloseDistanceTiles));
                    ValidateRange(settings.AiCloseDistanceTiles, GatehouseTimingValues.MinimumDistanceTiles, GatehouseTimingValues.MaximumDistanceTiles, nameof(settings.AiCloseDistanceTiles));
                    humanDelay = ConvertNativeUInt16(settings.HumanReopenDelaySeconds, TicksPerSecond, nameof(settings.HumanReopenDelaySeconds));
                    aiDelay = ConvertNativeUInt16(settings.AiReopenDelaySeconds, TicksPerSecond, nameof(settings.AiReopenDelaySeconds));
                    humanDistance = ConvertNativeUInt16(settings.HumanCloseDistanceTiles, UnitsPerTile, nameof(settings.HumanCloseDistanceTiles));
                    aiDistance = ConvertNativeUInt16(settings.AiCloseDistanceTiles, UnitsPerTile, nameof(settings.AiCloseDistanceTiles));
                }
                else
                {
                    aiDistance = GatehouseTimingTarget.VanillaAiDistance;
                    aiDelay = GatehouseTimingTarget.VanillaAiDelay;
                    humanDistance = GatehouseTimingTarget.VanillaHumanDistance;
                    humanDelay = GatehouseTimingTarget.VanillaHumanDelay;
                }
            }
            catch (Exception ex)
            {
                diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, ex.Message);
                return false;
            }

            lock (mutationSync)
            {
                if (!ownership.TryReserve(
                        ownerGuid,
                        NativeCapabilityIds.GatehouseTiming,
                        NativeReservationMode.Exclusive,
                        target.Intervals,
                        out string conflictOwner))
                {
                    diagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.GatehouseTiming,
                        NativeCapabilityState.Conflict,
                        binaryHash,
                        "The gatehouse timing memory is already reserved by another owner.",
                        conflictOwner);
                    return false;
                }

                try
                {
                    VerifyOwnedValues();
                    if (aiDistance == expectedAiDistance && aiDelay == expectedAiDelay &&
                        humanDistance == expectedHumanDistance && humanDelay == expectedHumanDelay)
                    {
                        diagnostic = Diagnostic(NativeCapabilityState.Available, "The requested gatehouse timing values are already active and were verified.");
                        return true;
                    }
                    runtimeState.PublishTiming(aiDistance, aiDelay, humanDistance, humanDelay);
                    expectedAiDistance = aiDistance;
                    expectedAiDelay = aiDelay;
                    expectedHumanDistance = humanDistance;
                    expectedHumanDelay = humanDelay;
                    VerifyOwnedValues();
                    string values = FormatValues(aiDistance, aiDelay, humanDistance, humanDelay);
                    diagnostic = Diagnostic(NativeCapabilityState.Available, "Gatehouse timing values were applied and verified: " + values);
                    NativeApiLog.Debug(log, $"capability={NativeCapabilityIds.GatehouseTiming}, build={binaryHash}, owner={ownerGuid}, enabled={settings.Enabled}, status=applied, {values}");
                    return true;
                }
                catch (Exception ex)
                {
                    diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, ex.Message);
                    NativeApiLog.Error(log, $"capability={NativeCapabilityIds.GatehouseTiming}, build={binaryHash}, owner={ownerGuid}, status=failed, error={ex}");
                    return false;
                }
            }
        }

        private void VerifyInitialLayoutAndOwnedValues()
        {
            try
            {
                VerifyOwnedValues();
            }
            catch (InvalidOperationException ex)
            {
                throw new NativeResolutionException(NativeCapabilityState.ValidationFailed, ex.Message);
            }
        }

        private void VerifyOwnedValues()
        {
            runtimeState.ReadTiming(
                out int aiDistance,
                out int aiDelay,
                out int humanDistance,
                out int humanDelay);
            if (!runtimeState.IsTimingInstalled ||
                aiDistance != expectedAiDistance || aiDelay != expectedAiDelay ||
                humanDistance != expectedHumanDistance || humanDelay != expectedHumanDelay)
            {
                throw new InvalidOperationException(
                    "Gatehouse timing logical state changed unexpectedly.");
            }
        }

        private NativeCapabilityDiagnostic Diagnostic(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.GatehouseTiming, state, binaryHash, reason);

        private static void ValidateRange(double value, double minimum, double maximum, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < minimum || value > maximum)
                throw new ArgumentOutOfRangeException(name, $"{name} must be finite and between {minimum} and {maximum}.");
        }

        internal static int ConvertNativeUInt16(double value, int multiplier, string name)
        {
            int converted = checked((int)Math.Round(value * multiplier, MidpointRounding.AwayFromZero));
            if (converted < ushort.MinValue || converted > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(name, $"{name} converts to {converted}, outside the native UInt16 range.");
            return converted;
        }

        private static string FormatValues(int aiDistance, int aiDelay, int humanDistance, int humanDelay) =>
            $"humanClose={humanDistance / (double)UnitsPerTile:0.###}tiles/{humanDistance}units, " +
            $"humanReopen={humanDelay / (double)TicksPerSecond:0.###}s/{humanDelay}ticks, " +
            $"aiClose={aiDistance / (double)UnitsPerTile:0.###}tiles/{aiDistance}units, " +
            $"aiReopen={aiDelay / (double)TicksPerSecond:0.###}s/{aiDelay}ticks";

        private sealed class OwnerCapability : IGatehouseTimingCapability, IGatehouseAutomationCapability
        {
            private readonly GatehouseTimingService service;
            private readonly string ownerGuid;
            public OwnerCapability(GatehouseTimingService service, string ownerGuid) { this.service = service; this.ownerGuid = ownerGuid; }
            public bool TryApply(GatehouseTimingSettings settings, out NativeCapabilityDiagnostic diagnostic) =>
                service.TryApply(ownerGuid, settings, out diagnostic);
            public bool TrySetManualOnlyResolver(Func<int, bool> resolver, out NativeCapabilityDiagnostic diagnostic) =>
                service.TrySetManualOnlyResolver(ownerGuid, resolver, out diagnostic);
        }
    }
}

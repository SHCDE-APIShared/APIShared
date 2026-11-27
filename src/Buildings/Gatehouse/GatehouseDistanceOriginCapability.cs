using BepInEx.Logging;
using System;
using System.Collections.Generic;

namespace APIShared
{
    /// <summary>Selects the native coordinate used as the origin of gatehouse enemy distance checks.</summary>
    public enum GatehouseDistanceOrigin
    {
        /// <summary>Use Vanilla's begin coordinate at one corner of the gatehouse bounds.</summary>
        VanillaBuildingBegin,
        /// <summary>Use the exact center of the complete gatehouse bounding box.</summary>
        BuildingBoundsCenter
    }

    /// <summary>
    /// Applies a validated, transactional gatehouse distance origin.
    /// </summary>
    public interface IGatehouseDistanceOriginCapability
    {
        /// <summary>Attempts to apply and verify the requested process-wide gatehouse distance origin.</summary>
        bool TryApply(GatehouseDistanceOrigin origin, out NativeCapabilityDiagnostic diagnostic);
    }

    internal sealed class GatehouseDistanceOriginTarget
    {
        public GatehouseDistanceOriginTarget(long block, byte[] vanillaBytes)
        {
            if (vanillaBytes == null || vanillaBytes.Length == 0)
                throw new ArgumentException("The Vanilla gatehouse distance block must be non-empty.", nameof(vanillaBytes));

            Intervals = new[] { new NativeInterval(block, checked(block + vanillaBytes.Length)) };
        }

        public IReadOnlyList<NativeInterval> Intervals { get; }
    }

    internal sealed class GatehouseDistanceOriginService
    {
        private readonly string binaryHash;
        private readonly GatehouseDistanceOriginTarget target;
        private readonly GatehousePermanentRuntimeState runtimeState;
        private readonly NativeOwnershipRegistry ownership;
        private readonly object mutationSync;
        private readonly ManualLogSource log;
        private GatehouseDistanceOrigin expectedOrigin = GatehouseDistanceOrigin.VanillaBuildingBegin;

        public GatehouseDistanceOriginService(
            string binaryHash,
            GatehouseDistanceOriginTarget target,
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
            VerifyExpected();
        }

        public IGatehouseDistanceOriginCapability Bind(string ownerGuid) => new OwnerCapability(this, ownerGuid);

        private bool TryApply(
            string ownerGuid,
            GatehouseDistanceOrigin origin,
            out NativeCapabilityDiagnostic diagnostic)
        {
            if (origin != GatehouseDistanceOrigin.VanillaBuildingBegin &&
                origin != GatehouseDistanceOrigin.BuildingBoundsCenter)
            {
                diagnostic = Diagnostic(
                    NativeCapabilityState.ValidationFailed,
                    $"Unknown gatehouse distance origin value: {(int)origin}.");
                return false;
            }

            lock (mutationSync)
            {
                if (!ownership.TryReserve(
                        ownerGuid,
                        NativeCapabilityIds.GatehouseDistanceOrigin,
                        NativeReservationMode.Exclusive,
                        target.Intervals,
                        out string conflictOwner))
                {
                    diagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.GatehouseDistanceOrigin,
                        NativeCapabilityState.Conflict,
                        binaryHash,
                        "The gatehouse distance-origin memory is already reserved by another owner.",
                        conflictOwner);
                    return false;
                }

                try
                {
                    VerifyExpected();
                    if (origin == expectedOrigin)
                    {
                        diagnostic = Diagnostic(
                            NativeCapabilityState.Available,
                            $"The requested gatehouse distance origin {origin} is already active and was verified.");
                        return true;
                    }

                    runtimeState.PublishOrigin(origin);
                    expectedOrigin = origin;
                    VerifyExpected();
                    diagnostic = Diagnostic(
                        NativeCapabilityState.Available,
                        $"Gatehouse distance origin {origin} was applied and verified.");
                    NativeApiLog.Debug(
                        log,
                        $"capability={NativeCapabilityIds.GatehouseDistanceOrigin}, build={binaryHash}, owner={ownerGuid}, origin={origin}, status=applied");
                    return true;
                }
                catch (Exception ex)
                {
                    diagnostic = Diagnostic(NativeCapabilityState.ValidationFailed, ex.Message);
                    NativeApiLog.Error(
                        log,
                        $"capability={NativeCapabilityIds.GatehouseDistanceOrigin}, build={binaryHash}, owner={ownerGuid}, origin={origin}, status=failed, error={ex}");
                    return false;
                }
            }
        }

        private void VerifyExpected()
        {
            if (!runtimeState.IsDistanceInstalled || runtimeState.Origin != expectedOrigin)
                throw new InvalidOperationException(
                    $"Gatehouse distance-origin logical state changed unexpectedly: expected={expectedOrigin}, actual={runtimeState.Origin}.");
        }

        private NativeCapabilityDiagnostic Diagnostic(NativeCapabilityState state, string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.GatehouseDistanceOrigin, state, binaryHash, reason);

        private sealed class OwnerCapability : IGatehouseDistanceOriginCapability
        {
            private readonly GatehouseDistanceOriginService service;
            private readonly string ownerGuid;

            public OwnerCapability(GatehouseDistanceOriginService service, string ownerGuid)
            {
                this.service = service;
                this.ownerGuid = ownerGuid;
            }

            public bool TryApply(GatehouseDistanceOrigin origin, out NativeCapabilityDiagnostic diagnostic) =>
                service.TryApply(ownerGuid, origin, out diagnostic);
        }
    }

}

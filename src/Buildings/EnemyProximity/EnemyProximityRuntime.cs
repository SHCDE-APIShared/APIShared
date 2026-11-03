using System;
using System.Threading;
using BepInEx.Logging;
using R3;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;

namespace APIShared.Buildings
{
    public static partial class EnemyProximityPolicies
    {
        private const string DiagnosticId = NativeCapabilityIds.EnemyUnitProximity;
        private static int initializationAttempted, integrationReady, queryFailureReported;
        private static IDisposable subscription;
        private static EnemyProximityQuery query;
        private static ManualLogSource logger;
        private static NativeCapabilityDiagnostic queryDiagnostic = new NativeCapabilityDiagnostic(
            DiagnosticId, NativeCapabilityState.Pending, "", "Native query preparation has not run.");

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initializationAttempted, 1, 0) != 0) return;
            logger = log;
            try
            {
                // This subscription, registry and private predicate are process-rooted. The SE
                // remains the sole owner of the live function; there is no new native hook.
                subscription = BuildingR3EventHooks.OnBuildingAllowRepairInProximity.Observable.Subscribe(OnQuery);
                Volatile.Write(ref integrationReady, 1);
            }
            catch (Exception error)
            {
                Volatile.Write(ref queryDiagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.Faulted, hash, "Event integration failed: " + error.Message));
                NativeApiLog.Error(log, "Enemy proximity event integration unavailable: " + error);
                return;
            }
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref queryDiagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.UnsupportedBuild, hash, "The enemy-list and unit-record layout requires the audited native build."));
                NativeApiLog.Error(log, "Enemy proximity category predicate unavailable for native build " + hash + "; original radii and independent block decisions remain active.");
                return;
            }
            try
            {
                var prepared = new EnemyProximityQuery(context.Memory, unchecked((ulong)context.ModuleHandle.ToInt64()));
                Volatile.Write(ref query, prepared);
                Volatile.Write(ref queryDiagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.Available, hash, "Complete 502-byte read-only predicate validated and prepared."));
                NativeApiLog.Info(log, "Enemy proximity category predicate prepared: method=reference-rva; rva=0xEE640; complete body and native layout validated; no additional native hook.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref queryDiagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.ValidationFailed, hash, error.Message));
                NativeApiLog.Error(log, "Enemy proximity category predicate unavailable; original radii and independent block decisions remain active: " + error);
            }
        }

        // Internal so integration fixtures execute this actual publisher adapter, including
        // early skips and unavailable native backends, instead of reproducing its decisions.
        internal static void OnQuery(BuildingAllowRepairInProximityEventArgs args)
        {
            if (args.Phase != EventHookPhase.Pre || args.SkipOriginalFunction || !Registry.HasDemand) return;
            EnemyProximityDecision result = Registry.Evaluate(args.PlayerId, args.TileX, args.TileY, args.Proximity, args.Unknown);
            if (result.Block)
            {
                args.ReturnValue = 1;
                args.SkipOriginalFunction = true;
                return;
            }
            if (!result.CivilianRadius.HasValue && !result.MilitaryRadius.HasValue) return;
            EnemyProximityQuery current = Volatile.Read(ref query);
            if (current == null) return;
            try
            {
                // Compute before publishing either event mutation. Failure leaves Vanilla intact.
                long value = current.Invoke(args.PlayerId, args.TileX, args.TileY,
                    result.MilitaryRadius ?? args.Proximity, args.Unknown, result.CivilianRadius ?? args.Proximity);
                args.ReturnValue = value;
                args.SkipOriginalFunction = true;
            }
            catch (Exception error)
            {
                Volatile.Write(ref query, null);
                var previous = QueryDiagnostic;
                Volatile.Write(ref queryDiagnostic, new NativeCapabilityDiagnostic(DiagnosticId,
                    NativeCapabilityState.Faulted, previous.BinaryHash, error.Message));
                if (Interlocked.Exchange(ref queryFailureReported, 1) == 0)
                    NativeApiLog.Error(logger, "Enemy proximity query failed; category overrides disabled, independent vetoes retained: " + error);
            }
        }
    }
}

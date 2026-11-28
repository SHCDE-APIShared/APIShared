using System;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;

namespace APIShared.Buildings
{
    internal static partial class HumanConstructionRuntime
    {
        private static IDisposable placementSubscription;
        private static void InitializePlacementPreview()
        {
            if (placementSubscription != null) return;
            // The process-held publisher roots this handler after startup cleanup. It is read-only:
            // simulation payment remains enforced at the final Original construction boundary.
            placementSubscription = BuildingR3EventHooks.OnPlacementValidation.Observable.Subscribe(ValidatePlacement);
        }
        private static void ValidatePlacement(BuildingPlacementValidationEventArgs args)
        {
            if (!ready || args.Phase != EventHookPhase.Pre || !HumanConstructionCosts.HasDemand) return;
            try
            {
                var target = ConstructionCostTarget.FromMapper(args.Mappers);
                if (!target.SupportsHumanExtraCosts) return;
                // Quote visibility is not construction eligibility: canceled owner sums and
                // unreadable payment archives must still use the execution affordability check.
                // Begin is read-only; Reserve remains exclusive to the simulation command.
                var frame = building;
                // The native build calls this validator with mode 0 after reserving extras.
                // Standalone probes also use mode 0, so match the active build's arguments.
                var reservation = args.Unknown2 == 0 && frame != null && frame.Player == args.PlayerId &&
                    frame.Mapper == args.Mappers && frame.TileX == args.TileX && frame.TileY == args.TileY &&
                    frame.Scale == args.Unknown1 ? frame.Transaction : null;
                if (!Begin(target, args.PlayerId, false, out _, reservation))
                {
                    args.CustomValidationRules = true;
                    args.ForceBlockPlacementState = true;
                }
            }
            catch(Exception error) { ReportFailure("placement preview",error); }
        }
    }
}

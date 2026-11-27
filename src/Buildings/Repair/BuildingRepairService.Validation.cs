using SHCDESE.API;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared
{
    internal sealed unsafe partial class BuildingRepairService
    {
        // Preserve the original short-circuit order, including which native fields are safe to read.
        internal static RepairRejectionReason ValidateRepair(GameBuildingManagerAPI buildings,
            GamePlayerManagerAPI players, BuildingRepairEventArgs args, out GameBuilding* building,
            out RepairRejectionSnapshot snapshot)
        {
            building = null;
            snapshot = new RepairRejectionSnapshot { PlayerId = args.PlayerId, BuildingId = args.BuildingId,
                ExpectedGlobalId = args.BuildingGlobalId };
            if (buildings == null) return RepairRejectionReason.BuildingManagerUnavailable;
            if (players == null) return RepairRejectionReason.PlayerManagerUnavailable;
            if (args.BuildingId <= 0) return RepairRejectionReason.InvalidBuildingId;
            if (!players.TryGetPlayerResourcesById(args.PlayerId, out GamePlayerResources* resources))
                return RepairRejectionReason.PlayerResourcesUnavailable;
            bool found = BuildingAccess.TryGetById(buildings, args.BuildingId, out building, out var lookup);
            snapshot.LookupFailure = lookup;
            if (!found || building == null) return RepairRejectionReason.BuildingUnavailable;
            snapshot.Owner = building->r_PlayerIdOwner;
            if (snapshot.Owner != args.PlayerId) return RepairRejectionReason.OwnerMismatch;
            snapshot.GlobalId = unchecked((int)building->r_GlobalId);
            if (snapshot.GlobalId != args.BuildingGlobalId) return RepairRejectionReason.GlobalIdMismatch;
            snapshot.Alive = (int)building->r_AliveState;
            if (building->r_AliveState != AliveState.IsAlive) return RepairRejectionReason.NotAlive;
            snapshot.MaximumHealth = building->r_MaxHealth;
            if (snapshot.MaximumHealth == 0) return RepairRejectionReason.ZeroMaximumHealth;
            if (snapshot.MaximumHealth > short.MaxValue) return RepairRejectionReason.ExcessiveMaximumHealth;
            snapshot.Health = building->r_CurrentHealth;
            if (snapshot.Health < 0) return RepairRejectionReason.NegativeHealth;
            snapshot.Type = (int)building->r_BuildingType;
            if (building->r_BuildingType <= eStructs.STRUCT_NULL ||
                building->r_BuildingType > eStructs.STRUCT_GARDEN_LARGE)
                return RepairRejectionReason.InvalidBuildingType;
            return RepairRejectionReason.None;
        }

        private void RecordRepairRejection(RepairRejectionReason reason, RepairRejectionSnapshot snapshot)
        {
            // Rejection is already final. Support diagnostics must never change gameplay on failure.
            try
            {
                snapshot.Session = MissionLifecycleService.ActiveContext?.SessionId;
                repairRejections.Record(reason, snapshot);
            }
            catch { }
        }
    }
}

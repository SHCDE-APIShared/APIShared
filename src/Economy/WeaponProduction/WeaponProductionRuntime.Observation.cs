using System.Threading;
using APIShared.GameModes;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // A receipt for the existing batch owner's world, not a second batch registry. Once bound,
    // the same owner cannot bless retained rows after a different native load in that mission.
    // The coordinator serializes Begin/End/Bind and readers with the actual engine monitor.
    internal sealed class WeaponProductionObservationWorld
    {
        private long session;
        private MissionLifecycleState.NativeWorldState world;
        internal void Begin(long ownerSession) { session = ownerSession; world = null; }
        internal void End() { session = 0; world = null; }
        internal void Bind(long ownerSession, MissionLifecycleState.NativeWorldState loaded)
        {
            if (world == null && session != 0 && session == ownerSession && loaded != null && loaded.Mission == session)
                world = loaded;
        }
        internal bool IsCurrent(MissionLifecycleState.NativeClockState clock, bool admitted, bool faulted) =>
            world != null && clock != null && ReferenceEquals(world, clock.World) &&
            clock.Context.SessionId == session && clock.Context.Mode.Kind != GameModeKind.Unknown && admitted && !faulted;
    }

    internal static unsafe partial class WeaponProductionRuntime
    {
        private static readonly WeaponProductionObservationWorld workerObservationWorld = new WeaponProductionObservationWorld();

        // Called synchronously with the exact just-published world, before public NativeLoaded
        // notifications can nest another load. Pre-native Activate/queued notifications never bind it.
        // Failure affects only the optional read; it must not veto or initialize native production.
        internal static void BindWorkerObservationWorld(long session, MissionLifecycleState.NativeWorldState world)
        {
            if (!frozen.HasOptions || engine == null || world == null) return;
            try
            {
                lock (engine)
                    // Keep the first matching completed token even if it has already retired.
                    // Skipping it could let a later load bless this owner's retained batch rows.
                    if (frozen.HasOptions)
                        workerObservationWorld.Bind(session, world);
            }
            catch { /* Observation is optional; existing production admission remains independent. */ }
        }

        internal static bool TryGetWorkerBatch(int unitId, uint globalId, out WeaponProductionWorkerBatch snapshot)
        {
            snapshot = default;
            if (!HasHudDemand || engine == null || !Monitor.IsEntered(engine) ||
                unitId < 1 || unitId > MaxUnitId || globalId == 0) return false;
            var clock = MissionLifecycleService.NativeClock;
            if (!workerObservationWorld.IsCurrent(clock, WorkerProductionWorldSafety.IsAdmitted, WorkerProductionWorldSafety.IsFaulted) ||
                !workers.TryGetValue(unitId, out var batch) || batch.UnitId != unitId || batch.UnitGlobalId != globalId ||
                !UnitAccess.TryGetById(unitId, out GameUnit* worker, out _) ||
                !TryBuilding(batch.BuildingId, batch.BuildingGlobalId, out GameBuilding* home)) return false;
            if (!TryObserveWorkerBatch(active, unitId, globalId, batch.BuildingId, batch.BuildingGlobalId,
                batch.Option, (int)batch.Phase, worker, home, out var observed) ||
                !ReferenceEquals(clock.World, MissionLifecycleService.NativeWorld)) return false;
            snapshot = observed; return true;
        }

        // Typed leaf shared by the public read and packed-actor fixtures. Inputs are the existing
        // Batch fields; no selection fallback, cargo read, callback, row repair or native pointer escapes.
        internal static bool TryObserveWorkerBatch(WeaponProductionCatalog catalog, int unitId, uint unitGlobalId,
            int homeId, uint homeGlobalId, WeaponProductionOption option, int phase, GameUnit* worker, GameBuilding* home,
            out WeaponProductionWorkerBatch snapshot)
        {
            snapshot = default;
            if (catalog == null || option == null || option.IsVanilla ||
                !ReferenceEquals(catalog.At(catalog.IndexOf(option.Id)), option) ||
                unitId < 1 || unitId > MaxUnitId || unitGlobalId == 0 || homeId < 1 || homeId > MaxBuildingId || homeGlobalId == 0 ||
                phase < (int)BatchPhase.Manufacturing || phase > (int)BatchPhase.Cargo || worker == null || home == null ||
                worker->r_GlobalId != unitGlobalId || home->r_GlobalId != homeGlobalId ||
                (!UnitAccess.IsReallyAlive(worker) && !(worker->r_AliveState == AliveState.Paused && worker->r_IsKilledByProjectile == 0)) ||
                home->r_AliveState != AliveState.IsAlive || home->r_BuildingType != option.BuildingType ||
                worker->r_UnitChimp == eChimps.CHIMP_TYPE_NULL || !MatchesWorkerRole(option.BuildingType, worker->r_UnitChimp) ||
                worker->r_LinkedProductionBuildingId != homeId || worker->r_LinkedProductionBuildingGlobalId != homeGlobalId ||
                home->r_PlayerIdOwner < 1 || home->r_PlayerIdOwner > 8 || worker->r_ControllableForPlayerId != home->r_PlayerIdOwner)
                return false;
            snapshot = new WeaponProductionWorkerBatch(unitId, unitGlobalId, homeId, homeGlobalId, option,
                (WeaponProductionBatchPhase)phase);
            return true;
        }
    }
}

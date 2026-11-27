using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Savegames;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Closed runtime source; no public producer callback participates in the commit.
    // Actor-buffer fixtures substitute only this native-read/capability boundary while
    // exercising the same owner maps, wire codec and storage publication code.
    internal unsafe interface IWeaponManufacturingAdoptionSource
    {
        bool IsRecipeReady(WeaponProductionOption option);
        bool TryRead(WeaponProductionPaidManufacturing declaration, out GameUnit* unit, out GameBuilding* building);
    }

    internal static unsafe partial class WeaponProductionRuntime
    {
        private sealed class ManufacturingAdoptionSource : IWeaponManufacturingAdoptionSource
        {
            public bool IsRecipeReady(WeaponProductionOption option) => backendReady && cleanupReady &&
                WorkerProductionWorldSafety.IsAvailable && WorkerProductionWorldSafety.IsAdmitted &&
                ReferenceEquals(engine, WorkerProductionWorldSafety.EngineGate) &&
                WeaponProductionNativeRuntime.CanAdoptPaidManufacturing(option) &&
                NativeStorageRuntime.IsRegistrationReady(option.StoredOutput);
            public bool TryRead(WeaponProductionPaidManufacturing declaration, out GameUnit* unit, out GameBuilding* building)
            {
                unit = null; building = null;
                return TryBuilding(declaration.BuildingId, declaration.BuildingGlobalId, out building) &&
                    UnitAccess.TryGetById(declaration.UnitId, out unit, out _);
            }
        }
        internal static readonly IWeaponManufacturingAdoptionSource ManufacturingAdoptionNativeSource = new ManufacturingAdoptionSource();

        internal static bool TryCopyManufacturingDeclarations(string owner,
            IReadOnlyList<WeaponProductionPaidManufacturing> source,
            out WeaponProductionPaidManufacturing[] result, out string reason)
        {
            result = null;
            WeaponProductionRegistration[] known;
            lock (RegistrationGate) known = registrations;
            try
            {
                if (source == null)
                { reason = "A bounded nonempty paid-manufacturing list is required."; return false; }
                int count = source.Count;
                if (count < 1 || count > MaxBuildingId)
                { reason = "A bounded nonempty paid-manufacturing list is required."; return false; }
                var copied = new WeaponProductionPaidManufacturing[count];
                for (int i = 0; i < count; i++)
                {
                    var value = source[i];
                    if (!ValidateManufacturingDeclaration(owner, value, out reason)) return false;
                    bool registered = false;
                    foreach (var entry in known) if (ReferenceEquals(entry, value.Recipe)) { registered = true; break; }
                    if (!registered) { reason = "The exact process recipe registration is required."; return false; }
                    copied[i] = value;
                }
                Array.Sort(copied, (left, right) => left.BuildingId.CompareTo(right.BuildingId));
                var workerIds = new HashSet<int>();
                for (int i = 0; i < copied.Length; i++)
                    if (!workerIds.Add(copied[i].UnitId) || i > 0 && copied[i - 1].BuildingId == copied[i].BuildingId)
                    { reason = "Each workshop and worker may adopt only one paid charge."; return false; }
                result = copied; reason = string.Empty; return true;
            }
            catch (Exception error) { reason = "Manufacturing declarations could not be copied: " + error.Message; return false; }
        }
        internal static bool ValidateManufacturingDeclaration(string owner, WeaponProductionPaidManufacturing value, out string reason)
        {
            var option = value.Recipe?.Option;
            if (option == null || option.IsVanilla || !StringComparer.Ordinal.Equals(owner, option.Id.OwnerGuid) ||
                option.BuildingType != eStructs.STRUCT_ARMOURERS_WORKSHOP || option.TemplateGood != eGoods.STORED_METAL_ARMOUR ||
                option.StoredOutput == null || !StringComparer.Ordinal.Equals(owner, option.StoredOutput.OwnerGuid) ||
                option.StoredOutput.Pool != NativeStoragePool.Armory || !option.BaseAmount.HasValue || option.BaseAmount != value.BaseAmount ||
                option.WorkTicks.HasValue || (option.IngredientAmount ?? 1) != value.PaidIngredientAmount ||
                value.BaseAmount < 0 || value.BaseAmount > 100 || value.PaidIngredientAmount < 1 || value.PaidIngredientAmount > 48 ||
                value.BuildingId < 1 || value.BuildingId > MaxBuildingId || value.BuildingGlobalId == 0 ||
                value.UnitId < 1 || value.UnitId > MaxUnitId || value.UnitGlobalId == 0 || value.PlayerId < 1 || value.PlayerId > 8)
            { reason = "Adoption requires an exact owned stored-output Armourer recipe, matching base/paid cost, native duration and valid saved identities."; return false; }
            reason = string.Empty; return true;
        }
        internal static bool SameManufacturingDeclarations(WeaponProductionPaidManufacturing[] left, WeaponProductionPaidManufacturing[] right)
        {
            if (left.Length != right.Length) return false;
            for (int i = 0; i < left.Length; i++)
            {
                var a = left[i]; var b = right[i];
                if (!ReferenceEquals(a.Recipe, b.Recipe) || a.BuildingId != b.BuildingId || a.BuildingGlobalId != b.BuildingGlobalId ||
                    a.UnitId != b.UnitId || a.UnitGlobalId != b.UnitGlobalId || a.PlayerId != b.PlayerId ||
                    a.BaseAmount != b.BaseAmount || a.PaidIngredientAmount != b.PaidIngredientAmount) return false;
            }
            return true;
        }

        // This validates a present manufacturing phase, NOT its historical payment. The
        // latter remains the importing record owner's declaration, bound to exact origin.
        // Native progress/iron/product/carry bytes are read only and remain unchanged.
        internal static bool ValidateManufacturingActor(WeaponProductionPaidManufacturing value,
            GameUnit* unit, GameBuilding* building, out string reason)
        {
            if (unit == null || building == null || unit->r_GlobalId != value.UnitGlobalId || building->r_GlobalId != value.BuildingGlobalId ||
                !(UnitAccess.IsReallyAlive(unit) || unit->r_AliveState == AliveState.Paused && unit->r_IsKilledByProjectile == 0) ||
                building->r_AliveState != AliveState.IsAlive ||
                unit->r_UnitChimp != eChimps.CHIMP_TYPE_ARMOURER || building->r_BuildingType != eStructs.STRUCT_ARMOURERS_WORKSHOP ||
                unit->r_ControllableForPlayerId != value.PlayerId || building->r_PlayerIdOwner != value.PlayerId ||
                unit->r_LinkedProductionBuildingId != value.BuildingId || unit->r_LinkedProductionBuildingGlobalId != value.BuildingGlobalId ||
                unit->r_AIState != 5 || *(short*)((byte*)unit + NativeStorageDeliveryEngine.CargoOffset) != 0 ||
                building->r_ArmouryMetalArmorAmount != 0)
            { reason = "The live paid-manufacturing witness does not match its exact Armourer/workshop/owner/state or has native product/cargo."; return false; }
            reason = string.Empty; return true;
        }

        internal static bool TryPrepareManufacturingPublication(WeaponProductionManufacturingAdoption adoption, object world,
            out PreparedManufacturingPublication publication, out string reason) =>
            PreparedManufacturingPublication.TryPrepare(adoption, world, out publication, out reason);

        private sealed class ManufacturingWireRow
        {
            internal readonly WeaponProductionPaidManufacturing Actor;
            internal readonly byte[] Building = new byte[BuildingWireBytes], Worker = new byte[WorkerWireBytes];
            internal ManufacturingWireRow(WeaponProductionPaidManufacturing actor, int option)
            {
                Actor = actor;
                BinaryPrimitives.WriteUInt16LittleEndian(Building, (ushort)actor.BuildingId);
                BinaryPrimitives.WriteUInt32LittleEndian(Building.AsSpan(2), actor.BuildingGlobalId);
                BinaryPrimitives.WriteUInt16LittleEndian(Building.AsSpan(6), (ushort)eStructs.STRUCT_ARMOURERS_WORKSHOP);
                Building[9] = (byte)option;
                BinaryPrimitives.WriteUInt16LittleEndian(Building.AsSpan(10), (ushort)actor.UnitId);
                BinaryPrimitives.WriteUInt32LittleEndian(Building.AsSpan(12), actor.UnitGlobalId);
                BinaryPrimitives.WriteUInt16LittleEndian(Worker, (ushort)actor.UnitId);
                BinaryPrimitives.WriteUInt32LittleEndian(Worker.AsSpan(2), actor.UnitGlobalId);
                BinaryPrimitives.WriteUInt16LittleEndian(Worker.AsSpan(6), (ushort)actor.BuildingId);
                BinaryPrimitives.WriteUInt32LittleEndian(Worker.AsSpan(8), actor.BuildingGlobalId);
                Worker[12] = (byte)option;
                // Managed override fields remain zero because WorkTicks is required null.
                // Native animation/manufacturing progress is never initialized or rewritten.
            }
        }

        internal sealed class PreparedManufacturingPublication
        {
            internal static bool TryPrepare(WeaponProductionManufacturingAdoption adoption, object world,
                out PreparedManufacturingPublication publication, out string reason)
            {
                publication = null;
                if (adoption == null || engine == null || !Monitor.IsEntered(engine) || world == null ||
                    !ReferenceEquals(world, MissionLifecycleService.NativeWorld) || !ReferenceEquals(active, frozen) ||
                    !active.HasStoredOutputs || WorkerProductionWorldSafety.IsFaulted)
                { reason = "Paid manufacturing requires the admitted exact producer/native world under its engine monitor."; return false; }
                var source = adoption.Source;
                var catalog = frozen;
                var oldBuildings = buildings; var oldWorkers = workers;
                var nextBuildings = new Dictionary<int, BuildingState>(oldBuildings);
                var nextWorkers = new Dictionary<int, Batch>(oldWorkers);
                var rows = new ManufacturingWireRow[adoption.Values.Length];
                for (int i = 0; i < rows.Length; i++)
                {
                    var value = adoption.Values[i]; var option = value.Recipe.Option;
                    if (!ValidateManufacturingDeclaration(adoption.OwnerGuid, value, out reason)) return false;
                    int index = catalog.IndexOf(option.Id);
                    if (index < 1 || index > OptionMask || !ReferenceEquals(catalog.At(index), option) || !source.IsRecipeReady(option) ||
                        !source.TryRead(value, out var unit, out var building) || !ValidateManufacturingActor(value, unit, building, out reason))
                    { reason = "The adopted recipe or native paid-manufacturing actor is not admitted/current."; return false; }
                    if (oldBuildings.ContainsKey(value.BuildingId) || oldWorkers.ContainsKey(value.UnitId))
                    { reason = "An existing shared workshop selection/batch already owns this actor."; return false; }
                    var batch = new Batch { BuildingId = value.BuildingId, BuildingGlobalId = value.BuildingGlobalId,
                        UnitId = value.UnitId, UnitGlobalId = value.UnitGlobalId, Option = option, Phase = BatchPhase.Manufacturing };
                    nextWorkers.Add(value.UnitId, batch);
                    // Current latch only. Future recipe choice remains an explicit consumer policy.
                    nextBuildings.Add(value.BuildingId, new BuildingState { GlobalId = value.BuildingGlobalId,
                        Type = option.BuildingType, Current = batch });
                    rows[i] = new ManufacturingWireRow(value, index);
                }
                if (!WorkerProductionWorldSafety.TryPrepareAdoptionAdmission(adoption.StorageImport.Participant.Registry,
                    adoption.StorageImport.Origin.Generation, out var admission))
                { reason = "This protected native epoch cannot adopt a replacement metadata generation."; return false; }
                publication = new PreparedManufacturingPublication(adoption, world, catalog, oldBuildings, oldWorkers,
                    nextBuildings, nextWorkers, rows, admission);
                if (!publication.IsCurrent(out reason)) { publication = null; return false; }
                reason = string.Empty; return true;
            }

            private readonly WeaponProductionManufacturingAdoption adoption;
            private readonly object world;
            private readonly WeaponProductionCatalog catalog;
            private readonly Dictionary<int, BuildingState> beforeBuildings, afterBuildings;
            private readonly Dictionary<int, Batch> beforeWorkers, afterWorkers;
            private readonly KeyValuePair<int, BuildingState>[] buildingSnapshot;
            private readonly KeyValuePair<int, Batch>[] workerSnapshot;
            private readonly ManufacturingWireRow[] rows;
            private readonly WorkerProductionAdoptionStamp admission;
            private PreparedManufacturingPublication(WeaponProductionManufacturingAdoption adoption, object world,
                WeaponProductionCatalog catalog, Dictionary<int, BuildingState> beforeBuildings, Dictionary<int, Batch> beforeWorkers,
                Dictionary<int, BuildingState> afterBuildings, Dictionary<int, Batch> afterWorkers, ManufacturingWireRow[] rows, WorkerProductionAdoptionStamp admission)
            {
                this.adoption = adoption; this.world = world; this.catalog = catalog;
                this.beforeBuildings = beforeBuildings; this.beforeWorkers = beforeWorkers;
                this.afterBuildings = afterBuildings; this.afterWorkers = afterWorkers; this.rows = rows; this.admission = admission;
                buildingSnapshot = beforeBuildings.ToArray(); workerSnapshot = beforeWorkers.ToArray();
            }
            internal bool IsCurrent(out string reason)
            {
                try { return IsCurrentCore(out reason); }
                catch (Exception error)
                { reason = "Manufacturing actor revalidation failed: " + error.Message; return false; }
            }
            private bool IsCurrentCore(out string reason)
            {
                if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld) || !ReferenceEquals(catalog, active) ||
                    !ReferenceEquals(catalog, frozen) || !ReferenceEquals(buildings, beforeBuildings) || !ReferenceEquals(workers, beforeWorkers) ||
                    beforeBuildings.Count != buildingSnapshot.Length || beforeWorkers.Count != workerSnapshot.Length || WorkerProductionWorldSafety.IsFaulted ||
                    !WorkerProductionWorldSafety.CanPublishAdoptionAdmission(admission))
                { reason = "The producer world/catalogue/actor publication changed before adoption."; return false; }
                foreach (var pair in buildingSnapshot)
                    if (!beforeBuildings.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                    { reason = "The producer building map changed before adoption."; return false; }
                foreach (var pair in workerSnapshot)
                    if (!beforeWorkers.TryGetValue(pair.Key, out var value) || !ReferenceEquals(value, pair.Value))
                    { reason = "The producer worker map changed before adoption."; return false; }
                foreach (var row in rows)
                    if (!adoption.Source.IsRecipeReady(row.Actor.Recipe.Option) || !adoption.Source.TryRead(row.Actor, out var unit, out var building) ||
                        !ValidateManufacturingActor(row.Actor, unit, building, out reason))
                    { reason = "The producer actor changed before adoption."; return false; }
                if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld))
                { reason = "The native world changed during final actor validation."; return false; }
                reason = string.Empty; return true;
            }
            // Called only under engine -> importSync -> registry import lease. Assignment-only:
            // no user callback, native actor store, allocation, dictionary mutation or validation.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void PublishManaged()
            { buildings = afterBuildings; workers = afterWorkers; adoption.PublishedWorld = world; WorkerProductionWorldSafety.PublishAdoptionAdmission(admission); }

            // Trusted process-owned wire/demand only, after releasing the registry lease and
            // before releasing the engine/import monitors. Never writes native actor/resource cells.
            [MethodImpl(MethodImplOptions.NoInlining)]
            internal void PublishWire()
            {
                foreach (var row in rows)
                {
                    byte* target = (byte*)BuildingWireAddress + row.Actor.BuildingId * BuildingWireBytes;
                    for (int i = 0; i < BuildingWireBytes; i++) target[i] = row.Building[i];
                    target = (byte*)WorkerWireAddress + row.Actor.UnitId * WorkerWireBytes;
                    for (int i = 0; i < WorkerWireBytes; i++) target[i] = row.Worker[i];
                    ((ushort*)WorkerWireKindsAddress)[row.Actor.UnitId] = (ushort)eChimps.CHIMP_TYPE_ARMOURER;
                    WeaponProductionNativeRuntime.SetBuildingDemand(row.Actor.BuildingId, true);
                }
            }
        }
    }
}

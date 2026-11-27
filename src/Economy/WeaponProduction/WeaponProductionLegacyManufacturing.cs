using System;
using System.Collections.Generic;
using APIShared.Savegames;

namespace APIShared.Economy
{
    /// <summary>A consumer-declared, already-paid native Armourer charge from its original legacy record.</summary>
    /// <remarks>This is not proof of payment. The consumer must decode its complete original record,
    /// distinguish unfinished work from local product/final cargo, and supply the original base and
    /// paid ingredient amount. The service checks current actor identity/phase but cannot infer a
    /// historical iron debit from a native state number. No ingredient or native progress is written.</remarks>
    public readonly struct WeaponProductionPaidManufacturing
    {
        /// <summary>Describes one paid charge; construction performs no registration or native access.</summary>
        /// <param name="recipe">The exact process registration, owned by the importing participant.</param>
        /// <param name="buildingId">One-based saved Armourer slot.</param>
        /// <param name="buildingGlobalId">Unsigned saved workshop instance identity.</param>
        /// <param name="unitId">One-based saved Armourer worker slot.</param>
        /// <param name="unitGlobalId">Unsigned saved worker instance identity.</param>
        /// <param name="playerId">Saved producing owner, 1-8.</param>
        /// <param name="baseAmount">Original whole base input to native productivity, not final cargo.</param>
        /// <param name="paidIngredientAmount">Original already-debited native ingredient amount.</param>
        public WeaponProductionPaidManufacturing(WeaponProductionRegistration recipe, int buildingId,
            uint buildingGlobalId, int unitId, uint unitGlobalId, int playerId, int baseAmount, int paidIngredientAmount)
        {
            Recipe = recipe; BuildingId = buildingId; BuildingGlobalId = buildingGlobalId;
            UnitId = unitId; UnitGlobalId = unitGlobalId; PlayerId = playerId;
            BaseAmount = baseAmount; PaidIngredientAmount = paidIngredientAmount;
        }
        /// <summary>Exact process registration, not a reconstructed option with equal key values.</summary>
        public WeaponProductionRegistration Recipe { get; }
        /// <summary>Saved one-based workshop slot.</summary>
        public int BuildingId { get; }
        /// <summary>Saved nonzero unsigned workshop instance identity.</summary>
        public uint BuildingGlobalId { get; }
        /// <summary>Saved one-based worker slot.</summary>
        public int UnitId { get; }
        /// <summary>Saved nonzero unsigned worker instance identity.</summary>
        public uint UnitGlobalId { get; }
        /// <summary>Saved producing player.</summary>
        public int PlayerId { get; }
        /// <summary>Original base yield, which must equal this admitted recipe's explicit base.</summary>
        public int BaseAmount { get; }
        /// <summary>Declared historical input, which must equal the recipe's ingredient cost.</summary>
        public int PaidIngredientAmount { get; }
    }

    /// <summary>Immutable cold attachment joining paid manufacturing to one exact storage import.</summary>
    /// <remarks>This token publishes no actor, selection, cargo or stock. Commit through the existing
    /// NativeStorageClient.TryCommitImport with StorageImport; it atomically publishes both owners.
    /// Do not clear the consumer's actor map during preparation. Preallocate its retirement map and
    /// publish only nonthrowing reference assignments after successful commit under the same real
    /// engine monitor. Storage remains unavailable while any other legacy imports are pending.
    /// Native producer custody is the existing signed native carry; never create a second external
    /// cargo lot for this charge. This subset adopts Manufacturing only, not Ready or Carrying.</remarks>
    public sealed class WeaponProductionManufacturingAdoption
    {
        internal readonly WeaponProductionPaidManufacturing[] Values;
        internal readonly IWeaponManufacturingAdoptionSource Source;
        internal object PublishedWorld;
        internal WeaponProductionManufacturingAdoption(NativeStorageImportPlan storage, WeaponProductionPaidManufacturing[] values)
            : this(storage, values, WeaponProductionRuntime.ManufacturingAdoptionNativeSource) { }
        // Only the closed runtime constructor above is reached from public registration.
        // Actor-buffer fixtures replace native reads, never the publication implementation.
        internal WeaponProductionManufacturingAdoption(NativeStorageImportPlan storage,
            WeaponProductionPaidManufacturing[] values, IWeaponManufacturingAdoptionSource source)
        { StorageImport = storage; Values = values; Actors = Array.AsReadOnly(values); Source = source; }
        /// <summary>Existing exact storage import which must commit this attachment.</summary>
        public NativeStorageImportPlan StorageImport { get; }
        /// <summary>Importing participant owner.</summary>
        public string OwnerGuid => StorageImport.OwnerGuid;
        /// <summary>Exact original archive's causal mission association; not native mutation authority.</summary>
        public long MissionSessionId => StorageImport.MissionSessionId;
        /// <summary>Owned immutable copied actor declarations.</summary>
        public IReadOnlyList<WeaponProductionPaidManufacturing> Actors { get; }
    }

    public static partial class WeaponProduction
    {
        /// <summary>Attaches bounded already-paid Armourer manufacturing to a staged legacy storage import.</summary>
        /// <remarks>Call synchronously inside the participant's exact Prepared callback; no engine
        /// access or live-state clearing occurs. The participant, original load receiver, storage plan
        /// and recipe registrations must share an owner. The original must be readable, associated,
        /// unprotected legacy data. The recipe must have a registered custom Armory output, matching
        /// explicit base/ingredient count, and null WorkTicks so native progress is retained. Duplicate
        /// workers/workshops and conflicting retries refuse. Each row must represent Manufacturing;
        /// Ready base yield and final Carrying have different custody and are unsupported here.
        /// Successful staging is process memory only. Commit through StorageImport after the full
        /// matching load in an engine-protected known offline/editor world. A matching live Armourer
        /// in state5, linked exact workshop/owner, zero carry/local product and an admitted recipe are
        /// revalidated before the joint publication. Native state is only corroboration, not payment
        /// proof. Existing recipes/imports and their wire/save versions are unchanged unless opted in.
        /// Successful adoption opts this native epoch into direct registry-generation admission:
        /// metadata load/unload revocation stops new managed engine Original entries until actual
        /// native Admit/Retire, without waiting for observers. An already-admitted run may finish.
        /// The stamp cannot be overwritten by a later metadata import in the same native epoch.
        /// No-adoption worlds keep their existing admission decisions and perform no registry query.
        /// No public callback, new native hook, input debit, progress reset or cargo issuance is added.</remarks>
        public static bool TryPrepareManufacturingAdoption(string ownerGuid, CriticalSaveParticipant participant,
            VersionedSaveLoad load, NativeStorageImportPlan storageImport,
            IReadOnlyList<WeaponProductionPaidManufacturing> actors,
            out WeaponProductionManufacturingAdoption adoption, out string reason) =>
            NativeStorageRuntime.Instance.TryPrepareManufacturingAdoption(ownerGuid, participant, load,
                storageImport, actors, out adoption, out reason);
    }
}

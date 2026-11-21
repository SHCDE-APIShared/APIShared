using System;
using System.Collections.Generic;
using APIShared.Goods;
using APIShared.Savegames;

namespace APIShared.Economy
{
    /// <summary>One positive legacy physical allocation, before binding to the newly loaded world.
    /// Slot, unsigned GlobalId, owner and registered pool must all match at commit. This describes
    /// existing stock; it neither observes an old native world nor grants mutation authority.</summary>
    public readonly struct NativeStorageImportAllocation
    {
        /// <summary>Describes existing legacy stock without changing it. Validation and copying
        /// occur in TryPrepareImport; the new world's native identity/capacity is checked at commit.</summary>
        public NativeStorageImportAllocation(NativeStoredGood good, int buildingId, uint buildingGlobalId, int playerId, int quantity)
        { Good = good; BuildingId = buildingId; BuildingGlobalId = buildingGlobalId; PlayerId = playerId; Quantity = quantity; }
        /// <summary>This client's exact registered output handle; another owner's or a reconstructed handle refuses.</summary>
        public NativeStoredGood Good { get; }
        /// <summary>Native building slot, strictly between zero and the storage building-slot bound.</summary>
        public int BuildingId { get; }
        /// <summary>Nonzero unsigned GlobalId of that exact saved building instance, not a slot-derived ID.</summary>
        public uint BuildingGlobalId { get; }
        /// <summary>Owning one-based player, 1 through 8; must match the destination's current owner.</summary>
        public int PlayerId { get; }
        /// <summary>Positive already-existing physical quantity; not a price, production yield or signed exchange delta.</summary>
        public int Quantity { get; }
    }

    /// <summary>Final undelivered quantity with an owner-local positive creation sequence. This is
    /// not market/recruitment stock and does not require a live source workshop. Do not use base
    /// yield or work-in-progress as final quantity. Retain actor/phase associations in the owner's
    /// enrolled record and allocate creation sequences in its existing cargo stream.</summary>
    public readonly struct NativeStorageImportCargo
    {
        /// <summary>Describes final legacy undelivered quantity without creating a lot. Issued
        /// and remaining quantities begin equal; previous deliveries are already physical stock.</summary>
        public NativeStorageImportCargo(NativeStoredGood good, long creationSequence, int playerId, int quantity)
        { Good = good; CreationSequence = creationSequence; PlayerId = playerId; Quantity = quantity; }
        /// <summary>This client's exact registered output handle; its pool is retained by the lot.</summary>
        public NativeStoredGood Good { get; }
        /// <summary>Positive owner-local cargo stream identity. It must be newer than the current
        /// cargo high-watermark and unique across this owner's imported goods.</summary>
        public long CreationSequence { get; }
        /// <summary>Producing one-based player, 1 through 8; not the current local view player.</summary>
        public int PlayerId { get; }
        /// <summary>Positive observed final quantity still undelivered; actor work/base yield is not interchangeable with it.</summary>
        public int Quantity { get; }
    }

    /// <summary>Owned cold import input for one exact successful Prepared delivery. No native
    /// stock or cargo quantity is published by preparation. Successful preparation publishes a
    /// conservative pending-import barrier: abandoning it keeps storage reads, mutation, capacity
    /// admission and protected saves passive until replacement load/unload. Failed preparation
    /// leaves that metadata unchanged. It cannot be used in another archive window even with the same mission ID.
    /// Cargo creation sequences are known before commit so replacement actor state can be fully
    /// allocated first. A plan is not a live cargo/storage observation or permission to retire stock.</summary>
    public sealed class NativeStorageImportPlan
    {
        internal readonly CriticalSaveParticipant Participant;
        internal readonly VersionedSavePreparedOrigin Origin;
        internal readonly NativeStorageState.Row[] Rows;
        internal readonly NativeStorageState.CargoLot[] Lots;
        internal readonly byte[] Intent;
        internal NativeStorageImportPlan(CriticalSaveParticipant participant, VersionedSavePreparedOrigin origin, long session,
            NativeStorageState.Row[] rows, NativeStorageState.CargoLot[] lots, byte[] intent,
            NativeStorageImportCargo[] cargo)
        {
            Participant = participant; Origin = origin; MissionSessionId = session;
            Rows = rows; Lots = lots; Intent = intent; Cargo = Array.AsReadOnly(cargo);
        }
        /// <summary>Exact owner of the enrolled legacy actor record and imported goods.</summary>
        public string OwnerGuid => Participant.OwnerGuid;
        /// <summary>Exact existing archive identifier of the participant whose original authorizes this plan.</summary>
        public string SaveDataIdentifier => Participant.SaveDataIdentifier;
        /// <summary>Positive process-local causal mission attempt captured at preparation, not a unique archive ID or native authority.</summary>
        public long MissionSessionId { get; }
        /// <summary>Owned immutable final-lot descriptions available for actor-state preallocation.
        /// Creation sequences remain unusable through live cargo operations until commit succeeds.</summary>
        public IReadOnlyList<NativeStorageImportCargo> Cargo { get; }
    }

    public sealed partial class NativeStorageClient
    {
        /// <summary>Maximum physical rows in one import; also bounded by remaining world allocation space.</summary>
        public static int MaximumImportAllocations => NativeStorageState.MaximumRows;
        /// <summary>Maximum final cargo lots in one import; existing active lots also consume the bounded cargo table.</summary>
        public static int MaximumImportCargo => NativeStorageState.MaximumCargoRecords;
        /// <summary>Maximum distinct good/player physical accounts aggregated by one atomic import.</summary>
        public static int MaximumImportAccounts => NativeStorageBatch.MaximumAccounts;
        /// <summary>Copies bounded typed legacy stock during this owner's exact active Prepared
        /// callback. Requires a participant jointly enrolled with shared NativeStorage. Only a
        /// readable original record with positive causal association and no protected marker may
        /// import. NoRecord, a copied/stale receiver, unknown association and passive data refuse.
        /// Reads no native world; supports up to the storage row/cargo bounds and 32 aggregated
        /// good/player physical accounts. Empty input is valid for actor-only legacy progress.
        /// Successful preparation blocks ordinary storage until all current pending imports commit;
        /// do not abandon a successful plan and resume a private competing stock authority.</summary>
        public bool TryPrepareImport(CriticalSaveParticipant participant, VersionedSaveLoad load,
            IReadOnlyList<NativeStorageImportAllocation> allocations, IReadOnlyList<NativeStorageImportCargo> cargo,
            out NativeStorageImportPlan plan, out string reason) =>
            NativeStorageRuntime.Instance.TryPrepareImport(owner, participant, load, allocations, cargo, out plan, out reason);

        /// <summary>Publishes all imported physical stock and final undelivered cargo together.
        /// The caller must hold the actual engine monitor in a confirmed SP/editor world after the
        /// entire matching load pass. Revalidates exact archive, world, goods, building identities,
        /// combined native/virtual capacity and current storage publication before any effect.
        /// Refusal changes no stock, native gold, archive or caller state. Exact retry within the
        /// same original window/world returns AlreadyApplied; another intent conflicts. A new
        /// protected archive must use its shared state, not reimport retained legacy bytes.
        /// Prepare replacement actor state first; on success perform only nonthrowing assignments
        /// under this same monitor to route every consumer through shared stock. Other owners may
        /// still have pending imports; wait for IsCurrentWorldReady before using the shared world.
        /// The library never
        /// calls consumer retirement code. Capture bytes alone do not establish durable migration;
        /// the existing critical writer must commit all related records. No MP authority, native
        /// actor mutation, game hook, UI update or later command transport is supplied.</summary>
        public bool TryCommitImport(NativeStorageImportPlan plan, out GoodsTransactionStatus status, out string reason) =>
            NativeStorageRuntime.Instance.TryCommitImport(owner, plan, out status, out reason);
    }
}

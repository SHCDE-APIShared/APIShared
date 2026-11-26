using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using APIShared.Goods;
using APIShared.Savegames;

namespace APIShared.Economy
{
    internal sealed unsafe partial class NativeStorageRuntime
    {
        // Only completed imports for the currently published native world are retained. The
        // original registry token revokes them on every replacement load/unload; no new disk
        // format is needed because a subsequent protected original cannot be reimported.
        private readonly object importSync = new object();
        private long importGeneration = -1;
        private NativeStorageImportPlan[] pendingImports = Array.Empty<NativeStorageImportPlan>();
        private object importWorld;
        private Dictionary<CriticalSaveParticipant, NativeStorageImportPlan> completedImports;

        // Positive-only demand, published by owned cold transitions. The zero path does
        // not enumerate dictionaries, query a registry, allocate or inspect native state.
        internal bool HasPendingImports => Volatile.Read(ref pendingImports).Length != 0;

        // Bound once by the existing registry owner. Notification is outside its lock and
        // follows every actual BeginLoad/terminal Unload reset. Nested old-world unloads
        // do not notify; stale/reentrant notifications cannot erase a replacement's plans.
        internal void ImportGenerationChanged(long generation)
        {
            lock (importSync)
            {
                if (generation <= importGeneration) return;
                importGeneration = generation;
                // Publish protection before revoking binding or releasing pending
                // metadata. Every delayed empty-stock writer uses this same gate.
                PublishCapacityDemand(true);
                // Lock-free readiness/capacity readers observe pending before binding.
                // Revoke the old binding BEFORE releasing the pending barrier: publishing
                // zero first could briefly expose one owner's partial imported stock.
                // Retain quantities until the real load controller replaces them.
                Volatile.Write(ref boundWorld, null);
                completedImports = null; importWorld = null;
                Volatile.Write(ref pendingImports, Array.Empty<NativeStorageImportPlan>());
            }
        }

        internal bool TryPrepareImport(string owner, CriticalSaveParticipant participant, VersionedSaveLoad load,
            IReadOnlyList<NativeStorageImportAllocation> allocations, IReadOnlyList<NativeStorageImportCargo> cargo,
            out NativeStorageImportPlan plan, out string reason)
        {
            plan = null;
            if (!ready || criticalPreparedSource == null || participant == null || !participant.RequiresNativeStorage ||
                !StringComparer.Ordinal.Equals(owner, participant.OwnerGuid) ||
                !ReferenceEquals(participant.Registry, criticalPreparedSource.Registry))
            { reason = "The exact owner participant must jointly enroll initialized protected storage."; return false; }
            if (load == null || load.Status != VersionedSaveLoadStatus.Restored || load.MissionSessionId <= 0 ||
                load.MissionSessionId != MissionLifecycleService.ExecutingSessionId ||
                !participant.TryGetPreparedOrigin(owner, load, out var origin, out reason))
            { reason = "Only the exact readable associated Prepared record can stage an import."; return false; }
            if (!origin.HasRecord || !participant.Registry.TryReadCriticalPreparedMarker(participant.CriticalSource, origin,
                    out byte[] originalMarker, out bool protectedRecord, out reason) || protectedRecord)
            { reason = "A protected, unavailable or absent original must not import legacy stock again."; return false; }
            try
            {
                if (allocations == null || cargo == null) { reason = "Explicit bounded allocation and cargo lists are required."; return false; }
                int rowCount = allocations.Count, lotCount = cargo.Count;
                if (rowCount < 0 || rowCount > NativeStorageState.MaximumRows || lotCount < 0 || lotCount > NativeStorageState.MaximumCargoRecords)
                { reason = "The import exceeds the existing physical/cargo record bounds."; return false; }
                var rows = new NativeStorageState.Row[rowCount];
                var lots = new NativeStorageState.CargoLot[lotCount];
                var publicCargo = new NativeStorageImportCargo[lotCount];
                NativeStoredGood[] handles = Volatile.Read(ref registeredHandles);
                for (int i = 0; i < rowCount; i++)
                {
                    NativeStorageImportAllocation value = allocations[i];
                    if (!ImportGood(owner, value.Good, handles) || value.BuildingId <= 0 || value.BuildingId >= NativeStorageState.BuildingSlots ||
                        value.BuildingGlobalId == 0 || value.PlayerId < 1 || value.PlayerId > 8 || value.Quantity <= 0)
                    { reason = "Every allocation requires this owner's exact good and a positive exact building/player quantity."; return false; }
                    rows[i] = new NativeStorageState.Row(value.Good.Key, value.BuildingId, value.BuildingGlobalId,
                        value.PlayerId, value.Good.Kind, value.Quantity);
                }
                for (int i = 0; i < lotCount; i++)
                {
                    NativeStorageImportCargo value = cargo[i];
                    if (!ImportGood(owner, value.Good, handles) || value.CreationSequence <= 0 || value.PlayerId < 1 || value.PlayerId > 8 || value.Quantity <= 0)
                    { reason = "Every cargo lot requires this owner's exact good and positive creation sequence/final quantity."; return false; }
                    lots[i] = new NativeStorageState.CargoLot(value.Good.Key, value.CreationSequence, value.PlayerId,
                        value.Good.Kind, value.Quantity, value.Quantity, default);
                    publicCargo[i] = value;
                }
                Array.Sort(rows, CompareImportRows);
                Array.Sort(lots, (left, right) => left.Sequence.CompareTo(right.Sequence));
                for (int i = 1; i < rows.Length; i++)
                    if (CompareImportRows(rows[i - 1], rows[i]) == 0)
                    { reason = "One physical import row appears more than once."; return false; }
                for (int i = 1; i < lots.Length; i++)
                    if (lots[i - 1].Sequence == lots[i].Sequence)
                    { reason = "One owner cargo sequence appears more than once."; return false; }
                // Caller collections may have reentered another load or service. Do not bind
                // their copied payload to the replacement, even within the same mission attempt.
                if (!ready || criticalPreparedSource == null || !participant.TryGetPreparedOrigin(owner, load, out var after, out reason) ||
                    !ReferenceEquals(after, origin)) return false;
                byte[] intent = ImportFingerprint(origin.CopyOriginalRecordDigest(), rows, lots);
                var preparedPlan = new NativeStorageImportPlan(participant, origin, load.MissionSessionId, rows, lots, intent, publicCargo);
                lock (importSync)
                {
                    if (importGeneration != origin.Generation)
                    { reason = "The import lifecycle binding or original load changed during preparation."; return false; }
                    var pending = pendingImports;
                    foreach (var prior in pending)
                        if (ReferenceEquals(prior.Participant, participant))
                        {
                            if (ReferenceEquals(prior.Origin, origin) && SameImportIntent(prior.Intent, intent))
                            { plan = prior; reason = string.Empty; return true; }
                            reason = "This participant already staged another import intent."; return false;
                        }
                    if (pending.Length >= VersionedSaveData.MaximumSimulationRecords)
                    { reason = "The bounded pending import cohort is full."; return false; }
                    var next = new NativeStorageImportPlan[pending.Length + 1];
                    Array.Copy(pending, next, pending.Length); next[pending.Length] = preparedPlan;
                    // This successful preparation publishes conservative metadata only.
                    // Abandonment cannot expose empty capacity before migration finishes.
                    Volatile.Write(ref pendingImports, next); plan = preparedPlan;
                }
                reason = string.Empty; return true;
            }
            catch (Exception error)
            { reason = "The bounded import could not be copied: " + error.Message; return false; }
        }

        internal bool TryCommitImport(string owner, NativeStorageImportPlan plan, out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            if (plan == null || !StringComparer.Ordinal.Equals(owner, plan.OwnerGuid) || !plan.Participant.RequiresNativeStorage)
            { reason = "The exact owner's import plan is required."; return false; }
            if (!ImportContext(plan, out object world, out NativeStorageState before, out reason)) return false;
            NativeStorageImportPlan[] pendingBefore;
            Dictionary<CriticalSaveParticipant, NativeStorageImportPlan> completedBefore;
            WeaponProductionManufacturingAdoption manufacturingAdoption;
            lock (importSync)
            {
                if (importGeneration != plan.Origin.Generation)
                { reason = "The import's original generation was replaced."; return false; }
                manufacturingAdoption = plan.ManufacturingAdoption;
                completedBefore = ReferenceEquals(importWorld, world) ? completedImports : null;
                if (completedBefore != null && completedBefore.TryGetValue(plan.Participant, out var prior))
                {
                    if (ReferenceEquals(prior.Origin, plan.Origin) && SameImportIntent(prior.Intent, plan.Intent))
                    {
                        if (manufacturingAdoption != null && !ReferenceEquals(manufacturingAdoption.PublishedWorld, world))
                        { reason = "The storage receipt has no matching manufacturing publication."; return false; }
                        status = GoodsTransactionStatus.AlreadyApplied; reason = string.Empty; return true;
                    }
                    status = GoodsTransactionStatus.Conflict; reason = "This participant already imported another original or intent in this world."; return false;
                }
                pendingBefore = pendingImports;
                bool found = false;
                foreach (var pending in pendingBefore) if (ReferenceEquals(pending, plan)) { found = true; break; }
                if (!found) { reason = "The exact prepared import is not pending."; return false; }
            }
            object publication = before.PublicationIdentity, cargoPublication = before.CargoPublicationIdentity;
            NativeStoragePreparedLoad prepared = preparedLoad;
            var observed = new Dictionary<int, NativeStorageProbe>();
            NativeStorageState candidate;
            Dictionary<CriticalSaveParticipant, NativeStorageImportPlan> next;
            NativeStorageImportPlan[] remaining;
            WeaponProductionRuntime.PreparedManufacturingPublication manufacturing = null;
            try
            {
                if (manufacturingAdoption != null &&
                    !WeaponProductionRuntime.TryPrepareManufacturingPublication(manufacturingAdoption, world, out manufacturing, out reason)) return false;
                NativeStorageProbe Probe(int slot)
                {
                    if (!observed.TryGetValue(slot, out var value)) { value = readProbe(slot); observed.Add(slot, value); }
                    return value;
                }
                if (!before.Reconcile(Probe, out var reconciled, out reason) || !ReferenceEquals(reconciled, before))
                { reason = "Existing shared allocations require independent reconciliation before this import."; return false; }
                if (!before.TryImport(owner, plan.Rows, plan.Lots, plan.Intent, Probe, out candidate, out reason)) return false;
                if (!ImportContext(plan, out object after, out NativeStorageState afterState, out reason) || !ReferenceEquals(world, after) || !ReferenceEquals(afterState, before) ||
                    !ReferenceEquals(preparedLoad, prepared) ||
                    !ReferenceEquals(publication, before.PublicationIdentity) || !ReferenceEquals(cargoPublication, before.CargoPublicationIdentity))
                { reason = "The import's world or shared state changed during preparation."; return false; }
                foreach (var entry in observed)
                {
                    NativeStorageProbe current = readProbe(entry.Key), expected = entry.Value;
                    if (!current.IsLive || !NativeStorageBatch.SameIdentity(expected, current) ||
                        current.NativeUsed != expected.NativeUsed || current.Capacity != expected.Capacity)
                    { reason = "A physical import destination changed before publication."; return false; }
                }
                if (!ImportContext(plan, out after, out afterState, out reason) || !ReferenceEquals(world, after) || !ReferenceEquals(afterState, before) ||
                    !ReferenceEquals(preparedLoad, prepared) ||
                    !ReferenceEquals(publication, before.PublicationIdentity) || !ReferenceEquals(cargoPublication, before.CargoPublicationIdentity))
                { reason = "The import's final source/world/publication changed."; return false; }
                // Allocate the entire receipt table before the first shared-state effect.
                next = completedBefore != null
                    ? new Dictionary<CriticalSaveParticipant, NativeStorageImportPlan>(completedBefore)
                    : new Dictionary<CriticalSaveParticipant, NativeStorageImportPlan>();
                next.Add(plan.Participant, plan);
                remaining = new NativeStorageImportPlan[pendingBefore.Length - 1];
                int output = 0;
                foreach (var pending in pendingBefore) if (!ReferenceEquals(pending, plan)) remaining[output++] = pending;
            }
            catch (Exception error)
            { reason = "Import preparation failed: " + error.Message; return false; }
            // Exact engine ownership excludes another publisher. This is outside the refusal
            // catch: no callback, stock validation or allocation follows the first
            // publication. The existing demand DWORD is then updated for the new rows.
            // The caller's preallocated actor-state assignment follows under this same
            // monitor. A previously staged world is activated only on success.
            lock (importSync)
            {
                if (importGeneration != plan.Origin.Generation || !ReferenceEquals(pendingBefore, pendingImports) ||
                    !ImportContext(plan, out object finalWorld, out NativeStorageState finalState, out reason) ||
                    !ReferenceEquals(world, finalWorld) || !ReferenceEquals(before, finalState) ||
                    !ReferenceEquals(preparedLoad, prepared) || !ReferenceEquals(publication, before.PublicationIdentity) ||
                    !ReferenceEquals(cargoPublication, before.CargoPublicationIdentity))
                { reason = "The final import generation, source, pending cohort or state changed."; return false; }
                if (manufacturingAdoption != null)
                {
                    if (!ReferenceEquals(manufacturingAdoption, plan.ManufacturingAdoption) || manufacturing == null || !manufacturing.IsCurrent(out reason))
                    { reason = "The attached producer actor state changed before publication."; return false; }
                    // Native actor/capability reads above are outside the registry lease. A
                    // replacement load or another import during those reads must still refuse.
                    // Ordinary unattached imports retain their original validation path.
                    if (importGeneration != plan.Origin.Generation || !ReferenceEquals(pendingBefore, pendingImports) ||
                        !ImportContext(plan, out finalWorld, out finalState, out reason) ||
                        !ReferenceEquals(world, finalWorld) || !ReferenceEquals(before, finalState) ||
                        !ReferenceEquals(preparedLoad, prepared) || !ReferenceEquals(publication, before.PublicationIdentity) ||
                        !ReferenceEquals(cargoPublication, before.CargoPublicationIdentity))
                    { reason = "The producer validation changed the final import source/state."; return false; }
                }
                // Lock order is actual engine -> importSync -> registry. The registry
                // allocates its guard before locking, then rechecks only owned metadata.
                // This closes invalidation between source validation and publication;
                // generation observers run only after its monitor has been released.
                if (!plan.Participant.Registry.TryAcquireCriticalImportCommit(plan.Participant.PreparedSource,
                    plan.Origin, criticalPreparedSource, plan.MissionSessionId, out var lease, out reason)) return false;
                using (lease)
                {
                    manufacturing?.PublishManaged();
                    state = candidate; completedImports = next; importWorld = world;
                    Volatile.Write(ref boundWorld, world); staged = null;
                    Volatile.Write(ref pendingImports, remaining);
                }
                // The registry lease has ended. Publish only trusted process-owned wire
                // bytes/demand while engine/import monitors still exclude native execution.
                // No native actor/resource write or consumer callback follows publication.
                manufacturing?.PublishWire();
            }
            prepared?.ReleaseState();
            RefreshImportPublicationDemand();
            status = GoodsTransactionStatus.Applied; reason = string.Empty; return true;
        }

        private bool ImportContext(NativeStorageImportPlan plan, out object world, out NativeStorageState currentState, out string reason)
        {
            world = null; currentState = null;
            if (productionSave != null || NativeStorageNativeRuntime.B6100Depth != 0 || criticalPreparedSource == null ||
                !ReferenceEquals(plan.Participant.Registry, criticalPreparedSource.Registry) ||
                !Context(true, out world, out reason))
            { reason = "Import requires the ready protected owner outside native add-goods and frozen saves."; return false; }
            var clock = MissionLifecycleService.NativeClock;
            if (clock == null || clock.Context.Mode.IsRealMultiplayer || clock.Context.SessionId != plan.MissionSessionId ||
                plan.MissionSessionId <= 0 || !plan.Participant.TryValidateCurrentWorld(plan.Origin, world, plan.MissionSessionId, out reason))
            { reason = "The exact complete original load and current SP/editor world are required."; return false; }
            if (blocked || saveHandle?.KeepsLoadedRecord == true ||
                !criticalPreparedSource.Registry.TryValidateCriticalWorld(criticalPreparedSource, world, plan.MissionSessionId, out reason))
            { reason = "The shared storage origin must be complete and readable."; return false; }
            // EnsureWorld performs activation/reconciliation; calling it before a possible
            // import refusal could publish maintenance losses. Select the exact staged/current
            // owner without mutation instead, then publish the entire imported result once.
            currentState = ReferenceEquals(world, Volatile.Read(ref boundWorld)) ? state : staged;
            if (currentState == null || stagedSession != plan.MissionSessionId ||
                (MissionLifecycleService.ActiveContext?.SessionId ?? 0) != plan.MissionSessionId)
            { reason = "The matching storage state has not been staged for this world."; return false; }
            reason = string.Empty;
            return true;
        }

        private static bool ImportGood(string owner, NativeStoredGood good, NativeStoredGood[] handles)
        {
            if (good == null || !StringComparer.Ordinal.Equals(owner, good.OwnerGuid)) return false;
            foreach (var handle in handles) if (ReferenceEquals(handle, good)) return true;
            return false;
        }
        private static int CompareImportRows(NativeStorageState.Row left, NativeStorageState.Row right)
        {
            int order = StringComparer.Ordinal.Compare(left.Good.LocalId, right.Good.LocalId);
            if (order == 0) order = left.Slot.CompareTo(right.Slot);
            if (order == 0) order = left.GlobalId.CompareTo(right.GlobalId);
            if (order == 0) order = left.Player.CompareTo(right.Player);
            if (order == 0) order = left.Kind.CompareTo(right.Kind);
            return order;
        }
        private static bool SameImportIntent(byte[] left, byte[] right)
        { if (left.Length != right.Length) return false; for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false; return true; }
        private static byte[] ImportFingerprint(byte[] digest, NativeStorageState.Row[] rows, NativeStorageState.CargoLot[] lots)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x31504D49); writer.Write(digest.Length); writer.Write(digest); writer.Write(rows.Length);
                foreach (var row in rows)
                { writer.Write(row.Good.LocalId); writer.Write(row.Slot); writer.Write(row.GlobalId); writer.Write(row.Player); writer.Write((int)row.Kind); writer.Write(row.Quantity); }
                writer.Write(lots.Length);
                foreach (var lot in lots)
                { writer.Write(lot.Good.LocalId); writer.Write(lot.Sequence); writer.Write(lot.Player); writer.Write(lot.Issued); writer.Write((int)lot.Kind); }
                writer.Flush(); using (var sha = SHA256.Create()) return sha.ComputeHash(stream.ToArray());
            }
        }
    }
}

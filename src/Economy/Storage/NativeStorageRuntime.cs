using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using APIShared.Goods;
using APIShared.Internal;
using APIShared.Savegames;
using APIShared.Simulation;

namespace APIShared.Economy
{
    // Single owner for physical virtual rows, their private ledger and matching archive entries.
    // All mutations/capture serialize through the actual engine monitor. Native callbacks only
    // read exact published claims; cold restoration runs at a protected matching-world boundary.
    internal sealed unsafe partial class NativeStorageRuntime
    {
        internal static readonly NativeStorageRuntime Instance = new NativeStorageRuntime();
        private readonly Dictionary<CustomGoodId, NativeStoredGood> registered = new Dictionary<CustomGoodId, NativeStoredGood>();
        private NativeStoredGood[] registeredHandles = Array.Empty<NativeStoredGood>();
        private readonly List<NativeStorageGoodRegistration> definitions = new List<NativeStorageGoodRegistration>();
        private readonly Func<int, NativeStorageProbe> readProbe;
        private readonly NativeStorageExchangeEngine exchanges;
        private readonly NativeStorageDeliveryEngine deliveries;
        private readonly Action<bool> publishNativeCapacityDemand;
        private object engine, boundWorld;
        private NativeStorageState state;
        private NativeStorageState staged;
        private NativeStoragePreparedLoad preparedLoad;
        private long stagedSession => preparedLoad?.MissionSessionId ?? 0;
        private object blockedWorld;
        private volatile bool ready, blocked;
        private bool attempted;
        private string failure;
        private VersionedSavePreparedSource criticalPreparedSource;
        private bool criticalEnrollmentRequested;
        private bool preparedCreatedWorld;
        private VersionedSavePreparedOrigin preparedStorageOrigin;
        private VersionedSaveCriticalCreatedWorldOrigin preparedCreatedOrigin;
        private volatile bool unreadyArchiveSaveDemand;
        internal bool HasUnreadyArchiveSaveDemand => unreadyArchiveSaveDemand;
        internal bool HasCriticalWriterEnrollment => ready && criticalPreparedSource != null;

        private NativeStorageRuntime() : this(NativeStorageNativeRuntime.UpdateDemand) { }

        // The process-owned leaf is a nonthrowing demand-DWORD store; it performs no
        // game callback or native helper call. A private constructor also lets the
        // actual runtime fixture observe this publication without installing hooks.
        private NativeStorageRuntime(Action<bool> publishNativeCapacityDemand)
        {
            this.publishNativeCapacityDemand = publishNativeCapacityDemand ?? throw new ArgumentNullException(nameof(publishNativeCapacityDemand));
            readProbe = ReadProbe; exchanges = new NativeStorageExchangeEngine(GoodsWalletNative.Instance);
            deliveries = new NativeStorageDeliveryEngine(GoodsWalletNative.Instance);
        }
        internal bool IsCurrentWorldReady
        {
            get
            {
                if (!ready || blocked || HasPendingImports) return false;
                object current = MissionLifecycleService.NativeWorld;
                if (current == null || !ReferenceEquals(current, Volatile.Read(ref boundWorld))) return false;
                if (criticalEnrollmentRequested || criticalPreparedSource != null)
                {
                    var clock = MissionLifecycleService.NativeClock;
                    if (clock == null || clock.Context.Mode.Kind == GameModes.GameModeKind.Unknown ||
                        clock.Context.Mode.IsRealMultiplayer || !ReferenceEquals(clock.World, current)) return false;
                }
                return ReferenceEquals(current, MissionLifecycleService.NativeWorld);
            }
        }

        internal bool TryRegister(string owner, string localId, NativeStoragePool pool, out NativeStoredGood good, out string reason)
        {
            good = null;
            if (!UnityMainThreadDispatch.IsMainThread || ApiSharedRuntime.ProcessInstance.State != NativeApiState.Ready)
            { reason = "Register storage goods during initialized Unity startup."; return false; }
            CustomGoodId key;
            try { key = new CustomGoodId(owner, localId); }
            catch (ArgumentException error) { reason = error.Message; return false; }
            StoragePool kind = (StoragePool)pool;
            if (!NativeStorageState.ValidKind(kind)) { reason = "Only audited Armory and Granary pools are supported."; return false; }
            if (MissionLifecycleService.NativeWorld != null || MissionLifecycleService.ActiveContext != null)
            { reason = "Storage goods must register before loading a world."; return false; }
            if (registered.ContainsKey(key)) { reason = "This owner already registered the storage good."; return false; }
            if (registered.Count >= NativeStorageState.MaximumGoods) { reason = "The storage catalog is full."; return false; }
            if (!EnsureInitialized(out reason)) return false;
            lock (engine)
            {
                if (MissionLifecycleService.NativeWorld != null || MissionLifecycleService.ActiveContext != null)
                { reason = "A world started during storage registration."; return false; }
                // Allocate both process-owned handles before publishing their registration.
                var handle = new NativeStoredGood(key, kind);
                var published = registeredHandles;
                var nextHandles = new NativeStoredGood[published.Length + 1];
                Array.Copy(published, nextHandles, published.Length);
                nextHandles[published.Length] = handle;
                definitions.Add(new NativeStorageGoodRegistration(key, kind));
                try { registered.Add(key, handle); }
                catch { definitions.RemoveAt(definitions.Count - 1); throw; }
                Volatile.Write(ref registeredHandles, nextHandles);
                good = handle; reason = string.Empty; return true;
            }
        }

        // Public enrollment first validates a metadata-only plan. The native
        // mode request is assigned only after that exact plan commits, so false
        // cannot change a later ordinary reader2 registration into protected3.
        internal bool TryRequestCriticalSaveEnrollment(out string reason)
        {
            if (!CanRequestCriticalSaveEnrollment(out reason)) return false;
            CommitCriticalSaveEnrollmentRequest(); return true;
        }

        internal bool CanRequestCriticalSaveEnrollment(out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread || ApiSharedRuntime.ProcessInstance.State != NativeApiState.Ready)
            { reason = "Request protected storage during initialized Unity startup."; return false; }
            if (MissionLifecycleService.NativeWorld != null || MissionLifecycleService.ActiveContext != null ||
                (!criticalEnrollmentRequested && (attempted || ready || registeredHandles.Length != 0)) ||
                (criticalEnrollmentRequested && attempted && !HasCriticalWriterEnrollment))
            { reason = "Request protection before the first storage registration or world."; return false; }
            if (!WorkerProductionCriticalSaveRuntime.IsAvailable)
            { reason = "The qualified critical writer is unavailable."; return false; }
            reason = string.Empty; return true;
        }

        internal void CommitCriticalSaveEnrollmentRequest() => criticalEnrollmentRequested = true;

        // The normal startup path uses this exact descriptor. Keeping the legacy
        // descriptor at reader2 also makes protected3 archives TooNew/passive in
        // an unenrolled runtime rather than silently dropping their known marker.
        internal VersionedSaveDataRegistration CreateSaveRegistration(bool critical) =>
            new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, NativeStorageProductionSave.RecordIdentifier, critical ? 3 : 2, 1,
                critical ? new[] { "ledger", "stores", NativeStorageProductionSave.CriticalMarkerEntry } : new[] { "ledger", "stores" }, Capture, Loaded)
            { Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = Prepare };

        private bool EnsureInitialized(out string reason)
        {
            if (ready) { reason = string.Empty; return true; }
            if (attempted) { reason = failure; return false; }
            attempted = true;
            try
            {
                engine = GoodsWalletNative.Instance.EngineMonitor ?? throw new InvalidOperationException("The wallet/engine boundary is unavailable.");
                if (!GoodsWalletNative.Instance.IsReady) throw new InvalidOperationException("The checked native wallet is unavailable.");
                NativeStorageState.WarmPublication();
                if (!NativeStorageNativeRuntime.EnsureInstalled(out reason)) throw new InvalidOperationException(reason);
                // Physical-stock callers keep their original default2/MP path.
                // Only explicit protected startup selects the reserved marker/reader3.
                if (criticalEnrollmentRequested && !WorkerProductionCriticalSaveRuntime.IsAvailable)
                    throw new InvalidOperationException("Protected native storage requires the available qualified critical writer.");
                var archive = CreateSaveRegistration(criticalEnrollmentRequested);
                if (!VersionedSaveData.TryRegister(archive, out saveHandle, out reason)) throw new InvalidOperationException(reason);
                if (criticalEnrollmentRequested && !VersionedSaveDataRuntime.TryTrackPreparedSource(APISharedPlugin.PluginGuid, saveHandle, out criticalPreparedSource, out reason))
                    throw new InvalidOperationException(reason);
                if (criticalEnrollmentRequested && !VersionedSaveDataRuntime.TryObserveGeneration(ImportGenerationChanged, out reason))
                    throw new InvalidOperationException(reason);
                if (!SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "native-storage/reconcile", Tick, out reason, -1000))
                    throw new InvalidOperationException(reason);
                PublishCapacityDemand(false);
                // Joining adds permanent demand: publish it only after every other
                // fallible startup operation. Tick remains inert until ready=true.
                if (criticalEnrollmentRequested && !VersionedSaveDataRuntime.TryJoinCriticalWriterCohort(criticalPreparedSource, NativeStorageProductionSave.CriticalMarkerEntry, 3, out reason))
                    throw new InvalidOperationException(reason);
                ready = true; reason = string.Empty; return true;
            }
            catch (Exception error)
            {
                failure = reason = error.Message;
                RuntimeHealth.Failure(ApiSharedRuntime.ProcessInstance.Logger, "native-storage/startup", true,
                    "Native storage unavailable; no stored-good registrations were published: " + error);
                return false;
            }
        }

        private bool Context(bool mutation, out object world, out string reason)
        {
            world = null;
            if (!ready) { reason = failure ?? "Native storage is unavailable."; return false; }
            if (engine == null || !Monitor.IsEntered(engine)) { reason = "The caller must hold the actual engine monitor."; return false; }
            var clock = MissionLifecycleService.NativeClock;
            if (clock == null || clock.Context.Mode.Kind == GameModes.GameModeKind.Unknown)
            { reason = "No confirmed known native world is available."; return false; }
            if (mutation && !GoodsWalletNative.Instance.HasAuthorityContext)
            { reason = "Multiplayer mutation requires a native tick or authenticated Chore boundary."; return false; }
            world = clock.World;
            if (world == null || !ReferenceEquals(world, MissionLifecycleService.NativeWorld))
            { reason = "The current native world changed."; return false; }
            reason = string.Empty; return true;
        }

        // Archive Prepared is synchronous but lifecycle observer delivery may be queued by nested
        // loads. Bind only the staged session matching the exact currently published world; no stale
        // BeforeLoad/End callback is allowed to clear a replacement's state.
        private bool EnsureWorld(out object world, out string reason)
        {
            if (!Context(false, out world, out reason)) return false;
            if (HasPendingImports)
            { reason = "The matching legacy storage imports must all commit before shared state activation."; return false; }
            var current = MissionLifecycleService.NativeWorld;
            var clock = MissionLifecycleService.NativeClock;
            if ((criticalEnrollmentRequested || criticalPreparedSource != null) &&
                (clock == null || clock.Context.Mode.Kind == GameModes.GameModeKind.Unknown ||
                    clock.Context.Mode.IsRealMultiplayer || !ReferenceEquals(clock.World, world)))
            { reason = "Protected native storage activation requires its exact known single-player/editor world."; return false; }
            // Creating a blank editor map has no archive callback. Only that explicit native
            // operation may synthesize NoRecord; a missing callback on a loaded map/save must
            // never silently erase its unknown state, including after a previous blocked load.
            if (current != null && clock != null &&
                CanCreateWithoutArchive(stagedSession, current.Mission, clock.Context.StartKind) &&
                (!blocked || !ReferenceEquals(current, blockedWorld)) &&
                current.Mission == (MissionLifecycleService.ActiveContext?.SessionId ?? 0))
            {
                var empty = CreateEmpty();
                preparedLoad = NativeStoragePreparedLoad.ForBlankWorld(empty, current.Mission);
                preparedCreatedWorld = true;
                staged = empty; blocked = false; blockedWorld = null; failure = null; passiveCargoRecord = false;
                preparedStorageOrigin = null; preparedCreatedOrigin = null;
                Volatile.Write(ref boundWorld, null);
            }
            if (criticalPreparedSource != null && !preparedCreatedWorld && saveHandle?.KeepsLoadedRecord == true)
                return Block(reason = "The original storage cohort record must remain passive.");
            if (blocked) { reason = failure ?? "The native-storage record is blocked."; return false; }
            if (ReferenceEquals(world, boundWorld) && state != null) return true;
            if (current == null || staged == null || stagedSession == 0 || stagedSession != current.Mission ||
                stagedSession != (MissionLifecycleService.ActiveContext?.SessionId ?? 0))
            { reason = "The matching storage archive is not prepared yet."; return false; }
            NativeStoragePreparedLoad prepared = preparedLoad;
            NativeStorageState pending = staged;
            VersionedSavePreparedOrigin archiveOrigin = preparedStorageOrigin;
            VersionedSaveCriticalCreatedWorldOrigin createdOrigin = preparedCreatedOrigin;
            if (!pending.Reconcile(readProbe, out NativeStorageState candidate, out reason)) return Block(reason);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "World replacement interrupted storage activation."; return false; }
            if (preparedCreatedWorld && criticalPreparedSource != null && createdOrigin == null)
            {
                // Retain the token minted for this exact empty candidate. A later
                // metadata reset must not be repaired by recapturing a new token.
                if (!criticalPreparedSource.Registry.TryAuthorizeCriticalCreatedWorld(criticalPreparedSource, world,
                    current.Mission, out createdOrigin, out reason)) return false;
                preparedCreatedOrigin = createdOrigin;
            }
            if (criticalPreparedSource != null)
            {
                lock (importSync)
                {
                    if (HasPendingImports || !ReferenceEquals(preparedLoad, prepared) || !ReferenceEquals(staged, pending) ||
                        !ReferenceEquals(preparedStorageOrigin, archiveOrigin) || !ReferenceEquals(preparedCreatedOrigin, createdOrigin))
                    { reason = "The protected storage candidate or its retained origin changed."; return false; }
                    // The registry guard closes invalidation after validation and
                    // before publication. Only preallocated assignments run inside.
                    if (!criticalPreparedSource.Registry.TryAcquireCriticalWorldPublication(criticalPreparedSource,
                        archiveOrigin, createdOrigin, world, current.Mission, out var lease, out reason)) return false;
                    using (lease)
                    {
                        state = candidate; Volatile.Write(ref boundWorld, world); staged = null;
                    }
                }
            }
            else
            { state = candidate; Volatile.Write(ref boundWorld, world); staged = null; }
            prepared.ReleaseState();
            PublishCapacityDemand(candidate.HasRows);
            reason = string.Empty; return true;
        }

        internal static bool CanCreateWithoutArchive(long preparedSession, long currentSession, MissionStartKind kind) =>
            currentSession != 0 && preparedSession != currentSession && kind == MissionStartKind.EditorCreated;

        private NativeStorageState CreateEmpty()
        {
            var empty = new NativeStorageState();
            foreach (var entry in definitions)
                if (!empty.TryRegister(entry.Good, entry.Kind, out string error)) throw new InvalidDataException(error);
            return empty;
        }

        private bool Block(string reason)
        {
            failure = reason; blocked = true;
            // Metadata refresh keeps this identity. Only a genuinely new explicit blank
            // world may recover without an archive; the same Ready world cannot erase a refusal.
            blockedWorld = MissionLifecycleService.NativeWorld;
            PublishCapacityDemand(true); // Unknown occupied storage cannot safely admit more goods.
            return false;
        }

        // All owned demand decisions and their native-DWORD publication share the
        // revocation gate. A late empty-stock result cannot undo a newer protected
        // load/unload barrier. The ordinary unenrolled path retains its exact input.
        private void PublishCapacityDemand(bool requested)
        {
            if (!criticalEnrollmentRequested && criticalPreparedSource == null)
            { publishNativeCapacityDemand(requested); return; }
            lock (importSync)
                publishNativeCapacityDemand(requested || HasPendingImports || blocked ||
                    Volatile.Read(ref boundWorld) == null);
        }

        // No native occupancy is implied by cargo alone. Unbound protected state,
        // however, remains conservative until exact activation/import binds it.
        internal void RefreshImportPublicationDemand() => PublishCapacityDemand(HasPendingImports || blocked || state?.HasRows == true);

        private static NativeStorageProbe ReadProbe(int slot) =>
            NativeStorageNativeRuntime.TryRead(slot, out NativeStorageProbe probe) ? probe : default;

        internal bool TryObserve(int slot, out NativeStorageSnapshot snapshot, out string reason)
        {
            snapshot = default;
            if (!EnsureWorld(out object world, out reason)) return false;
            if (!NativeStorageNativeRuntime.TryRead(slot, out NativeStorageProbe probe) || !probe.IsLive)
            { reason = "The building is not an exact live supported storage."; return false; }
            int occupied = state.GetOccupied(probe.Slot, probe.GlobalId, probe.PlayerId, probe.Kind);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "The native world changed during observation."; return false; }
            snapshot = new NativeStorageSnapshot(world, probe, occupied); reason = string.Empty; return true;
        }

        internal bool TryGetStock(CustomGoodId good, int player, out GoodsBalance balance, out string reason)
        {
            balance = default;
            if (!EnsureWorld(out _, out reason)) return false;
            if (!state.TryGetBalance(good, player, out balance)) { reason = "The good or player is unavailable."; return false; }
            reason = string.Empty; return true;
        }

        internal bool TryGetQuantity(NativeStoredGood good, NativeStorageSnapshot expected, out int quantity, out string reason)
        {
            quantity = 0;
            // Registration handles are immutable/process-lived; the shared lookup reads their
            // published array and does not race the Unity startup dictionary.
            if (!IsRegistrationReady(good)) { reason = "The exact registered output handle is unavailable."; return false; }
            try
            {
                if (!EnsureWorld(out object world, out reason)) return false;
                if (!ReferenceEquals(expected.World, world)) { reason = "The storage observation belongs to another native world."; return false; }
                NativeStorageNativeRuntime.TryRead(expected.BuildingId, out NativeStorageProbe current);
                if (!TryReadQuantity(state, good.Key, world, blocked, expected, current, out quantity, out reason)) return false;
                if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld) || blocked)
                { quantity = 0; reason = "The native world changed during quantity observation."; return false; }
                return true;
            }
            catch (Exception error)
            { quantity = 0; reason = "Storage quantity observation failed: " + error.Message; return false; }
        }

        // The production binding supplies a protected current native probe after EnsureWorld.
        // Kept value-only so lifetime/identity rejection can be tested without executing game code.
        internal static bool TryReadQuantity(NativeStorageState owner, CustomGoodId good, object world, bool blockedRecord,
            NativeStorageSnapshot expected, NativeStorageProbe current, out int quantity, out string reason)
        {
            quantity = 0;
            if (owner == null || world == null || blockedRecord)
            { reason = "The matching storage state is pending, blocked or unavailable."; return false; }
            if (!ReferenceEquals(expected.World, world)) { reason = "The storage observation belongs to another native world."; return false; }
            NativeStorageProbe prior = expected.Probe;
            if (!prior.IsLive || !current.IsLive || prior.Slot != current.Slot || prior.GlobalId != current.GlobalId ||
                prior.PlayerId != current.PlayerId || prior.Kind != current.Kind)
            { reason = "The observed storage was destroyed, reused, captured or retyped."; return false; }
            return owner.TryGetQuantity(good, current, out quantity, out reason);
        }
        internal bool TryGetSequence(string owner, out long sequence, out string reason)
        {
            sequence = 0;
            if (productionSave != null) { reason = "A production save checkpoint is frozen."; return false; }
            if (!Context(true, out _, out reason) || !EnsureWorld(out _, out reason) || !Reconcile(out reason)) return false;
            sequence = state.GetLastSequence(owner); reason = string.Empty; return true;
        }

        internal bool TryGetNativeOccupied(NativeStorageProbe probe, out int occupied)
        {
            occupied = 0;
            if (!EnsureWorld(out _, out _)) return false;
            occupied = state.GetOccupied(probe.Slot, probe.GlobalId, probe.PlayerId, probe.Kind);
            return true;
        }

        internal bool TryExchange(NativeStoredGood good, NativeStorageSnapshot expected, long sequence, int quantityDelta, int goldDelta,
            out GoodsTransactionStatus status, out string reason)
        {
            status = GoodsTransactionStatus.InvalidRequest;
            if (productionSave != null) { reason = "A production save checkpoint is frozen."; return false; }
            if (NativeStorageNativeRuntime.B6100Depth != 0)
            { reason = "Storage exchanges cannot change capacity inside a native add-goods Pre/Original/Post chain."; return false; }
            if (!Context(true, out object world, out reason) || !EnsureWorld(out world, out reason)) return false;
            if (!ReferenceEquals(expected.World, world)) { reason = "The storage observation belongs to another native world."; return false; }
            // Like batch retries, an existing receipt must be resolved before maintenance
            // can advance the sequence and evict it. First-time world binding stays above.
            if (exchanges.ProbeExchangeReceipt(state, good.Key, expected, sequence, quantityDelta, goldDelta, out var receipt, out reason))
            { status = receipt; return status == GoodsTransactionStatus.AlreadyApplied; }
            if (!Reconcile(out reason)) return false;
            NativeStorageNativeRuntime.TryRead(expected.BuildingId, out NativeStorageProbe current);
            bool applied = exchanges.TryExchange(state, good.Key, expected, current, sequence, quantityDelta, goldDelta, out status, out reason);
            if (applied) PublishCapacityDemand(state.HasRows);
            return applied;
        }

        private bool Reconcile(out string reason)
        {
            if (productionSave != null) { reason = "A production save checkpoint is frozen."; return false; }
            object world = boundWorld;
            if (!state.Reconcile(readProbe, out NativeStorageState next, out reason)) return Block(reason);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "The native world changed during reconciliation."; return false; }
            state = next;
            PublishCapacityDemand(next.HasRows);
            return true;
        }

        private void Tick(int ignored)
        {
            if (productionSave != null) return;
            if (!ready || engine == null || !Monitor.IsEntered(engine) || !GoodsWalletNative.Instance.HasAuthorityContext) return;
            if (!EnsureWorld(out _, out _)) return;
            if (state.HasRows) Reconcile(out _);
        }

        private bool Capture(VersionedSaveWriter writer)
        {
            if (!ready) return false;
            lock (engine)
            {
                if (productionSave != null)
                {
                    if (!productionSave.Matches(state, MissionLifecycleService.NativeWorld) || blocked) return false;
                    productionSave.Write(writer); return true;
                }
                if (!EnsureWorld(out object world, out _)) return false;
                if (GoodsWalletNative.Instance.HasAuthorityContext && !Reconcile(out _)) return false;
                state.Capture(out byte[] ledger, out byte[] stores);
                if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld) || blocked) return false;
                // Cargo history requires reader 2; untouched worlds retain byte-identical v1 envelopes.
                writer.UseVersion(state.StoresSchema, state.StoresSchema);
                writer.Write("ledger", ledger); writer.Write("stores", stores); return true;
            }
        }

        // A registered callback outlives partial startup failure. Only exact
        // causally associated absence proves there is no virtual occupancy to
        // protect; even readable legacy stores can contain saved physical rows.
        internal static bool FailedInitializationRequiresProtection(VersionedSaveLoad load, long executingSession) =>
            load == null || load.MissionSessionId <= 0 || load.MissionSessionId != executingSession ||
            (load.Status != VersionedSaveLoadStatus.NoRecord && load.Status != VersionedSaveLoadStatus.OutOfScope);

        private void Prepare(VersionedSaveLoad load)
        {
            lock (engine)
            {
                long executingSession = MissionLifecycleService.ExecutingSessionId;
                if (!ready)
                {
                    bool protect = FailedInitializationRequiresProtection(load, executingSession);
                    // Same owned transition drives capacity protection and native
                    // save demand. Keep original bytes; never publish failed state.
                    unreadyArchiveSaveDemand = protect;
                    PublishCapacityDemand(protect);
                    throw new InvalidOperationException(failure ?? "Native storage startup has not completed; preserve the original archive.");
                }
                unreadyArchiveSaveDemand = false;
                blocked = false; blockedWorld = null; failure = null; staged = null; preparedCreatedWorld = false;
                preparedStorageOrigin = null; preparedCreatedOrigin = null;
                Volatile.Write(ref boundWorld, null);
                if (criticalPreparedSource != null &&
                    !criticalPreparedSource.TryGetCurrent(load, out preparedStorageOrigin, out string originReason))
                {
                    Block(originReason);
                    throw new InvalidOperationException(originReason);
                }
                PublishCapacityDemand(true);
                var prepared = new NativeStoragePreparedLoad();
                preparedLoad = prepared;
                try { prepared.Prepare(definitions, load, executingSession); }
                finally
                {
                    staged = prepared.State;
                    passiveCargoRecord = prepared.MayContainCargo;
                    if (prepared.Blocked) Block(prepared.Reason);
                }
            }
        }

        private void Loaded(VersionedSaveLoad load)
        {
            if (blocked && load.Status == VersionedSaveLoadStatus.Restored)
                throw new InvalidDataException("Storage restoration is blocked; preserve the original archive bytes.");
        }
    }
}

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
    internal sealed unsafe class NativeStorageRuntime
    {
        internal static readonly NativeStorageRuntime Instance = new NativeStorageRuntime();
        private readonly Dictionary<CustomGoodId, NativeStoredGood> registered = new Dictionary<CustomGoodId, NativeStoredGood>();
        private readonly List<NativeStorageGoodRegistration> definitions = new List<NativeStorageGoodRegistration>();
        private readonly Func<int, NativeStorageProbe> readProbe;
        private readonly NativeStorageExchangeEngine exchanges;
        private object engine, boundWorld;
        private NativeStorageState state;
        private NativeStorageState staged;
        private long stagedSession;
        private volatile bool ready, blocked;
        private bool attempted;
        private string failure;

        private NativeStorageRuntime()
        { readProbe = ReadProbe; exchanges = new NativeStorageExchangeEngine(GoodsWalletNative.Instance); }
        internal bool IsCurrentWorldReady
        {
            get
            {
                object current = MissionLifecycleService.NativeWorld;
                return ready && !blocked && current != null && ReferenceEquals(current, Volatile.Read(ref boundWorld)) &&
                    ReferenceEquals(current, MissionLifecycleService.NativeWorld);
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
                definitions.Add(new NativeStorageGoodRegistration(key, kind));
                try { registered.Add(key, handle); }
                catch { definitions.RemoveAt(definitions.Count - 1); throw; }
                good = handle; reason = string.Empty; return true;
            }
        }

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
                var archive = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, "APIShared.NativeStorage.v1", 1, 1,
                    new[] { "ledger", "stores" }, Capture, Loaded)
                { Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = Prepare };
                if (!VersionedSaveData.TryRegister(archive, out _, out reason)) throw new InvalidOperationException(reason);
                if (!SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "native-storage/reconcile", Tick, out reason, -1000))
                    throw new InvalidOperationException(reason);
                NativeStorageNativeRuntime.UpdateDemand(false);
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
            var current = MissionLifecycleService.NativeWorld;
            var clock = MissionLifecycleService.NativeClock;
            // Creating a blank editor map has no archive callback. Only that explicit native
            // operation may synthesize NoRecord; a missing callback on a loaded map/save must
            // never silently erase its unknown state, including after a previous blocked load.
            if (current != null && clock != null &&
                CanCreateWithoutArchive(stagedSession, current.Mission, clock.Context.StartKind) &&
                current.Mission == (MissionLifecycleService.ActiveContext?.SessionId ?? 0))
            {
                var empty = CreateEmpty();
                staged = empty; stagedSession = current.Mission; blocked = false; failure = null;
                Volatile.Write(ref boundWorld, null);
            }
            if (blocked) { reason = failure ?? "The native-storage record is blocked."; return false; }
            if (ReferenceEquals(world, boundWorld) && state != null) return true;
            if (current == null || staged == null || stagedSession == 0 || stagedSession != current.Mission ||
                stagedSession != (MissionLifecycleService.ActiveContext?.SessionId ?? 0))
            { reason = "The matching storage archive is not prepared yet."; return false; }
            if (!staged.Reconcile(readProbe, out NativeStorageState candidate, out reason)) return Block(reason);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "World replacement interrupted storage activation."; return false; }
            state = candidate;
            Volatile.Write(ref boundWorld, world);
            staged = null;
            NativeStorageNativeRuntime.UpdateDemand(state.HasRows);
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
            NativeStorageNativeRuntime.UpdateDemand(true); // Unknown occupied storage cannot safely admit more goods.
            return false;
        }

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

        internal bool TryGetSequence(string owner, out long sequence, out string reason)
        {
            sequence = 0;
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
            if (NativeStorageNativeRuntime.B6100Depth != 0)
            { reason = "Storage exchanges cannot change capacity inside a native add-goods Pre/Original/Post chain."; return false; }
            if (!Context(true, out object world, out reason) || !EnsureWorld(out world, out reason)) return false;
            if (!ReferenceEquals(expected.World, world)) { reason = "The storage observation belongs to another native world."; return false; }
            if (!Reconcile(out reason)) return false;
            NativeStorageNativeRuntime.TryRead(expected.BuildingId, out NativeStorageProbe current);
            bool applied = exchanges.TryExchange(state, good.Key, expected, current, sequence, quantityDelta, goldDelta, out status, out reason);
            if (applied) NativeStorageNativeRuntime.UpdateDemand(state.HasRows);
            return applied;
        }

        private bool Reconcile(out string reason)
        {
            object world = boundWorld;
            if (!state.Reconcile(readProbe, out NativeStorageState next, out reason)) return Block(reason);
            if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld)) { reason = "The native world changed during reconciliation."; return false; }
            state = next;
            NativeStorageNativeRuntime.UpdateDemand(next.HasRows);
            return true;
        }

        private void Tick(int ignored)
        {
            if (!ready || engine == null || !Monitor.IsEntered(engine) || !GoodsWalletNative.Instance.HasAuthorityContext) return;
            if (!EnsureWorld(out _, out _)) return;
            if (state.HasRows) Reconcile(out _);
        }

        private bool Capture(VersionedSaveWriter writer)
        {
            if (!ready) return false;
            lock (engine)
            {
                if (!EnsureWorld(out object world, out _)) return false;
                if (GoodsWalletNative.Instance.HasAuthorityContext && !Reconcile(out _)) return false;
                state.Capture(out byte[] ledger, out byte[] stores);
                if (!ReferenceEquals(world, MissionLifecycleService.NativeWorld) || blocked) return false;
                writer.Write("ledger", ledger); writer.Write("stores", stores); return true;
            }
        }

        private void Prepare(VersionedSaveLoad load)
        {
            lock (engine)
            {
                blocked = false; failure = null; staged = null;
                stagedSession = MissionLifecycleService.ActiveContext?.SessionId ?? 0;
                Volatile.Write(ref boundWorld, null);
                NativeStorageNativeRuntime.UpdateDemand(true);
                if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope)
                {
                    staged = CreateEmpty(); return;
                }
                if (load.Status != VersionedSaveLoadStatus.Restored) { Block("The native-storage save record cannot be read."); return; }
                if (!load.Entries.TryGetValue("ledger", out byte[] ledger) || !load.Entries.TryGetValue("stores", out byte[] stores) ||
                    !NativeStorageState.TryRestore(definitions, ledger, stores, out NativeStorageState next, out string reason))
                { Block("Invalid native-storage ledger/allocation pair."); throw new InvalidDataException(failure); }
                staged = next;
            }
        }

        private void Loaded(VersionedSaveLoad load)
        {
            if (blocked && load.Status == VersionedSaveLoadStatus.Restored)
                throw new InvalidDataException("Storage restoration is blocked; preserve the original archive bytes.");
        }
    }
}

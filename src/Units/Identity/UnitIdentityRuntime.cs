using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Savegames;
using APIShared.Simulation;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    // One process-owned adapter. No native hook: lifecycle, native ticks and versioned archives already
    // have shared owners. Writes use the actual engine monitor; snapshots contain no native pointers.
    internal sealed unsafe class UnitIdentityRuntime
    {
        internal static readonly UnitIdentityRuntime Instance = new UnitIdentityRuntime();
        private object engine;
        private UnitIdentityRegistry registry;
        private volatile bool ready, blocked;
        private bool attempted;
        private string failure;
        private IMissionLifecycleCapability missions;
        private readonly UnitIdentityLifetime lifetime = new UnitIdentityLifetime();
        private UnitIdentityRuntime() { }

        internal bool Register(string owner, string id, int nativeType, out UnitVariantRegistration registration, out string reason)
        {
            registration = null;
            if (!UnitIdentityRegistry.ValidText(owner) || !UnitIdentityRegistry.ValidText(id) || nativeType < 0 || nativeType > ushort.MaxValue)
            { reason = "Owner and variant IDs need 1–128 nonblank characters and a carrier in 0–65535."; return false; }
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Register troop variants on the initialized Unity startup thread."; return false; }
            if (!EnsureReady(out reason)) return false;
            lock (engine)
            {
                if (MissionLifecycleService.NativeWorld != null || missions.Current != null)
                { reason = "Register variants before the first world that uses them is loaded."; return false; }
                if (!registry.Register(owner, id, nativeType, out var definition, out reason)) return false;
                registration = new UnitVariantRegistration(this, definition); return true;
            }
        }

        private bool EnsureReady(out string reason)
        {
            if (ready) { reason = string.Empty; return true; }
            if (attempted) { reason = failure; return false; }
            if (ApiSharedRuntime.ProcessInstance.State != NativeApiState.Ready)
            { reason = "Register troop variants after APIShared native readiness."; return false; }
            attempted = true;
            try
            {
                if (ApiSharedRuntime.ProcessInstance.State != NativeApiState.Ready)
                    throw new InvalidOperationException("Register troop variants after APIShared native readiness.");
                engine = EngineMonitor.Current ?? throw new InvalidOperationException("The engine monitor is unavailable.");
                // New native read: the peasant's pending carrier, audited against the SE packed unit view.
                // The public checked-slot boundary and Vanilla death predicate validate the other reads.
                if (Marshal.SizeOf(typeof(GameUnit)) != 1168 ||
                    Marshal.OffsetOf(typeof(GameUnit), nameof(GameUnit.r_TransformIntoUnitOfType)).ToInt32() != 710 ||
                    typeof(GameUnit).GetField(nameof(GameUnit.r_TransformIntoUnitOfType))?.FieldType != typeof(eChimps))
                    throw new InvalidOperationException("The pending recruitment carrier layout is unsupported.");
                registry = new UnitIdentityRegistry(Read, () => MissionLifecycleService.NativeWorld, CanMutate,
                    (int)eChimps.CHIMP_TYPE_PEASANT, () => MissionLifecycleService.NativeWorld?.Mission ?? 0);
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out missions, out var diagnostic))
                    throw new InvalidOperationException("Unit identities need mission lifecycle: " + diagnostic?.Reason);
                if (MissionLifecycleService.NativeWorld != null || missions.Current != null)
                    throw new InvalidOperationException("Unit identity persistence must register before loading its first world.");
                var record = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, "APIShared.UnitIdentities.v1", 1, 1,
                    new[] { "marks" }, Capture, Loaded)
                { Scope = VersionedSaveScope.SaveGamesAndMaps, SimulationRelevant = true, Prepared = Prepare };
                if (!VersionedSaveData.TryRegister(record, out _, out string error)) throw new InvalidOperationException(error);
                if (!missions.TryRegisterObserver("unit-identities/lifetime", Started, Ended, Initializing, out diagnostic))
                    throw new InvalidOperationException("Unit identity lifecycle failed: " + diagnostic?.Reason);
                if (!SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "unit-identities/reconcile", Tick, out error, -1000))
                    throw new InvalidOperationException(error);
                ready = true; reason = string.Empty; return true;
            }
            catch (Exception error)
            {
                reason = failure = error.Message;
                RuntimeHealth.Failure(ApiSharedRuntime.ProcessInstance.Logger, "unit-identities/startup", true,
                    "Shared unit identities are unavailable: " + error);
                return false;
            }
        }

        private bool CanMutate()
        {
            if (!ready || blocked) return false;
            var clock = MissionLifecycleService.NativeClock;
            return UnitIdentityRegistry.AllowsMutation(Monitor.IsEntered(engine),
                clock != null && clock.Context.Mode.Kind != GameModes.GameModeKind.Unknown,
                clock?.Context.Mode.IsRealMultiplayer == true,
                SimulationMutationScope.IsTickActive, SimulationMutationScope.IsAuthenticatedChore);
        }
        internal bool Mark(UnitVariantDefinition definition, int slot, uint globalId, out string reason)
        { return registry.Mark(definition, slot, globalId, out reason); }
        internal bool Unmark(UnitVariantDefinition definition, int slot, uint globalId, out string reason)
        { return registry.Unmark(definition, slot, globalId, out reason); }
        internal bool IsCurrentWorldReady => ready && !blocked && registry.IsCurrentWorldReady && !blocked;
        internal bool TryGet(string owner, int slot, uint globalId, out UnitIdentitySnapshot identity)
        {
            identity = default;
            return ready && !blocked && registry.TryGet(owner, slot, globalId, out identity);
        }
        internal bool TryGetLive(string owner, int slot, uint globalId, out UnitIdentitySnapshot identity)
        {
            identity = default;
            return ready && !blocked && Monitor.IsEntered(engine) && registry.TryGetLive(owner, slot, globalId, out identity);
        }
        internal IReadOnlyList<UnitIdentitySnapshot> Members(UnitVariantDefinition definition) => registry.Members(definition);
        private static UnitIdentityProbe Read(int slot)
        {
            if (!UnitAccess.TryGetById(slot, out GameUnit* unit, out _) || unit == null) return default;
            return new UnitIdentityProbe { Alive = UnitAccess.IsReallyAlive(unit), GlobalId = unit->r_GlobalId,
                NativeType = (int)unit->r_UnitChimp, TransformType = (int)unit->r_TransformIntoUnitOfType };
        }
        private void Tick(int ignored)
        {
            if (!ready || blocked || !Monitor.IsEntered(engine)) return;
            registry.Reconcile();
        }
        private bool Capture(VersionedSaveWriter writer)
        {
            if (!ready || blocked) return false;
            lock (engine)
            {
                // A load may have changed admission while this save waited for the monitor.
                if (!ready || blocked) return false;
                // A save must not capture an old world's marks during a replacement load.
                object expected = MissionLifecycleService.NativeWorld;
                if (expected == null) return false;
                byte[] bytes = registry.Capture();
                if (bytes == null || !ReferenceEquals(expected, MissionLifecycleService.NativeWorld) || blocked) return false;
                writer.Write("marks", bytes); return true;
            }
        }
        private void Prepare(VersionedSaveLoad load)
        {
            lock (engine)
            {
                blocked = false; registry.Clear();
                lifetime.Prepared(MissionLifecycleService.ActiveContext?.SessionId ?? 0);
                if (load.Status == VersionedSaveLoadStatus.NoRecord || load.Status == VersionedSaveLoadStatus.OutOfScope)
                { registry.StageRestore(Array.Empty<UnitIdentityRow>(), lifetime.PreparedSession); return; }
                if (load.Status != VersionedSaveLoadStatus.Restored)
                { blocked = true; return; }
                try
                {
                    if (!load.Entries.TryGetValue("marks", out var bytes)) throw new InvalidDataException("Identity record lacks marks.");
                    registry.StageRestore(UnitIdentityRegistry.Decode(bytes), lifetime.PreparedSession);
                }
                catch { blocked = true; throw; } // VersionedSaveData keeps the complete unreadable original record.
            }
        }
        private void Loaded(VersionedSaveLoad load)
        {
            // The ordinary archive Post can precede native-world publication. NativeLoaded/Start
            // applies the decoded candidates; no live world is read during Prepared.
            if (blocked && load.Status == VersionedSaveLoadStatus.Restored)
                throw new InvalidDataException("Shared unit identities could not decode; original bytes are preserved.");
        }
        private void Initializing(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay) return;
            lock (engine)
            {
                long active = MissionLifecycleService.ActiveContext?.SessionId ?? 0;
                if (notification.Phase == MissionInitializationPhase.BeforeLoad &&
                    lifetime.ClearBeforeLoad(notification.Context.SessionId, active, MissionLifecycleService.NativeWorld != null))
                { registry.Clear(); lifetime.Clear(); blocked = false; }
                else if (notification.Phase == MissionInitializationPhase.NativeLoaded) ApplyPrepared(notification);
            }
        }
        private void Started(MissionLifecycleNotification notification) { if (!notification.IsReplay) ApplyPrepared(notification); }
        private void Ended(MissionLifecycleNotification notification)
        {
            if (notification.IsReplay) return;
            lock (engine)
                if (lifetime.ClearEnded(notification.Context.SessionId, MissionLifecycleService.ActiveContext?.SessionId ?? 0))
                { registry.Clear(); lifetime.Clear(); blocked = false; }
        }
        private void ApplyPrepared(MissionLifecycleNotification notification)
        {
            lock (engine)
            {
                if (!ready || blocked || !UnitIdentityLifetime.CanApply(notification.Context.SessionId,
                    MissionLifecycleService.ActiveContext?.SessionId ?? 0, MissionLifecycleService.NativeWorld?.Mission ?? 0)) return;
                registry.ApplyPrepared();
            }
        }
    }
}

using APIShared.Events;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;

namespace APIShared
{
    internal sealed unsafe partial class PlayerDefeatService
    {
        private readonly object sync = new object();
        private readonly ManualLogSource log;
        private readonly PlayerDefeatState state = new PlayerDefeatState();
        private readonly List<Registration> registrations = new List<Registration>();
        private Registration[] observers = Array.Empty<Registration>();
        private readonly Queue<Publication> pendingPublications = new Queue<Publication>();
        private readonly Queue<Publication> pendingTransitions = new Queue<Publication>();
        private readonly Action<PlayerLordDeathNotification> enqueueLordDied;
        private readonly Action<PlayerDefeatNotification> enqueueDefeated;
        private readonly Action<PlayerDefeatSnapshot> enqueueSnapshot;
        private bool notificationActive;
        private bool transitionNotificationActive;
        private volatile bool hasObservers;
        private int observationFailures;
        private readonly Func<long?> sessionProvider;
        private readonly Func<PlayerDefeatObservation[]> observationProvider;

        private PlayerDefeatService(ManualLogSource logger, Func<long?> sessionProvider = null,
            Func<PlayerDefeatObservation[]> observationProvider = null)
        {
            log = logger;
            this.sessionProvider = sessionProvider;
            this.observationProvider = observationProvider;
            enqueueLordDied = EnqueueLordDied;
            enqueueDefeated = EnqueueDefeated;
            enqueueSnapshot = EnqueueSnapshot;
        }

        // Script Extender and game members this service's own code binds; checked once at creation (issue #7).
        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Field(typeof(global::SHCDESE.Interop.GamePlayerResources), "r_LordUnitId", typeof(uint))
            .Field(typeof(global::SHCDESE.Interop.GamePlayerResources), "r_WinLossState", typeof(global::SHCDESE.Interop.Enums.WinLossState))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_AliveState", typeof(global::SHCDESE.Interop.Enums.AliveState))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_ControllableForPlayerId", typeof(ushort))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_CurrentHealth", typeof(uint))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_GlobalId", typeof(uint))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_IsKilledByProjectile", typeof(ushort))
            .Field(typeof(global::SHCDESE.Interop.GameUnit), "r_UnitChimp", typeof(global::SHCDESE.Interop.eChimps))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GamePlayerManagerAPI))
            .Method(typeof(global::SHCDESE.API.GamePlayerManagerAPI), "TryGetPlayerResourcesById", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.GamePlayerResources*).MakeByRefType())
            .Method(typeof(global::SHCDESE.API.GameTimeManagerAPI), "add_OnTick", typeof(void), typeof(global::System.Action<int>))
            .Method(typeof(global::SHCDESE.API.GameTimeManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GameTimeManagerAPI))
            .Method(typeof(global::SHCDESE.API.GameTimeManagerAPI), "remove_OnTick", typeof(void), typeof(global::System.Action<int>))
            .Method(typeof(global::SHCDESE.API.GameUnitManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GameUnitManagerAPI))
            .Method(typeof(global::SHCDESE.API.GameUnitManagerAPI), "IsValidId", typeof(bool), typeof(int))
            .Method(typeof(global::SHCDESE.API.GameUnitManagerAPI), "TryGetUnitById", typeof(bool), typeof(int), typeof(global::SHCDESE.Interop.GameUnit*).MakeByRefType());

        internal static bool TryCreate(
            ManualLogSource log,
            MissionLifecycleService missionLifecycle,
            out PlayerDefeatService service,
            out NativeCapabilityDiagnostic diagnostic)
        {
            service = null;
            if (!ManagedMemberContracts.TryValidate(NativeCapabilityIds.PlayerDefeat, log, out diagnostic))
                return false;
            PlayerDefeatService candidate = null;
            bool subscribed = false;
            try
            {
                if (missionLifecycle == null)
                    throw new InvalidOperationException("The managed mission lifecycle is unavailable.");
                candidate = new PlayerDefeatService(log);
                GameTimeManagerAPI.Instance.OnTick += candidate.OnTick;
                subscribed = true;
                if (!missionLifecycle.Bind("APIShared.player-defeat").TryRegisterObserver(
                        "session-reset",
                        candidate.OnMissionStarted,
                        candidate.OnMissionEnded,
                        null,
                        out NativeCapabilityDiagnostic lifecycleDiagnostic))
                {
                    throw new InvalidOperationException(
                        "Mission-lifecycle registration failed: " + lifecycleDiagnostic.Reason);
                }
                service = candidate;
                diagnostic = candidate.Available("Process-wide managed player-defeat observer is active.");
                return true;
            }
            catch (Exception ex)
            {
                if (subscribed && candidate != null && GameTimeManagerAPI.Instance != null)
                    GameTimeManagerAPI.Instance.OnTick -= candidate.OnTick;
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.PlayerDefeat,
                    NativeCapabilityState.Faulted,
                    string.Empty,
                    "Managed player-defeat initialization failed: " + ex.Message);
                return false;
            }
        }

        internal IPlayerDefeatCapability Bind(string owner) => new Binding(this, owner);

        private void OnMissionStarted(MissionLifecycleNotification notification) =>
            SynchronizeSession(notification?.Context?.SessionId);

        private void OnMissionEnded(MissionLifecycleNotification notification) => SynchronizeSession(null);

        private void SynchronizeSession(long? sessionId)
        {
            lock (sync) state.SynchronizeSession(sessionId, enqueueSnapshot);
            DrainPublications();
        }

        internal bool TryGetState(long sessionId, int playerId, out PlayerDefeatSnapshot snapshot)
        {
            lock (sync)
            {
                snapshot = null;
                if (sessionId <= 0) return false;
                // A lifecycle invalidation must take effect even before this service's
                // observer runs, and lookup never samples native memory from the caller.
                long? current = CurrentSession();
                return current.HasValue && current.Value == sessionId &&
                    state.TryGetSnapshot(sessionId, playerId, out snapshot);
            }
        }

        private long? CurrentSession() => sessionProvider != null ? sessionProvider() : MissionLifecycleService.ActiveContext?.SessionId;

        private void OnTick(int simulationTick)
        {
            // The first observed tick establishes a baseline, including late registrations.
            if (!hasObservers) return;
            OnObservedTick(simulationTick);
        }

        private void OnObservedTick(int simulationTick)
        {
            try
            {
                lock (sync)
                {
                    long? sessionId = CurrentSession();
                    state.SynchronizeSession(sessionId, enqueueSnapshot);
                    if (sessionId.HasValue)
                    {
                        PlayerDefeatObservation[] observations = observationProvider != null ? observationProvider() : CaptureObservations();
                        // An injected/provider call can synchronously replace the world.
                        if (CurrentSession() == sessionId)
                            state.Observe(simulationTick, observations, enqueueLordDied, enqueueDefeated, enqueueSnapshot);
                        else
                            state.SynchronizeSession(CurrentSession(), enqueueSnapshot);
                    }
                }
            }
            catch (Exception ex)
            {
                lock (sync)
                {
                    state.Observe(simulationTick, null, null, null, enqueueSnapshot);
                }
                // Throttled: the first failure and every 1024th repeat are logged, with the count.
                int failures = System.Threading.Interlocked.Increment(ref observationFailures);
                if (failures == 1 || (failures & 1023) == 0)
                {
                    try { NativeApiLog.Error(log, "Player-defeat observation failed closed (" + failures + " failures): " + ex); }
                    catch { }
                }
            }
            finally
            {
                // Existing transition observers retain their synchronous simulation
                // publisher. A registering thread draining state replay cannot steal
                // these callbacks or move gameplay work outside the engine context.
                DrainTransitions();
                DrainPublications();
            }
        }

        private static PlayerDefeatObservation[] CaptureObservations()
        {
            GamePlayerManagerAPI players = GamePlayerManagerAPI.Instance;
            GameUnitManagerAPI units = GameUnitManagerAPI.Instance;
            if (players == null || units == null) return null;
            var observations = new PlayerDefeatObservation[PlayerSlots.Count];
            for (int playerId = PlayerSlots.FirstPlayerId; playerId <= PlayerSlots.LastPlayerId; playerId++)
                observations[playerId - 1] = Capture(players, units, playerId);
            return observations;
        }

        private static PlayerDefeatObservation Capture(
            GamePlayerManagerAPI players,
            GameUnitManagerAPI units,
            int playerId)
        {
            if (!players.TryGetPlayerResourcesById(playerId, out GamePlayerResources* resources) ||
                resources == null)
            {
                return default;
            }

            int lordUnitId = checked((int)resources->r_LordUnitId);
            GameUnit* unit = null;
            bool livingLord = lordUnitId > 0 &&
                APIShared.UnitAccess.TryGetById(units, lordUnitId, out unit, out _) &&
                unit != null &&
                APIShared.UnitAccess.IsReallyAlive(unit) &&
                unit->r_UnitChimp == eChimps.CHIMP_TYPE_LORD &&
                unit->r_CurrentHealth > 0 &&
                unit->r_ControllableForPlayerId == playerId &&
                unit->r_GlobalId != 0;
            int lordGlobalId = livingLord ? checked((int)unit->r_GlobalId) : 0;
            return new PlayerDefeatObservation(
                true,
                resources->r_WinLossState,
                livingLord,
                livingLord ? lordUnitId : 0,
                lordGlobalId);
        }

        private NativeCapabilityDiagnostic Available(string reason) =>
            new NativeCapabilityDiagnostic(
                NativeCapabilityIds.PlayerDefeat,
                NativeCapabilityState.Available,
                string.Empty,
                reason);

        internal void TestPublish(PlayerLordDeathNotification notification)
        { lock (sync) EnqueueLordDied(notification); DrainTransitions(); }
        internal void TestPublish(PlayerDefeatNotification notification)
        { lock (sync) EnqueueDefeated(notification); DrainTransitions(); }
        internal static PlayerDefeatService TestCreate(Func<long?> session = null,
            Func<PlayerDefeatObservation[]> observations = null, ManualLogSource log = null) =>
            new PlayerDefeatService(log, session, observations);
        internal void TestTick(int tick) => OnTick(tick);

        private sealed class Binding : IPlayerDefeatStateCapability
        {
            private readonly PlayerDefeatService service;
            private readonly string owner;

            internal Binding(PlayerDefeatService service, string owner)
            {
                this.service = service;
                this.owner = owner;
            }

            public bool TryRegisterObserver(
                string registrationId,
                Action<PlayerLordDeathNotification> onLordDied,
                Action<PlayerDefeatNotification> onDefeated,
                out NativeCapabilityDiagnostic diagnostic) =>
                service.Register(owner, registrationId, onLordDied, onDefeated, out diagnostic);

            public bool TryGetObservedState(long sessionId, int playerId, out PlayerDefeatSnapshot snapshot) =>
                service.TryGetState(sessionId, playerId, out snapshot);
            public bool TryRegisterStateObserver(string registrationId, Action<PlayerDefeatSnapshot> onChanged,
                out NativeCapabilityDiagnostic diagnostic) => service.RegisterState(owner, registrationId, onChanged, out diagnostic);
        }
    }
}

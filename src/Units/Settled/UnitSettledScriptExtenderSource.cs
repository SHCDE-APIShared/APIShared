using System;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    // Owns UnitSettledEvents' Script Extender subscriptions (SE 2.14.1 sources):
    // - OnUnitCreate: Pre and Post around c_game_unit_spawn_ex; Post carries the new unit ID in
    //   ReturnValue (Detours/BulkUnitDetours.cs:1620-1646).
    // - OnUnitTransition: Pre only, from four context hooks in the purchase, worker-assignment and
    //   disband code (BulkUnitDetours.cs:752-821); no completion event exists.
    // - OnTick: raised inside the native update loop just before the date update and not while paused
    //   (BulkTimeDetours.cs:42-66, StrongholdFrameProvider.cs:101-110). SE invokes all subscribers in one
    //   try block, so an escaping exception would skip later subscribers.
    // - OnUnloadMap/OnLoadMap/OnLoadSave Pre (BulkMapLoaderDetours.cs:179, 217, 356) end the world
    //   that queued unit IDs refer to.
    // Activate runs under the tracker's registration lock when the first consumer registers. The
    // subscriptions are process roots and are never disposed. No handler may throw into SE.
    internal static unsafe class UnitSettledScriptExtenderSource
    {
        internal static readonly IUnitSettleReader Reader = new ScriptExtenderUnitReader();
        private static bool tickSubscribed;
        private static IDisposable created, transitions, unloadMap, loadMap, loadSave;

        internal static bool IsActive => tickSubscribed;

        internal static void Activate()
        {
            // The tick comes first: if SE time is unavailable, no trigger is queued without a settler.
            if (!tickSubscribed) { GameTimeManagerAPI.Instance.OnTick += OnTick; tickSubscribed = true; }
            if (unloadMap == null) unloadMap = MapLoaderR3EventHooks.OnUnloadMap.Observable.Subscribe(OnUnloadMap);
            if (loadMap == null) loadMap = MapLoaderR3EventHooks.OnLoadMap.Observable.Subscribe(OnLoadMap);
            if (loadSave == null) loadSave = MapLoaderR3EventHooks.OnLoadSave.Observable.Subscribe(OnLoadSave);
            if (created == null) created = UnitR3EventHooks.OnUnitCreate.Observable.Subscribe(OnUnitCreate);
            if (transitions == null) transitions = UnitR3EventHooks.OnUnitTransition.Observable.Subscribe(OnUnitTransition);
        }

        private static void OnTick(int simulationTick)
        {
            try { UnitSettledEvents.Tracker.Tick(simulationTick); }
            catch { }
        }

        private static void OnUnitCreate(UnitCreateEventArgs args)
        {
            try { Forward(args, UnitSettledEvents.Tracker); }
            catch { }
        }

        private static void OnUnitTransition(UnitTransitionEventArgs args)
        {
            try { Forward(args, UnitSettledEvents.Tracker); }
            catch { }
        }

        private static void OnUnloadMap(MapUnloadEventArgs args) => Discard(args);
        private static void OnLoadMap(MapLoadEventArgs args) => Discard(args);
        private static void OnLoadSave(LoadSaveGameEventArgs args) => Discard(args);

        private static void Discard(EventHookBase args)
        {
            try { if (args != null && args.Phase == EventHookPhase.Pre) UnitSettledEvents.Tracker.Discard(); }
            catch { }
        }

        // Post only: Pre has no unit yet, and a skipped spawn raises no Post.
        internal static void Forward(UnitCreateEventArgs args, UnitSettledTracker tracker)
        {
            if (args == null || args.Phase != EventHookPhase.Post) return;
            long unitId = args.ReturnValue;
            if (unitId > 0 && unitId <= int.MaxValue) tracker.Enqueue((int)unitId, UnitSettledCause.Created);
        }

        // SE raises transitions in Pre; the filter keeps a possible future Post raise from queuing twice.
        internal static void Forward(UnitTransitionEventArgs args, UnitSettledTracker tracker)
        {
            if (args == null || args.Phase != EventHookPhase.Pre) return;
            tracker.Enqueue(args.UnitId, Map(args.Source));
        }

        internal static UnitSettledCause Map(UnitTransitionSource source)
        {
            switch (source)
            {
                case UnitTransitionSource.MercenaryOutpost: return UnitSettledCause.MercenaryOutpost;
                case UnitTransitionSource.EuropeanBarracks: return UnitSettledCause.EuropeanBarracks;
                case UnitTransitionSource.Worker: return UnitSettledCause.Worker;
                case UnitTransitionSource.Disband: return UnitSettledCause.Disband;
                default: return UnitSettledCause.OtherTransition;
            }
        }

        // Interop fields read here are audited by tools/Validation/Verify-Interop.ps1.
        internal static UnitSettleProbe Probe(in GameUnit unit) => new UnitSettleProbe
        {
            Found = true,
            Alive = UnitAccess.IsReallyAlive(in unit),
            Dead = unit.r_AliveState == AliveState.None || unit.r_AliveState == AliveState.MarkedForDeletion ||
                unit.r_IsKilledByProjectile != 0,
            GlobalId = unchecked((int)unit.r_GlobalId),
            PlayerId = unit.r_ControllableForPlayerId,
            UnitType = unit.r_UnitChimp,
        };

        private sealed class ScriptExtenderUnitReader : IUnitSettleReader
        {
            public UnitSettleProbe Read(int unitId)
            {
                try
                {
                    return UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) ? Probe(in *unit) : default(UnitSettleProbe);
                }
                catch { return default(UnitSettleProbe); }
            }
        }
    }
}

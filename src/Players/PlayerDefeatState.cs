using SHCDESE.Interop.Enums;
using System;

namespace APIShared
{
    internal readonly struct PlayerDefeatObservation
    {
        internal PlayerDefeatObservation(
            bool readable,
            WinLossState winLossState,
            bool hasLivingLord,
            int lordUnitId,
            int lordGlobalId)
        {
            Readable = readable;
            WinLossState = winLossState;
            HasLivingLord = hasLivingLord;
            LordUnitId = lordUnitId;
            LordGlobalId = lordGlobalId;
        }

        internal bool Readable { get; }
        internal WinLossState WinLossState { get; }
        internal bool HasLivingLord { get; }
        internal int LordUnitId { get; }
        internal int LordGlobalId { get; }
    }

    internal sealed class PlayerDefeatState
    {
        private const int FirstPlayerId = 1;
        private const int LastPlayerId = 8;
        private readonly Slot[] slots = new Slot[LastPlayerId];
        private long sessionId;
        private bool sessionActive;

        internal void SynchronizeSession(long? activeSessionId, Action<PlayerDefeatSnapshot> changed = null)
        {
            if (!activeSessionId.HasValue || activeSessionId.Value <= 0)
            {
                Invalidate(changed);
                Reset();
                return;
            }

            if (sessionActive && sessionId == activeSessionId.Value)
                return;

            Invalidate(changed);
            Reset();
            sessionActive = true;
            sessionId = activeSessionId.Value;
        }

        // The service serializes observation, lookup and lifecycle under its managed gate.
        internal bool TryGetSnapshot(long expectedSession, int playerId, out PlayerDefeatSnapshot snapshot)
        {
            snapshot = null;
            if (!sessionActive || expectedSession != sessionId || playerId < FirstPlayerId || playerId > LastPlayerId)
                return false;
            snapshot = slots[playerId - 1]?.Snapshot;
            return snapshot != null;
        }

        internal void Observe(
            int simulationTick,
            PlayerDefeatObservation[] observations,
            Action<PlayerLordDeathNotification> lordDied,
            Action<PlayerDefeatNotification> defeated,
            Action<PlayerDefeatSnapshot> changed = null)
        {
            if (!sessionActive)
                return;

            for (int playerId = FirstPlayerId; playerId <= LastPlayerId; playerId++)
            {
                PlayerDefeatObservation observation = observations != null && observations.Length >= LastPlayerId
                    ? observations[playerId - 1] : default;
                Slot slot = slots[playerId - 1] ?? (slots[playerId - 1] = new Slot());
                if (!observation.Readable)
                {
                    UpdateSnapshot(slot, playerId, simulationTick, false, false, false, changed);
                    continue;
                }
                if (!slot.Observed)
                {
                    slot.Observed = true;
                    slot.PreviousWinLossState = observation.WinLossState;
                    if (observation.WinLossState == WinLossState.Loss)
                        slot.LordDeathPublished = true;
                    else
                        ArmLivingLord(slot, observation);
                    UpdateSnapshot(slot, playerId, simulationTick, true,
                        observation.WinLossState == WinLossState.Loss, observation.HasLivingLord, changed);
                    continue;
                }

                bool publishDefeat = !slot.DefeatPublished &&
                    slot.PreviousWinLossState != WinLossState.Loss &&
                    observation.WinLossState == WinLossState.Loss;
                if (publishDefeat)
                {
                    slot.DefeatPublished = true;
                }
                slot.PreviousWinLossState = observation.WinLossState;

                bool publishLordDeath = false;
                if (!slot.LordDeathPublished)
                {
                    if (observation.HasLivingLord)
                        ArmLivingLord(slot, observation);
                    else if (slot.LordArmed)
                    {
                        slot.LordDeathPublished = slot.LordDeathConfirmed = true;
                        slot.LordArmed = false;
                        publishLordDeath = true;
                    }
                }
                // Commit state before exposing either transition callback. Callbacks publish
                // official defeat, then lord death.
                UpdateSnapshot(slot, playerId, simulationTick, true,
                    observation.WinLossState == WinLossState.Loss, observation.HasLivingLord, changed);
                if (publishDefeat)
                    defeated?.Invoke(new PlayerDefeatNotification(sessionId, playerId, simulationTick));
                if (publishLordDeath)
                    lordDied?.Invoke(new PlayerLordDeathNotification(
                        sessionId,
                        playerId,
                        slot.LordUnitId,
                        slot.LordGlobalId,
                        simulationTick));
            }
        }

        private void UpdateSnapshot(Slot slot, int playerId, int tick, bool readable,
            bool loss, bool living, Action<PlayerDefeatSnapshot> changed)
        {
            var previous = slot.Snapshot;
            bool death = readable && slot.LordDeathConfirmed;
            if (previous != null && previous.Readable == readable && previous.OfficialLoss == loss &&
                previous.HasLivingLord == living && previous.LordDeathConfirmed == death) return;
            slot.Snapshot = new PlayerDefeatSnapshot(sessionId, playerId, tick, readable, loss, living, death);
            changed?.Invoke(slot.Snapshot);
        }

        private void Invalidate(Action<PlayerDefeatSnapshot> changed)
        {
            if (!sessionActive) return;
            for (int playerId = FirstPlayerId; playerId <= LastPlayerId; playerId++)
            {
                var slot = slots[playerId - 1];
                if (slot?.Snapshot != null)
                    UpdateSnapshot(slot, playerId, slot.Snapshot.SimulationTick, false, false, false, changed);
            }
        }

        private static void ArmLivingLord(Slot slot, PlayerDefeatObservation observation)
        {
            if (!observation.HasLivingLord)
                return;
            slot.LordArmed = true;
            slot.LordUnitId = observation.LordUnitId;
            slot.LordGlobalId = observation.LordGlobalId;
        }

        private void Reset()
        {
            sessionActive = false;
            sessionId = 0;
            Array.Clear(slots, 0, slots.Length);
        }

        private sealed class Slot
        {
            internal bool Observed;
            internal WinLossState PreviousWinLossState;
            internal bool DefeatPublished;
            internal bool LordArmed;
            internal bool LordDeathPublished;
            internal bool LordDeathConfirmed;
            internal PlayerDefeatSnapshot Snapshot;
            internal int LordUnitId;
            internal int LordGlobalId;
        }
    }
}

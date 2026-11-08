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
        private bool Register(
            string owner,
            string id,
            Action<PlayerLordDeathNotification> onLordDied,
            Action<PlayerDefeatNotification> onDefeated,
            out NativeCapabilityDiagnostic diagnostic)
        {
            if (string.IsNullOrWhiteSpace(id) || (onLordDied == null && onDefeated == null))
            {
                diagnostic = new NativeCapabilityDiagnostic(
                    NativeCapabilityIds.PlayerDefeat,
                    NativeCapabilityState.ValidationFailed,
                    string.Empty,
                    "A non-empty registration ID and at least one callback are required.");
                return false;
            }

            lock (sync)
            {
                if (registrations.Exists(registration =>
                    string.Equals(registration.Owner, owner, StringComparison.Ordinal) &&
                    string.Equals(registration.Id, id, StringComparison.Ordinal)))
                {
                    diagnostic = new NativeCapabilityDiagnostic(
                        NativeCapabilityIds.PlayerDefeat,
                        NativeCapabilityState.Conflict,
                        string.Empty,
                        "The owner already registered this player-defeat observer ID.",
                        owner);
                    return false;
                }

                registrations.Add(new Registration(owner, id, onLordDied, onDefeated, null));
                registrations.Sort(Registration.Compare);
                observers = registrations.ToArray();
                hasObservers = true;
            }
            diagnostic = Available("The process-lifetime player-defeat observer was registered.");
            return true;
        }

        private bool RegisterState(string owner, string id, Action<PlayerDefeatSnapshot> changed,
            out NativeCapabilityDiagnostic diagnostic)
        {
            if (string.IsNullOrWhiteSpace(id) || changed == null)
            {
                diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.PlayerDefeat,
                    NativeCapabilityState.ValidationFailed, string.Empty,
                    "A non-empty registration ID and a state callback are required.");
                return false;
            }
            lock (sync)
            {
                if (registrations.Exists(registration => string.Equals(registration.Owner, owner, StringComparison.Ordinal) &&
                    string.Equals(registration.Id, id, StringComparison.Ordinal)))
                {
                    diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.PlayerDefeat,
                        NativeCapabilityState.Conflict, string.Empty,
                        "The owner already registered this player-defeat observer ID.", owner);
                    return false;
                }
                state.SynchronizeSession(CurrentSession(), enqueueSnapshot);
                var registration = new Registration(owner, id, null, null, changed);
                registrations.Add(registration);
                registrations.Sort(Registration.Compare);
                observers = registrations.ToArray();
                hasObservers = true;
                var replayTarget = new[] { registration };
                long? current = CurrentSession();
                if (current.HasValue)
                    for (int player = 1; player <= LastPlayerId; player++)
                        if (state.TryGetSnapshot(current.Value, player, out var snapshot))
                            pendingPublications.Enqueue(new Publication(snapshot, replayTarget));
            }
            diagnostic = Available("The process-lifetime player-defeat state observer was registered.");
            DrainPublications();
            return true;
        }

        private void EnqueueLordDied(PlayerLordDeathNotification notification) =>
            pendingTransitions.Enqueue(new Publication(notification, observers));
        private void EnqueueDefeated(PlayerDefeatNotification notification) =>
            pendingTransitions.Enqueue(new Publication(notification, observers));
        private void EnqueueSnapshot(PlayerDefeatSnapshot snapshot) =>
            pendingPublications.Enqueue(new Publication(snapshot, observers));

        private void DrainPublications()
            => Drain(pendingPublications, ref notificationActive);

        private void DrainTransitions()
            => Drain(pendingTransitions, ref transitionNotificationActive);

        private void Drain(Queue<Publication> pending, ref bool active)
        {
            lock (sync)
            {
                if (active || pending.Count == 0)
                    return;
                active = true;
            }

            while (true)
            {
                Publication current;
                lock (sync)
                {
                    if (pending.Count == 0)
                    {
                        active = false;
                        return;
                    }
                    current = pending.Dequeue();
                }

                foreach (Registration observer in current.Observers)
                {
                    try { current.Invoke(observer); }
                    catch (Exception ex)
                    {
                        try
                        {
                            NativeApiLog.Error(
                                log,
                                $"Player-defeat observer failed: owner={observer.Owner}, registration={observer.Id}, error={ex}");
                        }
                        catch { }
                    }
                }
            }
        }

        private sealed class Registration
        {
            internal Registration(
                string owner,
                string id,
                Action<PlayerLordDeathNotification> onLordDied,
                Action<PlayerDefeatNotification> onDefeated,
                Action<PlayerDefeatSnapshot> onChanged)
            {
                Owner = owner;
                Id = id;
                OnLordDied = onLordDied;
                OnDefeated = onDefeated;
                OnChanged = onChanged;
            }

            internal string Owner { get; }
            internal string Id { get; }
            internal Action<PlayerLordDeathNotification> OnLordDied { get; }
            internal Action<PlayerDefeatNotification> OnDefeated { get; }
            internal Action<PlayerDefeatSnapshot> OnChanged { get; }

            internal static int Compare(Registration left, Registration right) =>
                RegistrationKey.Compare(new RegistrationKey(left.Owner, left.Id), new RegistrationKey(right.Owner, right.Id));
        }

        private readonly struct Publication
        {
            private readonly PlayerLordDeathNotification lordDied;
            private readonly PlayerDefeatNotification defeated;
            private readonly PlayerDefeatSnapshot snapshot;
            internal Registration[] Observers { get; }

            internal Publication(PlayerLordDeathNotification notification, Registration[] observers)
            {
                lordDied = notification;
                defeated = null;
                snapshot = null;
                Observers = observers;
            }

            internal Publication(PlayerDefeatNotification notification, Registration[] observers)
            {
                lordDied = null;
                defeated = notification;
                snapshot = null;
                Observers = observers;
            }

            internal Publication(PlayerDefeatSnapshot state, Registration[] observers)
            { snapshot = state; lordDied = null; defeated = null; Observers = observers; }

            internal void Invoke(Registration observer)
            {
                if (snapshot != null)
                    observer.OnChanged?.Invoke(snapshot);
                else if (lordDied != null)
                    observer.OnLordDied?.Invoke(lordDied);
                else
                    observer.OnDefeated?.Invoke(defeated);
            }
        }
    }
}

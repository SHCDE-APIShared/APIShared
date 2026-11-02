using System;
using System.Threading;
using System.Runtime.ExceptionServices;

namespace APIShared.PlayerControl
{
    // The single coordinator of the local control building blocks. Session, queue,
    // save identity, handler demand and input actor are only correct together: they
    // must bind to the same native world and retire in one ordered step. Callers never
    // begin or end those components individually. Every transition holds the engine
    // monitor, so the simulation sees either the complete old or the complete new state.
    // Still internal and unwired: native producer/handler adapters will drive it.
    internal sealed class LocalControlRuntime
    {
        internal const int SuspendedActor = -1;

        private readonly object engineGate;
        private readonly SinglePlayerControlSession session;
        private readonly SinglePlayerNativeQueue queue;
        private readonly ControlSaveIdentity saveIdentity;
        private readonly PlayerViewIdentityOwner identityOwner;
        private readonly Action<int> writeHandlerDemand, writeActor;
        private readonly Func<ActorZeroWrite, bool> writeActorZero;
        private ControlSaveIdentity.Lease identityLease;
        private long world;
        private int actor, realController;
        private bool actorResetPending, retirementPending;
        private bool retiring;

        private readonly struct ActorZeroWrite
        {
            internal readonly long World;
            internal readonly ControlSaveIdentity.Lease Lease;
            internal readonly bool StopDemand;
            internal ActorZeroWrite(long world, ControlSaveIdentity.Lease lease, bool stopDemand)
            { World = world; Lease = lease; StopDemand = stopDemand; }
        }

        // Original spectators can use a coordinator without positive-view protection;
        // every positive lease, when available, still belongs to its one shared core.
        internal LocalControlRuntime(PlayerViewIdentityRuntime coordinator, SinglePlayerControlSession session, SinglePlayerNativeQueue queue,
            Action<int> writeHandlerDemand, Action<int> writeActor)
        {
            if (coordinator == null) throw new ArgumentNullException(nameof(coordinator));
            engineGate = coordinator.EngineGate ?? throw new InvalidOperationException("The shared identity coordinator has no engine monitor.");
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            saveIdentity = coordinator.Identity;
            identityOwner = new PlayerViewIdentityOwner("local-control", OnIdentityFailure);
            this.writeHandlerDemand = writeHandlerDemand ?? throw new ArgumentNullException(nameof(writeHandlerDemand));
            this.writeActor = writeActor ?? throw new ArgumentNullException(nameof(writeActor));
            writeActorZero = WriteActorZero;
        }

        // The central lifecycle's native world, distinct from metadata checkpoints.
        internal static bool IsCurrentNativeWorld(long expected) =>
            expected > 0 && MissionLifecycleService.NativeWorld?.Sequence == expected;

        internal long World => Volatile.Read(ref world);
        internal int Actor => Volatile.Read(ref actor);

        // Binds session and queue to one confirmed world. Demand is published last,
        // so the native guard never dispatches into a partially bound runtime.
        internal void Begin(long confirmedWorld, long nativeManager, int realController)
        {
            lock (engineGate)
            {
                if (world != 0 || identityLease != null || actorResetPending || retirementPending)
                    throw new InvalidOperationException("Retire the previous control world and its protected identity first.");
                session.Begin(confirmedWorld, realController);
                try
                {
                    queue.Begin(confirmedWorld, nativeManager);
                    this.realController = realController;
                    Volatile.Write(ref world, confirmedWorld);
                    writeHandlerDemand(1);
                }
                catch
                {
                    // No partially bound world may survive a failed demand publication.
                    Volatile.Write(ref world, 0);
                    this.realController = 0;
                    retirementPending = true;
                    // Keep the original Begin failure. A failed rollback leaves an explicit
                    // cleanup debt which prevents another Begin until Retire succeeds.
                    try { Retire(out _); } catch { }
                    throw;
                }
            }
        }

        // Stop dispatch, restore only this owner's exact identity lease, then return input
        // to Vanilla. A failed live-world restoration keeps input suspended and the lease
        // available for another Retire attempt. Queue/grant cleanup still runs on failures.
        // Future native wiring must first prove that queued foreign native Chore slots
        // have drained or been reset before demand zero is enabled. queue.End retires
        // managed sidecars only; these fixtures do not establish that native prerequisite.
        internal bool Retire(out string reason)
        {
            reason = null;
            lock (engineGate)
            {
                if (world == 0 && identityLease == null && !actorResetPending && !retirementPending) return true;
                if (retiring) { reason = "Native input retirement is already in progress."; return false; }
                retiring = true;
                try
                {
                    retirementPending = true;
                    Volatile.Write(ref world, 0);
                    realController = 0;
                    bool saveRetired = true;
                    Exception first = null;
                    bool suspended = true;
                    try
                    {
                        bool otherOwner = saveIdentity?.CurrentLease != null && !OwnsIdentityLease();
                        if (otherOwner)
                        {
                            suspended = false;
                            reason = "Another role owns native input; stale retirement cannot change its guard or actor.";
                        }
                        else
                        {
                            if (actor > 0 || actorResetPending || OwnsIdentityLease()) EnsureSuspended();
                            // The actor adapter may reenter role management. Never turn off
                            // the replacement owner's guard after that callback returns.
                            if (saveIdentity?.CurrentLease != null && !OwnsIdentityLease())
                            { suspended = false; reason = "The native input owner changed during suspension."; }
                        }
                    }
                    catch (Exception error)
                    {
                        suspended = false;
                        reason = "Native input suspension was not confirmed: " + error.Message;
                        if (first == null) first = error;
                    }
                    saveRetired = false;
                    if (suspended)
                    {
                        try { saveRetired = RetireSave(out reason); }
                        catch (Exception error)
                        {
                            reason = "The protected identity could not be retired: " + error.Message;
                            if (first == null) first = error;
                        }
                    }
                    bool inputRetired = false;
                    if (saveRetired)
                    {
                        try { inputRetired = TryWriteActorZero(0, true, out reason); }
                        catch (Exception error)
                        { if (first == null) first = error; }
                    }
                    try { queue.End(); } catch (Exception error) { if (first == null) first = error; }
                    try { session.End(); } catch (Exception error) { if (first == null) first = error; }
                    retirementPending = first != null || !saveRetired || !inputRetired || actorResetPending;
                    if (first != null) ExceptionDispatchInfo.Capture(first).Throw();
                    return saveRetired && inputRetired;
                }
                finally { retiring = false; }
            }
        }

        private bool RetireSave(out string reason)
        {
            reason = null;
            var captured = identityLease;
            if (saveIdentity == null || captured == null) return true;
            // Another owner may already hold the same shared core in a later world. A stale
            // local handle is not authority to inspect, restore or release that owner's lease.
            if (!saveIdentity.IsOwned(identityOwner, captured))
            { identityLease = null; return true; }
            if (!saveIdentity.IsCurrent(identityOwner, captured))
            { reason = "The current role cannot release an invalidated native world's identity."; return false; }
            if (actor != SuspendedActor || actorResetPending)
            { reason = "Confirm native input suspension before releasing the participant identity."; return false; }
            if (!saveIdentity.TryRestoreAndRelease(identityOwner, captured, out reason)) return false;
            if (!ReferenceEquals(identityLease, captured) || world != 0 || saveIdentity.CurrentLease != null)
            { reason = "The protected role changed during identity restoration."; return false; }
            identityLease = null;
            return true;
        }

        // 0 = Vanilla input, -1 = suspended while a view is restored, 1..8 = the controlled view.
        internal bool TrySetActor(int value, out string reason)
        {
            reason = null;
            lock (engineGate)
            {
                if (value != 0 && value != SuspendedActor && (value < 1 || value > 8))
                { reason = "Invalid input actor."; return false; }
                if (actorResetPending || retirementPending)
                { reason = "Complete pending native input retirement before changing the actor."; return false; }
                if (value != 0 && world == 0) { reason = "No bound control world."; return false; }
                if (world == 0 && identityLease != null)
                { reason = "Restore the retained participant identity before enabling Vanilla input."; return false; }
                if (value > 0 && realController > 0 && value != realController && identityLease == null)
                { reason = "A foreign human-player input actor requires this runtime's protected participant lease."; return false; }
                if (value == 0 && saveIdentity?.CurrentLease != null && !OwnsIdentityLease())
                { reason = "Another role owns the view; the previous actor cannot publish Vanilla input."; return false; }
                if (value == 0 && identityLease != null)
                {
                    var captured = identityLease;
                    long expectedWorld = world;
                    if (saveIdentity == null || !saveIdentity.IsCurrent(identityOwner, captured))
                    { reason = "Only the current owning role may return its participant view to Vanilla input."; return false; }
                    EnsureSuspended();
                    if (world != expectedWorld || !ReferenceEquals(identityLease, captured) ||
                        !saveIdentity.IsCurrent(identityOwner, captured))
                    { reason = "The protected role changed while suspending native input."; return false; }
                    if (!saveIdentity.TryRestoreAndRelease(identityOwner, captured, out reason))
                        return false;
                    if (world != expectedWorld || !ReferenceEquals(identityLease, captured) || saveIdentity.CurrentLease != null)
                    { reason = "The protected role changed during identity restoration."; return false; }
                    identityLease = null;
                }
                if (identityLease != null && value != SuspendedActor && saveIdentity != null &&
                    (!saveIdentity.IsCurrent(identityOwner, identityLease) || saveIdentity.Failure != null))
                { reason = "The owned participant identity is not current and healthy; input remains suspended."; return false; }
                if (value == 0)
                {
                    if (actor > 0) EnsureSuspended();
                    return TryWriteActorZero(world, false, out reason);
                }
                try { writeActor(value); Volatile.Write(ref actor, value); }
                catch { actorResetPending = true; throw; }
                return true;
            }
        }

        // The suspended actor writes 0 into the native view, which the protected writer
        // must refuse; never acquire a save lease in that state.
        internal bool TryBindSave(int actualHuman, out long token, out string reason)
        {
            token = 0; reason = null;
            if (saveIdentity == null) { reason = "Save identity protection is unavailable."; return false; }
            lock (engineGate)
            {
                if (world == 0 || actor == SuspendedActor)
                { reason = "No stable control world for a protected save identity."; return false; }
                if (actualHuman < 1 || actualHuman > 8 || actualHuman != realController)
                { reason = "The requested save identity is not this session's real positive controller."; return false; }
                if (identityLease != null)
                {
                    if (!saveIdentity.IsOwned(identityOwner, identityLease) || !saveIdentity.IsCurrent(identityOwner, identityLease) ||
                        saveIdentity.Failure != null || identityLease.Human != actualHuman)
                    { reason = "The retained identity lease is not current, healthy and owned by this runtime."; return false; }
                    token = identityLease.Token;
                    return true;
                }
                if (!saveIdentity.TryBind(identityOwner, world, actualHuman, out var acquired, out reason)) return false;
                identityLease = acquired;
                token = acquired.Token;
                return true;
            }
        }

        private void OnIdentityFailure(ControlSaveIdentity.FailureRecord failure)
        {
            lock (engineGate)
            {
                var captured = identityLease;
                if (retiring || failure == null || captured == null || !ReferenceEquals(failure.Lease, captured) ||
                    saveIdentity == null || !ReferenceEquals(saveIdentity.LastFailure?.Lease, captured) ||
                    !saveIdentity.IsCurrent(identityOwner, captured)) return;
                // The root dispatches this on Unity after leaving the native critical section.
                // A stale queued error cannot suspend a newer owner or its replacement lease.
                SuspendActor();
            }
        }

        private bool OwnsIdentityLease() => saveIdentity != null && identityLease != null &&
            saveIdentity.IsOwned(identityOwner, identityLease);

        private void EnsureSuspended()
        {
            if (actor != SuspendedActor || actorResetPending) SuspendActor();
        }

        private bool TryWriteActorZero(long expectedWorld, bool stopDemand, out string reason)
        {
            reason = null;
            var request = new ActorZeroWrite(expectedWorld, identityLease, stopDemand);
            if (!ActorZeroContextCurrent(request))
            { reason = "Another role owns the native view; no stale Vanilla-input write is allowed."; return false; }
            if (saveIdentity == null) return WriteActorZero(request);
            // Even an unbound core serializes ownership acquisition for this callback.
            // A reentrant native adapter cannot acquire another role before storing actor zero.
            bool completed = saveIdentity.TryRunWriter(request, writeActorZero, out bool written, out reason);
            if (completed && !written) reason = "The role changed while publishing Vanilla input.";
            return completed && written;
        }

        private bool ActorZeroContextCurrent(ActorZeroWrite request) => world == request.World &&
            ReferenceEquals(identityLease, request.Lease) && (saveIdentity == null || saveIdentity.CurrentLease == null);

        private bool WriteActorZero(ActorZeroWrite request)
        {
            if (!ActorZeroContextCurrent(request)) return false;
            // Retirement stops the guard only after own-view recovery. The same unbound
            // core scope prevents another owner entering through the demand-write callback.
            if (request.StopDemand)
            {
                writeHandlerDemand(0);
                if (!ActorZeroContextCurrent(request)) return false;
            }
            try
            {
                writeActor(0);
                if (!ActorZeroContextCurrent(request)) return false;
                Volatile.Write(ref actor, 0);
                actorResetPending = false;
                return true;
            }
            catch { actorResetPending = true; throw; }
        }

        private void SuspendActor()
        {
            try
            {
                writeActor(SuspendedActor);
                Volatile.Write(ref actor, SuspendedActor);
                actorResetPending = false;
            }
            catch { actorResetPending = true; throw; }
        }
    }
}

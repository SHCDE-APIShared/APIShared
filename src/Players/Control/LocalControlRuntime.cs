using System;
using System.Threading;
using BepInEx.Logging;

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
        private readonly Action<int> writeHandlerDemand, writeActor;
        private long world;
        private int actor;

        // saveIdentity is optional: original spectators do not need view-lease protection.
        internal LocalControlRuntime(object engineGate, SinglePlayerControlSession session, SinglePlayerNativeQueue queue,
            ControlSaveIdentity saveIdentity, Action<int> writeHandlerDemand, Action<int> writeActor)
        {
            this.engineGate = engineGate ?? throw new ArgumentNullException(nameof(engineGate));
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
            this.saveIdentity = saveIdentity;
            this.writeHandlerDemand = writeHandlerDemand ?? throw new ArgumentNullException(nameof(writeHandlerDemand));
            this.writeActor = writeActor ?? throw new ArgumentNullException(nameof(writeActor));
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
                if (world != 0) throw new InvalidOperationException("Retire the previous control world first.");
                session.Begin(confirmedWorld, realController);
                try
                {
                    queue.Begin(confirmedWorld, nativeManager);
                    Volatile.Write(ref world, confirmedWorld);
                    writeHandlerDemand(1);
                }
                catch
                {
                    // No partially bound world may survive a failed demand publication.
                    Volatile.Write(ref world, 0);
                    try { writeHandlerDemand(0); }
                    finally { try { queue.End(); } finally { session.End(); } }
                    throw;
                }
            }
        }

        // Called for world invalidation, mode loss and replacement. Order: stop native
        // dispatch, return input to Vanilla, retire queued sidecars and grants, then the
        // ended world's save binding. Returns false if a save binding could not be retired.
        internal bool Retire(out string reason)
        {
            reason = null;
            lock (engineGate)
            {
                long ended = world;
                Volatile.Write(ref world, 0);
                bool saveRetired = true;
                // Every step runs even if an earlier native write throws; the first error propagates.
                try { writeHandlerDemand(0); }
                finally
                {
                    try { writeActor(0); }
                    finally
                    {
                        Volatile.Write(ref actor, 0);
                        try { queue.End(); }
                        finally
                        {
                            try { session.End(); }
                            finally { saveRetired = RetireSave(ended); }
                        }
                    }
                }
                // A still-current world (mode loss) keeps its lease: its view adapter must
                // restore and release it. The protected writer stays fail-closed meanwhile.
                if (!saveRetired) reason = "The protected save identity was not retired; its world is still current or the binding changed.";
                return saveRetired;
            }
        }

        private bool RetireSave(long ended) =>
            saveIdentity == null || ended == 0 || saveIdentity.ProtectedHumanId == 0 || saveIdentity.RetireEndedWorld(ended);

        // 0 = Vanilla input, -1 = suspended while a view is restored, 1..8 = the controlled view.
        internal bool TrySetActor(int value, out string reason)
        {
            reason = null;
            lock (engineGate)
            {
                if (value != 0 && value != SuspendedActor && (value < 1 || value > 8))
                { reason = "Invalid input actor."; return false; }
                if (value != 0 && world == 0) { reason = "No bound control world."; return false; }
                writeActor(value); Volatile.Write(ref actor, value);
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
                return saveIdentity.TryBind(world, actualHuman, out token, out reason);
            }
        }

        // Unity-side drain of failures recorded inside the native writer boundary.
        internal static void ReportDeferredFailures(ManualLogSource log)
        {
            string failure = ControlSaveNative.TakeFailure();
            if (failure != null) NativeApiLog.Error(log, "PLAYER_CONTROL_SAVE: " + failure);
        }
    }
}

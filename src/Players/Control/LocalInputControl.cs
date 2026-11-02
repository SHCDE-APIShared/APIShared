using System;
using System.Threading;

namespace APIShared.PlayerControl
{
    // The only coordinator of the spectator input actor (full Vanilla input of one chosen
    // player, no rights categories). The actor is bound to one native world; every
    // transition runs under the engine monitor, which also serializes DLL_RunTick, so an
    // input pass sees either the old or the new actor. PlayerPerspectiveAPI couples it to
    // the view transaction: the bridge admits input only while actor == native view.
    // Independent of the rights/grant session (WIP), which must reuse this owner later.
    internal sealed class LocalInputControl
    {
        internal const int SuspendedActor = -1;
        private readonly Action<int> writeActor;
        private long world;
        private int player;
        private bool suspended;

        internal LocalInputControl(Action<int> writeActor)
        { this.writeActor = writeActor ?? throw new ArgumentNullException(nameof(writeActor)); }

        internal int Player => Volatile.Read(ref player);
        internal long World => Volatile.Read(ref world);
        internal bool IsSuspended => suspended;
        internal bool IsEngaged => player > 0 || suspended;
        internal bool IsActiveIn(long currentWorld) => currentWorld > 0 && player > 0 && world == currentWorld;

        // Publish only after the native view was confirmed as playerId in confirmedWorld.
        internal void Activate(long confirmedWorld, int playerId)
        {
            if (confirmedWorld <= 0 || playerId < 1 || playerId > 8)
                throw new ArgumentOutOfRangeException(nameof(playerId), "A confirmed world and player 1..8 are required.");
            Write(playerId, confirmedWorld, playerId, false);
        }

        // Fail-closed state when the view could not be confirmed: input stays blocked (view 0)
        // until Release, exactly like an ordinary spectator.
        internal void Suspend() => Write(SuspendedActor, world, 0, true);

        // Vanilla spectator input again; also used for an ended world. The actor cell is
        // process-owned, so this never writes into the replacement world's game state.
        internal void Release() => Write(0, 0, 0, false);

        private void Write(int actor, long boundWorld, int boundPlayer, bool isSuspended)
        {
            try { writeActor(actor); }
            catch
            {
                // The cell's content is unknown: fail closed to blocked input if possible.
                if (actor != SuspendedActor) try { writeActor(SuspendedActor); } catch { }
                Volatile.Write(ref player, 0); suspended = true;
                throw;
            }
            Volatile.Write(ref world, boundWorld);
            Volatile.Write(ref player, boundPlayer);
            suspended = isSuspended;
        }
    }
}

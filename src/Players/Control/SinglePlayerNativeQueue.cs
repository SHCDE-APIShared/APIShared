using System;

namespace APIShared.PlayerControl
{
    // Identity of a native queue ENTRY, not a network envelope. Scheduled tick is
    // deliberately absent: the native barrier may change it after local enqueue.
    // The adapter supplies the confirmed world and native source independently of
    // the session's actual controller (zero for an original offline spectator).
    internal readonly struct ControlNativeQueueEntry : IEquatable<ControlNativeQueueEntry>
    {
        internal readonly long World, Manager;
        internal readonly int Slot, CommandId, SourceOwner;
        internal readonly byte Opcode;
        internal ControlNativeQueueEntry(long world, long manager, int slot, int commandId, int sourceOwner, byte opcode)
        { World = world; Manager = manager; Slot = slot; CommandId = commandId; SourceOwner = sourceOwner; Opcode = opcode; }
        internal bool IsValid => World > 0 && Manager > 0 && Slot >= 0 && Slot < SinglePlayerControlSession.MaximumPendingCommands &&
            CommandId >= 0 && SourceOwner >= 1 && SourceOwner <= 8 && Opcode <= 120;
        public bool Equals(ControlNativeQueueEntry other) => World == other.World && Manager == other.Manager &&
            Slot == other.Slot && CommandId == other.CommandId && SourceOwner == other.SourceOwner && Opcode == other.Opcode;
        public override bool Equals(object obj) => obj is ControlNativeQueueEntry other && Equals(other);
        public override int GetHashCode() => unchecked((((World.GetHashCode() * 397 ^ Manager.GetHashCode()) * 397 ^ Slot) * 397 ^
            CommandId) * 397 ^ SourceOwner * 127 ^ Opcode);
    }

    internal enum ControlNativeQueueResult { NotTracked, Rejected, Executed }

    // Local native sidecars only: no serializer, peer roster or mixed-owner
    // backend. A rejected/consumed entry remains a tombstone until native slot
    // reuse or world retirement. Revoking a session ticket therefore cannot turn
    // its still-queued native command back into an unguarded Vanilla command.
    // All calls require the engine monitor. Cleanup is a trusted adapter which
    // validates its captured native world/identity before releasing a reservation;
    // it must never write into a replaced world or a recycled group slot.
    internal sealed class SinglePlayerNativeQueue
    {
        private sealed class Pending
        {
            internal readonly ControlNativeQueueEntry Entry;
            internal readonly byte[] Body;
            internal long Ticket;
            internal string Reason;
            internal Action ReleaseReservation;
            internal Pending(ControlNativeQueueEntry entry, ReadOnlySpan<byte> body, Action release)
            { Entry = entry; Body = body.ToArray(); ReleaseReservation = release; }
        }

        private readonly SinglePlayerControlSession session;
        private readonly Pending[] slots = new Pending[SinglePlayerControlSession.MaximumPendingCommands];
        private long world, manager, lastWorld;
        private bool retiring;
        private string failure;

        internal SinglePlayerNativeQueue(SinglePlayerControlSession session)
        { this.session = session ?? throw new ArgumentNullException(nameof(session)); }
        internal string Failure => failure;

        internal void Begin(long confirmedWorld, long nativeManager)
        {
            if (retiring || world != 0 || confirmedWorld <= lastWorld || nativeManager <= 0 || !session.IsReady(confirmedWorld))
                throw new InvalidOperationException("A confirmed, independent local control session is required.");
            world = lastWorld = confirmedWorld; manager = nativeManager; failure = null;
        }

        internal void End()
        {
            if (retiring) return;
            // Make reentrant callbacks unable to bind more entries to this world.
            world = manager = 0; retiring = true;
            try
            {
                for (int slot = 0; slot < slots.Length; slot++)
                {
                    var item = slots[slot]; slots[slot] = null;
                    if (item != null) Retire(item);
                }
            }
            finally { retiring = false; }
        }

        internal bool Capture(ControlNativeQueueEntry entry, ReadOnlySpan<byte> nativeBody,
            string registration, long revision, ControlCommandBatch batch, Action releaseReservation, out string reason)
        {
            reason = null;
            // A non-current entry is rejected by Run anyway; every other failure below
            // must leave a tombstone, including an oversized native body.
            if (!Current(entry)) { reason = "No valid current native control entry."; return false; }
            var previous = slots[entry.Slot];
            if (previous != null && previous.Entry.Equals(entry))
            {
                // No replacement/retry of a previously consumed identity, even
                // when a caller supplies another payload for the same command ID.
                Retire(previous);
                previous.Reason = reason = "Native control entry was already captured.";
                return false;
            }
            bool oversized = nativeBody.Length > ControlCommandBatch.MaximumCommandBytes;
            // An oversized body is never admitted; its tombstone keeps no copy of it.
            var item = new Pending(entry, oversized ? ReadOnlySpan<byte>.Empty : nativeBody, releaseReservation);
            // Publish the rejection tombstone BEFORE a validator can fail/reenter.
            slots[entry.Slot] = item;
            if (previous != null) Retire(previous);
            item.Reason = "Native control input has not been admitted.";
            if (failure != null) { item.Reason = reason = failure; Release(item); return false; }
            if (oversized) { item.Reason = reason = "Native control body exceeds the command limit."; Release(item); return false; }
            try
            {
                if (!MatchesCommand(entry, nativeBody, batch) ||
                    !session.TryCapture(entry.World, registration, revision, batch, out long ticket, out reason))
                {
                    item.Reason = reason = reason ?? "Native entry and validated command disagree.";
                    Release(item); return false;
                }
                // Trusted validation must not reentrantly replace this slot/world.
                if (!Current(entry) || !ReferenceEquals(slots[entry.Slot], item))
                {
                    session.CancelCaptured(ticket); Release(item);
                    item.Reason = reason = "Native control entry changed during admission."; return false;
                }
                item.Ticket = ticket; item.Reason = null;
                return true;
            }
            catch
            {
                item.Reason = "Native control admission failed."; Release(item); throw;
            }
        }

        internal ControlNativeQueueResult Run(ControlNativeQueueEntry entry, ReadOnlySpan<byte> nativeBody,
            Action<ControlExecutionPlan> execute, out string reason)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            reason = null;
            if (!Active(entry)) { reason = "Native control world changed."; return ControlNativeQueueResult.Rejected; }
            if (failure != null) { reason = failure; return ControlNativeQueueResult.Rejected; }
            var item = slots[entry.Slot];
            if (item == null) return ControlNativeQueueResult.NotTracked;
            if (!item.Entry.Equals(entry))
            {
                // Actual slot reuse is not a delegated command. Retire only the
                // old sidecar; a stale world/manager never reaches this branch.
                slots[entry.Slot] = null; Retire(item);
                if (!Active(entry) || failure != null)
                { reason = failure ?? "Native control world changed during retirement."; return ControlNativeQueueResult.Rejected; }
                return ControlNativeQueueResult.NotTracked;
            }
            long ticket = item.Ticket; item.Ticket = 0;
            if (ticket == 0) { reason = item.Reason ?? "Native control command was already consumed."; return ControlNativeQueueResult.Rejected; }
            item.Reason = "Native control command was already consumed.";
            try
            {
                if (failure != null || !nativeBody.SequenceEqual(item.Body))
                {
                    session.CancelCaptured(ticket);
                    item.Reason = reason = failure ?? "Native control payload changed after admission.";
                    return ControlNativeQueueResult.Rejected;
                }
                if (!session.TryExecuteCaptured(entry.World, ticket, execute, out reason))
                { item.Reason = reason; return ControlNativeQueueResult.Rejected; }
                return ControlNativeQueueResult.Executed;
            }
            finally { Release(item); }
        }

        private bool Current(ControlNativeQueueEntry entry) => world != 0 && entry.IsValid && entry.World == world && entry.Manager == manager;
        // NotTracked allows the adapter to follow Vanilla. Never return it merely
        // because an entry lacks a sidecar after its local session became invalid.
        private bool Active(ControlNativeQueueEntry entry) => Current(entry) && session.IsReady(entry.World) && Current(entry);
        private static bool MatchesCommand(ControlNativeQueueEntry entry, ReadOnlySpan<byte> nativeBody, ControlCommandBatch batch)
        {
            if (batch == null || batch.Commands.Count != 1) return false;
            var command = batch.Commands[0];
            if (command.Owner != entry.SourceOwner || command.Opcode != entry.Opcode || command.Length < nativeBody.Length) return false;
            // The native rules may append audited identity metadata; they still
            // validate that suffix. The actual serialized body must be the exact
            // prefix of the command that passed their permission checks.
            for (int i = 0; i < nativeBody.Length; i++) if (command.Read(i) != nativeBody[i]) return false;
            return true;
        }
        private void Retire(Pending item)
        {
            long ticket = item.Ticket; item.Ticket = 0;
            if (ticket != 0) session.CancelCaptured(ticket);
            Release(item);
        }
        private void Release(Pending item)
        {
            var release = item.ReleaseReservation; item.ReleaseReservation = null;
            if (release == null) return;
            try { release(); }
            catch (Exception error)
            {
                // Keep old tombstones effective but stop this local queue from
                // admitting more work. Optional MP/mixed providers are untouched.
                if (failure == null) failure = "Local native reservation release failed: " + error.GetType().Name + ": " + error.Message;
            }
        }
    }
}

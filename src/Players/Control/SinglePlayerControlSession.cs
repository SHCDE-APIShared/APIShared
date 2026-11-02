using System;
using System.Collections.Generic;

namespace APIShared.PlayerControl
{
    // WIP (see docs/PLAYER_CONTROL.md): rights-gated local control; unwired.
    // This path deliberately has no transport, fingerprint, fragment assembler,
    // peer agreement or mixed-selection backend. An optional network provider can
    // fail even during construction without preventing local single-owner control.
    // Call only under the engine/lifecycle serialization contract. The world-mode
    // predicate is a trusted runtime adapter, never a consumer setting or packet.
    internal sealed class SinglePlayerControlSession
    {
        private readonly HashSet<string> registrations = new HashSet<string>(StringComparer.Ordinal);
        private readonly ControlGrantTable grants = new ControlGrantTable();
        private readonly ControlExecutionGate gate;
        private readonly Func<bool> isSingleplayerWorld;
        private readonly Dictionary<long, PendingCommand> pending = new Dictionary<long, PendingCommand>();
        private readonly List<long> revoked = new List<long>();
        // A local queue budget, independent of packet/chunk limits. The native
        // adapter must retain this ticket beside the queued command, not in a packet.
        internal const int MaximumPendingCommands = 500;
        private long epoch;
        private long lastEpoch;
        private long nextTicket;
        private int actualController;
        private bool worldLost;

        private sealed class PendingCommand
        {
            internal readonly string Registration;
            internal readonly long Revision;
            internal readonly ControlCommandBatch Batch;
            internal PendingCommand(string registration, long revision, ControlCommandBatch batch)
            { Registration = registration; Revision = revision; Batch = batch; }
        }

        internal SinglePlayerControlSession(IControlCommandRules rules, ResolveControlObject resolve, Func<bool> isSingleplayerWorld)
        {
            this.isSingleplayerWorld = isSingleplayerWorld ?? throw new ArgumentNullException(nameof(isSingleplayerWorld));
            gate = new ControlExecutionGate(grants, rules, resolve, IsReady);
        }

        internal bool Register(string ownerGuid, string registrationId, out string key)
        {
            key = null;
            if (epoch != 0 || registrations.Count >= ControlRegistrationKeys.MaximumRegistrations ||
                !ControlRegistrationKeys.TryCreate(ownerGuid, registrationId, out var candidate) || !registrations.Add(candidate)) return false;
            key = candidate; return true;
        }

        internal void Begin(long newEpoch, int realController)
        {
            if (epoch != 0 || newEpoch <= lastEpoch || registrations.Count == 0 || realController < 0 || realController > 8 || !isSingleplayerWorld())
                throw new InvalidOperationException("A confirmed local singleplayer/editor context is required.");
            actualController = realController;
            grants.Reset(newEpoch, realController, false); epoch = lastEpoch = newEpoch;
        }

        internal void End()
        {
            epoch = 0; actualController = 0; worldLost = false; grants.Clear(); pending.Clear(); revoked.Clear();
            // Do not recycle ticket identities when a mission is replaced. A stale
            // native sidecar must never select a command from the next mission.
        }
        internal bool IsReady(long expectedEpoch)
        {
            if (epoch <= 0 || epoch != expectedEpoch || worldLost) return false;
            bool current;
            try { current = isSingleplayerWorld(); }
            catch { if (epoch == expectedEpoch) LoseWorld(); throw; }
            // A trusted adapter can cross a synchronous lifecycle notification.
            // Its old read must neither admit the old epoch nor retire a newer
            // session that was established reentrantly by that notification.
            if (epoch != expectedEpoch || worldLost) return false;
            if (current) return true;
            // Losing the trusted world context retires this session's permissions,
            // not just its current frame. A later true predicate cannot resurrect
            // inputs captured before a load, replacement or mode transition. Only
            // End followed by a confirmed Begin can establish another context.
            LoseWorld();
            return false;
        }

        private void LoseWorld()
        {
            worldLost = true;
            grants.Clear(); pending.Clear(); revoked.Clear();
        }

        internal bool SetGrant(long expectedEpoch, string registration, int ownerMask, PlayerControlRights rights, long revision)
        {
            if (!IsReady(expectedEpoch) || !Known(registration) ||
                !(rights == PlayerControlRights.None ? ownerMask == 0 : ownerMask > 0 && (ownerMask & (ownerMask - 1)) == 0) ||
                !grants.Apply(expectedEpoch, actualController, registration, actualController, ownerMask, rights, revision)) return false;
            // Grant replacement retires only this consumer's pending inputs. Other
            // consumers and the optional network/mixed providers are not consulted.
            revoked.Clear();
            foreach (var entry in pending)
                if (StringComparer.Ordinal.Equals(entry.Value.Registration, registration)) revoked.Add(entry.Key);
            foreach (long ticket in revoked) pending.Remove(ticket);
            revoked.Clear();
            return true;
        }

        internal bool TryPreflight(long expectedEpoch, string registration, long revision, ControlCommandBatch batch, out string reason)
        {
            reason = null;
            if (!Known(registration)) { reason = "Unknown control registration."; return false; }
            return gate.TryPrepare(expectedEpoch, registration, revision, actualController, batch, out _, out reason);
        }

        internal bool TryExecute(long expectedEpoch, string registration, long revision, ControlCommandBatch batch,
            Action<ControlExecutionPlan> execute, out string reason)
        {
            reason = null;
            if (!Known(registration)) { reason = "Unknown control registration."; return false; }
            return gate.TryExecute(expectedEpoch, registration, revision, actualController, batch, execute, out reason);
        }

        internal bool TryCapture(long expectedEpoch, string registration, long revision, ControlCommandBatch batch,
            out long ticket, out string reason)
        {
            ticket = 0;
            if (pending.Count >= MaximumPendingCommands || nextTicket == long.MaxValue)
            { reason = "The local control queue is full."; return false; }
            if (!TryPreflight(expectedEpoch, registration, revision, batch, out reason)) return false;
            ticket = ++nextTicket;
            pending.Add(ticket, new PendingCommand(registration, revision, batch));
            return true;
        }

        internal bool TryExecuteCaptured(long expectedEpoch, long ticket, Action<ControlExecutionPlan> execute, out string reason)
        {
            if (execute == null) throw new ArgumentNullException(nameof(execute));
            reason = null;
            // Check session before consuming: stale callbacks cannot discard a new
            // session's work. Consume before revalidation/backend invocation, so an
            // exception or recursive callback cannot execute the same command twice.
            if (!IsReady(expectedEpoch) || ticket <= 0 || !pending.TryGetValue(ticket, out var command))
            { reason = "No current local control command."; return false; }
            pending.Remove(ticket);
            return TryExecute(expectedEpoch, command.Registration, command.Revision, command.Batch, execute, out reason);
        }

        internal bool CancelCaptured(long ticket) => ticket > 0 && pending.Remove(ticket);
        internal int PendingCount => pending.Count;

        private bool Known(string registration) => registration != null && registrations.Contains(registration);
    }
}

using System;
using System.Collections.Generic;

namespace APIShared.PlayerControl
{
    // WIP (see docs/PLAYER_CONTROL.md): multiplayer control protocol; unwired.
    // Protocol agreement is synchronized state, not a local feature setting. The
    // lifecycle/network adapter supplies a verified roster and the host's epoch;
    // process-local MissionContext.SessionId must never be used as a wire epoch.
    internal sealed class ControlProtocolAgreement
    {
        private byte[] expected;
        private long epoch;
        private int participants, acknowledged, incompatible;

        internal void Reset(long epoch, int participantMask, byte[] protocolFingerprint)
        {
            if (epoch <= 0 || participantMask == 0 || (participantMask & ~0x1fe) != 0 ||
                protocolFingerprint == null || protocolFingerprint.Length != 32) throw new ArgumentException("Invalid control protocol session.");
            this.epoch = epoch; participants = participantMask;
            expected = (byte[])protocolFingerprint.Clone(); acknowledged = 0; incompatible = 0;
        }

        internal void Clear() { epoch = 0; participants = acknowledged = incompatible = 0; expected = null; }

        internal bool Acknowledge(long expectedEpoch, int authenticatedIssuer, byte[] fingerprint)
        {
            if (epoch == 0 || epoch != expectedEpoch || authenticatedIssuer < 1 || authenticatedIssuer > 8 ||
                (participants & (1 << authenticatedIssuer)) == 0) return false;
            if (!EqualFingerprint(expected, fingerprint))
            { incompatible |= 1 << authenticatedIssuer; return false; }
            // An incompatible participant cannot revive an epoch by later echoing
            // another peer's fingerprint. A fresh host epoch is required.
            if ((incompatible & (1 << authenticatedIssuer)) != 0) return false;
            acknowledged |= 1 << authenticatedIssuer;
            return true;
        }

        internal bool IsReady(long expectedEpoch) => epoch > 0 && epoch == expectedEpoch &&
            incompatible == 0 && acknowledged == participants;

        internal bool IsReady(long expectedEpoch, int participantMask) => participants == participantMask && IsReady(expectedEpoch);

        internal static bool EqualFingerprint(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != 32 || right.Length != 32) return false;
            int difference = 0;
            for (int i = 0; i < 32; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }

    internal readonly struct ControlTransactionKey : IEquatable<ControlTransactionKey>
    {
        internal ControlTransactionKey(string registration, int controller, long sequence)
        { Registration = registration; Controller = controller; Sequence = sequence; }
        internal string Registration { get; }
        internal int Controller { get; }
        internal long Sequence { get; }
        public bool Equals(ControlTransactionKey other) => Controller == other.Controller && Sequence == other.Sequence &&
            StringComparer.Ordinal.Equals(Registration, other.Registration);
        public override bool Equals(object obj) => obj is ControlTransactionKey other && Equals(other);
        public override int GetHashCode() => unchecked((StringComparer.Ordinal.GetHashCode(Registration ?? string.Empty) * 397 ^ Controller) * 397 ^ Sequence.GetHashCode());
    }

    // Reassembly alone does not prove that every peer received the whole command.
    // Peers acknowledge a complete digest; only the real host may then commit it.
    // All operations here run in synchronized Chore context. No world mutation,
    // Steam message or consumer callback is performed by this state machine.
    internal sealed class ControlCommitBarrier
    {
        private sealed class Pending
        {
            internal readonly ControlCommandBatch Batch;
            internal readonly byte[] Digest;
            internal readonly long Revision;
            internal readonly int StartedTick;
            internal int Ready;
            internal Pending(ControlCommandBatch batch, byte[] digest, long revision, int tick)
            { Batch = batch; Digest = (byte[])digest.Clone(); Revision = revision; StartedTick = tick; }
        }

        private readonly Dictionary<ControlTransactionKey, Pending> pending = new Dictionary<ControlTransactionKey, Pending>();
        // Keys with sequence zero identify registered controller streams. A consumed,
        // expired or revoked transaction cannot be registered again. A late older
        // completion must never override a newer accepted input from the same stream.
        private readonly Dictionary<ControlTransactionKey, long> highWater = new Dictionary<ControlTransactionKey, long>();
        private readonly List<ControlTransactionKey> expired = new List<ControlTransactionKey>();
        private readonly ControlProtocolAgreement agreement;
        private long epoch;
        private int host, participants;

        internal ControlCommitBarrier(ControlProtocolAgreement agreement)
        { this.agreement = agreement ?? throw new ArgumentNullException(nameof(agreement)); }

        internal void Reset(long epoch, int host, int participantMask)
        {
            if (epoch <= 0 || host < 1 || host > 8 || (participantMask & ~0x1fe) != 0 || (participantMask & (1 << host)) == 0)
                throw new ArgumentException("Invalid control commit session.");
            this.epoch = epoch; this.host = host; participants = participantMask; pending.Clear(); highWater.Clear(); expired.Clear();
        }

        internal void Clear() { epoch = 0; host = participants = 0; pending.Clear(); highWater.Clear(); expired.Clear(); }

        internal bool RegisterComplete(long expectedEpoch, ControlTransactionKey key, long revision,
            ControlCommandBatch batch, byte[] digest, int nativeTick)
        {
            if (!Ready(expectedEpoch) || string.IsNullOrWhiteSpace(key.Registration) || key.Controller < 1 || key.Controller > 8 ||
                (participants & (1 << key.Controller)) == 0 || key.Sequence <= 0 || revision <= 0 || batch == null ||
                digest == null || digest.Length != 32 || pending.Count >= ControlBatchAssembler.MaximumPending || pending.ContainsKey(key)) return false;
            var stream = new ControlTransactionKey(key.Registration, key.Controller, 0);
            if (highWater.TryGetValue(stream, out long previous) && key.Sequence <= previous ||
                !ControlProtocolAgreement.EqualFingerprint(batch.ComputeDigest(), digest)) return false;
            pending.Add(key, new Pending(batch, digest, revision, nativeTick));
            highWater[stream] = key.Sequence;
            return true;
        }

        internal bool RecordReady(long expectedEpoch, ControlTransactionKey key, long revision,
            int authenticatedIssuer, byte[] digest)
        {
            if (!Ready(expectedEpoch) || authenticatedIssuer < 1 || authenticatedIssuer > 8 ||
                (participants & (1 << authenticatedIssuer)) == 0 || !pending.TryGetValue(key, out var item) ||
                item.Revision != revision || !ControlProtocolAgreement.EqualFingerprint(item.Digest, digest)) return false;
            item.Ready |= 1 << authenticatedIssuer;
            return true;
        }

        // Only the host adapter uses this to decide whether to enqueue a commit.
        // Recipients trust the authenticated host decision, not the sender field in
        // the payload. Requiring remote Ready arrival order again at receipt would
        // incorrectly reject a legitimate commit after cross-sender reordering.
        internal bool CanCommit(long expectedEpoch, ControlTransactionKey key, long revision, byte[] digest) =>
            Ready(expectedEpoch) && pending.TryGetValue(key, out var item) && item.Revision == revision &&
            item.Ready == participants && ControlProtocolAgreement.EqualFingerprint(item.Digest, digest);

        internal bool TryTakeCommit(long expectedEpoch, ControlTransactionKey key, long revision,
            int authenticatedIssuer, byte[] digest, out ControlCommandBatch batch)
        {
            batch = null;
            if (!Ready(expectedEpoch) || authenticatedIssuer != host || !pending.TryGetValue(key, out var item) ||
                item.Revision != revision || !ControlProtocolAgreement.EqualFingerprint(item.Digest, digest)) return false;
            // Consume before the backend can run/reenter/throw. No automatic retry
            // can apply an already accepted command a second time.
            pending.Remove(key); batch = item.Batch;
            return true;
        }

        internal void Expire(int nativeTick, int maximumAgeTicks)
        {
            if (maximumAgeTicks < 1) throw new ArgumentOutOfRangeException(nameof(maximumAgeTicks));
            expired.Clear();
            foreach (var pair in pending)
                if (Simulation.SimulationClock.TryGetElapsedTicks(pair.Value.StartedTick, nativeTick, out int elapsed) && elapsed >= maximumAgeTicks)
                    expired.Add(pair.Key);
            foreach (var key in expired) pending.Remove(key);
            expired.Clear();
        }

        internal void Revoke(string registration, int controller)
        {
            expired.Clear();
            foreach (var pair in pending)
                if (pair.Key.Controller == controller && StringComparer.Ordinal.Equals(pair.Key.Registration, registration)) expired.Add(pair.Key);
            foreach (var key in expired) pending.Remove(key);
            expired.Clear();
        }

        private bool Ready(long expectedEpoch) => epoch != 0 && epoch == expectedEpoch && agreement.IsReady(epoch, participants);
        internal int PendingCount => pending.Count;
    }
}

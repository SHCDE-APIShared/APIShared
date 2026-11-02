using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace APIShared.PlayerControl
{
    internal sealed class ControlReadyNotice
    {
        private readonly byte[] digest;
        internal ControlReadyNotice(ControlTransactionKey key, long revision, byte[] digest)
        { Key = key; Revision = revision; this.digest = (byte[])digest.Clone(); }
        internal ControlTransactionKey Key { get; }
        internal long Revision { get; }
        internal byte[] CopyDigest() => (byte[])digest.Clone();
    }

    // WIP (see docs/PLAYER_CONTROL.md): multiplayer control protocol; unwired.
    // Multiplayer-only protocol state, composed from the same admission path used
    // at execution. The independent SinglePlayerControlSession never constructs or
    // consults this object. Only synchronized simulation/lifecycle adapters call it.
    internal sealed class ControlProtocolSession
    {
        private readonly HashSet<string> registrations = new HashSet<string>(StringComparer.Ordinal);
        private readonly byte[] backendFingerprint;
        private readonly ControlGrantTable grants = new ControlGrantTable();
        private readonly ControlBatchAssembler assembler = new ControlBatchAssembler();
        private readonly ControlProtocolAgreement agreement = new ControlProtocolAgreement();
        private readonly ControlCommitBarrier commits;
        private readonly ControlExecutionGate gate;
        private byte[] fingerprint;
        private long epoch;

        internal ControlProtocolSession(byte[] backendFingerprint, IControlCommandRules rules, ResolveControlObject resolve)
        {
            if (backendFingerprint == null || backendFingerprint.Length != 32) throw new ArgumentException("A complete audited backend fingerprint is required.", nameof(backendFingerprint));
            this.backendFingerprint = (byte[])backendFingerprint.Clone();
            commits = new ControlCommitBarrier(agreement);
            gate = new ControlExecutionGate(grants, rules, resolve, IsReady);
        }

        internal bool Register(string ownerGuid, string registrationId, out string key)
        {
            key = null;
            if (epoch != 0 || registrations.Count >= ControlRegistrationKeys.MaximumRegistrations ||
                !ControlRegistrationKeys.TryCreate(ownerGuid, registrationId, out var candidate) || !registrations.Add(candidate)) return false;
            key = candidate;
            return true;
        }

        internal void Begin(long newEpoch, int host, int participantMask)
        {
            if (epoch != 0 || newEpoch <= 0 || registrations.Count == 0 ||
                host < 1 || host > 8 || (participantMask & ~0x1fe) != 0 || (participantMask & (1 << host)) == 0)
                throw new ArgumentException("Invalid control session identity/roster.");
            // Peer protocol identity is independent of registration order and of each
            // process's lifecycle sequence. No missing/late consumer is inferred away.
            fingerprint = ComputeFingerprint();
            epoch = newEpoch;
            grants.Reset(epoch, host, true); assembler.Reset(epoch);
            agreement.Clear(); commits.Clear();
            agreement.Reset(epoch, participantMask, fingerprint); commits.Reset(epoch, host, participantMask);
        }

        internal void End()
        {
            epoch = 0; fingerprint = null; grants.Clear(); assembler.Reset(0); agreement.Clear(); commits.Clear();
        }

        internal byte[] CopyFingerprint() => fingerprint == null ? null : (byte[])fingerprint.Clone();
        internal bool Acknowledge(long expectedEpoch, int authenticatedIssuer, byte[] peerFingerprint) =>
            epoch == expectedEpoch && agreement.Acknowledge(expectedEpoch, authenticatedIssuer, peerFingerprint);
        internal bool IsReady(long expectedEpoch) => epoch > 0 && epoch == expectedEpoch && agreement.IsReady(epoch);

        internal bool ApplyGrant(long expectedEpoch, int authenticatedIssuer, string registration, int controller,
            int ownerMask, PlayerControlRights rights, long revision)
        {
            if (!IsReady(expectedEpoch) || !Known(registration) ||
                !grants.Apply(expectedEpoch, authenticatedIssuer, registration, controller, ownerMask, rights, revision)) return false;
            // A new revision, including revoke, invalidates all pending input under
            // the previous grant immediately, without freeing replay tombstones.
            assembler.Revoke(registration, controller); commits.Revoke(registration, controller);
            return true;
        }

        internal bool TryPreflight(long expectedEpoch, string registration, long revision, int actualController,
            ControlCommandBatch batch, out string reason)
        {
            reason = null;
            if (!Known(registration) || !grants.HasCurrentGrant(expectedEpoch, registration, revision, actualController))
            { reason = "No current delegated grant."; return false; }
            return gate.TryPrepare(expectedEpoch, registration, revision, actualController, batch, out _, out reason);
        }

        internal bool ReceivePart(long expectedEpoch, string registration, int authenticatedIssuer, long sequence,
            long revision, int index, int count, int length, byte[] bytes, int nativeTick, out ControlReadyNotice ready)
        {
            ready = null;
            // Unknown streams cannot allocate assembler/replay state. The issuer is
            // supplied by the native receive adapter, never a payload player field.
            if (!IsReady(expectedEpoch) || !Known(registration) ||
                !grants.HasCurrentGrant(expectedEpoch, registration, revision, authenticatedIssuer) ||
                !assembler.Receive(expectedEpoch, registration, authenticatedIssuer, sequence, revision,
                    index, count, length, bytes, nativeTick, out var complete)) return false;
            if (complete == null) return true;
            if (!ControlCommandBatch.TryDecode(complete, out var batch) ||
                !TryPreflight(expectedEpoch, registration, revision, authenticatedIssuer, batch, out _)) return false;
            var key = new ControlTransactionKey(registration, authenticatedIssuer, sequence);
            byte[] digest = batch.ComputeDigest();
            if (!commits.RegisterComplete(expectedEpoch, key, revision, batch, digest, nativeTick)) return false;
            ready = new ControlReadyNotice(key, revision, digest);
            return true;
        }

        internal bool ReceiveReady(long expectedEpoch, ControlTransactionKey key, long revision, int authenticatedIssuer, byte[] digest) =>
            Known(key.Registration) &&
            grants.HasCurrentGrant(expectedEpoch, key.Registration, revision, key.Controller) &&
            commits.RecordReady(expectedEpoch, key, revision, authenticatedIssuer, digest);

        internal bool CanCommit(long expectedEpoch, ControlTransactionKey key, long revision, byte[] digest) =>
            Known(key.Registration) &&
            grants.HasCurrentGrant(expectedEpoch, key.Registration, revision, key.Controller) &&
            commits.CanCommit(expectedEpoch, key, revision, digest);

        internal bool ReceiveCommit(long expectedEpoch, ControlTransactionKey key, long revision, int authenticatedIssuer,
            byte[] digest, Action<ControlExecutionPlan> execute, out string reason)
        {
            reason = null;
            if (!Known(key.Registration) ||
                !commits.TryTakeCommit(expectedEpoch, key, revision, authenticatedIssuer, digest, out var batch))
            { reason = "No complete host-committed transaction."; return false; }
            // The original controller was authenticated on EVERY part and retained
            // in the pending transaction. It is distinct from this host commit's
            // issuer and from command object owners. Never substitute the host here.
            return gate.TryExecute(expectedEpoch, key.Registration, revision, key.Controller, batch, execute, out reason);
        }

        internal void Expire(int nativeTick, int maximumAgeTicks)
        {
            if (epoch == 0) return;
            assembler.Expire(nativeTick, maximumAgeTicks); commits.Expire(nativeTick, maximumAgeTicks);
        }

        private bool Known(string registration) => registration != null && registrations.Contains(registration);

        private byte[] ComputeFingerprint()
        {
            var ordered = new List<string>(registrations); ordered.Sort(StringComparer.Ordinal);
            using (var bytes = new MemoryStream())
            using (var writer = new BinaryWriter(bytes, ControlRegistrationKeys.WireText))
            using (var sha = SHA256.Create())
            {
                writer.Write(ControlCommandBatch.WireVersion); writer.Write(backendFingerprint);
                writer.Write(ordered.Count);
                foreach (string key in ordered) writer.Write(key);
                writer.Flush(); return sha.ComputeHash(bytes.ToArray());
            }
        }
    }
}

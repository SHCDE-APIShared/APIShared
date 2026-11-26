using System;
using System.Collections.Generic;

namespace APIShared.Networking
{
    /// <summary>Agreement on one native readiness Chore across the expected participants.</summary>
    public enum NativeReadinessState
    {
        /// <summary>Some expected participants have not acknowledged this attempt.</summary>
        Pending,
        /// <summary>All expected participants, including the local participant, agree.</summary>
        Ready,
        /// <summary>Native receipts for this attempt disagree about the command ID or tick.</summary>
        Mismatch
    }

    /// <summary>
    /// Evaluates acknowledgements for one outgoing native Chore proof. Callers own participant
    /// discovery, session lifetime, transport authentication and packet validation. Serialize calls
    /// with the caller's existing lock when both the simulation and control threads access this instance.
    /// </summary>
    public sealed class NativeReadinessProof
    {
        private readonly Dictionary<ulong, string> expected = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, (int CommandId, int Tick)> receipts = new Dictionary<ulong, (int, int)>();
        private ulong localSteamId;
        private string worldToken;
        private long attempt;
        private bool conflictingReceipt;

        /// <summary>Discards the outgoing attempt and all of its receipts.</summary>
        public void Clear()
        {
            expected.Clear();
            receipts.Clear();
            localSteamId = 0;
            worldToken = null;
            attempt = 0;
            conflictingReceipt = false;
        }

        /// <summary>
        /// Replaces the outgoing attempt and snapshots the expected Steam IDs and world nonces.
        /// The peers must have unique nonzero IDs and nonempty nonces, including a local entry.
        /// A rejected attempt leaves this instance inactive.
        /// </summary>
        public void Begin(ulong localSteamId, string worldToken, long attempt,
            IEnumerable<KeyValuePair<ulong, string>> peers)
        {
            Clear();
            if (localSteamId == 0 || string.IsNullOrEmpty(worldToken) || attempt <= 0 || peers == null)
                throw new ArgumentException("Invalid native readiness attempt.");
            foreach (var peer in peers)
            {
                if (peer.Key == 0 || string.IsNullOrEmpty(peer.Value) || expected.ContainsKey(peer.Key))
                    throw new ArgumentException("Invalid or duplicate readiness participant.", nameof(peers));
                expected.Add(peer.Key, peer.Value);
            }
            if (!expected.ContainsKey(localSteamId))
                throw new ArgumentException("The readiness participants omit the local peer.", nameof(peers));

            this.localSteamId = localSteamId;
            this.worldToken = worldToken;
            this.attempt = attempt;
        }

        /// <summary>
        /// Records a receipt for a known transport sender in the active attempt.
        /// The local receipt must come from the same native Chore execution path as remote receipts.
        /// A receipt for another world, attempt, or sender leaves agreement unchanged.
        /// Repeating the same receipt is safe. A contradictory repeat invalidates this attempt.
        /// </summary>
        public bool TryAccept(ulong senderSteamId, string senderWorldNonce, string worldToken,
            long attempt, int commandId, int tick)
        {
            if (this.attempt == 0 || attempt != this.attempt || worldToken != this.worldToken ||
                !expected.TryGetValue(senderSteamId, out string nonce) || senderWorldNonce != nonce)
                return false;
            if (receipts.TryGetValue(senderSteamId, out var earlier))
            {
                if (earlier.CommandId != commandId || earlier.Tick != tick)
                    conflictingReceipt = true;
            }
            else
                receipts.Add(senderSteamId, (commandId, tick));
            return true;
        }

        /// <summary>Checks that every expected receipt agrees with the local native execution.</summary>
        public NativeReadinessState Evaluate()
        {
            if (conflictingReceipt)
                return NativeReadinessState.Mismatch;
            if (attempt == 0 || receipts.Count != expected.Count || !receipts.TryGetValue(localSteamId, out var local))
                return NativeReadinessState.Pending;
            foreach (var receipt in receipts.Values)
                if (receipt.CommandId != local.CommandId || receipt.Tick != local.Tick)
                    return NativeReadinessState.Mismatch;
            return NativeReadinessState.Ready;
        }
    }
}

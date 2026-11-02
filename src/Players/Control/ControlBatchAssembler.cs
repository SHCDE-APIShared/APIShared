using System;
using System.Collections.Generic;

namespace APIShared.PlayerControl
{
    // WIP (see docs/PLAYER_CONTROL.md): multiplayer control protocol; unwired.
    // Called only from synchronized receive context. An incomplete command has no
    // execution callback and cannot publish a partially assembled object selection.
    internal sealed class ControlBatchAssembler
    {
        internal const int ChunkBytes = 1024, MaximumParts = 128, MaximumPending = 32;
        private const int MaximumBufferedBytes = MaximumParts * ChunkBytes * MaximumPending;
        private readonly Dictionary<StreamKey, long> highestSequence = new Dictionary<StreamKey, long>();
        private readonly Dictionary<BatchKey, Pending> pending = new Dictionary<BatchKey, Pending>();
        private readonly List<BatchKey> expired = new List<BatchKey>();
        private int buffered;
        private long epoch;

        private readonly struct StreamKey : IEquatable<StreamKey>
        {
            private readonly string registration;
            private readonly int sender;
            internal StreamKey(string registration, int sender) { this.registration = registration; this.sender = sender; }
            public bool Equals(StreamKey other) => sender == other.sender && StringComparer.Ordinal.Equals(registration, other.registration);
            public override bool Equals(object obj) => obj is StreamKey other && Equals(other);
            public override int GetHashCode() => unchecked(StringComparer.Ordinal.GetHashCode(registration) * 397 ^ sender);
        }
        private readonly struct BatchKey : IEquatable<BatchKey>
        {
            private readonly StreamKey stream;
            private readonly long sequence;
            internal BatchKey(StreamKey stream, long sequence) { this.stream = stream; this.sequence = sequence; }
            internal bool BelongsTo(StreamKey other) => stream.Equals(other);
            public bool Equals(BatchKey other) => sequence == other.sequence && stream.Equals(other.stream);
            public override bool Equals(object obj) => obj is BatchKey other && Equals(other);
            public override int GetHashCode() => unchecked(stream.GetHashCode() * 397 ^ sequence.GetHashCode());
        }
        private sealed class Pending
        {
            internal readonly byte[][] Parts;
            internal readonly int Length, StartedTick;
            internal readonly long Revision;
            internal int Count, Bytes;
            internal Pending(int parts, int length, int tick, long revision)
            { Parts = new byte[parts][]; Length = length; StartedTick = tick; Revision = revision; }
        }

        internal void Reset(long nextEpoch)
        {
            epoch = nextEpoch; pending.Clear(); highestSequence.Clear(); buffered = 0;
        }

        internal bool Receive(long expectedEpoch, string registration, int nativeIssuer, long sequence,
            long grantRevision, int index, int partCount, int totalLength, byte[] bytes, int tick, out byte[] completed)
        {
            completed = null;
            if (epoch <= 0 || expectedEpoch != epoch || string.IsNullOrWhiteSpace(registration) ||
                nativeIssuer < 1 || nativeIssuer > 8 || sequence <= 0 || grantRevision <= 0 ||
                partCount < 1 || partCount > MaximumParts || index < 0 || index >= partCount ||
                totalLength < 1 || totalLength > MaximumParts * ChunkBytes ||
                partCount != (totalLength + ChunkBytes - 1) / ChunkBytes || bytes == null ||
                bytes.Length != (index == partCount - 1 ? totalLength - index * ChunkBytes : ChunkBytes)) return false;
            var stream = new StreamKey(registration, nativeIssuer);
            var key = new BatchKey(stream, sequence);
            if (!pending.TryGetValue(key, out var item))
            {
                if (highestSequence.TryGetValue(stream, out long highest) && sequence <= highest) return false;
                if (pending.Count >= MaximumPending || buffered > MaximumBufferedBytes - bytes.Length) return false;
                highestSequence[stream] = sequence;
                item = new Pending(partCount, totalLength, tick, grantRevision);
                pending.Add(key, item);
            }
            if (item.Revision != grantRevision || item.Length != totalLength || item.Parts.Length != partCount)
            { Drop(key, item); return false; }
            var previous = item.Parts[index];
            if (previous != null)
            {
                for (int i = 0; i < previous.Length; i++)
                    if (previous[i] != bytes[i]) { Drop(key, item); return false; }
                return true;
            }
            if (buffered > MaximumBufferedBytes - bytes.Length) { Drop(key, item); return false; }
            item.Parts[index] = (byte[])bytes.Clone(); item.Count++; item.Bytes += bytes.Length; buffered += bytes.Length;
            if (item.Count != partCount) return true;
            completed = new byte[totalLength];
            int offset = 0;
            foreach (var part in item.Parts) { Buffer.BlockCopy(part, 0, completed, offset, part.Length); offset += part.Length; }
            Drop(key, item);
            return true;
        }

        internal void Expire(int tick, int maximumAgeTicks)
        {
            if (maximumAgeTicks < 1) throw new ArgumentOutOfRangeException(nameof(maximumAgeTicks));
            expired.Clear();
            foreach (var pair in pending)
                if (Simulation.SimulationClock.TryGetElapsedTicks(pair.Value.StartedTick, tick, out int elapsed) && elapsed >= maximumAgeTicks)
                    expired.Add(pair.Key);
            foreach (var key in expired) Drop(key, pending[key]);
            expired.Clear();
        }

        private void Drop(BatchKey key, Pending value) { pending.Remove(key); buffered -= value.Bytes; }
        internal void Revoke(string registration, int controller)
        {
            var stream = new StreamKey(registration, controller);
            expired.Clear();
            foreach (var pair in pending) if (pair.Key.BelongsTo(stream)) expired.Add(pair.Key);
            foreach (var key in expired) Drop(key, pending[key]);
            expired.Clear();
        }
        internal int PendingCount => pending.Count;
        internal int BufferedBytes => buffered;
    }
}

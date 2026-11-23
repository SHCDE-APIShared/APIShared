using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using APIShared.Networking;
using static APIShared.Multiplayer.MultiplayerResyncDataPacket;
using Record = APIShared.Multiplayer.MultiplayerResyncDataRuntime.Record;
using World = APIShared.Multiplayer.SimulationAgreement.AdmittedResyncWorld;

namespace APIShared.Multiplayer
{
    // The production repair state machine. Native/Unity adapters supply external facts;
    // tests instantiate this same implementation with queued peers, never a second protocol.
    internal sealed partial class MultiplayerResyncDataSession
    {
        internal sealed class Services
        {
            internal Func<World> GetWorld, BindWorld;
            internal Func<World, bool> IsWorldCurrent, VerifyRoster;
            internal Func<bool> IsCohortAdmitted, IsFaulted, IsHost, IsClientReady, HoldsEngineMonitor;
            internal Func<int> NativeRepairState;
            internal Func<long, int, bool> NativeRosterMatches;
            internal Func<long, bool> HasFreeQueueSlot;
            internal Func<long> Timestamp;
            internal long Frequency;
            internal Func<ulong> NewNonce;
            internal Func<Action, bool> EnqueueUnity;
            internal Func<ulong, MultiplayerResyncDataPacket, NetworkSendResult> Send;
            internal Action<Action> UnderEngineMonitor;
            internal Action<string> ReportFault;
            internal Action<long, int, int, int> Completed;
        }
        private readonly object Gate = new object();
        private readonly IReadOnlyList<Record> providers;
        private readonly Services services;
        private readonly Action bindWorld;
        private World observedWorld;
        private long repairNumber;
        private int bindQueued;
        private volatile Attempt current;
        private const int TransferTimeoutSeconds = 90;
        internal MultiplayerResyncDataSession(IReadOnlyList<Record> providers, Services services)
        {
            this.providers = providers ?? throw new ArgumentNullException(nameof(providers));
            this.services = services ?? throw new ArgumentNullException(nameof(services));
            if (services.Frequency <= 0) throw new ArgumentOutOfRangeException(nameof(services.Frequency));
            bindWorld = BindWorldOnUnity;
        }
        internal void Reset()
        {
            lock (Gate) { current = null; observedWorld = null; repairNumber = 0; }
        }
        private int EarlyRecordCount
        {
            get { int count = 0; foreach (var provider in providers) if (provider.CaptureAtStart) count++; return count; }
        }
        private sealed class Attempt
        {
            internal Attempt(World world, long repair, int recordCount, Func<ulong> nonce)
            {
                World = world; Repair = repair; IsHost = world.Host == world.Local;
                foreach (int slot in world.Peers.Keys) PlayerMask |= 1 << slot;
                if (IsHost)
                {
                    Peers = new Dictionary<ulong, Peer>();
                    foreach (ulong steam in world.Peers.Values)
                        if (steam != world.Local) Peers.Add(steam, new Peer(recordCount));
                }
                else Nonce = nonce();
            }
            internal readonly World World;
            internal readonly long Repair;
            internal readonly bool IsHost;
            internal readonly ulong Nonce;
            internal readonly Dictionary<ulong, Peer> Peers;
            internal readonly int PlayerMask;
            internal byte[][] Local, Records;
            internal byte[] Manifest, ManifestHash;
            internal object[] Prepared;
            internal long Deadline, NextHello;
            internal bool RosterVerified, ReadySubmitted, Released, Committed;
            internal string Fault;
            internal int ReceiveIndex = -1, Received;
            internal int RequestedRecords, TransferredBytes;
        }

        private sealed class Peer
        {
            internal Peer(int recordCount) { Delivered = new bool[recordCount]; }
            internal readonly bool[] Delivered;
            internal ulong Nonce;
            internal bool Ready;
            internal int Index = -1, Sent, Acknowledged;
        }

        internal void OnNativeStart()
        {
            var world = services.GetWorld();
            if (world == null)
            {
                if (services.IsCohortAdmitted()) Fail(null, "The admitted player slots could not be bound before native repair.");
                return;
            }
            lock (Gate)
            {
                if (current != null && ReferenceEquals(current.World, world))
                { Fail(current, "A new native repair overlapped an unfinished repair."); return; }
                if (!ReferenceEquals(observedWorld, world))
                { observedWorld = world; repairNumber = 0; }
                var attempt = current = new Attempt(world, ++repairNumber, providers.Count, services.NewNonce);
                try
                {
                    if (!attempt.IsHost)
                        attempt.Local = CaptureRecords(providers, new byte[providers.Count][], earlyOnly: false);
                    else if (EarlyRecordCount != 0)
                    {
                        attempt.Records = CaptureRecords(providers, new byte[providers.Count][], earlyOnly: true);
                        if (EarlyRecordCount == providers.Count) SetManifest(attempt);
                    }
                }
                catch (Exception error) { Fail(attempt, "Could not capture pre-repair data: " + error.Message); }
            }
        }

        internal void OnEngineRun()
        {
            if (services.GetWorld() == null)
            {
                if (!services.IsFaulted() &&
                    Interlocked.Exchange(ref bindQueued, 1) == 0 &&
                    !services.EnqueueUnity(bindWorld))
                    Volatile.Write(ref bindQueued, 0);
                return;
            }
            if (current == null) return;
            RunAttempt();
        }
        // Keep closures in the active-repair path, beyond ordinary engine-run guards.
        private void RunAttempt()
        {
            lock (Gate)
            {
                var attempt = current;
                if (attempt?.Fault != null && services.IsWorldCurrent(attempt.World))
                {
                    ReportFault(attempt.Fault);
                    return;
                }
                if (!Live(attempt) || attempt.Committed) return;
                try
                {
                    if (services.NativeRepairState() == 0)
                    { Fail(attempt, "Vanilla aborted the repair before shared data committed."); return; }
                    long now = services.Timestamp();
                    if (attempt.Deadline != 0 && now >= attempt.Deadline)
                    { Fail(attempt, "Timed out waiting for the resync data transfer."); return; }
                    if (attempt.IsHost) return;
                    if (attempt.Deadline == 0 && services.IsClientReady())
                        SetDeadline(attempt, now);
                    if (attempt.Manifest == null)
                    {
                        if (now >= attempt.NextHello)
                        {
                            attempt.NextHello = now + services.Frequency;
                            QueueUnity(attempt, () =>
                            {
                                if (Live(attempt) && attempt.Manifest == null)
                                    Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Hello);
                            });
                        }
                    }
                    if (attempt.Records == null || attempt.ReceiveIndex >= 0 || attempt.Prepared != null ||
                        !services.IsClientReady()) return;
                    attempt.Prepared = Prepare(attempt.Records);
                    QueueUnity(attempt, () =>
                    {
                        if (!Live(attempt)) return;
                        // A local Chore67 cannot observe the attempt before this assignment.
                        attempt.ReadySubmitted = Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Ready, body: attempt.ManifestHash);
                        if (attempt.ReadySubmitted) SetDeadline(attempt, services.Timestamp());
                    });
                }
                catch (Exception error) { Fail(attempt, "Could not prepare repaired mod data: " + error.Message); }
            }
        }

        private void BindWorldOnUnity()
        {
            try { services.BindWorld(); }
            finally { Volatile.Write(ref bindQueued, 0); }
        }

        internal bool AllowHostCompletion(long manager)
        {
            if (!services.IsCohortAdmitted()) return true;
            lock (Gate)
            {
                var attempt = current;
                if (attempt == null)
                {
                    Fail(null, "Vanilla reached host completion without an opened repair.");
                    return false;
                }
                if (!Live(attempt) || !attempt.IsHost || attempt.Released) return false;
                try
                {
                    if (!services.IsHost() ||
                        !services.NativeRosterMatches(manager, attempt.PlayerMask))
                    { Fail(attempt, "The native host or repair roster changed."); return false; }
                    if (attempt.Prepared == null)
                    {
                        SetDeadline(attempt, services.Timestamp());
                        attempt.Records = CaptureRecords(providers, attempt.Records ?? new byte[providers.Count][], earlyOnly: false);
                        attempt.Prepared = Prepare(attempt.Records);
                        if (attempt.Manifest == null)
                        {
                            SetManifest(attempt);
                            QueueUnity(attempt, () => AdvertiseManifest(attempt));
                        }
                        else
                            StartDeferredWindows(attempt);
                    }
                    if (!Live(attempt) || !attempt.RosterVerified) return false;
                    foreach (var peer in attempt.Peers.Values) if (!peer.Ready) return false;
                    if (!services.HasFreeQueueSlot(manager))
                    { Fail(attempt, "The host Chore queue is full at resync completion."); return false; }
                    // Vanilla queues the outgoing Chore 67 before executing it locally.
                    // Publish the prepared host state while the queue is still untouched.
                    Commit(attempt);
                    attempt.Released = true;
                    return true;
                }
                catch (Exception error) { Fail(attempt, "Could not prepare host resync data: " + error.Message); return false; }
            }
        }

        internal bool AllowRemoteRepairPacket(int sender)
        {
            var world = services.GetWorld() ??
                services.BindWorld();
            if (world == null)
            {
                Fail(null, "The admitted player slots could not be bound before a native repair packet.");
                return false;
            }
            return world.Host != world.Local && sender == world.HostPlayerId;
        }

        internal bool AllowRemoteCompletion(long manager, int sender)
        {
            if (!services.IsCohortAdmitted()) return true;
            lock (Gate)
            {
                var world = services.GetWorld();
                var attempt = current;
                if (world == null) return false;
                if (sender != world.HostPlayerId) return false;
                if (Live(attempt) && !attempt.IsHost && attempt.ReadySubmitted && !attempt.Committed &&
                    services.HasFreeQueueSlot(manager)) return true;
                Fail(attempt, "The host completion arrived without a prepared local snapshot or free Chore slot.");
                return false;
            }
        }

        internal bool BeforeNativeComplete()
        {
            if (!services.IsCohortAdmitted()) return true;
            lock (Gate)
            {
                var attempt = current;
                if (!Live(attempt) || attempt.Prepared == null ||
                    !(attempt.IsHost ? attempt.Released : attempt.ReadySubmitted))
                { Fail(attempt, "Native resync completion was not admitted."); return false; }
                if (attempt.IsHost) return attempt.Committed;
                if (attempt.Committed) return false;
                try
                {
                    Commit(attempt);
                    return true;
                }
                catch (Exception error)
                { Fail(attempt, "Resync data commit failed: " + error.Message); return false; }
            }
        }

        private void Commit(Attempt attempt)
        {
            for (int i = 0; i < providers.Count; i++) providers[i].Commit(attempt.Prepared[i]);
            attempt.Committed = true;
        }

        internal void OnNativeCompleted()
        {
            Attempt completed;
            lock (Gate)
            {
                completed = current;
                if (completed?.Committed != true) return;
                current = null;
            }
            services.Completed(completed.Repair, providers.Count, completed.RequestedRecords, completed.TransferredBytes);
        }

        private void SetDeadline(Attempt attempt, long now) =>
            attempt.Deadline = now + TransferTimeoutSeconds * services.Frequency;

        internal static byte[][] CaptureRecords(IReadOnlyList<Record> providers, byte[][] records, bool earlyOnly)
        {
            for (int i = 0; i < records.Length; i++)
            {
                var provider = providers[i];
                if (records[i] != null || earlyOnly && !provider.CaptureAtStart) continue;
                byte[] bytes = provider.Capture();
                if (bytes == null || bytes.Length > provider.Maximum)
                    throw new InvalidDataException("Resync provider exceeded its declared bound: " + provider.Id);
                records[i] = (byte[])bytes.Clone();
            }
            return records;
        }

        private object[] Prepare(byte[][] bytes)
        {
            var replacement = new object[providers.Count];
            for (int i = 0; i < replacement.Length; i++) replacement[i] = providers[i].Prepare(bytes[i]);
            return replacement;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        private bool Live(Attempt attempt) => attempt != null && ReferenceEquals(attempt, current) &&
            attempt.Fault == null && services.IsWorldCurrent(attempt.World);

        private void Fail(Attempt attempt, string reason)
        {
            if (attempt != null)
            {
                if (!Live(attempt)) return;
                attempt.Fault = reason;
            }
            if (services.HoldsEngineMonitor())
            {
                ReportFault(reason);
                return;
            }
            // Receive holds Gate. Reporting synchronously would take the engine monitor
            // in the opposite order to the native guards.
            services.EnqueueUnity(() =>
            {
                services.UnderEngineMonitor(() =>
                {
                    if (attempt != null && services.IsWorldCurrent(attempt.World)) ReportFault(reason);
                });
            });
        }

        private void ReportFault(string reason) =>
            services.ReportFault(reason);

        private void QueueUnity(Attempt attempt, Action action)
        {
            if (!services.EnqueueUnity(() =>
            {
                lock (Gate)
                    if (Live(attempt))
                        try { action(); }
                        catch (Exception error) { Fail(attempt, error.Message); }
            })) Fail(attempt, "The Unity transport dispatcher is unavailable.");
        }
    }
}

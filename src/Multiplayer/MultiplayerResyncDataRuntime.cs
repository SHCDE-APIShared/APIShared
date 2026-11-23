using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using APIShared.Internal;
using APIShared.Networking;
using BepInEx.Logging;
using static APIShared.Multiplayer.MultiplayerResyncDataPacket;
using World = APIShared.Multiplayer.SimulationAgreement.AdmittedResyncWorld;

namespace APIShared.Multiplayer
{
    internal static partial class MultiplayerResyncDataRuntime
    {
        internal sealed class Record
        {
            internal Record(string owner, string id, int schema, int maximum,
                Func<byte[]> capture, Func<ReadOnlyMemory<byte>, object> prepare, Action<object> commit)
            {
                Owner = owner; Id = id; Schema = schema; Maximum = maximum;
                Capture = capture; Prepare = prepare; Commit = commit;
            }
            internal readonly string Owner, Id;
            internal readonly int Schema, Maximum;
            internal readonly Func<byte[]> Capture;
            internal readonly Func<ReadOnlyMemory<byte>, object> Prepare;
            internal readonly Action<object> Commit;
        }

        private sealed class Attempt
        {
            internal Attempt(World world, long repair, int recordCount)
            {
                World = world; Repair = repair; IsHost = world.Host == world.Local;
                foreach (int slot in world.Peers.Keys) PlayerMask |= 1 << slot;
                if (IsHost)
                {
                    Peers = new Dictionary<ulong, Peer>();
                    foreach (ulong steam in world.Peers.Values)
                        if (steam != world.Local) Peers.Add(steam, new Peer(recordCount));
                }
                else Nonce = NewNonce();
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

        private static readonly object Gate = new object();
        private static readonly List<Record> Providers = new List<Record>();
        private const int TransferTimeoutSeconds = 90;
        private static MessageChannel<MultiplayerResyncDataPacket> channel;
        private static ManualLogSource log;
        private static bool frozen;
        private static string manifestDigest;
        private static World observedWorld;
        private static long repairNumber;
        private static int bindQueued;
        private static volatile Attempt current;

        internal static bool IsAvailable => channel != null && MultiplayerResyncNativeRuntime.IsDataAvailable;
        // An elected world must reject a completion even when its local attempt was never opened.
        internal static bool HasActiveAttempt => SimulationAgreement.IsResyncCohortAdmitted;

        internal static void Initialize(ManualLogSource logger)
        {
            log = logger;
            channel = ApiShared.ForMod(APISharedPlugin.PluginGuid).Network.RegisterMessage<MultiplayerResyncDataPacket>(
                "multiplayer-resync-data-v1", Receive, new MessageChannelOptions<MultiplayerResyncDataPacket>
                {
                    MaximumBodyBytes = ChunkBytes + 96,
                    Snapshot = packet => packet, // Formatter makes an immutable, private packet.
                    IsEnabled = () => HasActiveAttempt
                });
        }

        internal static bool TryRegister(Record record, out string reason)
        {
            if (!UnityMainThreadDispatch.IsMainThread || !MultiplayerResyncData.CheckIntegrity())
            { reason = "The shared resync service is unavailable or registration is outside Unity startup."; return false; }
            if (!ValidKey(record.Owner) || !ValidKey(record.Id) || record.Schema < 1 ||
                record.Maximum < 1 || record.Maximum > MaxRecordBytes)
            { reason = "Invalid resync record identity, schema or size limit."; return false; }
            lock (Gate)
            {
                if (frozen || Providers.Count >= MaxRecords)
                { reason = "The resync record manifest is closed or full."; return false; }
                int declared = record.Maximum;
                foreach (var prior in Providers)
                {
                    if (prior.Owner == record.Owner && prior.Id == record.Id)
                    { reason = "Duplicate resync record identity."; return false; }
                    declared += prior.Maximum;
                }
                if (declared > MaxTotalBytes)
                { reason = "Combined resync record bounds exceed four MiB."; return false; }
                if (Providers.Count == 0 &&
                    !SimulationAgreement.TryRegisterResyncCohort(FreezeManifest, MultiplayerResyncData.CheckIntegrity,
                        ApplyAgreement, out reason)) return false;
                Providers.Add(record);
            }
            reason = string.Empty;
            return true;
        }

        private static bool ValidKey(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 96) return false;
            foreach (char c in value)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' ||
                    c == '.' || c == '_' || c == '-')) return false;
            return true;
        }

        private static string FreezeManifest()
        {
            lock (Gate)
            {
                if (frozen) return manifestDigest;
                Providers.Sort((a, b) =>
                {
                    int owner = string.CompareOrdinal(a.Owner, b.Owner);
                    return owner == 0 ? string.CompareOrdinal(a.Id, b.Id) : owner;
                });
                using (var stream = new MemoryStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(Protocol);
                    foreach (var provider in Providers)
                    {
                        writer.Write(provider.Owner); writer.Write(provider.Id);
                        writer.Write(provider.Schema); writer.Write(provider.Maximum);
                    }
                    writer.Flush();
                    manifestDigest = BitConverter.ToString(Hash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
                }
                frozen = true;
                return manifestDigest;
            }
        }

        private static void ApplyAgreement(bool enabled)
        {
            lock (Gate)
            {
                current = null;
                observedWorld = null;
                repairNumber = 0;
            }
        }

        internal static void OnNativeStart()
        {
            var world = SimulationAgreement.GetAdmittedResyncWorld();
            if (world == null)
            {
                if (HasActiveAttempt) Fail(null, "The admitted player slots could not be bound before native repair.");
                return;
            }
            lock (Gate)
            {
                if (current != null && ReferenceEquals(current.World, world))
                { Fail(current, "A new native repair overlapped an unfinished repair."); return; }
                if (!ReferenceEquals(observedWorld, world))
                { observedWorld = world; repairNumber = 0; }
                var attempt = current = new Attempt(world, ++repairNumber, Providers.Count);
                try
                {
                    if (!attempt.IsHost) attempt.Local = Capture();
                }
                catch (Exception error) { Fail(attempt, "Could not capture pre-repair data: " + error.Message); }
            }
        }

        internal static void OnEngineRun()
        {
            if (SimulationAgreement.GetAdmittedResyncWorld() == null)
            {
                if (!Economy.WorkerProductionWorldSafety.IsFaulted &&
                    Interlocked.Exchange(ref bindQueued, 1) == 0 &&
                    !UnityMainThreadDispatch.TryEnqueue(BindWorldOnUnity))
                    Volatile.Write(ref bindQueued, 0);
                return;
            }
            if (current == null) return;
            lock (Gate)
            {
                var attempt = current;
                if (attempt?.Fault != null && SimulationAgreement.IsAdmittedResyncWorldCurrent(attempt.World))
                {
                    ReportFault(attempt.Fault);
                    return;
                }
                if (!Live(attempt) || attempt.Committed) return;
                try
                {
                    if (MultiplayerResyncNativeRuntime.NativeRepairState == 0)
                    { Fail(attempt, "Vanilla aborted the repair before shared data committed."); return; }
                    long now = Stopwatch.GetTimestamp();
                    if (attempt.Deadline != 0 && now >= attempt.Deadline)
                    { Fail(attempt, "Timed out waiting for the resync data transfer."); return; }
                    if (attempt.IsHost) return;
                    if (attempt.Manifest == null)
                    {
                        if (now >= attempt.NextHello)
                        {
                            attempt.NextHello = now + Stopwatch.Frequency;
                            QueueUnity(attempt, () =>
                            {
                                if (Live(attempt) && attempt.Manifest == null)
                                    Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Hello);
                            });
                        }
                        if (attempt.Deadline == 0 && MultiplayerResyncNativeRuntime.IsNativeClientReady)
                            SetDeadline(attempt, now);
                    }
                    if (attempt.Records == null || attempt.ReceiveIndex >= 0 || attempt.Prepared != null ||
                        !MultiplayerResyncNativeRuntime.IsNativeClientReady) return;
                    attempt.Prepared = Prepare(attempt.Records);
                    QueueUnity(attempt, () =>
                    {
                        if (!Live(attempt)) return;
                        // A successful send is acted on by the host only after this monitor is released.
                        attempt.ReadySubmitted = Send(attempt, attempt.World.Host, attempt.Nonce, Kind.Ready, body: attempt.ManifestHash);
                        if (attempt.ReadySubmitted) SetDeadline(attempt, Stopwatch.GetTimestamp());
                    });
                }
                catch (Exception error) { Fail(attempt, "Could not prepare repaired mod data: " + error.Message); }
            }
        }

        private static void BindWorldOnUnity()
        {
            try { SimulationAgreement.TryBindAdmittedResyncWorld(); }
            finally { Volatile.Write(ref bindQueued, 0); }
        }

        internal static bool AllowHostCompletion(long manager)
        {
            if (!HasActiveAttempt) return true;
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
                    if (!MultiplayerResyncNativeRuntime.IsNativeHost ||
                        !MultiplayerResyncDataNativeRuntime.NativeRosterMatches(manager, attempt.PlayerMask))
                    { Fail(attempt, "The native host or repair roster changed."); return false; }
                    if (attempt.Records == null)
                    {
                        SetDeadline(attempt, Stopwatch.GetTimestamp());
                        attempt.Records = Capture();
                        attempt.Prepared = Prepare(attempt.Records);
                        attempt.Manifest = EncodeManifest(attempt.Records);
                        attempt.ManifestHash = Hash(attempt.Manifest);
                        QueueUnity(attempt, () =>
                        {
                            if (!Live(attempt)) return;
                            foreach (var peer in attempt.Peers)
                                if (peer.Value.Nonce != 0)
                                    Send(attempt, peer.Key, peer.Value.Nonce, Kind.Manifest, body: attempt.Manifest);
                            UpdateRosterProof(attempt);
                        });
                    }
                    if (!attempt.RosterVerified) return false;
                    foreach (var peer in attempt.Peers.Values) if (!peer.Ready) return false;
                    if (!NativeChoreContext.HasFreeQueueSlot(manager))
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

        internal static bool AllowRemoteRepairPacket(int sender)
        {
            var world = SimulationAgreement.GetAdmittedResyncWorld() ??
                SimulationAgreement.TryBindAdmittedResyncWorld();
            if (world == null)
            {
                Fail(null, "The admitted player slots could not be bound before a native repair packet.");
                return false;
            }
            return world.Host != world.Local && sender == world.HostPlayerId;
        }

        internal static bool AllowRemoteCompletion(long manager, int sender)
        {
            if (!HasActiveAttempt) return true;
            lock (Gate)
            {
                var world = SimulationAgreement.GetAdmittedResyncWorld();
                var attempt = current;
                if (world == null) return false;
                if (sender != world.HostPlayerId) return false;
                if (Live(attempt) && !attempt.IsHost && attempt.ReadySubmitted && !attempt.Committed &&
                    NativeChoreContext.HasFreeQueueSlot(manager)) return true;
                Fail(attempt, "The host completion arrived without a prepared local snapshot or free Chore slot.");
                return false;
            }
        }

        internal static bool BeforeNativeComplete()
        {
            if (!HasActiveAttempt) return true;
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

        private static void Commit(Attempt attempt)
        {
            for (int i = 0; i < Providers.Count; i++) Providers[i].Commit(attempt.Prepared[i]);
            attempt.Committed = true;
        }

        internal static void OnNativeCompleted()
        {
            Attempt completed;
            lock (Gate)
            {
                completed = current;
                if (completed?.Committed != true) return;
                current = null;
            }
            NativeApiLog.Info(log, $"RESYNC_DATA_COMPLETED repair={completed.Repair} records={Providers.Count} " +
                $"requestedRecords={completed.RequestedRecords} transferredBytes={completed.TransferredBytes}");
        }

        private static void SetDeadline(Attempt attempt, long now) =>
            attempt.Deadline = now + TransferTimeoutSeconds * Stopwatch.Frequency;

        private static byte[][] Capture()
        {
            var records = new byte[Providers.Count][];
            for (int i = 0; i < records.Length; i++)
            {
                byte[] bytes = Providers[i].Capture();
                if (bytes == null || bytes.Length > Providers[i].Maximum)
                    throw new InvalidDataException("Resync provider exceeded its declared bound: " + Providers[i].Id);
                records[i] = (byte[])bytes.Clone();
            }
            return records;
        }

        private static object[] Prepare(byte[][] bytes)
        {
            var replacement = new object[Providers.Count];
            for (int i = 0; i < replacement.Length; i++) replacement[i] = Providers[i].Prepare(bytes[i]);
            return replacement;
        }

        private static byte[] Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }

        private static ulong NewNonce()
        {
            var bytes = new byte[sizeof(ulong)];
            using (var rng = RandomNumberGenerator.Create())
                do { rng.GetBytes(bytes); } while (BitConverter.ToUInt64(bytes, 0) == 0);
            return BitConverter.ToUInt64(bytes, 0);
        }

        private static bool Live(Attempt attempt) => attempt != null && ReferenceEquals(attempt, current) &&
            attempt.Fault == null && SimulationAgreement.IsAdmittedResyncWorldCurrent(attempt.World);

        private static void Fail(Attempt attempt, string reason)
        {
            if (attempt != null)
            {
                if (!Live(attempt)) return;
                attempt.Fault = reason;
            }
            if (MultiplayerResyncDataNativeRuntime.HoldsEngineMonitor)
            {
                ReportFault(reason);
                return;
            }
            // Receive holds Gate. Reporting synchronously would take the engine monitor
            // in the opposite order to the native guards.
            UnityMainThreadDispatch.TryEnqueue(() =>
            {
                lock (Economy.WorkerProductionWorldSafety.EngineGate)
                    if (attempt != null && SimulationAgreement.IsAdmittedResyncWorldCurrent(attempt.World))
                        ReportFault(reason);
            });
        }

        private static void ReportFault(string reason) =>
            SimulationAgreement.ReportFault("Multiplayer resync data: " + reason);

        private static void QueueUnity(Attempt attempt, Action action)
        {
            if (!UnityMainThreadDispatch.TryEnqueue(() =>
            {
                lock (Gate)
                    if (Live(attempt))
                        try { action(); }
                        catch (Exception error) { Fail(attempt, error.Message); }
            })) Fail(attempt, "The Unity transport dispatcher is unavailable.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using APIShared.Internal;
using APIShared.Networking;
using BepInEx.Logging;
using static APIShared.Multiplayer.MultiplayerResyncDataPacket;

namespace APIShared.Multiplayer
{
    // Process-lifetime adapters and registration. All repair flow is the same
    // instantiable session implementation exercised by the multi-peer fixtures.
    internal static partial class MultiplayerResyncDataRuntime
    {
        internal sealed class Record
        {
            internal Record(string owner, string id, int schema, int maximum,
                Func<byte[]> capture, Func<ReadOnlyMemory<byte>, object> prepare, Action<object> commit, bool captureAtStart)
            {
                Owner = owner; Id = id; Schema = schema; Maximum = maximum;
                Capture = capture; Prepare = prepare; Commit = commit;
                CaptureAtStart = captureAtStart;
            }
            internal readonly string Owner, Id;
            internal readonly int Schema, Maximum;
            internal readonly bool CaptureAtStart;
            internal readonly Func<byte[]> Capture;
            internal readonly Func<ReadOnlyMemory<byte>, object> Prepare;
            internal readonly Action<object> Commit;
        }

        private static readonly object Gate = new object();
        private static readonly List<Record> Providers = new List<Record>();
        private static MessageChannel<MultiplayerResyncDataPacket> channel;
        private static ManualLogSource log;
        private static bool frozen;
        private static string manifestDigest;
        private static MultiplayerResyncDataSession session;
        internal static bool IsAvailable => channel != null && MultiplayerResyncNativeRuntime.IsDataAvailable;
        // An elected world rejects completion even before its local attempt has opened.
        internal static bool HasActiveAttempt => SimulationAgreement.IsResyncCohortAdmitted;

        internal static void Initialize(ManualLogSource logger)
        {
            log = logger;
            session = new MultiplayerResyncDataSession(Providers, new MultiplayerResyncDataSession.Services
            {
                GetWorld = SimulationAgreement.GetAdmittedResyncWorld,
                BindWorld = SimulationAgreement.TryBindAdmittedResyncWorld,
                IsWorldCurrent = SimulationAgreement.IsAdmittedResyncWorldCurrent,
                VerifyRoster = SimulationAgreement.VerifyLiveResyncRoster,
                IsCohortAdmitted = () => HasActiveAttempt,
                IsFaulted = () => Economy.WorkerProductionWorldSafety.IsFaulted,
                IsHost = () => MultiplayerResyncNativeRuntime.IsNativeHost,
                IsClientReady = () => MultiplayerResyncNativeRuntime.IsNativeClientReady,
                HoldsEngineMonitor = () => MultiplayerResyncDataNativeRuntime.HoldsEngineMonitor,
                NativeRepairState = () => MultiplayerResyncNativeRuntime.NativeRepairState,
                NativeRosterMatches = MultiplayerResyncDataNativeRuntime.NativeRosterMatches,
                HasFreeQueueSlot = NativeChoreContext.HasFreeQueueSlot,
                Timestamp = Stopwatch.GetTimestamp,
                Frequency = Stopwatch.Frequency,
                NewNonce = NewNonce,
                EnqueueUnity = UnityMainThreadDispatch.TryEnqueue,
                Send = (recipient, packet) => channel.SendToSteamPeer(packet, recipient),
                UnderEngineMonitor = RunUnderEngineMonitor,
                ReportFault = reason => SimulationAgreement.ReportFault("Multiplayer resync data: " + reason),
                Completed = Completed
            });
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
                    uint earlyMask = 0;
                    int slot = 0;
                    foreach (var provider in Providers)
                    {
                        writer.Write(provider.Owner); writer.Write(provider.Id);
                        writer.Write(provider.Schema); writer.Write(provider.Maximum);
                        if (provider.CaptureAtStart) earlyMask |= 1u << slot;
                        slot++;
                    }
                    // Default-only cohorts retain their existing agreement digest.
                    if (earlyMask != 0) writer.Write(earlyMask);
                    writer.Flush();
                    manifestDigest = BitConverter.ToString(Hash(stream.ToArray())).Replace("-", "").ToLowerInvariant();
                }
                frozen = true;
                return manifestDigest;
            }
        }

        private static void ApplyAgreement(bool enabled) => session?.Reset();
        internal static void OnNativeStart() => session?.OnNativeStart();
        internal static void OnEngineRun() => session?.OnEngineRun();
        internal static bool AllowHostCompletion(long manager) => session?.AllowHostCompletion(manager) ?? !HasActiveAttempt;
        internal static bool AllowRemoteRepairPacket(int sender) => session?.AllowRemoteRepairPacket(sender) == true;
        internal static bool AllowRemoteCompletion(long manager, int sender) => session?.AllowRemoteCompletion(manager, sender) ?? !HasActiveAttempt;
        internal static bool BeforeNativeComplete() => session?.BeforeNativeComplete() ?? !HasActiveAttempt;
        internal static void OnNativeCompleted() => session?.OnNativeCompleted();
        internal static byte[][] CaptureRecords(IReadOnlyList<Record> providers, byte[][] records, bool earlyOnly) =>
            MultiplayerResyncDataSession.CaptureRecords(providers, records, earlyOnly);
        private static void RunUnderEngineMonitor(Action action)
        {
            lock (Economy.WorkerProductionWorldSafety.EngineGate) action();
        }
        private static void Completed(long repair, int records, int requested, int transferred) =>
            NativeApiLog.Info(log, $"RESYNC_DATA_COMPLETED repair={repair} records={records} " +
                $"requestedRecords={requested} transferredBytes={transferred}");
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
    }
}
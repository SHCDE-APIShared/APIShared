using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Economy;
using APIShared.Internal;
using APIShared.NativeSites;
using APIShared.Networking;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Multiplayer
{
    // Host: gate the specific E8 which sends Chore67 and then executes it locally.
    // Client: gate the already decoded issuer before 23F10 allocates a queue slot.
    internal static class MultiplayerResyncDataNativeRuntime
    {
        internal const string SiteName = "Multiplayer.ResyncData";
        internal const int ReceiveRva = 0x23F10, ReceiveDisplaced = 6, HostCallRva = 0x281B5;
        private const int QueueRva = 0x23990, HostFlagRva = 0x8574CC0;
        private const int PlayerSlots = 0x8B0, KickedPlayers = 0x919;
        // These three Vanilla opcodes have no named entries in the Extender's ChoreType enum.
        private const byte RepairStart = 54, RepairFinishData = 55, RepairComplete = 67;
        internal static readonly byte[] ReceiveEntry = { 0x48, 0x89, 0x5C, 0x24, 0x20, 0x57, 0x41, 0x56, 0x41, 0x57 };
        internal static readonly byte[] HostCall = { 0xE8, 0xD6, 0xB7, 0xFF, 0xFF };
        private static readonly byte[] HostSetup = { 0xB2, 0x43, 0x48, 0x8B, 0xCB };
        private static readonly byte[] HostContinuation = { 0x48, 0x8B, 0x5C, 0x24, 0x38 };

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void QueueCall(long manager, byte opcode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void ReceiveCall(long manager, byte opcode, int sender, int tick, int commandId, IntPtr payload);

        private static readonly QueueCall HostCallback = OnHostQueue;
        private static readonly ReceiveCall ReceiveCallback = OnReceive;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<ReceiveCall> receive;
        private static QueueCall queue;
        private static IntPtr module, relay;
        private static byte[] relayBytes, patchedCall;
        private static object engineGate;
        [ThreadStatic] private static bool authorizedCompletion;

        internal static bool IsReady => publication.IsReady;
        internal static bool HasAuthorizedCompletion => authorizedCompletion;
        internal static bool HoldsEngineMonitor => engineGate != null && Monitor.IsEntered(engineGate);
        internal static bool IsHost => IsReady && Marshal.ReadInt32(module + HostFlagRva) != 0;
        internal static bool IsClientReady => IsReady && !IsHost && (RepairState is 3 or 4);
        internal static int RepairState => IsReady ? Marshal.ReadInt32(module + MultiplayerResyncNativeRuntime.RepairStateRva) : 0;

        internal static NativeSiteSpan[] Spans() => new[]
        {
            new NativeSiteSpan(ReceiveRva, ReceiveDisplaced, NativeSiteMode.Exclusive),
            new NativeSiteSpan(ReceiveRva + ReceiveDisplaced, ReceiveEntry.Length - ReceiveDisplaced, NativeSiteMode.Observe),
            new NativeSiteSpan(HostCallRva - HostSetup.Length, HostSetup.Length, NativeSiteMode.Observe),
            new NativeSiteSpan(HostCallRva, HostCall.Length, NativeSiteMode.Exclusive),
            new NativeSiteSpan(HostCallRva + HostCall.Length, HostContinuation.Length, NativeSiteMode.Observe)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (!NativeStartupPatchWindow.IsOpen || hash != ApiSharedRuntime.SupportedHash || !NativeChoreContext.IsReady ||
                context.Memory.Length < HostFlagRva + sizeof(int) ||
                !context.Memory.Slice(ReceiveRva, ReceiveEntry.Length).SequenceEqual(ReceiveEntry) ||
                !context.Memory.Slice(HostCallRva - HostSetup.Length, HostSetup.Length).SequenceEqual(HostSetup) ||
                !context.Memory.Slice(HostCallRva, HostCall.Length).SequenceEqual(HostCall) ||
                !context.Memory.Slice(HostCallRva + HostCall.Length, HostContinuation.Length).SequenceEqual(HostContinuation) ||
                HostCallRva + HostCall.Length + BitConverter.ToInt32(HostCall, 1) != QueueRva)
                throw new InvalidOperationException("Resync data requires the audited enqueue and receive paths at startup.");

            engineGate = WorkerProductionWorldSafety.EngineGate;
            if (engineGate == null || !ReferenceEquals(engineGate, EngineMonitor.Current))
                throw new InvalidOperationException("Resync data requires the shared engine monitor.");

            module = context.ModuleHandle;
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, SiteName, Spans(), reason => new InvalidOperationException(reason));
            HookTransaction candidate = null;
            try
            {
                RequireUnpatched();
                queue = Marshal.GetDelegateForFunctionPointer<QueueCall>(module + QueueRva);
                ulong callAddress = Address(module + HostCallRva);
                relayBytes = CodePatch.BuildAbsoluteJump(Address(Marshal.GetFunctionPointerForDelegate(HostCallback)));
                relay = NativeMemoryManager.AllocateStub(callAddress, relayBytes.Length);
                if (relay == IntPtr.Zero || !NativeMemoryManager.WriteStub(relay, relayBytes))
                    throw new InvalidOperationException("Resync completion requires a nearby E8 relay.");
                patchedCall = EncodeCall(callAddress, Address(relay));
                NativeIndirectEntry.VerifyBytes(relay, relayBytes);

                candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions {
                            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false })) });
                receive = new DetourHandle<ReceiveCall>();
                candidate.AddDetour(receive, HookTarget.FromAddress(Address(module + ReceiveRva)), ReceiveCallback);
                var result = candidate.Commit();
                if (!result.IsCompleteSuccess || !receive.Success)
                    throw new InvalidOperationException("Resync data receive detour preparation failed.");
                NativeIndirectEntry.Validate(receive.Hook, module + ReceiveRva, ReceiveEntry, ReceiveDisplaced, false);
                RequireUnpatched();

                // Root both callbacks and the transaction before changing either span. On partial
                // publication both callbacks stay available and pass through while the feature is dormant.
                NativeHookPublication.Enable(candidate, result, publication, typeof(MultiplayerResyncDataNativeRuntime), () =>
                {
                    NativeIndirectEntry.VerifyBytes(module + HostCallRva, HostCall);
                    CodePatch.Write(Address(module + HostCallRva), patchedCall);
                    ValidatePublished();
                });
                foreach (NativeSiteSpan span in Spans())
                    if (span.Rva != ReceiveRva) // NativeHookPublication already watches the receive detour.
                        NativeForeignPatchGuard.Watch(Address(module + span.Rva), span.Length, SiteName, SiteName);
                NativeApiLog.Info(logger, "RESYNC_DATA_NATIVE_READY: host=281B5/E8; receive=23F10/6; engine monitor shared.");
            }
            catch
            {
                publication.RollbackUnpublished(candidate, () => ApiSharedNativeSites.Release(sites, SiteName));
                throw;
            }
        }

        private static void RequireUnpatched()
        {
            foreach (NativeSiteSpan span in Spans())
                NativeForeignPatchGuard.RequireUnpatched(Address(module + span.Rva), span.Length, SiteName, SiteName);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva - HostSetup.Length, HostSetup);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva, HostCall);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva + HostCall.Length, HostContinuation);
        }

        private static void ValidatePublished()
        {
            NativeIndirectEntry.Validate(receive.Hook, module + ReceiveRva, ReceiveEntry, ReceiveDisplaced, true);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva - HostSetup.Length, HostSetup);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva, patchedCall);
            NativeIndirectEntry.VerifyBytes(module + HostCallRva + HostCall.Length, HostContinuation);
            NativeIndirectEntry.VerifyBytes(relay, relayBytes);
        }

        internal static bool CheckIntegrity()
        {
            if (!IsReady) return false;
            try { ValidatePublished(); return true; }
            catch { return false; }
        }

        internal static byte[] EncodeCall(ulong site, ulong target)
        {
            long displacement = checked((long)target - (long)site - HostCall.Length);
            if (displacement < int.MinValue || displacement > int.MaxValue)
                throw new InvalidOperationException("Resync completion relay is outside E8 reach.");
            byte[] result = new byte[HostCall.Length];
            result[0] = 0xE8;
            Buffer.BlockCopy(BitConverter.GetBytes((int)displacement), 0, result, 1, sizeof(int));
            return result;
        }

        private static ulong Address(IntPtr value) => unchecked((ulong)value.ToInt64());

        private static bool MatchesManager(long manager) => NativeChoreContext.TryGetManager(out long expected) && expected == manager;

        // 28160 can mark unresponsive peers as kicked and synthesize their acknowledgements
        // just before reaching the completion CALL. Re-read slots before releasing that barrier.
        internal static bool NativeRosterMatches(long manager, int expectedPlayerMask)
        {
            if (!HoldsEngineMonitor || !MatchesManager(manager)) return false;
            int current = 0;
            var address = new IntPtr(manager);
            for (int player = 1; player <= 8; player++)
            {
                int offset = player - 1;
                if (Marshal.ReadInt32(address, PlayerSlots + offset * sizeof(int)) == -1) continue;
                if (Marshal.ReadByte(address, KickedPlayers + offset) != 0) return false;
                current |= 1 << player;
            }
            return current == expectedPlayerMask;
        }

        private static void OnHostQueue(long manager, byte opcode)
        {
            if (opcode != RepairComplete || !IsReady || !MultiplayerResyncDataRuntime.HasActiveAttempt)
            {
                queue(manager, opcode);
                return;
            }

            try
            {
                if (!HoldsEngineMonitor || !MatchesManager(manager) || !IsHost || RepairState <= 2)
                    throw new InvalidOperationException("Native host resync release reached an unexpected engine context.");
                if (WorkerProductionWorldSafety.IsFaulted || !MultiplayerResyncDataRuntime.AllowHostCompletion(manager)) return;
                authorizedCompletion = true;
                try { queue(manager, opcode); }
                finally { authorizedCompletion = false; }
                if (RepairState != 0 && !WorkerProductionWorldSafety.IsFaulted)
                    throw new InvalidOperationException("Native host resync release did not execute immediately.");
            }
            catch (Exception error) { MultiplayerResyncNativeRuntime.ReportFault(error); }
        }

        private static void OnReceive(long manager, byte opcode, int sender, int tick, int commandId, IntPtr payload)
        {
            if ((opcode != RepairStart && opcode != RepairFinishData && opcode != RepairComplete) ||
                !IsReady || !MultiplayerResyncDataRuntime.HasActiveAttempt)
            {
                receive.Original(manager, opcode, sender, tick, commandId, payload);
                return;
            }

            try
            {
                if (!HoldsEngineMonitor || !MatchesManager(manager))
                    throw new InvalidOperationException("Native receive resync command reached an unexpected engine context.");
                if (WorkerProductionWorldSafety.IsFaulted || IsHost) return;
                if (opcode != RepairComplete)
                {
                    if (MultiplayerResyncDataRuntime.AllowRemoteRepairPacket(sender))
                        receive.Original(manager, opcode, sender, tick, commandId, payload);
                    return;
                }
                if (tick != 0 || !MultiplayerResyncDataRuntime.AllowRemoteCompletion(manager, sender)) return;
                if (!IsClientReady)
                    throw new InvalidOperationException("Native client resync release preceded native data completion.");
                authorizedCompletion = true;
                try { receive.Original(manager, opcode, sender, tick, commandId, payload); }
                finally { authorizedCompletion = false; }
                if (RepairState != 0 && !WorkerProductionWorldSafety.IsFaulted)
                    throw new InvalidOperationException("Native client resync release did not execute immediately.");
            }
            catch (Exception error) { MultiplayerResyncNativeRuntime.ReportFault(error); }
        }
    }
}

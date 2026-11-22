using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using APIShared.Simulation;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Multiplayer
{
    // The two actual Chore handlers, never their PACK/SIZE callers or the UI's resync panel.
    // Full native audit: Chore54/67 and host28160; phase1 in67 changes phase to0 before return.
    internal static class MultiplayerResyncNativeRuntime
    {
        internal const string SiteName = "Multiplayer.ResyncLifecycle";
        internal const int StartRva = 0x15170, CompleteRva = 0x16E40, TransferPhaseRva = 0x85F8FEC,
            RepairStateRva = 0x85750CC;
        internal static readonly byte[] StartEntry = { 0x48, 0x83, 0xEC, 0x28, 0x8B, 0x05, 0x72, 0x3E, 0x5E, 0x08 };
        internal static readonly byte[] CompleteEntry = { 0x48, 0x89, 0x7C, 0x24, 0x18, 0x55 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void ChoreCall();
        private static readonly ChoreCall StartCallback = OnStart, CompleteCallback = OnComplete;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<ChoreCall> start, complete;
        private static HookTransaction transaction;
        private static IntPtr module;
        private static ManualLogSource log;
        private static int initialized, healthy;
        private static bool runGuardReady;
        internal static bool IsAvailable => publication.IsReady && runGuardReady && Volatile.Read(ref healthy) != 0;
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(StartRva, StartEntry.Length, NativeSiteMode.Exclusive),
            new NativeSiteSpan(CompleteRva, CompleteEntry.Length, NativeSiteMode.Exclusive)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) { publication.ThrowIfUnavailable(); return; }
            log = logger;
            if (hash != Economy.AiMarketNativeRuntime.AuditedHash)
                throw new InvalidOperationException("Resync lifecycle requires the audited native image.");
            if (!Economy.WorkerProductionWorldSafety.IsAvailable || !Economy.WorkerProductionRuntime.HasLifecycleOwner)
                throw new InvalidOperationException("Resync lifecycle requires the terminal engine safety boundary and mission admission owner.");
            if (!context.Memory.Slice(StartRva, StartEntry.Length).SequenceEqual(StartEntry) ||
                !context.Memory.Slice(CompleteRva, CompleteEntry.Length).SequenceEqual(CompleteEntry))
                throw new InvalidOperationException("Resync handler load-image entries differ.");
            // The saved entry's RIP operand independently binds the phase read by our callback.
            if (StartRva + StartEntry.Length + BitConverter.ToInt32(StartEntry, 6) != TransferPhaseRva ||
                RepairStateRva > context.Memory.Length - sizeof(int) || TransferPhaseRva > context.Memory.Length - sizeof(int))
                throw new InvalidOperationException("Resync phase/state globals differ from the audited image.");
            module = context.ModuleHandle;
            NativeForeignPatchGuard.RequireUnpatched(unchecked((ulong)(module + StartRva).ToInt64()), StartEntry.Length, "resync-start", SiteName);
            NativeForeignPatchGuard.RequireUnpatched(unchecked((ulong)(module + CompleteRva).ToInt64()), CompleteEntry.Length, "resync-complete", SiteName);
            NativeIndirectEntry.VerifyBytes(module + StartRva, StartEntry);
            NativeIndirectEntry.VerifyBytes(module + CompleteRva, CompleteEntry);
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            HookTransaction candidate = null;
            bool claimed = false;
            try
            {
                ApiSharedNativeSites.Claim(sites, SiteName, Spans(), m => new InvalidOperationException(m)); claimed = true;
                candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions {
                            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false })) });
                start = new DetourHandle<ChoreCall>(); complete = new DetourHandle<ChoreCall>();
                candidate.AddDetour(start, HookTarget.FromAddress(unchecked((ulong)(module + StartRva).ToInt64())), StartCallback);
                candidate.AddDetour(complete, HookTarget.FromAddress(unchecked((ulong)(module + CompleteRva).ToInt64())), CompleteCallback);
                var result = candidate.Commit();
                if (!result.IsCompleteSuccess || !start.Success || !complete.Success)
                    throw new InvalidOperationException("Resync boundary preparation failed.");
                ValidateEntry(start.Hook, module + StartRva, StartEntry, false);
                ValidateEntry(complete.Hook, module + CompleteRva, CompleteEntry, false);
                if (!EngineRunEvents.TryRegister(APISharedPlugin.PluginGuid, "resync-boundary-guard", GuardRun, GuardRun, out string reason))
                    throw new InvalidOperationException(reason);
                runGuardReady = true;
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var diagnostic) ||
                    !lifecycle.TryRegisterObserver("resync-boundaries", null,
                        note => MultiplayerResyncEvents.Registry.EndWorld(note.Context.SessionId), OnInitializing, out diagnostic))
                    throw new InvalidOperationException(diagnostic?.Reason ?? "Resync lifecycle binding failed.");
                transaction = candidate;
                NativeHookPublication.Enable(candidate, result, publication, typeof(MultiplayerResyncNativeRuntime), () => {
                    ValidateEntry(start.Hook, module + StartRva, StartEntry, true);
                    ValidateEntry(complete.Hook, module + CompleteRva, CompleteEntry, true);
                });
                Volatile.Write(ref healthy, 1);
                NativeApiLog.Info(log, "RESYNC_LIFECYCLE_READY: reference-rva=15170/10,16E40/6; entryPhase0-only; start-before/checksum, complete-after; local abort never resumes participants.");
            }
            catch
            {
                publication.RollbackUnpublished(candidate, () => { if (claimed) ApiSharedNativeSites.Release(sites, SiteName); });
                throw;
            }
        }
        private static void OnInitializing(MissionLifecycleNotification note)
        {
            if (note.Phase != MissionInitializationPhase.BeforeLoad && note.Phase != MissionInitializationPhase.BeforeNativeStart) return;
            MultiplayerResyncEvents.Registry.BeginWorld(note.Context.SessionId);
            if (publication.IsReady && !CheckIntegrity())
            {
                Volatile.Write(ref healthy, 0);
                NativeForeignPatchGuard.Recheck();
                NativeApiLog.Error(log, "RESYNC_LIFECYCLE_UNAVAILABLE: published native boundaries differ; no executable retry.");
            }
        }
        private static void GuardRun()
        {
            if (module == IntPtr.Zero || !MultiplayerResyncEvents.Registry.IsInProgress) return;
            MultiplayerResyncEvents.Registry.ValidateNativeState(Marshal.ReadInt32(module + RepairStateRva));
        }
        private static void OnStart()
        {
            int phase = Marshal.ReadInt32(module + TransferPhaseRva);
            if (IsAvailable) MultiplayerResyncEvents.Registry.Start(phase);
            start.Original();
        }
        private static void OnComplete()
        {
            int phase = Marshal.ReadInt32(module + TransferPhaseRva); // PACK mutates the global phase itself.
            complete.Original();
            if (IsAvailable) MultiplayerResyncEvents.Registry.Complete(phase);
        }
        internal static void ReportFault(Exception error)
        {
            try { NativeApiLog.Error(log, "RESYNC_PARTICIPANT_FAILED: " + error); } catch { }
            try { SimulationAgreement.ReportFault("A required resync participant failed: " + error.Message); } catch { }
            // Public participants need not register a SimulationAgreement feature. Its absent
            // receipt must not turn a critical local failure into a merely diagnostic fence.
            try { Economy.WorkerProductionWorldSafety.Fault("A required resync participant failed: " + error.Message); } catch { }
        }
        internal static bool CheckIntegrity()
        {
            if (!IsAvailable) return false;
            try
            {
                ValidateEntry(start.Hook, module + StartRva, StartEntry, true);
                ValidateEntry(complete.Hook, module + CompleteRva, CompleteEntry, true);
                return true;
            }
            catch { return false; }
        }

        internal static void ValidateEntry(IDetour<ChoreCall> hook, IntPtr target, byte[] entry, bool installed)
        {
            if (entry.Length == CompleteEntry.Length)
            { NativeIndirectEntry.Validate(hook, target, entry, entry.Length, installed); return; }
            var native = hook as NativeDetour<ChoreCall>;
            if (native == null || native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != entry.Length || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Resync start Indirect/10 backend contract differs.");
            if (!installed) NativeIndirectEntry.VerifyBytes(target, entry);
            else
            {
                if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                    target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                    Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                    throw new InvalidOperationException("Resync start published pointer slot differs.");
                for (int i = 6; i < entry.Length; i++)
                    if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Resync start padding differs.");
            }
            var bytes = NativeCodeReader.ReadBounded(native.TrampolineAddress, 32);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(bytes));
            decoder.IP = unchecked((ulong)native.TrampolineAddress.ToInt64());
            var sub = decoder.Decode(); var read = decoder.Decode(); var jump = decoder.Decode();
            ulong originalOperand = unchecked((ulong)(target + entry.Length).ToInt64()) + unchecked((ulong)(long)BitConverter.ToInt32(entry, 6));
            if (sub.Code != Code.Sub_rm64_imm8 || sub.Op0Register != Register.RSP || sub.Immediate8 != 0x28 ||
                read.Code != Code.Mov_r32_rm32 || read.Op0Register != Register.EAX || !read.IsIPRelativeMemoryOperand ||
                read.IPRelativeMemoryAddress != originalOperand || jump.Mnemonic != Mnemonic.Jmp ||
                jump.Op0Kind != OpKind.NearBranch64 || jump.NearBranchTarget != unchecked((ulong)(target + entry.Length).ToInt64()))
                throw new InvalidOperationException("Resync start relocated RIP read or continuation differs.");
        }
    }
}

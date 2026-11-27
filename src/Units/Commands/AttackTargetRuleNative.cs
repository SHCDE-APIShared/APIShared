using System;
using System.Runtime.InteropServices;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Bootstrap;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Units
{
    internal static unsafe class AttackTargetRuleNative
    {
        internal const string SiteName = "Units.AttackTargetRules";
        internal const int EntryRva = 0x1867A0;
        internal const int DisplacedLength = 8;
        private const int ManagerRva = 0x67E8400;
        private const int UnitSentinel = 0x65C;
        private const int UnitStride = 0x490;
        private const int MaximumUnitId = 9999;
        private const string QoL = "BugfixesAndQoL_Serp";
        private static readonly Version SharedEntryQoLVersion = new Version(1, 0, 218);
        internal static readonly byte[] Entry = {
            0x48, 0x89, 0x5C, 0x24, 0x08, 0x48, 0x63, 0xC2, 0x48, 0x8B, 0xD9,
            0x4C, 0x69, 0xD8, 0x90, 0x04, 0x00, 0x00, 0x4C, 0x03, 0xD9
        };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CheckPair(IntPtr manager, int attacker, int target, byte mode);
        private static readonly CheckPair Callback = Check;
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(new NativeDetourOptions
        { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static CrusaderLibraryLoadContext context;
        private static ManualLogSource log;
        private static DetourHandle<CheckPair> handle;
        private static IntPtr demand, managerAddress, peaceFlag;
        private static bool startupFinished;
        private static string failure;

        internal static bool IsReady => Publication.IsReady;
        internal static NativeSiteSpan[] Spans() =>
            new[] { new NativeSiteSpan(EntryRva, DisplacedLength, NativeSiteMode.Exclusive) };

        internal static void Initialize(CrusaderLibraryLoadContext loaded, string hash, ManualLogSource logger)
        {
            log = logger;
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
            {
                failure = "Attack target rules require the audited native image.";
                return;
            }
            context = loaded;
            if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var error) ||
                !lifecycle.TryRegisterObserver("attack-target-rules", null,
                    ended => AttackTargetRules.Retire(ended.Context.SessionId), null, out error))
            {
                failure = "Attack target rules require mission lifecycle: " + error?.Reason;
                return;
            }
            if (!UnityMainThreadDispatch.TryEnqueue(AttackTargetRules.FinishStartup))
                NativeApiLog.Info(log, "Attack target rule startup dispatch unavailable. Compatible QoL can request installation during its native initialization.");
        }

        // Called under the rule registry lock. PluginInfos is populated before each Awake,
        // but does not describe later plugins until the synchronous startup loop has returned.
        internal static bool PrepareRegistration(string owner, out string reason)
        {
            if (IsReady) { reason = string.Empty; return true; }
            bool hasQoL = Chainloader.PluginInfos.TryGetValue(QoL, out var plugin);
            bool legacyQoL = hasQoL && plugin.Metadata.Version < SharedEntryQoLVersion;
            if (legacyQoL)
            {
                reason = "Legacy BugfixesAndQoL owns the native target entry.";
                return false;
            }
            if (failure != null) { reason = failure; return false; }
            if (context == null) { reason = "The native target entry is not initialized."; return false; }
            if ((owner == QoL && hasQoL) || startupFinished)
                return TryInstall(out reason);
            reason = string.Empty;
            return true;
        }

        internal static void FinishStartup(bool hasConsumers)
        {
            startupFinished = true;
            if (hasConsumers && !PrepareRegistration(string.Empty, out string reason))
                NativeApiLog.Info(log, "Attack target rules unavailable: " + reason);
        }

        internal static void SetDemand(int value)
        {
            if (demand != IntPtr.Zero) System.Threading.Volatile.Write(ref *(int*)demand, IsReady ? value : 0);
        }

        private static bool TryInstall(out string reason)
        {
            HookTransaction candidate = null;
            var registry = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            bool claimed = false;
            try
            {
                if (MissionLifecycleService.HasContext)
                    throw new InvalidOperationException("Register attack target rules before entering a mission.");
                var image = context.Memory;
                if (!image.Slice(EntryRva, Entry.Length).SequenceEqual(Entry) ||
                    Simulation.PeaceTimeState.ValidateAnchor(image) != Simulation.PeaceTimeState.FlagRva ||
                    image.Length < ManagerRva + UnitSentinel + (MaximumUnitId + 1) * UnitStride ||
                    Marshal.SizeOf(typeof(GameUnit)) != UnitStride ||
                    Marshal.OffsetOf(typeof(GameUnit), nameof(GameUnit.r_UnitChimp)).ToInt32() != 0x8A ||
                    Marshal.OffsetOf(typeof(GameUnit), nameof(GameUnit.r_ControllableForPlayerId)).ToInt32() != 0x92 ||
                    Marshal.OffsetOf(typeof(GameUnit), nameof(GameUnit.r_GlobalId)).ToInt32() != 0x94 ||
                    typeof(GameUnit).GetField(nameof(GameUnit.r_ControllableForPlayerId))?.FieldType != typeof(ushort))
                    throw new InvalidOperationException("Attack target native entry or unit layout differs.");
                var target = context.ModuleHandle + EntryRva;
                NativeIndirectEntry.VerifyBytes(target, Entry);
                ApiSharedNativeSites.Claim(registry, SiteName, Spans(), text => new InvalidOperationException(text));
                claimed = true;
                managerAddress = context.ModuleHandle + ManagerRva;
                peaceFlag = context.ModuleHandle + Simulation.PeaceTimeState.FlagRva;
                demand = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(demand, 0);
                candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(Backend) });
                handle = new DetourHandle<CheckPair>();
                var gate = new DemandGate(demand, peaceFlag, Callback);
                candidate.AddDetour(handle, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), Callback, gate);
                var result = candidate.Commit();
                if (!result.IsCompleteSuccess || !handle.Success)
                    throw new InvalidOperationException("Attack target hook preparation failed.");
                NativeIndirectEntry.Validate(handle.Hook, target, Entry, DisplacedLength, false);
                NativeHookPublication.Enable(candidate, result, Publication, typeof(AttackTargetRuleNative),
                    () => NativeIndirectEntry.Validate(handle.Hook, target, Entry, DisplacedLength, true));
                NativeApiLog.Info(log, "Attack target rules ready at RVA 0x1867A0, indirect span 8 bytes.");
                reason = string.Empty;
                return true;
            }
            catch (Exception error)
            {
                Publication.RollbackUnpublished(candidate, () =>
                {
                    if (demand != IntPtr.Zero) { Marshal.FreeHGlobal(demand); demand = IntPtr.Zero; }
                    if (claimed) ApiSharedNativeSites.Release(registry, SiteName);
                });
                reason = failure = error.Message;
                return false;
            }
        }

        private static int Check(IntPtr manager, int attackerId, int targetId, byte mode)
        {
            if (IsReady && manager == managerAddress && attackerId > 0 && attackerId <= MaximumUnitId &&
                targetId > 0 && targetId <= MaximumUnitId)
            {
                var attacker = ReadUnit(manager, attackerId);
                var target = ReadUnit(manager, targetId);
                if (AttackTargetRules.Excludes(attacker, target, *(byte*)peaceFlag != 0)) return 1;
            }
            return handle.Original(manager, attackerId, targetId, mode);
        }

        private static AttackTargetUnit ReadUnit(IntPtr manager, int unitId)
        {
            var unit = (GameUnit*)((byte*)manager + UnitSentinel + unitId * UnitStride);
            return new AttackTargetUnit(unit->r_ControllableForPlayerId, unit->r_UnitChimp, unit->r_GlobalId);
        }

        private sealed class DemandGate : IDetourIntermediaryFactory
        {
            private readonly IntPtr demand, flag;
            private readonly ulong callback;
            internal DemandGate(IntPtr demand, IntPtr flag, Delegate callback)
            {
                this.demand = demand;
                this.flag = flag;
                this.callback = unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64());
            }
            public DetourIntermediaryStubs Create(in DetourIntermediaryContext context)
            {
                if (context.Architecture != DetourArchitecture.X64 || context.TrampolineAddress == IntPtr.Zero)
                    throw new InvalidOperationException("Attack target rule gate requires an x64 trampoline.");
                ulong original = unchecked((ulong)context.TrampolineAddress.ToInt64());
                var stub = ExecutableFunction.Create(a => GenerateGate(a, unchecked((ulong)demand.ToInt64()),
                    unchecked((ulong)flag.ToInt64()), original, callback));
                return new DetourIntermediaryStubs(stub.EntryPoint, context.TrampolineAddress, stub);
            }
        }

        internal static void GenerateGate(Assembler assembler, ulong demandAddress, ulong peaceAddress,
            ulong original, ulong callback)
        {
            var dormant = assembler.CreateLabel();
            var managed = assembler.CreateLabel();
            assembler.pushfq(); assembler.push(rax);
            assembler.mov(rax, demandAddress); assembler.mov(eax, __dword_ptr[rax]);
            assembler.test(eax, 1); assembler.jne(managed);
            assembler.test(eax, 2); assembler.je(dormant);
            assembler.mov(rax, peaceAddress); assembler.cmp(__byte_ptr[rax], 0); assembler.je(dormant);
            assembler.Label(ref managed); assembler.pop(rax); assembler.popfq(); assembler.AddUnrestrictedJmp(callback);
            assembler.Label(ref dormant); assembler.pop(rax); assembler.popfq(); assembler.AddUnrestrictedJmp(original);
        }
    }
}

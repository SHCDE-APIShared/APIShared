using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using APIShared.NativeSites;
using APIShared.Networking;
using Iced.Intel;

namespace APIShared.Buildings
{
    internal static unsafe partial class HumanConstructionRuntime
    {
        internal const string DisbandSite = "Buildings.ConstructionDisband";
        internal const int DisbandRva = 0x17B10;
        internal const int DisbandLength = 421;
        internal const string DisbandHash = "ED1F269B1FED7EAB0F568734CE46BF921ECD1D517BC5DCA4FED92BA89BECFFE5";
        // Indirect/6: push rbx (2), sub rsp,30h (4). Every branch target is >=entry+60h.
        private static readonly byte[] DisbandEntry = { 0x40, 0x53, 0x48, 0x83, 0xEC, 0x30 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DisbandCommand();
        private static DetourHandle<DisbandCommand> disbandHook;
        private static HookTransaction disbandTransaction;
        private static readonly NativeHookPublicationState disbandPublication = new NativeHookPublicationState();
        [ThreadStatic] private static RefundFrame disband;
        [ThreadStatic] private static bool disbandRefundExecuted;
        internal static NativeSiteSpan[] DisbandSpans() => new[] { new NativeSiteSpan(DisbandRva, 6, NativeSiteMode.Exclusive) };
        internal static NativeDetourOptions DisbandBackendOptions => new NativeDetourOptions
        {
            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
            Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = DisbandLength, SuspendThreadsDuringPatch = false
        };

        private static void InitializeDisband(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (!NativeStartupPatchWindow.IsOpen || hash != ApiSharedRuntime.SupportedHash)
                throw new InvalidOperationException("Construction disband requires the audited startup image.");
            if (!context.Memory.Slice(DisbandRva, 6).SequenceEqual(DisbandEntry) ||
                ApiSharedRuntime.ComputeSha256(context.Memory.Slice(DisbandRva, DisbandLength)) != DisbandHash)
                throw new InvalidOperationException("Construction disband handler changed.");
            IntPtr target = context.ModuleHandle + DisbandRva;
            NativeIndirectEntry.VerifyBytes(target, DisbandEntry);
            var ownership = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(ownership, DisbandSite, DisbandSpans(), reason => new InvalidOperationException(reason));
            HookTransaction candidate = null;
            try
            {
                candidate = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { OwnsHooks = true, InstallOnCommit = false,
                        FailureMode = TransactionFailureMode.RollbackAndThrow, Backend = new NativeDetourBackend(DisbandBackendOptions) });
                disbandHook = new DetourHandle<DisbandCommand>();
                candidate.AddDetour(disbandHook, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), (DisbandCommand)ExecuteDisband);
                var result = candidate.Commit();
                if (!result.IsCompleteSuccess || !disbandHook.Success) throw new InvalidOperationException("Construction disband preparation failed.");
                ValidateDisbandEntry(disbandHook.Hook, target, false);
                disbandTransaction = candidate;
                NativeHookPublication.Enable(candidate, result, disbandPublication, typeof(HumanConstructionRuntime),
                    () => ValidateDisbandEntry(disbandHook.Hook, target, true));
                NativeApiLog.Info(logger, "CONSTRUCTION_DISBAND: method=reference-rva; rva=0x17B10; Indirect/6; process lifetime.");
            }
            catch
            {
                disbandPublication.RollbackUnpublished(candidate, () =>
                { disbandHook = null; disbandTransaction = null; ApiSharedNativeSites.Release(ownership, DisbandSite); });
                throw;
            }
        }

        internal static void ValidateDisbandEntry<T>(IDetour<T> hook, IntPtr target, bool installed) where T : Delegate
        {
            var native = hook as NativeDetour<T>;
            if (native == null || native.IsInstalled != installed || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != 6 || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                native.HookEntryPointAddress == IntPtr.Zero || native.OriginalEntryPointAddress != native.TrampolineAddress)
                throw new InvalidOperationException("Construction disband Indirect backend differs.");
            if (!installed) NativeIndirectEntry.VerifyBytes(target, DisbandEntry);
            else if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                throw new InvalidOperationException("Construction disband installed entry differs.");

            // NativeX64 reencodes the two instructions: redundant REX 40 before PUSH RBX
            // disappears. Relocated length is five; the original continuation remains +6.
            byte[] relocated = { 0x53, 0x48, 0x83, 0xEC, 0x30 };
            if (native.TrampolineSize != relocated.Length + 5)
                throw new InvalidOperationException("Construction disband trampoline size differs.");
            NativeIndirectEntry.VerifyBytes(native.TrampolineAddress, relocated);
            byte[] jumpBytes = new byte[5];
            Marshal.Copy(native.TrampolineAddress + relocated.Length, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + relocated.Length).ToInt64());
            decoder.Decode(out Instruction jump);
            if (jump.Code != Code.Jmp_rel32_64 || jump.Length != 5 || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + 6).ToInt64()))
                throw new InvalidOperationException("Construction disband trampoline continuation differs.");
        }

        private static bool TryCaptureDisband(out RefundFrame frame)
        {
            frame = null;
            if (!ready || !HumanConstructionCosts.HasReceipts || !NativeChoreContext.TryCaptureRaw(75, out var chore) || !Human(chore.Sender)) return false;
            long entry = chore.Manager + 0xB0BF8 + chore.Slot * 0x500L;
            int cursor = Marshal.ReadInt32(new IntPtr(chore.Manager + 0x370BF8));
            if (!TryDecodeDisbandPending(new ReadOnlySpan<byte>((void*)entry, 0x500), cursor, out short mode, out short slot) ||
                !NativeChoreContext.TryCaptureRaw(75, out var after) || !after.Matches(chore)) return false;
            ConstructionObjectIdentity identity;
            ConstructionCostTarget target;
            int percentage;
            if (mode == 2)
            {
                if (!UnitAccess.TryGetById(slot, out GameUnit* unit, out _) || unit->r_AliveState != AliveState.IsAlive ||
                    unit->r_ControllableForPlayerId != chore.Sender ||
                    (unit->r_UnitChimp != eChimps.CHIMP_TYPE_MANGONEL && unit->r_UnitChimp != eChimps.CHIMP_TYPE_BALLISTA)) return false;
                identity = new ConstructionObjectIdentity(ConstructionObjectKind.Unit, slot, unit->r_GlobalId);
                target = ConstructionCostTarget.FromStructure(unit->r_UnitChimp == eChimps.CHIMP_TYPE_MANGONEL ? eStructs.STRUCT_MANGONEL : eStructs.STRUCT_BALLISTA);
                percentage = 100;
            }
            else if (mode == 1)
            {
                var span = GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
                if (slot < 25 || slot >= span.Length || span[slot].r_AliveState != AliveState.IsAlive ||
                    span[slot].r_ProjectileType != ProjectileType.Brazier || (ushort)span[slot].r_PlayerSourceId != chore.Sender) return false;
                identity = new ConstructionObjectIdentity(ConstructionObjectKind.Decoration, slot, span[slot].r_GlobalId);
                target = ConstructionCostTarget.FromStructure(eStructs.STRUCT_BRAZIER);
                // Chore 75 is emitted by construction Undo (63CD0), whose building arm
                // passes 100 to Chore 29. Decorations use the same Undo action percentage.
                percentage = 100;
            }
            else return false;
            frame = new RefundFrame { Identity = identity, Player = chore.Sender, Target = target, Chore = chore, Percentage = percentage };
            return true;
        }

        internal static bool TryDecodeDisbandPending(ReadOnlySpan<byte> pending, int cursor, out short mode, out short slot)
        {
            mode = 0; slot = 0;
            // Chore 75 is two native int16 fields at the generic slot's parameter offset +17.
            // Only custom Chore 106 puts its own int32 length prefix there. Queued execution
            // resets the cursor before entry; payloadSize may still belong to a previous opcode
            // because this handler publishes its size of four AFTER the entry hook.
            if (cursor != 0 || pending.Length < 21) return false;
            mode = unchecked((short)(pending[17] | pending[18] << 8));
            slot = unchecked((short)(pending[19] | pending[20] << 8));
            return (mode == 1 || mode == 2) && slot > 0;
        }

        private static void ExecuteDisband()
        {
            RefundFrame captured = null;
            try { TryCaptureDisband(out captured); }
            catch (Exception error) { ReportFailure("disband capture", error); }
            RefundFrame previous = disband;
            bool previousExecuted = disbandRefundExecuted;
            disband = captured; disbandRefundExecuted = false;
            try
            {
                // Native mode 2 refunds and only then marks the unit. The native return is the
                // first boundary at which both operations are complete, not C4480's Post event.
                disbandHook.Original();
                if (captured == null) return;
                try
                {
                    if (!WasDisbanded(captured)) return;
                    if (captured.Identity.Kind == ConstructionObjectKind.Unit)
                    {
                        if (disbandRefundExecuted) PayDisbandRefund(captured);
                        else HumanConstructionCosts.Retire(captured.Identity);
                    }
                    else PayRefund(captured.Identity, captured.Player, captured.Player,
                        ConstructionRefundPolicies.Evaluate(captured.Target, captured.Player, captured.Percentage));
                }
                catch (Exception error) { ReportFailure("disband refund", error); }
            }
            finally { disband = previous; disbandRefundExecuted = previousExecuted; }
        }
        private static bool WasDisbanded(RefundFrame frame)
        {
            var identity = frame.Identity;
            if (identity.Kind == ConstructionObjectKind.Unit)
                return UnitAccess.TryGetById(identity.Slot, out GameUnit* unit, out _) && unit->r_GlobalId == identity.GlobalId &&
                    unit->r_ControllableForPlayerId == frame.Player && unit->r_AliveState == AliveState.MarkedForDeletion;
            var span = GameProjectileManagerAPI.Instance.GetProjectilesAsSpan();
            return identity.Slot < span.Length && span[identity.Slot].r_GlobalId == identity.GlobalId &&
                (ushort)span[identity.Slot].r_PlayerSourceId == frame.Player && span[identity.Slot].r_AliveState == AliveState.MarkedForDeletion;
        }
        private static bool TryDisbandRefund(Delegate original, object[] args, out object result)
        {
            result = null;
            var frame = disband;
            int sentinel = (int)args[1], actor = (int)args[2];
            int? owner = frame == null ? null : CurrentOwner(frame.Identity);
            if (frame == null || frame.Identity.Kind != ConstructionObjectKind.Unit || frame.Player != actor ||
                !NativeChoreContext.TryCaptureRaw(75, out var chore) || !chore.Matches(frame.Chore) ||
                !owner.HasValue || owner.Value != actor) return false;
            int expected = frame.Target.Equals(ConstructionCostTarget.FromStructure(eStructs.STRUCT_MANGONEL)) ? -3 : -4;
            if (sentinel != expected) return false;
            frame.Percentage = ConstructionRefundPolicies.Evaluate(frame.Target, actor, (int)args[3]);
            args[3] = frame.Percentage;
            var previous = refund; refund = frame;
            try { result = InvokeRefundOriginal(original, args); disbandRefundExecuted = true; return true; }
            finally { refund = previous; }
        }
        private static int QuoteFrameRefund(RefundFrame frame, eGoods good, int paid) =>
            frame.Identity.Kind == ConstructionObjectKind.Unit ? HumanConstructionRefundMath.StationaryUnit(paid, frame.Percentage) : QuoteMaterialRefund(good, paid, frame.Percentage);
        private static void PayDisbandRefund(RefundFrame frame)
        {
            if (!HumanConstructionCosts.TryConsumeRefund(frame.Identity, frame.Player, frame.Player, Human(frame.Player),
                (good, paid) => QuoteFrameRefund(frame, good, paid), out var amounts)) return;
            foreach (var amount in amounts) if (amount.Value > 0) GamePlayerManagerAPI.Instance.TryAddGood(frame.Player, amount.Key, amount.Value);
        }
    }
}

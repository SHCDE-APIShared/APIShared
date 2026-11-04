using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.NativeSites;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // Only this owner calls/observes the native gift primitive. Its implicit globals and record
    // layouts are hash-bound; resolving a new prologue alone cannot grant a new data contract.
    internal static unsafe class AllyTransfersNative
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const string SiteName = "Economy.AllyTransfers";
        internal const int TransferRva = 0xD7AD0, CapacityRva = 0xC5AD0, DisplacedLength = 9;
        internal static readonly byte[] TransferBytes = Convert.FromBase64String(
            "QFVWQVRIg+xASIlcJGBFM+RIiXwkaEGL+EyJbCRwTIl0JDhEi/JJY+lMiXwkMEyL/Uhj8YP6D3VPTWnXPFgAAEyNJflGWQNNA9RMjS3/x0kIRTmCzPASAEEPTLrM8BIAQQG8tcCtJwBIacY8WAAAQgG8IMzwEgBBKbrM8BIAQQG8reStJwDreEGD/ghIjQ1NUD8GuwcAAABEi89BD0Xei9ZEi8PoN939/0SLz0SJZCQgRIvDSI0NJVA/BovV6O4B/v9MjSV3RlkDRIvPSYvMRIvDi9bo52///0yNLXDHSQhEi89BAYS1wK0nAESLw4vWSYvM6Mhv//9BAYSt5K0nADLbOzWYwYAIdW//FXA3EwBIjQ0ZhPL/OYSpkFuQAHNZswH/FVg3EwAFMHUAAEiNFfyD8v+JhKqQW5AAQY1G+oP4EnckSJgPtoQCKH0NAIuMghx9DQBIA8r/4bokAAAA6wy6JwAAAOsFuiMAAABIjQ1MyD4G6Pes8v+LBUHPSQiFwHRkg/hjdF87NRLBgAh1M4TbdC9EiXQkKESLzUSLxol8JCC6CgAAAEmLzehQTPT/SI0VGaUTAEiNDUL2gAjoXXHz/zst18CACHUcRIl0JChEi85Ei8WJfCQgug8AAABJi83oGUz0/0yLbCRwSIt8JGhIi1wkYElpxx4sAABMi3wkMEgDxkiNDABCD7+EIeQjEwBEO/BMi3QkOHUUM8BmQomEIeQjEwBmQomEIfgjEwBEi8VIjQ1GTPcDi9ZIg8RAQVxeXelXe/b/");
        internal static readonly byte[] CapacityBytes = Convert.FromBase64String(
            "iVQkEFNXQVVBV0iD7DhNY+iL+kGL1UyL+bv/////6D2i//+JRCR4i9CD+Ch1ELhAQg8ASIPEOEFfQV1fW8NIiWwkYL0BAAAAQTlvUA+O2wAAAEiJdCQwSI0NcgQiAEyJZCQoSY23XgQAAEyJdCQgTo0kqU2Nt6gEAABPjTSuZpBmg376AnV9D78GO8d1dg+/Tvw7ynVuM/+D+/8PRfuD+Qt1FkWLxYvVSYvP6EAUAACLVCR4jRwH60eD+RN1HEG5+gAAAEWLxYvVSYvP6M94//+LVCR4jRwH6yaDvq4AAAAAdBdBiwaL34XAdBRBiwwkO8F9DIvZK9jrBEGLHCQD34t8JGj/xUiBxiwDAABJgcYsAwAAQTtvUA+MYv///0yLdCQgTItkJChIi3QkMEiLbCRgi8NIg8Q4QV9BXV9bww==");
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void TransferCall(int recipient, int good, int amount, int sender);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int CapacityCall(IntPtr buildings, int recipient, int good);
        internal static readonly NativeDetourBackend DetourBackend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        internal static readonly IAllyTransfersBackend EngineBackend = new Backend();
        private static readonly AllyTransferEvents.ReadBalance BalanceReader = ReadStock;
        private static readonly Installation owner = new Installation();
        private static CapacityCall capacity;
        private static IntPtr module;
        private static string hash;
        private static ManualLogSource logger;
        private static Exception startupFailure;
        private static int initialized, callFailures;
        internal static bool IsAvailable => owner.Publication.IsReady && Volatile.Read(ref startupFailure) == null;

        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(TransferRva, DisplacedLength, NativeSiteMode.Exclusive),
            new NativeSiteSpan(TransferRva + DisplacedLength, TransferBytes.Length - DisplacedLength, NativeSiteMode.Observe),
            new NativeSiteSpan(CapacityRva, CapacityBytes.Length, NativeSiteMode.Observe) };

        // Startup preparation roots the permanent owner before WhenReady can reach consumers.
        internal static void Initialize(IntPtr moduleHandle, ScanRegion region, ReadOnlySpan<byte> pristine,
            string binaryHash, ManualLogSource log)
        {
            if (Interlocked.CompareExchange(ref initialized, 1, 0) != 0)
            {
                if (module != moduleHandle || !string.Equals(hash, binaryHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ally transfers cannot change their native process image.");
                RequireReady(); return;
            }
            module = moduleHandle; hash = binaryHash; logger = log;
            HookTransaction pending = null;
            NativeOwnershipRegistry sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            bool claimed = false;
            try
            {
                if (module == IntPtr.Zero || region == null || !string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Ally transfers require the audited native image and LibraryLoaded initialization.");
                ValidatePristine(pristine);
                VerifyBytes(module + TransferRva, TransferBytes);
                VerifyBytes(module + CapacityRva, CapacityBytes);
                ApiSharedNativeSites.Claim(sites, SiteName, Spans(), message => new InvalidOperationException(message));
                claimed = true;
                var preparedCapacity = (CapacityCall)Marshal.GetDelegateForFunctionPointer(module + CapacityRva, typeof(CapacityCall));
                var handle = new DetourHandle<TransferCall>();
                pending = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true,
                        InstallOnCommit = false, Backend = NativeStartupPatchWindow.Backend(DetourBackend) });
                pending.AddDetour(handle, HookTarget.FromAddress(unchecked((ulong)(module + TransferRva).ToInt64())), (TransferCall)NativeTransfer);
                capacity = preparedCapacity;
                owner.Install(pending, handle, () => Validate(handle.Hook, module + TransferRva, TransferBytes));
                NativeApiLog.Info(log, "Ally transfers prepared: method=reference-rva; transfer=0xD7AD0 capacity=0xC5AD0; Indirect/9; process lifetime.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref startupFailure, error);
                owner.Publication.RollbackUnpublished(pending, () =>
                {
                    capacity = null;
                    if (claimed) ApiSharedNativeSites.Release(sites, SiteName);
                });
                NativeApiLog.Error(log, "Ally transfers unavailable; native gift behavior remains independent: " + error);
                throw;
            }
        }

        internal static void ValidatePristine(ReadOnlySpan<byte> memory)
        {
            VerifyImageFunction(memory, TransferRva, TransferBytes);
            VerifyImageFunction(memory, CapacityRva, CapacityBytes);
            // An exact full-body match pins the singleton reads and the complete capacity/gold
            // branches. No guessed stride or old address is used for an unknown native hash.
            if ((int)eGoods.STORED_GOLD != 15 || (int)eGoods.Count != 25)
                throw new InvalidOperationException("The native goods enum contract differs from the audited image.");
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(TransferBytes));
            decoder.IP = 0x180000000UL + TransferRva;
            int[] lengths = { 2, 1, 2, 4 };
            for (int index = 0; index < lengths.Length; index++)
            {
                decoder.Decode(out Instruction instruction);
                if (instruction.Length != lengths[index] || instruction.FlowControl != FlowControl.Next || instruction.IsIPRelativeMemoryOperand)
                    throw new InvalidOperationException("The native transfer entry instruction contract changed.");
            }
            if (decoder.IP != 0x180000000UL + TransferRva + DisplacedLength)
                throw new InvalidOperationException("The native transfer continuation is not +9.");
        }

        private static void VerifyImageFunction(ReadOnlySpan<byte> memory, int rva, byte[] expected)
        {
            if (rva < 0 || rva > memory.Length - expected.Length || !memory.Slice(rva, expected.Length).SequenceEqual(expected))
                throw new InvalidOperationException("The pristine ally-transfer function contract changed at RVA 0x" + rva.ToString("X") + ".");
        }
        internal static void RequireReady()
        {
            Exception failure = Volatile.Read(ref startupFailure);
            if (failure != null) throw new InvalidOperationException("Ally-transfer startup initialization failed; no late hook retry.", failure);
            if (!owner.Publication.IsReady) throw new InvalidOperationException("Ally transfers are not initialized.");
        }
        internal static void VerifyBytes(IntPtr target, byte[] expected)
        {
            for (int index = 0; index < expected.Length; index++)
                if (Marshal.ReadByte(target, index) != expected[index])
                    throw new InvalidOperationException("The native ally-transfer entry/body changed or is occupied.");
        }
        internal static void Validate(IDetour<TransferCall> hook, IntPtr target, byte[] expected)
        {
            var native = hook as NativeDetour<TransferCall>;
            if (native == null || !native.IsInstalled || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != DisplacedLength || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero)
                throw new InvalidOperationException("Native ally-transfer Indirect/9 detour contract mismatch.");
            if (Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                throw new InvalidOperationException("Native ally-transfer entry/pointer slot mismatch.");
            for (int index = 6; index < DisplacedLength; index++)
                if (Marshal.ReadByte(target, index) != 0x90) throw new InvalidOperationException("Native ally-transfer padding changed.");
            // Iced re-encoding removes the semantically redundant REX 40 from `40 55`
            // (push rbp). The actual overwrite is still nine bytes, but its four original
            // instructions occupy eight in this installed backend's trampoline. Admit only
            // those two exact byte forms, not arbitrary changed/shortened instructions.
            int sourceOffset = Marshal.ReadByte(native.TrampolineAddress) == 0x55 && expected[0] == 0x40 && expected[1] == 0x55 ? 1 : 0;
            int relocatedLength = DisplacedLength - sourceOffset;
            for (int index = 0; index < relocatedLength; index++)
                if (Marshal.ReadByte(native.TrampolineAddress, index) != expected[index + sourceOffset])
                    throw new InvalidOperationException("Native ally-transfer displaced instructions changed.");
            byte[] jumpBytes = new byte[6];
            Marshal.Copy(native.TrampolineAddress + relocatedLength, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + relocatedLength).ToInt64());
            decoder.Decode(out Instruction jump);
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + DisplacedLength).ToInt64()))
                throw new InvalidOperationException("The ally-transfer trampoline does not resume at +9.");
            for (int index = DisplacedLength; index < expected.Length; index++)
                if (Marshal.ReadByte(target, index) != expected[index])
                    throw new InvalidOperationException("Native ally-transfer continuation changed.");
        }
        private static bool ReadStock(int player, eGoods good, out int amount)
        {
            amount = 0;
            if (!AllyTransfersEngine.ValidPlayer(player) || good <= eGoods.STORED_NULL || good >= eGoods.Count ||
                !GamePlayerManagerAPI.Instance.TryGetPlayerResourcesById(player, out GamePlayerResources* resource) || resource == null)
                return false;
            amount = GamePlayerManagerAPI.Instance.GetGoodAmount(player, good);
            return true;
        }
        private static void NativeTransfer(int recipient, int good, int amount, int sender) => owner.NativeTransfer(recipient, good, amount, sender);
        internal static void ReportCallFailure(Exception error)
        {
            int count = Interlocked.Increment(ref callFailures);
            if ((count & (count - 1)) == 0)
                try { NativeApiLog.Error(logger, "Ally-transfer call-chain failure; no retry or rollback: " + error); }
                catch { /* A diagnostic failure must not hide partial operation observations. */ }
        }

        private sealed class Backend : IAllyTransfersBackend
        {
            public bool IsReady => IsAvailable;
            public bool AreAlliedLiving(int sender, int recipient) => Living(sender) && Living(recipient) &&
                GamePlayerManagerAPI.Instance.IsPlayerAlliedTo(sender, recipient);
            private static bool Living(int player)
            {
                var players = GamePlayerManagerAPI.Instance;
                if (!players.TryGetPlayerResourcesById(player, out GamePlayerResources* resource) || resource == null ||
                    resource->r_WinLossState != WinLossState.None || resource->r_LordUnitId == 0 || resource->r_LordUnitId > int.MaxValue)
                    return false;
                return UnitAccess.TryGetById(GameUnitManagerAPI.Instance, (int)resource->r_LordUnitId, out GameUnit* unit, out _) &&
                    UnitAccess.IsReallyAlive(unit) && unit->r_UnitChimp == eChimps.CHIMP_TYPE_LORD &&
                    unit->r_CurrentHealth > 0 && unit->r_ControllableForPlayerId == player && unit->r_GlobalId != 0;
            }
            public bool TryReadStock(int player, eGoods good, out int amount) => ReadStock(player, good, out amount);
            public int GetFreeCapacity(int recipient, eGoods good)
            {
                RequireReady();
                var buildings = GameBuildingManagerAPI.Instance.GetBuildingManager();
                if (buildings.Pointer == null) throw new InvalidOperationException("The native building manager is unavailable.");
                return capacity((IntPtr)buildings.Pointer, recipient, (int)good);
            }
            public AllyTransferResult Execute(int sender, int recipient, eGoods good, int amount)
            {
                RequireReady();
                return AllyTransferEvents.Dispatch(sender, recipient, (int)good, amount, owner.Original, BalanceReader, true);
            }
        }

        internal sealed class Installation
        {
            internal readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
            private HookTransaction transaction;
            private DetourHandle<TransferCall> transfer;
            internal TransferCall Original => Volatile.Read(ref transfer).Original;
            internal void Install(HookTransaction pending, DetourHandle<TransferCall> candidate, Action validate)
            {
                Publication.ThrowIfUnavailable();
                if (Publication.IsReady) return;
                try
                {
                    CommitResult result = pending.Commit();
                    if (!result.IsCompleteSuccess || !candidate.Success)
                        throw new InvalidOperationException("The native ally-transfer hook did not prepare completely.");
                    Volatile.Write(ref transfer, candidate);
                    transaction = pending;
                    NativeHookPublication.Enable(pending, result, Publication, this, validate);
                }
                catch
                {
                    Publication.RollbackUnpublished(pending, () => { Volatile.Write(ref transfer, null); transaction = null; });
                    throw;
                }
            }
            internal void NativeTransfer(int recipient, int good, int amount, int sender)
            {
                TransferCall original = Original;
                if (!Publication.IsReady) { original(recipient, good, amount, sender); return; }
                AllyTransferEvents.Dispatch(sender, recipient, good, amount, original, BalanceReader, false);
            }
        }
    }
}

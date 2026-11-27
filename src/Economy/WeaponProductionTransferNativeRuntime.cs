using System;
using System.IO;
using System.Runtime.InteropServices;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Building record half of the shared production repair transport.</summary>
    internal static unsafe class WeaponProductionTransferNativeRuntime
    {
        internal const string SiteGroup = "Economy.WeaponProductionTransfer";
        private const int ChoreRva = 0x158B0;
        private const int ChoreDisplacedBytes = 6;
        private const int ChecksumRva = 0x22236;
        internal const int NativeBodyBytes = 4 + 0x32C;
        internal const int ExtendedBodyBytes = NativeBodyBytes + WeaponProductionRuntime.BuildingWireBytes;
        private const int BuildingCapacity = 4000;
        private const int GlobalIdInRecord = 0xD8;
        private const int TypeInRecord = 0xD2;
        private static readonly byte[] ChoreEntry = WorkerProductionBonusNativeDefinition.Hex("48895C2408574883EC3048630527375E08");
        private static readonly byte[] ChecksumEntry = WorkerProductionBonusNativeDefinition.Hex("488DB62C03000003D048638350FA3600");

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ChoreCall();

        private static readonly ChoreCall ChoreCallback = OnChore;
        private static readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        private static DetourHandle<ChoreCall> chore;
        private static HookHandle<X64InlineHook> checksum;
        private static HookTransaction transaction;
        private static IntPtr module;

        internal static bool IsAvailable => Publication.IsReady && WorkerProductionStateNativeRuntime.IsAvailable;

        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(ChoreRva, ChoreDisplacedBytes, NativeSiteMode.Exclusive),
            new NativeSiteSpan(ChoreRva + ChoreDisplacedBytes, ChoreEntry.Length - ChoreDisplacedBytes, NativeSiteMode.Observe),
            new NativeSiteSpan(ChecksumRva, ChecksumEntry.Length, NativeSiteMode.Exclusive)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            Publication.ThrowIfUnavailable();
            if (IsAvailable) return;
            if (!WorkerProductionStateNativeRuntime.IsAvailable || !WorkerProductionWorldSafety.IsAvailable ||
                !string.Equals(hash, WorkerProductionBonusNativeDefinition.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Weapon workshop transfer requires the shared unit transport and audited native image.");

            Verify(context, ChoreRva, ChoreEntry);
            Verify(context, ChecksumRva, ChecksumEntry);
            if (NativeInlineStub.ProbeDisplacement(unchecked((ulong)(context.ModuleHandle + ChecksumRva).ToInt64()), ChecksumEntry.Length) != ChecksumEntry.Length)
                throw new InvalidOperationException("Weapon workshop building checksum displacement differs.");

            module = context.ModuleHandle;
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup, Spans(), m => new InvalidOperationException(m));
            try
            {
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions {
                            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false })) });
                chore = new DetourHandle<ChoreCall>();
                checksum = new HookHandle<X64InlineHook>();
                transaction.AddDetour(chore, HookTarget.FromAddress(Address(ChoreRva)), ChoreCallback);
                transaction.AddInline(checksum, HookTarget.FromAddress(Address(ChecksumRva)),
                    (assembler, instructions, back) => GenerateChecksum(assembler, instructions, back), hookSize: ChecksumEntry.Length);

                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !chore.Success || !checksum.Success)
                    throw new InvalidOperationException("Weapon workshop building transfer hooks could not prepare.");
                NativeIndirectEntry.Validate(chore.Hook, module + ChoreRva, ChoreEntry, ChoreDisplacedBytes, false);
                VerifyChecksumStub();
                NativeHookPublication.Enable(transaction, result, Publication, typeof(WeaponProductionTransferNativeRuntime), () =>
                {
                    NativeIndirectEntry.Validate(chore.Hook, module + ChoreRva, ChoreEntry, ChoreDisplacedBytes, true);
                    VerifyChecksumStub();
                    IntPtr target = module + ChecksumRva;
                    if (!checksum.Hook.IsInstalled || !NativeInlineStub.JumpMatches(target, checksum.Hook.StubAddress) ||
                        !NativeInlineStub.PaddingMatches(target, ChecksumEntry.Length))
                        throw new InvalidOperationException("Weapon workshop building checksum installed jump differs.");
                });
                NativeApiLog.Info(log, "WEAPON_PRODUCTION_TRANSFER_READY: native Chore57 tail16, Chore56 weapon tail20 and building/unit checksum folds.");
            }
            catch
            {
                Publication.RollbackUnpublished(transaction, () =>
                    ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup));
                throw;
            }
        }

        private static ulong Address(int rva) => unchecked((ulong)(module + rva).ToInt64());

        private static void Verify(CrusaderLibraryLoadContext context, int rva, byte[] bytes)
        {
            if (!context.Memory.Slice(rva, bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException("Weapon workshop transfer snapshot differs at " + rva.ToString("X"));
            NativeIndirectEntry.VerifyBytes(context.ModuleHandle + rva, bytes);
        }

        private static void GenerateChecksum(Assembler assembler, ReadOnlySpan<Instruction> instructions, ulong back) =>
            WorkerProductionStateAssembly.GenerateBuildingChecksum(assembler, instructions, back,
                unchecked((ulong)WeaponProductionRuntime.BuildingWireAddress.ToInt64()),
                unchecked((ulong)WeaponProductionRuntime.TransferDemandAddress.ToInt64()),
                Address(WorkerProductionBonusNativeDefinition.BuildingManagerRva));

        private static void VerifyChecksumStub()
        {
            if (checksum.Hook.DisplacedByteCount != ChecksumEntry.Length)
                throw new InvalidOperationException("Weapon workshop building checksum span differs.");
            var original = WorkerProductionBonusNativeDefinition.Decode(ChecksumEntry, Address(ChecksumRva));
            var assembler = new Assembler(64);
            GenerateChecksum(assembler, original, Address(ChecksumRva + ChecksumEntry.Length));
            // Compares the generator's bytes only, without the backend's trailing continuation jump.
            byte[] expected = NativeInlineStub.Assemble(assembler, unchecked((ulong)checksum.Hook.StubAddress.ToInt64()));
            if (!NativeInlineStub.StubMatches(checksum.Hook.StubAddress, expected))
                throw new InvalidOperationException("Weapon workshop building checksum generated stub differs.");
        }

        private static void OnChore()
        {
            if (!WeaponProductionRuntime.HasTransferDemand) { chore.Original(); return; }
            if (WorkerProductionWorldSafety.IsFaulted) return;
            try
            {
                if (!IsAvailable) throw new InvalidDataException("Weapon workshop transfer cohort is unavailable.");
                int mode = Marshal.ReadInt32(module + WorkerProductionStateNativeRuntime.TransferModeRva);
                IntPtr manager = module + WorkerProductionStateNativeRuntime.ManagerRva;
                if (mode == 2 || (mode == 0 && Marshal.ReadInt32(manager + WorkerProductionStateNativeRuntime.SkipUnpack) != 0))
                {
                    chore.Original();
                    Marshal.WriteInt32(module + WorkerProductionStateNativeRuntime.PayloadLengthRva, ExtendedBodyBytes);
                    return;
                }
                if (mode != 0 && mode != 1) throw new InvalidDataException("Unknown building transfer mode.");
                int cursor = Marshal.ReadInt32(manager + WorkerProductionStateNativeRuntime.TransferCursor);
                if (cursor < 0 || cursor > WorkerProductionStateNativeRuntime.MaximumBody - ExtendedBodyBytes)
                    throw new InvalidDataException("The building transfer would exceed its unscheduled buffer.");
                byte* payload = (byte*)manager + WorkerProductionStateNativeRuntime.UnscheduledBody + cursor;
                int id = mode == 1 ? Marshal.ReadInt32(module + WorkerProductionStateNativeRuntime.UnitIdRva) : *(int*)payload;
                if (!ValidSlot(id)) throw new InvalidDataException("Invalid building transfer slot.");
                var body = new Span<byte>(payload, ExtendedBodyBytes);
                if (mode == 0 && !ValidateBody(body)) throw new InvalidDataException("Invalid weapon workshop building row.");

                chore.Original();
                if (Marshal.ReadInt32(manager + WorkerProductionStateNativeRuntime.TransferCursor) != cursor + NativeBodyBytes)
                    throw new InvalidDataException("Native building transfer cursor differs.");
                if (mode == 1)
                {
                    WeaponProductionRuntime.WriteBuildingWire(id, body.Slice(NativeBodyBytes));
                    if (!ValidateBody(body)) throw new InvalidDataException("Invalid outgoing weapon workshop building row.");
                }
                else WeaponProductionRuntime.ReadBuildingWire(id, body.Slice(NativeBodyBytes));
                Marshal.WriteInt32(manager + WorkerProductionStateNativeRuntime.TransferCursor, cursor + ExtendedBodyBytes);
                Marshal.WriteInt32(module + WorkerProductionStateNativeRuntime.PayloadLengthRva, ExtendedBodyBytes);
            }
            catch (Exception error) { WorkerProductionWorldSafety.Fault("Weapon workshop building transfer rejected: " + error.Message); }
        }

        internal static bool ValidateFrame(ReadOnlySpan<byte> frame, out string reason)
        {
            if (frame.Length != ExtendedBodyBytes + 8 || frame[0] != 57 || frame[1] != 0 || frame[2] != 0 || frame[3] != 0)
            {
                reason = "Extended building transfer requires its exact unscheduled frame length.";
                return false;
            }
            bool valid = ValidateBody(frame.Slice(8));
            reason = valid ? string.Empty : "Invalid weapon workshop building row in native building transfer.";
            return valid;
        }

        private static bool ValidateBody(ReadOnlySpan<byte> body)
        {
            if (body.Length != ExtendedBodyBytes) return false;
            int id = unchecked((int)WorkerProductionStateNativeRuntime.Read32(body, 0));
            return ValidSlot(id) && WeaponProductionRuntime.ValidateBuildingWire(id,
                WorkerProductionStateNativeRuntime.Read32(body, 4 + GlobalIdInRecord),
                (eStructs)WorkerProductionStateNativeRuntime.Read16(body, 4 + TypeInRecord), body.Slice(NativeBodyBytes));
        }

        private static bool ValidSlot(int id) => id > 0 && id < BuildingCapacity;
    }
}

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Iced.Intel;
using MonoMod.RuntimeDetour;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Extensions;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using APIShared.NativeSites;
using static Iced.Intel.AssemblerRegisters;

namespace APIShared.Economy
{
    // Hash-bound transfer/checksum ABI. Native records are never enlarged or reinterpreted.
    internal static unsafe class WorkerProductionStateNativeRuntime
    {
        internal const string SiteGroup = "Economy.WorkerProductionState";
        internal const int ChoreRva = 0x15710, ChecksumRva = 0x22149, NativePayloadBytes = 2172,
            ExtendedPayloadBytes = NativePayloadBytes + WorkerProductionStateStore.RowSize;
        internal static readonly byte[] ChoreEntry = WorkerProductionBonusNativeDefinition.Hex("48895C240848896C24104889742418");
        internal static readonly byte[] ChecksumEntry = WorkerProductionBonusNativeDefinition.Hex("8B9424A000000003F948638B50FA3600");
        internal const int ManagerRva = 0x8574320, TransferModeRva = 0x85F8FEC, PayloadLengthRva = 0x85F8FF4,
            UnitIdRva = 0x86C132C, TransferCursor = 0x370BF8, UnscheduledBody = 0x84CD8,
            PendingSlot = 0x84CC8, SkipUnpack = 0x9A0, MaximumBody = 180000;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ChoreCall();
        private delegate void ReceiveCall(int player, byte[] bytes, int length);
        private static readonly ChoreCall ChoreCallback = OnChore;
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<ChoreCall> chore;
        private static HookHandle<X64InlineHook> checksum;
        private static HookTransaction transaction;
        private static Hook receiveHook;
        private static ReceiveCall receive;
        private static IntPtr module;
        internal static bool IsAvailable => publication.IsReady && receiveHook != null;
        internal static NativeSiteSpan[] Spans() => new[] {
            new NativeSiteSpan(ChoreRva, 10, NativeSiteMode.Exclusive),
            new NativeSiteSpan(ChoreRva + 10, ChoreEntry.Length - 10, NativeSiteMode.Observe),
            new NativeSiteSpan(ChecksumRva, ChecksumEntry.Length, NativeSiteMode.Exclusive)
        };

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            publication.ThrowIfUnavailable(); if (IsAvailable) return;
            if (!string.Equals(hash, WorkerProductionBonusNativeDefinition.ReferenceHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Production wire/checksum layout requires the audited native hash.");
            // The permanent receive wrapper takes this exact monitor even in legacy
            // worlds. Never publish it after an optional safety-initialization failure.
            if (!WorkerProductionWorldSafety.IsAvailable)
                throw new InvalidOperationException("Production transfer requires the validated world-safety boundary.");
            Verify(context, ChoreRva, ChoreEntry); Verify(context, ChecksumRva, ChecksumEntry);
            using (var probe = new X64InlineHook(unchecked((ulong)(context.ModuleHandle + ChecksumRva).ToInt64()), ChecksumEntry.Length))
                if (probe.DisplacedByteCount != ChecksumEntry.Length) throw new InvalidOperationException("Production checksum backend displacement differs.");
            var method = typeof(EngineInterface).GetMethod("ReceiveChore", BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(int), typeof(byte[]), typeof(int) }, null);
            if (method == null || method.ReturnType != typeof(void)) throw new MissingMethodException("EngineInterface.ReceiveChore");
            WorkerProductionManagedContracts.ValidateReceive(method);
            module = context.ModuleHandle;
            ApiSharedNativeSites.Claim(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup, Spans(), m => new InvalidOperationException(m));
            Hook candidateReceive = null;
            try
            {
                candidateReceive = new Hook(method, (Action<ReceiveCall, int, byte[], int>)OnReceive,
                    new HookConfig { ManualApply = true, ID = "APIShared.WorkerProduction.Receive" });
                receive = candidateReceive.GenerateTrampoline<ReceiveCall>();
                transaction = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { InstallOnCommit = false, OwnsHooks = true,
                        FailureMode = TransactionFailureMode.RollbackAndThrow,
                        Backend = NativeStartupPatchWindow.Backend(new NativeDetourBackend(new NativeDetourOptions {
                            AllowedSchemes = DetourScheme.Indirect, FollowJumps = false })) });
                chore = new DetourHandle<ChoreCall>(); checksum = new HookHandle<X64InlineHook>();
                transaction.AddDetour(chore, HookTarget.FromAddress(unchecked((ulong)(module + ChoreRva).ToInt64())), ChoreCallback);
                transaction.AddInline(checksum, HookTarget.FromAddress(unchecked((ulong)(module + ChecksumRva).ToInt64())),
                    (a, instructions, back) => WorkerProductionStateAssembly.GenerateChecksum(a, instructions, back,
                        unchecked((ulong)WorkerProductionStateStore.RowsAddress.ToInt64()),
                        unchecked((ulong)WorkerProductionStateStore.ControlAddress.ToInt64()),
                        unchecked((ulong)(module + WorkerProductionBonusNativeDefinition.UnitManagerRva).ToInt64()),
                        unchecked((ulong)WeaponProductionRuntime.WorkerWireAddress.ToInt64()),
                        unchecked((ulong)WeaponProductionRuntime.TransferDemandAddress.ToInt64()),
                        unchecked((ulong)WeaponProductionRuntime.WorkerWireKindsAddress.ToInt64())), hookSize: ChecksumEntry.Length);
                var result = transaction.Commit();
                if (!result.IsCompleteSuccess || !chore.Success || !checksum.Success) throw new InvalidOperationException("Production state hooks could not prepare.");
                NativeIndirectEntry.Validate(chore.Hook, module + ChoreRva, ChoreEntry, false);
                if (checksum.Hook.DisplacedByteCount != ChecksumEntry.Length) throw new InvalidOperationException("Production checksum span differs.");
                VerifyChecksumStub(checksum.Hook);
                receiveHook = candidateReceive; // Root before either native or managed publication.
                NativeHookPublication.Enable(transaction, result, publication, typeof(WorkerProductionStateNativeRuntime), () => {
                    NativeIndirectEntry.Validate(chore.Hook, module + ChoreRva, ChoreEntry, true);
                    if (!checksum.Hook.IsInstalled || checksum.Hook.DisplacedByteCount != ChecksumEntry.Length)
                        throw new InvalidOperationException("Production checksum publication differs.");
                    VerifyChecksumStub(checksum.Hook);
                    IntPtr address = module + ChecksumRva;
                    if (Marshal.ReadByte(address) != 0xFF || Marshal.ReadByte(address, 1) != 0x25 || Marshal.ReadInt32(address, 2) != 0 ||
                        Marshal.ReadIntPtr(address, 6) != checksum.Hook.StubAddress || Marshal.ReadByte(address, 14) != 0x90 || Marshal.ReadByte(address, 15) != 0x90)
                        throw new InvalidOperationException("Production checksum installed jump differs.");
                    candidateReceive.Apply();
                });
                NativeApiLog.Info(log, "PRODUCTION_STATE_READY: native Chore56 tail32 and unit/category checksum; validated managed receive; dormant in legacy worlds.");
            }
            catch
            {
                publication.RollbackUnpublished(transaction, () => {
                    candidateReceive?.Dispose(); receiveHook = null;
                    ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, SiteGroup);
                });
                throw;
            }
        }
        private static void Verify(CrusaderLibraryLoadContext context, int rva, byte[] bytes)
        {
            if (!context.Memory.Slice(rva, bytes.Length).SequenceEqual(bytes))
                throw new InvalidOperationException("Production state snapshot contract differs at " + rva.ToString("X"));
            NativeIndirectEntry.VerifyBytes(context.ModuleHandle + rva, bytes);
        }
        private static void VerifyChecksumStub(X64InlineHook hook)
        {
            ulong back = unchecked((ulong)(module + ChecksumRva + ChecksumEntry.Length).ToInt64());
            var original = WorkerProductionBonusNativeDefinition.Decode(ChecksumEntry, unchecked((ulong)(module + ChecksumRva).ToInt64()));
            var assembler = new Assembler(64);
            WorkerProductionStateAssembly.GenerateChecksum(assembler, original, back,
                unchecked((ulong)WorkerProductionStateStore.RowsAddress.ToInt64()),
                unchecked((ulong)WorkerProductionStateStore.ControlAddress.ToInt64()),
                unchecked((ulong)(module + WorkerProductionBonusNativeDefinition.UnitManagerRva).ToInt64()),
                unchecked((ulong)WeaponProductionRuntime.WorkerWireAddress.ToInt64()),
                unchecked((ulong)WeaponProductionRuntime.TransferDemandAddress.ToInt64()),
                unchecked((ulong)WeaponProductionRuntime.WorkerWireKindsAddress.ToInt64()));
            using (var bytes = new MemoryStream())
            {
                assembler.Assemble(new StreamCodeWriter(bytes), unchecked((ulong)hook.StubAddress.ToInt64()));
                byte[] expected = bytes.ToArray(), actual = new byte[expected.Length]; Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
                if (!actual.AsSpan().SequenceEqual(expected)) throw new InvalidOperationException("Production checksum generated stub differs.");
            }
        }

        private static void OnChore()
        {
            bool baseExtended = WorkerProductionStateRuntime.IsExtended;
            bool weapons = WeaponProductionRuntime.HasTransferDemand;
            if ((!IsAvailable || !baseExtended) && !weapons) { chore.Original(); return; }
            if (WorkerProductionWorldSafety.IsFaulted) return;
            try
            {
                if (weapons && !WeaponProductionTransferNativeRuntime.IsAvailable)
                    throw new InvalidDataException("Weapon workshop transfer cohort is unavailable.");
                int bodyLength = ExtendedPayloadBytes + (weapons ? WeaponProductionRuntime.WorkerWireBytes : 0);
                IntPtr manager = module + ManagerRva;
                int mode = Marshal.ReadInt32(module + TransferModeRva);
                // Mode 2 is measurement only: no FieldHelper and no buffer/row mutation.
                if (mode == 2) { chore.Original(); Marshal.WriteInt32(module + PayloadLengthRva, bodyLength); return; }
                if (mode != 0 && mode != 1) throw new InvalidDataException("Unknown unit transfer mode.");
                if (mode == 0 && Marshal.ReadInt32(manager + SkipUnpack) != 0)
                { chore.Original(); Marshal.WriteInt32(module + PayloadLengthRva, bodyLength); return; }
                int cursor = Marshal.ReadInt32(manager + TransferCursor);
                if (cursor < 0 || cursor > MaximumBody - bodyLength)
                    throw new InvalidDataException("The production unit transfer would exceed its unscheduled buffer.");
                IntPtr payload = manager + UnscheduledBody + cursor;
                int id = mode == 1 ? Marshal.ReadInt32(module + UnitIdRva) : Marshal.ReadInt32(payload);
                if (id <= 0 || id >= WorkerProductionStateStore.RowCapacity)
                    throw new InvalidDataException("Invalid unit transfer slot.");
                WorkerProductionStateRow row;
                if (mode == 1) row = baseExtended ? WorkerProductionStateRuntime.LiveRow(id) : default;
                else
                {
                    if (!ValidateUnitTransfer(new ReadOnlySpan<byte>((void*)payload, bodyLength), weapons, out string reason))
                        throw new InvalidDataException(reason);
                    row = *(WorkerProductionStateRow*)((byte*)payload + NativePayloadBytes);
                }
                chore.Original();
                if (Marshal.ReadInt32(manager + TransferCursor) != cursor + NativePayloadBytes)
                    throw new InvalidDataException("Native unit transfer cursor differs.");
                // Commit the sidecar only after the complete validated native record/path transfer.
                if (mode == 1) *(WorkerProductionStateRow*)((byte*)payload + NativePayloadBytes) = row;
                else *WorkerProductionStateStore.RowPointer(id) = row;
                if (weapons)
                {
                    var tail = new Span<byte>((byte*)payload + ExtendedPayloadBytes, WeaponProductionRuntime.WorkerWireBytes);
                    if (mode == 1)
                    {
                        WeaponProductionRuntime.WriteWorkerWire(id, tail);
                        if (!ValidateUnitTransfer(new ReadOnlySpan<byte>((void*)payload, bodyLength), true, out string reason))
                            throw new InvalidDataException(reason);
                    }
                    else WeaponProductionRuntime.ReadWorkerWire(id, tail);
                }
                Marshal.WriteInt32(manager + TransferCursor, cursor + bodyLength);
                Marshal.WriteInt32(module + PayloadLengthRva, bodyLength);
            }
            catch (Exception error) { WorkerProductionWorldSafety.Fault("Production unit transfer rejected: " + error.Message); }
        }

        private static void OnReceive(ReceiveCall original, int player, byte[] bytes, int length)
        {
            lock (WorkerProductionWorldSafety.EngineGate)
            {
                if (WorkerProductionWorldSafety.IsFaulted) return;
                bool weapons = WeaponProductionRuntime.HasTransferDemand;
                if (((!IsAvailable || !WorkerProductionStateRuntime.IsExtended) && !weapons) || (player == -1 && length == -1))
                { original(player, bytes, length); return; }
                if (!ValidateReceived(bytes, length, weapons, out string reason))
                { WorkerProductionWorldSafety.Fault("Production network packet rejected: " + reason); return; }
                original(player, bytes, length);
            }
        }
        internal static bool ValidateReceived(byte[] bytes, int length, out string reason)
            => ValidateReceived(bytes, length, WeaponProductionRuntime.HasTransferDemand, out reason);

        private static bool ValidateReceived(byte[] bytes, int length, bool weapons, out string reason)
        {
            reason = "";
            if (bytes == null || length < 1 || length > bytes.Length)
            { reason = "Invalid Chore packet length."; return false; }
            // Native unit packets are never compressed by 23800. Accepting 125/56 here
            // would bypass validation after native decompression; fail closed instead.
            int opcode = bytes[0] == 125 && length > 1 ? bytes[1] : bytes[0];
            if (weapons && !WeaponProductionTransferNativeRuntime.IsAvailable && opcode >= 54 && opcode <= 57)
            { reason = "Native repair requires the complete weapon workshop transfer cohort."; return false; }
            if (bytes[0] == 125 && (opcode == 56 || (weapons && opcode == 57)))
            { reason = "Compressed extended production transfers are unsupported."; return false; }
            if (weapons && bytes[0] == 57)
                return WeaponProductionTransferNativeRuntime.ValidateFrame(bytes.AsSpan(0, length), out reason);
            if (bytes[0] != 56) return true;
            int bodyLength = ExtendedPayloadBytes + (weapons ? WeaponProductionRuntime.WorkerWireBytes : 0);
            if (length != bodyLength + 8 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0)
            { reason = "Extended unit transfer requires its exact unscheduled frame length."; return false; }
            return ValidateUnitTransfer(bytes.AsSpan(8, bodyLength), weapons, out reason);
        }

        private static bool ValidateUnitTransfer(ReadOnlySpan<byte> body, bool weapons, out string reason)
        {
            if (!ValidateBody(body.Slice(0, ExtendedPayloadBytes), out reason)) return false;
            if (weapons && !WorkerProductionStateRuntime.IsExtended)
            {
                foreach (byte value in body.Slice(NativePayloadBytes, WorkerProductionStateStore.RowSize))
                    if (value != 0)
                    { reason = "Unexpected production amount state in a weapon-only unit transfer."; return false; }
            }
            if (!weapons || WeaponProductionRuntime.ValidateWorkerWire((int)Read32(body, 0),
                Read32(body, 4 + 0x98), (eChimps)Read16(body, 4 + 0x8E), body.Slice(ExtendedPayloadBytes))) return true;
            reason = "Invalid weapon workshop worker row in native unit transfer.";
            return false;
        }
        internal static bool ValidateBody(ReadOnlySpan<byte> body, out string reason)
            => ValidateBody(body, WorkerProductionRuntime.CurrentCommonDenominator, out reason);
        internal static bool ValidateBody(ReadOnlySpan<byte> body, ulong common, out string reason)
        {
            reason = "";
            if (body.Length != ExtendedPayloadBytes) { reason = "Wrong unit body length."; return false; }
            int id = body[0] | body[1] << 8 | body[2] << 16 | body[3] << 24;
            if (id <= 0 || id >= WorkerProductionStateStore.RowCapacity) { reason = "Invalid unit ID."; return false; }
            WorkerProductionStateRow row;
            fixed (byte* p = body) row = *(WorkerProductionStateRow*)(p + NativePayloadBytes);
            if (!WorkerProductionStateStore.Validate(in row, out reason)) return false;
            if (row.IsEmpty) return true;
            if (common == 0 || common % row.Denominator != 0)
            { reason = "Production transfer precision does not belong to the admitted world."; return false; }
            // Body = ID4 + native record (origin+658), path1000, sidecar32.
            uint gid = Read32(body, 4 + 0x98);
            var kind = (eChimps)Read16(body, 4 + 0x8E);
            if (row.GlobalId != gid || row.ProducerKind != kind)
            { reason = "Production row identity differs from the transferred native unit."; return false; }
            // Flags record the producing operation, not a promise about final cargo:
            // the original fixed bonus and SE Post may subsequently change that amount.
            // Delivery guards always inspect the actual signed native cargo separately.
            return true;
        }
        internal static uint Read32(ReadOnlySpan<byte> b, int p) => (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24);
        internal static ushort Read16(ReadOnlySpan<byte> b, int p) => (ushort)(b[p] | b[p + 1] << 8);

    }
}

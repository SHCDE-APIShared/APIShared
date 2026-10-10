using System;
using System.Runtime.InteropServices;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;

namespace APIShared.Economy
{
    // Full helper and caller audit: current native hash, buy CEB10, sell CEB90; callers
    // 29650, 29700, 3EC90, D78C0, D7AD0. Only an unpublished failed transaction can roll back.
    internal static class MarketPriceNativeRuntime
    {
        internal const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int BuyRva = 0xCEB10, SellRva = 0xCEB90;
        internal const int DisplacedLength = 10;
        internal static readonly byte[] BuyBytes = { 0x49,0x63,0xC0,0x8B,0x8C,0xC1,0xB8,0x17,0x18,0,0xB8,0x67,0x66,0x66,0x66,0xF7,0xE9,0xD1,0xFA,0x8B,0xC2,0xC1,0xE8,0x1F,0x03,0xC2,0x41,0x0F,0xAF,0xC1,0xC3 };
        internal static readonly byte[] SellBytes = { 0x49,0x63,0xC0,0x8B,0x8C,0xC1,0xBC,0x17,0x18,0,0xB8,0x67,0x66,0x66,0x66,0xF7,0xE9,0xD1,0xFA,0x8B,0xC2,0xC1,0xE8,0x1F,0x03,0xC2,0x41,0x0F,0xAF,0xC1,0xC3 };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int PriceCall(IntPtr manager, int playerId, int good, int amount);
        internal static readonly NativeDetourBackend Backend = new NativeDetourBackend(
            new NativeDetourOptions { AllowedSchemes = DetourScheme.Indirect, FollowJumps = false });
        private static IntPtr module;
        private static ScanRegion region;
        private static string hash;
        private static ManualLogSource log;
        private static HookTransaction transaction;
        private static DetourHandle<PriceCall> buy, sell;
        private static bool published;
        internal static void Initialize(IntPtr moduleHandle, ScanRegion scanRegion, string binaryHash, ManualLogSource logger)
        {
            if (module != IntPtr.Zero)
            {
                if (module != moduleHandle || !string.Equals(hash, binaryHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Native market runtime cannot change its process image.");
                return;
            }
            module = moduleHandle; region = scanRegion; hash = binaryHash; log = logger;
        }
        internal static void VerifyBytes(IntPtr target, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i])
                    throw new InvalidOperationException("Native market helper changed or is already occupied.");
        }
        internal static void Validate(IDetour<PriceCall> hook, IntPtr target, byte[] expected)
        {
            var native = hook as NativeDetour<PriceCall>;
            if (native == null || !native.IsInstalled || native.Scheme != DetourScheme.Indirect ||
                native.DisplacedByteCount != DisplacedLength || native.TargetAddress != unchecked((ulong)target.ToInt64()) ||
                native.ChainDepth != 1 || native.PointerSlot == IntPtr.Zero || native.TrampolineAddress == IntPtr.Zero ||
                Marshal.ReadByte(target) != 0xFF || Marshal.ReadByte(target, 1) != 0x25 ||
                target + 6 + Marshal.ReadInt32(target, 2) != native.PointerSlot ||
                Marshal.ReadIntPtr(native.PointerSlot) != native.HookEntryPointAddress)
                throw new InvalidOperationException("Native market Indirect/10 detour contract mismatch.");
            for (int i = 6; i < DisplacedLength; i++)
                if (Marshal.ReadByte(target, i) != 0x90) throw new InvalidOperationException("Native market patch padding changed.");
            for (int i = 0; i < DisplacedLength; i++)
                if (Marshal.ReadByte(native.TrampolineAddress, i) != expected[i])
                    throw new InvalidOperationException("Native market displaced instructions changed.");
            byte[] jumpBytes = new byte[6];
            Marshal.Copy(native.TrampolineAddress + DisplacedLength, jumpBytes, 0, jumpBytes.Length);
            var decoder = Decoder.Create(64, new ByteArrayCodeReader(jumpBytes));
            decoder.IP = unchecked((ulong)(native.TrampolineAddress + DisplacedLength).ToInt64());
            decoder.Decode(out Instruction jump);
            if (jump.Mnemonic != Mnemonic.Jmp || jump.Op0Kind != OpKind.NearBranch64 ||
                jump.NearBranchTarget != unchecked((ulong)(target + DisplacedLength).ToInt64()))
                throw new InvalidOperationException("Native market trampoline must resume at the audited +10 instruction.");
            for (int i = DisplacedLength; i < expected.Length; i++)
                if (Marshal.ReadByte(target, i) != expected[i]) throw new InvalidOperationException("Native market continuation changed.");
        }
        internal static void EnsureInstalled()
        {
            if (published) return;
            if (module == IntPtr.Zero || region == null || !string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Native market events require the audited CrusaderDE image and LibraryLoaded initialization.");
            VerifyBytes(module + BuyRva, BuyBytes);
            VerifyBytes(module + SellRva, SellBytes);
            buy = new DetourHandle<PriceCall>(); sell = new DetourHandle<PriceCall>();
            HookTransaction pending = null;
            try
            {
                pending = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true, Backend = Backend });
                pending.AddDetour(buy, HookTarget.FromAddress(unchecked((ulong)(module + BuyRva).ToInt64())), (PriceCall)Buy);
                pending.AddDetour(sell, HookTarget.FromAddress(unchecked((ulong)(module + SellRva).ToInt64())), (PriceCall)Sell);
                if (!pending.Commit().IsCompleteSuccess || !buy.Success || !sell.Success)
                    throw new InvalidOperationException("Both native market hooks must install atomically.");
                Validate(buy.Hook, module + BuyRva, BuyBytes);
                Validate(sell.Hook, module + SellRva, SellBytes);
                transaction = pending;
                published = true; // Static hooks, delegates, transaction and registry root every callback for the process.
            }
            catch
            {
                if (!published) { pending?.Dispose(); buy = null; sell = null; }
                throw;
            }
            try { log?.LogInfo("Shared native market price events installed: CEB10/CEB90, Indirect/10; process lifetime."); } catch { }
        }
        private static int Buy(IntPtr manager, int playerId, int good, int amount) =>
            MarketPriceEvents.Dispatch(MarketPriceDirection.Buy, manager, playerId, good, amount, buy.Original);
        private static int Sell(IntPtr manager, int playerId, int good, int amount) =>
            MarketPriceEvents.Dispatch(MarketPriceDirection.Sell, manager, playerId, good, amount, sell.Original);
    }
}

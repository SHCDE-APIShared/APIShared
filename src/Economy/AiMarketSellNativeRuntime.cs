using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Core.Memory;
using RedBird.X64.Hooks;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    // LibraryLoaded binds the image; the first registration prepares both sites at startup.
    // Publication roots this owner before the first write. Its data and delegates never leave
    // that root, including a partially enabled/failed installation. No map or Unity teardown.
    internal sealed unsafe class AiMarketSellNativeRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int Adjust(int playerOrSlot, int good, int stock, int value);
        private static readonly Adjust ReserveCallback = Reserve;
        private static readonly Adjust QuantityCallback = Quantity;
        private static IntPtr module;
        private static ScanRegion region;
        private static bool pristineValidated;
        private static string hash;
        private static ManualLogSource log;
        private static AiMarketSellNativeRuntime instance;
        private static Exception initializationFailure;
        private static bool initializing;
        private readonly IntPtr state;
        private readonly ulong image;
        private HookTransaction transaction;
        private bool claimed;
        private readonly HookHandle<X64InlineHook>[] handles = new HookHandle<X64InlineHook>[2];
        internal readonly NativeHookPublicationState Publication = new NativeHookPublicationState();
        internal static void Initialize(IntPtr moduleHandle, ScanRegion scanRegion, ReadOnlySpan<byte> memory, string binaryHash, ManualLogSource logger)
        {
            if (module != IntPtr.Zero && (module != moduleHandle || hash != binaryHash))
                throw new InvalidOperationException("Sell runtime cannot change process image.");
            module = moduleHandle; region = scanRegion; hash = binaryHash; log = logger;
            initializing = true;
            try
            {
                // The load image is only available here; Install relies on this result.
                if (hash == AiMarketSellAssembly.NativeHash)
                {
                    AiMarketSellAssembly.ValidatePristine(memory);
                    pristineValidated = true;
                }
                EnsureInstalled();
            }
            catch (Exception error) { initializationFailure = error; throw; }
            finally { initializing = false; }
        }
        private static int Reserve(int slot, int good, int stock, int value)
        { try { return AiMarketSellPolicies.Reserve(slot, good, stock, value); } catch { return value; } }
        private static int Quantity(int player, int good, int stock, int value)
        { try { return AiMarketSellPolicies.Quantity(player, good, stock, value); } catch { return value; } }
        internal static void PublishDemand(int demand)
        {
            var owner = instance;
            if (owner == null || !owner.Publication.IsReady) throw new InvalidOperationException("Sell runtime was not published.");
            Volatile.Write(ref *(int*)owner.state, demand);
        }
        // Serialized by the policy registry; a failed publication is retained and cannot retry.
        internal static void EnsureInstalled()
        {
            if (initializationFailure != null) throw new InvalidOperationException("AI sell startup initialization failed; no late executable retry.", initializationFailure);
            if (instance != null)
            { instance.Publication.ThrowIfUnavailable(); if (instance.Publication.IsReady) return;
                throw new InvalidOperationException("AI sell startup preparation is still in progress."); }
            if (!initializing) throw new InvalidOperationException("AI sell policies require completed LibraryLoaded initialization.");
            if (module == IntPtr.Zero || region == null || hash != AiMarketSellAssembly.NativeHash)
                throw new InvalidOperationException("AI sell policies require LibraryLoaded and the audited native hash.");
            var candidate = new AiMarketSellNativeRuntime();
            instance = candidate;
            try { candidate.Install(); }
            catch
            {
                candidate.Publication.RollbackUnpublished(candidate.transaction, () =>
                {
                    if (candidate.claimed) ApiSharedNativeSites.Release(ApiSharedRuntime.ProcessInstance.NativeOwnership, ApiSharedNativeSites.AiMarketSell);
                    Marshal.FreeHGlobal(candidate.state); instance = null;
                });
                throw;
            }
        }
        private AiMarketSellNativeRuntime()
        { image = unchecked((ulong)module.ToInt64()); state = Marshal.AllocHGlobal(4); Marshal.WriteInt32(state, 0); }
        private void Install()
        {
            if (!pristineValidated) throw new InvalidOperationException("AI sell load-image contract was not validated.");
            if (sizeof(GamePlayerResources) != AiMarketSellAssembly.PlayerStride ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_TotalGoodsNull)).ToInt32() != 0x4D0 ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_TotalGoodsFoodAle)).ToInt32() != 0x508 ||
                Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_TotalGoodsGold)).ToInt32() != 0x50C ||
                (int)eGoods.STORED_FOOD_ALE != 14 || (int)eGoods.STORED_GOLD != 15 || (int)eGoods.Count != 25)
                throw new InvalidOperationException("AI sell player/goods layout differs from the audited contract.");
            for (int i = 0; i < AiMarketSellAssembly.BoundaryRvas.Length; i++)
                ValidateLiveBoundary(AiMarketSellAssembly.BoundaryRvas[i], AiMarketSellAssembly.Hex(AiMarketSellAssembly.BoundaryHex[i]));
            for (int i = 0; i < handles.Length; i++)
            {
                ValidateOwnSpan(AiMarketSellAssembly.Sites[i], AiMarketSellAssembly.Hex(AiMarketSellAssembly.OriginalHex[i]));
                using (var probe = new X64InlineHook(image + (uint)AiMarketSellAssembly.Sites[i], 15))
                    if (probe.DisplacedByteCount != 15) throw new InvalidOperationException("AI sell backend displacement differs.");
                var assembler = new Assembler(64);
                Generate(assembler, AiMarketSellAssembly.Decode(AiMarketSellAssembly.Hex(AiMarketSellAssembly.OriginalHex[i]),
                    image + (uint)AiMarketSellAssembly.Sites[i]), image + (uint)AiMarketSellAssembly.Sites[i] + 15, i);
                AiMarketSellAssembly.Assemble(assembler, image + 0x20000000);
            }
            var sites = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(sites, ApiSharedNativeSites.AiMarketSell, ApiSharedNativeSites.AiMarketSellSpans(),
                reason => new InvalidOperationException(reason));
            claimed = true;
            transaction = new HookTransaction(region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                new HookTransactionOptions { InstallOnCommit = false, FailureMode = TransactionFailureMode.RollbackAndThrow, OwnsHooks = true });
            for (int i = 0; i < handles.Length; i++)
            {
                int index = i; handles[i] = new HookHandle<X64InlineHook>();
                transaction.AddInline(handles[i], HookTarget.FromAddress(image + (uint)AiMarketSellAssembly.Sites[i]),
                    (a, original, back) => Generate(a, original, back, index), hookSize: 15);
            }
            var result = transaction.Commit();
            if (!result.IsCompleteSuccess) throw new InvalidOperationException("Incomplete AI sell preparation.");
            for (int i = 0; i < handles.Length; i++)
            {
                var hook = handles[i].Hook;
                if (!handles[i].Success || hook.IsInstalled || hook.DisplacedByteCount != 15 ||
                    hook.TargetAddress != image + (uint)AiMarketSellAssembly.Sites[i] || hook.StubAddress == IntPtr.Zero)
                    throw new InvalidOperationException("Prepared AI sell contract differs.");
                ValidateStub(i, hook);
            }
            NativeHookPublication.Enable(transaction, result, Publication, this, ValidateInstalled);
            try { log?.LogInfo($"{DateTime.Now:HH:mm:ss.fff} AI sell policy hooks confirmed: 3F1F3..3F202, 29723..29732; 15/15; player stride=583C, goods=4D0/508/50C; initially inactive, process lifetime."); } catch { }
        }
        private void Generate(Assembler a, ReadOnlySpan<Instruction> original, ulong back, int index) =>
            AiMarketSellAssembly.Generate(a, original, back, index, image, unchecked((ulong)state.ToInt64()),
                unchecked((ulong)Marshal.GetFunctionPointerForDelegate(ReserveCallback).ToInt64()),
                unchecked((ulong)Marshal.GetFunctionPointerForDelegate(QuantityCallback).ToInt64()));
        private void ValidateInstalled()
        {
            for (int i = 0; i < handles.Length; i++)
            {
                var hook = handles[i].Hook;
                if (!hook.IsInstalled || hook.DisplacedByteCount != 15 || hook.TargetAddress != image + (uint)AiMarketSellAssembly.Sites[i] ||
                    Marshal.ReadByte((IntPtr)hook.TargetAddress) != 0xFF || Marshal.ReadByte((IntPtr)hook.TargetAddress, 1) != 0x25 ||
                    Marshal.ReadInt32((IntPtr)hook.TargetAddress, 2) != 0 ||
                    Marshal.ReadIntPtr((IntPtr)hook.TargetAddress, 6) != hook.StubAddress || Marshal.ReadByte((IntPtr)hook.TargetAddress, 14) != 0x90)
                    throw new InvalidOperationException("Installed AI sell jump/span contract differs.");
            }
        }
        // RedBird relocates the live bytes of the span, so our own span must be unchanged live.
        private void ValidateOwnSpan(int rva, byte[] expected)
        {
            if (!Live(rva, expected.Length).AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException($"AI sell site RVA=0x{rva:X} is occupied or changed.");
            try { log?.LogInfo($"{DateTime.Now:HH:mm:ss.fff} AI sell hook resolved: reference-rva=0x{rva:X}, span=15."); } catch { }
        }
        private void ValidateLiveBoundary(int rva, byte[] original)
        {
            if (!AiMarketSellAssembly.AcceptsLiveBoundary(Live(rva, original.Length), original))
                throw new InvalidOperationException($"AI sell native boundary differs live at RVA=0x{rva:X}.");
        }
        private void ValidateStub(int index, X64InlineHook hook)
        {
            byte[] expected = AiMarketSellAssembly.ExpectedStub(index, image, unchecked((ulong)hook.StubAddress.ToInt64()),
                unchecked((ulong)state.ToInt64()),
                unchecked((ulong)Marshal.GetFunctionPointerForDelegate(ReserveCallback).ToInt64()),
                unchecked((ulong)Marshal.GetFunctionPointerForDelegate(QuantityCallback).ToInt64()));
            var actual = new byte[expected.Length];
            Marshal.Copy(hook.StubAddress, actual, 0, actual.Length);
            if (!actual.AsSpan().SequenceEqual(expected))
                throw new InvalidOperationException("AI sell backend stub differs from the generated contract at site " + index + ".");
        }
        private static byte[] Live(int rva, int length)
        {
            var bytes = new byte[length];
            Marshal.Copy(module + rva, bytes, 0, length);
            return bytes;
        }
    }
}

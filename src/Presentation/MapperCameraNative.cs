using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API.LowLevel;

namespace APIShared.Presentation
{
    internal static class MapperCameraNative
    {
        private const string AuditedHash = "FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const int Rva = 0x45F20, DisplacedLength = 7, CallerRva = 0x8BF96, ReturnRva = 0x8BF9B;
        internal static readonly byte[] Window = { 0x49,0x63,0xC0,0x4C,0x8D,0x04,0x40,
            0x42,0x03,0x94,0x81,0x6C,0xE1,0x61,0x00,0xE9,0xEC,0xA2,0x00,0x00 };
        internal static readonly byte[] Caller = { 0xE8,0x85,0x9F,0xFB,0xFF };
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void CameraCall();
        private static readonly CameraCall unreachable = Unreachable;
        private static void Unreachable() { }
        private static readonly IntPtr demand = Allocate();
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static DetourHandle<CameraCall> handle;
        private static MapperCameraBridge bridge;
        internal static bool IsReady => publication.IsReady;
        private static IntPtr Allocate() { var cell = Marshal.AllocHGlobal(sizeof(int)); Marshal.WriteInt32(cell, 0); return cell; }
        internal static unsafe int ReadDemand() => Volatile.Read(ref *(int*)demand);
        internal static unsafe void WriteDemand(int value) => Volatile.Write(ref *(int*)demand, value);

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            publication.ThrowIfUnavailable();
            if (IsReady) return;
            if (!NativeStartupPatchWindow.IsOpen || !string.Equals(hash, AuditedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Mapper camera requires the startup window and audited native image.");
            foreach (var item in new[] { Tuple.Create(Rva, Window), Tuple.Create(CallerRva, Caller) })
                if (item.Item1 < 0 || item.Item1 > context.Memory.Length - item.Item2.Length ||
                    !context.Memory.Slice(item.Item1, item.Item2.Length).SequenceEqual(item.Item2))
                    throw new InvalidOperationException("Mapper camera pristine function/caller differs from the audit.");
            IntPtr target = context.ModuleHandle + Rva;
            for (int i = 0; i < DisplacedLength; i++)
                if (Marshal.ReadByte(target, i) != Window[i]) throw new InvalidOperationException("Mapper camera entry is occupied.");
            NativeSites.NativeIndirectEntry.VerifyBytes(context.ModuleHandle + CallerRva, Caller);
            var ownership = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(ownership, ApiSharedNativeSites.MapperCamera,
                ApiSharedNativeSites.MapperCameraSpans(), reason => new InvalidOperationException(reason));
            HookTransaction pending = null;
            try
            {
                var backend = new NativeDetourBackend(new NativeDetourOptions {
                    AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
                    Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = Window.Length, SuspendThreadsDuringPatch = false
                });
                pending = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { OwnsHooks = true, InstallOnCommit = false,
                        FailureMode = TransactionFailureMode.RollbackAndThrow, Backend = backend });
                handle = new DetourHandle<CameraCall>();
                bridge = new MapperCameraBridge(unchecked((ulong)demand.ToInt64()),
                    unchecked((ulong)(context.ModuleHandle + ReturnRva).ToInt64()));
                pending.AddDetour(handle, HookTarget.FromAddress(unchecked((ulong)target.ToInt64())), unreachable, bridge);
                var result = pending.Commit();
                if (!result.IsCompleteSuccess || !handle.Success) throw new InvalidOperationException("Mapper camera preparation failed.");
                bridge.Validate(handle.Hook, target, Window, false);
                NativeHookPublication.Enable(pending, result, publication, handle,
                    () => bridge.Validate(handle.Hook, target, Window, true));
                NativeApiLog.Info(log, "MAPPER_CAMERA_NATIVE: method=reference-rva; [0x45F20,0x45F27) Indirect/7; " +
                    "caller=0x8BF96, return=0x8BF9B; demand-zero original; process lifetime.");
            }
            catch
            {
                publication.RollbackUnpublished(pending, () => {
                    handle = null; bridge = null; ApiSharedNativeSites.Release(ownership, ApiSharedNativeSites.MapperCamera);
                });
                throw;
            }
        }
    }
}

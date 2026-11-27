using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.X64.Hooks.Transaction;
using SHCDESE.API;
using SHCDESE.API.LowLevel;

namespace APIShared.PlayerControl
{
    // Network bookkeeping reads four participant indices independently from the
    // native presentation view. Install once before permitting a positive view lease.
    internal static class PlayerViewNetworkNative
    {
        internal static readonly PlayerViewNetworkSite[] Sites = {
            PlayerViewNetworkSite.Timing, PlayerViewNetworkSite.HeartbeatProgress,
            PlayerViewNetworkSite.HeartbeatAcknowledgement, PlayerViewNetworkSite.HeartbeatTimestamp
        };
        private static readonly byte[][] windows = {
            new byte[] { 0x8B, 0x15, 0x98, 0xD3, 0x85, 0x08, 0x49, 0x8B, 0xCE },
            new byte[] { 0x48, 0x63, 0x8B, 0x50, 0xFA, 0x36, 0x00, 0x8B, 0x05, 0x60, 0xDF, 0x63, 0x03 },
            new byte[] { 0x48, 0x63, 0x8B, 0x50, 0xFA, 0x36, 0x00, 0x8B, 0x05, 0x4C, 0xDF, 0x63, 0x03 },
            new byte[] { 0x48, 0x63, 0x8B, 0x50, 0xFA, 0x36, 0x00, 0x89, 0x84, 0x8B, 0x78, 0xD0, 0x14, 0x00 }
        };
        private static readonly IntPtr protectedPlayerCell = AllocateCell();
        private static readonly NativeHookPublicationState publication = new NativeHookPublicationState();
        private static readonly DetourHandle<BoundaryCall>[] handles = new DetourHandle<BoundaryCall>[4];
        private static readonly PlayerViewNetworkBridge[] bridges = new PlayerViewNetworkBridge[4];
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void BoundaryCall();
        private static readonly BoundaryCall unreachable = Unreachable;
        private static void Unreachable() { }
        internal static bool IsReady => publication.IsReady;

        internal static int Rva(PlayerViewNetworkSite site)
        {
            switch (site)
            {
                case PlayerViewNetworkSite.Timing: return 0x869D2;
                case PlayerViewNetworkSite.HeartbeatProgress: return 0x27FEB;
                case PlayerViewNetworkSite.HeartbeatAcknowledgement: return 0x27FFF;
                case PlayerViewNetworkSite.HeartbeatTimestamp: return 0x28019;
                default: throw new ArgumentOutOfRangeException(nameof(site));
            }
        }

        internal static byte[] Window(PlayerViewNetworkSite site)
        {
            Rva(site);
            return windows[(int)site];
        }

        private static IntPtr AllocateCell()
        {
            var cell = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(cell, 0);
            return cell; // Process lifetime, including a partially published hook group.
        }

        // Only the shared identity core calls this, while holding the engine monitor.
        // Publishing 0 is always available; a positive lease requires the complete group.
        // A valid prepared core publication therefore performs only a nonthrowing data store.
        internal static unsafe void WriteProtectedPlayer(int player)
        {
            if ((uint)player > GamePlayerManagerAPI.MAX_PLAYERS) throw new ArgumentOutOfRangeException(nameof(player));
            if (player != 0 && !IsReady)
                throw new InvalidOperationException("Network identity load boundaries are not ready.");
            Volatile.Write(ref *(int*)protectedPlayerCell, player);
        }

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource log)
        {
            publication.ThrowIfUnavailable();
            if (IsReady) return;
            if (!NativeStartupPatchWindow.IsOpen)
                throw new InvalidOperationException("Network identity boundaries require the startup patch window.");
            if (!string.Equals(hash, ApiSharedRuntime.SupportedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Network identity participant layout requires the audited native image.");
            ulong module = unchecked((ulong)context.ModuleHandle.ToInt64());
            ulong view = module + NativeViewCell.Rva;
            if (SHCDESE.GameGlobals.GameGlobalsManager.Instance.LocalPlayerIdVA != view ||
                (ulong)NativeViewCell.Rva + sizeof(int) > (ulong)context.Memory.Length)
                throw new InvalidOperationException("Network identity native view address differs from the audited image.");
            foreach (var site in Sites)
            {
                int rva = Rva(site); var window = Window(site);
                if (rva < 0 || rva > context.Memory.Length - window.Length ||
                    !context.Memory.Slice(rva, window.Length).SequenceEqual(window))
                    throw new InvalidOperationException("Network identity pristine load boundary differs at RVA 0x" + rva.ToString("X"));
                // The pristine window includes its continuation, but another owner may
                // already have an adjacent hook beginning exactly at that instruction.
                int displaced = PlayerViewNetworkBridge.DisplacedLength(site);
                for (int offset = 0; offset < displaced; offset++)
                    if (Marshal.ReadByte(context.ModuleHandle + rva, offset) != window[offset])
                        throw new InvalidOperationException("Network identity live load boundary is already occupied.");
            }
            var ownership = ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(ownership, ApiSharedNativeSites.PlayerViewNetwork,
                ApiSharedNativeSites.PlayerViewNetworkSpans(), reason => new InvalidOperationException(reason));
            HookTransaction pending = null;
            try
            {
                var backend = new NativeDetourBackend(new NativeDetourOptions {
                    AllowedSchemes = DetourScheme.Indirect, FollowJumps = false, FollowCalls = false,
                    Chaining = HookChainingPolicy.Refuse, FunctionScanLimit = 14,
                    SuspendThreadsDuringPatch = false
                });
                pending = new HookTransaction(context.Region, SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions { FailureMode = TransactionFailureMode.RollbackAndThrow,
                        OwnsHooks = true, InstallOnCommit = false, Backend = backend });
                foreach (var site in Sites)
                {
                    int index = (int)site, rva = Rva(site);
                    var handle = new DetourHandle<BoundaryCall>();
                    var bridge = new PlayerViewNetworkBridge(site, unchecked((ulong)protectedPlayerCell.ToInt64()),
                        view, module + (ulong)rva + (ulong)PlayerViewNetworkBridge.DisplacedLength(site));
                    handles[index] = handle; bridges[index] = bridge;
                    pending.AddDetour(handle, HookTarget.FromAddress(module + (ulong)rva), unreachable, bridge);
                }
                var result = pending.Commit();
                if (!result.IsCompleteSuccess)
                    throw new InvalidOperationException("Network identity load boundaries could not all prepare.");
                Validate(context.ModuleHandle, false);
                NativeHookPublication.Enable(pending, result, publication, handles,
                    () => Validate(context.ModuleHandle, true));
                NativeApiLog.Info(log, "PLAYER_VIEW_NETWORK: method=reference-rva; timing [0x869D2,0x869D8) Indirect/6; " +
                    "heartbeat [0x27FEB,0x27FF2), [0x27FFF,0x28006), [0x28019,0x28020) Indirect/7; " +
                    "positive lease participant indices only, native view unchanged; process lifetime.");
            }
            catch
            {
                publication.RollbackUnpublished(pending, () => {
                    Array.Clear(handles, 0, handles.Length); Array.Clear(bridges, 0, bridges.Length);
                    ApiSharedNativeSites.Release(ownership, ApiSharedNativeSites.PlayerViewNetwork);
                });
                throw;
            }
        }

        private static void Validate(IntPtr module, bool installed)
        {
            foreach (var site in Sites)
            {
                int index = (int)site;
                if (!handles[index].Success)
                    throw new InvalidOperationException("Network identity boundary handle is unavailable.");
                bridges[index].Validate(handles[index].Hook, module + Rva(site), Window(site), installed);
            }
        }
    }
}

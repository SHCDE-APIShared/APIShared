using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.Simulation;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.GameGlobals;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    // No hook or SE resource helper: native gold is one signed Int32, separately validated from pointer validity.
    internal sealed unsafe class GoodsWalletNative : IGoodsWalletBackend
    {
        internal static readonly GoodsWalletNative Instance = new GoodsWalletNative();
        internal static readonly GoodsWalletEngine Engine = new GoodsWalletEngine(Instance);
        private GamePlayerManagerAPI players;
        private object gate;
        private ulong manager;
        private volatile bool ready;
        private GoodsWalletNative() { }
        public bool IsReady => ready;
        public object EngineMonitor => gate;
        public object World => MissionLifecycleService.NativeWorld;
        public bool HasAuthorityContext
        {
            get
            {
                var current = MissionLifecycleService.NativeClock;
                return AllowsMode(current != null && current.Context.Mode.Kind != GameModes.GameModeKind.Unknown,
                    current?.Context.Mode.IsRealMultiplayer == true, SimulationMutationScope.IsTickActive, SimulationMutationScope.IsAuthenticatedChore);
            }
        }
        internal static void Initialize(string hash, ManualLogSource log)
        {
            var native = Instance;
            try
            {
                if (hash != ApiSharedRuntime.SupportedHash || !ValidateLayout())
                    throw new InvalidOperationException("The native wallet image or resource layout is unsupported.");
                native.gate = Internal.EngineMonitor.Current ?? throw new InvalidOperationException("The engine monitor is unavailable.");
                native.manager = GameGlobalsManager.Instance.GamePlayerManagerVA;
                if (native.manager == 0 || native.manager > (ulong)long.MaxValue - 0x1343FCul - 8ul * 0x583Cul)
                    throw new InvalidOperationException("The resolved player manager range is unavailable.");
                // Do not initialize this lazy API before its manager/readiness checks above.
                native.players = GamePlayerManagerAPI.Instance;
                native.ready = true;
            }
            catch (Exception error)
            {
                native.ready = false;
                RuntimeHealth.Failure(log, "goods-wallet/startup", true, "Native goods wallet unavailable: " + error);
            }
        }
        internal static bool AllowsMode(bool knownWorld, bool multiplayer, bool nativeTick, bool authenticatedChore) =>
            knownWorld && (!multiplayer || nativeTick || authenticatedChore);
        internal static bool ValidateLayout() => typeof(GamePlayerResources).StructLayoutAttribute?.Value == LayoutKind.Sequential &&
            typeof(GamePlayerResources).StructLayoutAttribute.Pack == 1 && Marshal.SizeOf(typeof(GamePlayerResources)) == 22588 &&
            Marshal.OffsetOf(typeof(GamePlayerResources), nameof(GamePlayerResources.r_TotalGoodsGold)).ToInt32() == 0x50C &&
            typeof(GamePlayerResources).GetField(nameof(GamePlayerResources.r_TotalGoodsGold))?.FieldType == typeof(int);
        internal static int* GoldCell(GamePlayerResources* resources) => resources == null ? null : &resources->r_TotalGoodsGold;
        // Reused by the storage owner. All checks and any native SDK work precede its direct
        // store plus prepared ledger/physical-row publication; this is not a commit callback.
        internal bool TryOpenCurrent(int player, out int* cell, out object world, out string reason)
        {
            cell = null; world = null;
            if (!ready || player < 1 || player > 8) { reason = "The wallet is unavailable or the player is invalid."; return false; }
            if (gate == null || !Monitor.IsEntered(gate) || !HasAuthorityContext)
            { reason = "An engine-protected authoritative simulation context is required."; return false; }
            object current = World;
            if (current == null || !TryGetGoldCell(player, out cell) || !ReferenceEquals(current, World))
            { cell = null; reason = "The exact native wallet/world is unavailable."; return false; }
            world = current; reason = string.Empty; return true;
        }
        public bool TryGetGoldCell(int player, out int* cell)
        {
            cell = null;
            if (!ready || player < 1 || player > 8 || manager != GameGlobalsManager.Instance.GamePlayerManagerVA ||
                !players.TryGetPlayerResourcesById(player, out GamePlayerResources* resources) || resources == null) return false;
            ulong expected = manager + 0x1343FCul + (ulong)(player - 1) * 0x583Cul;
            if ((ulong)resources != expected) return false;
            cell = GoldCell(resources); return true;
        }
    }
}

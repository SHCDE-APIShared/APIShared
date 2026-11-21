using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using APIShared.Goods;

namespace APIShared.Economy
{
    // Only the trusted native boundary is injected in offline fixtures. No interface call occurs after the gold store.
    internal unsafe interface IGoodsWalletBackend
    {
        bool IsReady { get; }
        object EngineMonitor { get; }
        object World { get; }
        bool HasAuthorityContext { get; }
        bool TryGetGoldCell(int player, out int* cell);
    }

    internal sealed unsafe class GoodsWalletEngine
    {
        private static readonly object warmSync = new object();
        private static bool warmed;
        private readonly IGoodsWalletBackend backend;
        private readonly ConditionalWeakTable<CustomGoodsInventory, Binding> bindings = new ConditionalWeakTable<CustomGoodsInventory, Binding>();
        private sealed class Binding { internal object Epoch, World; }
        private static readonly ConditionalWeakTable<CustomGoodsInventory, Binding>.CreateValueCallback NewBinding = _ => new Binding();
        internal GoodsWalletEngine(IGoodsWalletBackend backend) { this.backend = backend; }
        private static GoodsWalletResult Reject(GoodsWalletStatus status, string reason, GoodsTransactionStatus ledger = GoodsTransactionStatus.InvalidRequest) =>
            new GoodsWalletResult(status, ledger, reason);
        private bool Context(out object world, out string reason)
        {
            world = null;
            if (!backend.IsReady) { reason = "The native wallet is unavailable."; return false; }
            object gate = backend.EngineMonitor;
            if (gate == null || !Monitor.IsEntered(gate) || !backend.HasAuthorityContext)
            { reason = "An engine-protected known world and native-tick/authenticated-Chore multiplayer context are required."; return false; }
            world = backend.World;
            reason = world == null ? "No confirmed native world is available." : string.Empty;
            return world != null;
        }
        internal bool TryRead(int player, out int gold, out string reason)
        {
            gold = 0;
            if (player < 1 || player > 8) { reason = "Player IDs are 1–8."; return false; }
            if (!Context(out var world, out reason)) return false;
            try
            {
                if (!backend.TryGetGoldCell(player, out int* cell) || cell == null)
                { reason = "The checked native wallet pointer is unavailable."; return false; }
                int value = *cell;
                if (!ReferenceEquals(world, backend.World)) { reason = "The native world changed."; return false; }
                gold = value; reason = string.Empty; return true;
            }
            catch (Exception error) { reason = "Wallet read failed: " + error.Message; return false; }
        }
        internal GoodsWalletResult Exchange(string owner, CustomGoodsClient goods, long sequence, IReadOnlyList<GoodsChange> changes, int player, int goldDelta)
        {
            if (goods == null || goods.OwnerGuid != owner || sequence <= 0 || player < 1 || player > 8 || changes == null)
                return Reject(GoodsWalletStatus.InvalidRequest, "Matching owner, positive sequence, player 1–8 and changes are required.");
            // Consumer collection code executes outside the inventory monitor and before any preparation.
            int count = changes.Count;
            if (count < 1 || count > 32) return Reject(GoodsWalletStatus.InvalidRequest, "Supply 1–32 change lines.");
            var owned = new GoodsChange[count];
            for (int i = 0; i < count; i++)
            {
                owned[i] = changes[i];
                if (owned[i].PlayerId != player) return Reject(GoodsWalletStatus.InvalidRequest, "Every line must name the wallet player.");
            }
            if (!Context(out var world, out string reason)) return Reject(backend.IsReady ? GoodsWalletStatus.InvalidContext : GoodsWalletStatus.Unavailable, reason);
            // Kind 1, wire version 1, one player and exact signed delta. Future adapter-specific
            // storage intent uses a different kind; opaque intent is bounded to 256 bytes in Content.
            var intent = new byte[10]; intent[0] = 1; intent[1] = 1;
            WriteInt32(intent, 2, player); WriteInt32(intent, 6, goldDelta);
            try
            {
                WarmCommitPath(); // First-call JIT/publication failures precede every real native write.
                GoodsTransactionStatus status = goods.PrepareNative(sequence, owned, intent, out var prepared);
                if (prepared == null) return Reject(GoodsWalletStatus.LedgerRejected, "The virtual inventory rejected this exchange.", status);
                using (prepared)
                {
                    var binding = bindings.GetValue(goods.InventoryIdentity, NewBinding);
                    if (!ReferenceEquals(binding.Epoch, prepared.Epoch)) { binding.Epoch = prepared.Epoch; binding.World = world; }
                    if (!ReferenceEquals(binding.World, world) || !ReferenceEquals(world, backend.World))
                        return Reject(GoodsWalletStatus.WorldChanged, "Reset or restore the inventory generation before binding a different native world.", status);
                    if (status == GoodsTransactionStatus.AlreadyApplied)
                        return new GoodsWalletResult(GoodsWalletStatus.AlreadyApplied, status, string.Empty);
                    if (!backend.TryGetGoldCell(player, out int* cell) || cell == null)
                        return Reject(GoodsWalletStatus.Unavailable, "The checked native wallet pointer is unavailable.", status);
                    int before = *cell;
                    long target = (long)before + goldDelta;
                    if (target < 0) return Reject(GoodsWalletStatus.InsufficientGold, "The exchange would leave negative gold.", status);
                    if (target > int.MaxValue) return Reject(GoodsWalletStatus.GoldOverflow, "The exchange would overflow native signed gold.", status);
                    var result = new GoodsWalletResult(GoodsWalletStatus.Applied, status, string.Empty, before, (int)target);
                    if (!prepared.IsPrepared || !Context(out var finalWorld, out _) || !ReferenceEquals(world, finalWorld) || *cell != before)
                        return Reject(GoodsWalletStatus.WorldChanged, "The prepared native world/wallet changed before publication.", status);
                    // Critical section: direct validated Int32 write, then preallocated managed publication.
                    // No native helper, virtual dispatch, allocation or consumer callback occurs between them.
                    PublishGoldAndGoods(cell, (int)target, prepared);
                    return result;
                }
            }
            catch (Exception error) { return Reject(GoodsWalletStatus.Unavailable, "Wallet preparation failed before publication: " + error.Message); }
        }
        // Runs the actual critical method, Content publication and Dispose against isolated scratch
        // state and a stack-local gold cell. No real inventory, backend or native pointer is touched.
        internal static void WarmCommitPath()
        {
            lock (warmSync)
            {
                if (warmed) return;
                using (var scratch = CustomGoodsInventory.CreateWarmCommit())
                {
                    int scratchGold = 0;
                    PublishGoldAndGoods(&scratchGold, 1, scratch);
                }
                warmed = true;
            }
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void PublishGoldAndGoods(int* cell, int target, CustomGoodsInventory.PreparedGoodsCommit prepared)
        { *cell = target; prepared.Publish(); }
        private static void WriteInt32(byte[] bytes, int at, int value)
        { unchecked { bytes[at] = (byte)value; bytes[at + 1] = (byte)(value >> 8); bytes[at + 2] = (byte)(value >> 16); bytes[at + 3] = (byte)(value >> 24); } }
    }
}

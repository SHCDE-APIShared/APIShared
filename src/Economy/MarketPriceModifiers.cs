using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Modifiers;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.API.Components.SaveData;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>A base price per native lot of five goods, before the caller's own quantity arithmetic.</summary>
    public readonly struct MarketPriceModifierContext
    {
        /// <summary>Creates a value context, including for game-independent policy tests.</summary>
        public MarketPriceModifierContext(eGoods good, MarketPriceDirection direction, bool isAIPlayer, int originalBasePrice)
        { Good = good; Direction = direction; IsAIPlayer = isAIPlayer; OriginalBasePrice = originalBasePrice; }
        /// <summary>Goods type, not a one-based object ID.</summary>
        public eGoods Good { get; }
        /// <summary>Buy or sell base-price column.</summary>
        public MarketPriceDirection Direction { get; }
        /// <summary>True for the audited AI/helper projection; false for the shared current table.</summary>
        public bool IsAIPlayer { get; }
        /// <summary>Unmultiplied saved/default base price for one lot of five goods.</summary>
        public int OriginalBasePrice { get; }
    }

    /// <summary>Process-lived registration. Disable logically; never dispose a published native owner.</summary>
    public sealed class MarketPriceModifierHandle
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Func<MarketPriceModifierContext, ValueModifierContribution> Provider;
        internal bool Enabled;
        internal MarketPriceModifierHandle(string owner, string id, int order,
            Func<MarketPriceModifierContext, ValueModifierContribution> provider)
        { Owner = owner; Id = id; Order = order; Provider = provider; }
        /// <summary>Changes demand, then reconciles the shared table. Configure identically on every peer;
        /// call only at a synchronized settings/mission boundary, never from a policy callback.</summary>
        public void SetEnabled(bool enabled) => MarketPriceModifiers.SetEnabled(this, enabled);
    }

    /// <summary>Shared owner of composable market base prices, not completed transaction totals.</summary>
    /// <remarks>Register during LibraryLoaded after APIShared publication. Registrations start disabled,
    /// persist until process exit, and are ordered by order/ordinal owner GUID/registration ID. Providers
    /// run outside registry locks, under the engine monitor, and must be pure, deterministic and nonrecursive.
    /// Throwing providers are isolated. Current-table prices cover manual trade, HUD, auto-trade and valuation;
    /// the existing CEB10/CEB90 owner supplies the AI projection. A legacy MarketPriceEvents total veto remains
    /// authoritative and is never interpreted as a base-price override. Arbitrary direct writers must call
    /// Refresh after their change; detected foreign absolute prices win over BeforeFixedValue factors.
    /// Changing policies is an explicit synchronized operation; there is no periodic table scan.</remarks>
    public static class MarketPriceModifiers
    {
        private const string SaveKey = "APIShared.MarketPriceModifiers.v1";
        private static readonly object Sync = new object();
        private static readonly object InitializationSync = new object();
        private static readonly List<MarketPriceModifierHandle> Registrations = new List<MarketPriceModifierHandle>();
        private static MarketPriceModifierHandle[] snapshot = Array.Empty<MarketPriceModifierHandle>();
        private static readonly ManualLogSource Log = BepInEx.Logging.Logger.CreateLogSource("APIShared.MarketPriceModifiers");
        private static readonly MarketPriceTableCoordinator Table = new MarketPriceTableCoordinator((int)eGoods.Count,
            Read, Write, ReadDefault);
        private static bool initialized, initializationAttempted;
        private static object engineMonitor;
        private static string initializationFailure;
        private static volatile bool sessionActive, contextualPrices;
        private static long callbackFailures;
        private static byte[] archived;
        private static object archiveSource;
        [ThreadStatic] private static bool evaluating;

        /// <summary>Registers an initially disabled provider. A duplicate identity or unavailable dependency
        /// returns false and a reason. This adds no second native market hook.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId,
            Func<MarketPriceModifierContext, ValueModifierContribution> provider,
            out MarketPriceModifierHandle handle, out string reason, int order = 0)
        {
            handle = null;
            if (evaluating)
            { reason = "A market-price provider cannot register another provider."; return false; }
            if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(registrationId) || provider == null)
            { reason = "An owner, registration ID and provider are required."; return false; }
            try { lock (InitializationSync) EnsureInitialized(); }
            catch (Exception ex) { reason = ex.Message; return false; }
            lock (Sync)
            {
                foreach (var item in Registrations)
                    if (item.Owner == ownerGuid && item.Id == registrationId)
                    { reason = "Duplicate market-price modifier identity."; return false; }
                handle = new MarketPriceModifierHandle(ownerGuid, registrationId, order, provider);
                Registrations.Add(handle);
                Registrations.Sort(Compare);
                PublishEnabled();
            }
            reason = string.Empty;
            return true;
        }

        /// <summary>Isolated provider error count; not a trade acknowledgement.</summary>
        public static long CallbackFailures => Interlocked.Read(ref callbackFailures);

        /// <summary>Re-evaluates configured providers at an explicit synchronized boundary. It does nothing
        /// outside a completed mission. Existing outputs are never used as a new multiplier base.</summary>
        public static void Refresh()
        {
            if (evaluating) throw new InvalidOperationException("A market-price provider cannot mutate its registry.");
            if (!sessionActive) return;
            lock (engineMonitor) RefreshLocked();
        }

        internal static void SetEnabled(MarketPriceModifierHandle handle, bool enabled)
        {
            if (evaluating) throw new InvalidOperationException("A market-price provider cannot change activation.");
            lock (Sync)
            {
                if (handle.Enabled == enabled) return;
                handle.Enabled = enabled;
                // Copy activation as well as registration identity; readers retain an immutable selection.
                PublishEnabled();
            }
            Refresh();
        }

        private static int Compare(MarketPriceModifierHandle a, MarketPriceModifierHandle b)
        {
            int result = a.Order.CompareTo(b.Order);
            if (result == 0) result = StringComparer.Ordinal.Compare(a.Owner, b.Owner);
            return result != 0 ? result : StringComparer.Ordinal.Compare(a.Id, b.Id);
        }

        private static void PublishEnabled()
        {
            var active = new List<MarketPriceModifierHandle>();
            foreach (var item in Registrations) if (item.Enabled) active.Add(item);
            Volatile.Write(ref snapshot, active.ToArray());
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            if (initializationAttempted) throw new InvalidOperationException(initializationFailure);
            initializationAttempted = true;
            try
            {
                // Resolve the real engine monitor once; it is private in the shipping assembly.
                engineMonitor = Internal.EngineMonitor.Current ?? throw new MissingFieldException("EngineInterface.threadLock is unavailable.");
                MarketPriceNativeRuntime.EnsureInstalled();
                if (!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out var lifecycle, out var diagnostic))
                    throw new InvalidOperationException("Market-price lifecycle unavailable: " + diagnostic.Reason);
                if (!ModSaveDataAPI.Instance.RegisterModDataHandler(SaveKey, Save, Buffer, ClearArchive))
                    throw new InvalidOperationException("Market-price save-data registration failed.");
                if (!lifecycle.TryRegisterObserver("market-price-modifiers", OnStart, OnEnd, null, out diagnostic))
                    throw new InvalidOperationException("Market-price lifecycle registration failed: " + diagnostic.Reason);
                initialized = true;
            }
            catch (Exception ex) { initializationFailure = ex.Message; throw; }
        }

        private static void OnStart(MissionLifecycleNotification notification)
        {
            lock (engineMonitor)
            {
                byte[] bytes;
                lock (Sync)
                {
                    bytes = notification.Context.IsSave && ReferenceEquals(archiveSource,
                        GameMapArchiveManagerAPI.Instance.GetMapArchive()) ? archived : null;
                    archived = null; archiveSource = null;
                }
                MarketPriceSavedCell[] saved = null;
                if (bytes != null)
                {
                    try { saved = MarketPriceTableCoordinator.Decode(bytes, (int)eGoods.Count); }
                    catch (Exception ex) { Report("Invalid saved market-price bases; existing prices are preserved: " + ex.Message); }
                }
                Table.Begin(saved);
                sessionActive = true;
                RefreshLocked();
            }
        }

        private static void OnEnd(MissionLifecycleNotification notification)
        {
            lock (engineMonitor)
            {
                sessionActive = false; contextualPrices = false;
                Table.End(Report);
            }
        }

        private static void Buffer(byte[] bytes, LoadContext context)
        { lock (Sync) { archived = bytes; archiveSource = GameMapArchiveManagerAPI.Instance.GetMapArchive(); } }
        private static void ClearArchive() { lock (Sync) { archived = null; archiveSource = null; } }
        private static byte[] Save(SaveContext context)
        {
            if (!sessionActive || !context.IsSaveFile || context.IsMapEditorSave) return null;
            lock (engineMonitor) return sessionActive ? Table.Save() : null;
        }

        private static void RefreshLocked()
        {
            if (!sessionActive) return;
            var selected = Volatile.Read(ref snapshot);
            Table.Refresh((good, buying, ai, baseline, foreign) => Resolve(selected, good, buying, ai, baseline, foreign), Report);
            contextualPrices = Table.HasContextualPrices;
        }

        internal static MarketPriceResolution Resolve(MarketPriceModifierHandle[] selected, int good, bool buying,
            bool ai, int baseline, int? foreign)
        {
            var values = new List<ValueModifierContribution>(selected.Length + 1);
            var context = new MarketPriceModifierContext((eGoods)good,
                buying ? MarketPriceDirection.Buy : MarketPriceDirection.Sell, ai, baseline);
            bool effect = false, after = false;
            evaluating = true;
            try
            {
                foreach (var item in selected)
                {
                    try
                    {
                        ValueModifierContribution value = item.Provider(context);
                        if (value.Kind == ValueModifierKind.None) continue;
                        values.Add(value); effect = true;
                        after |= value.Kind == ValueModifierKind.Factor && value.Stage == ValueModifierStage.AfterFixedValue && value.Value != 1;
                    }
                    catch (Exception ex)
                    {
                        long count = Interlocked.Increment(ref callbackFailures);
                        if ((count & (count - 1)) == 0) Report("Market-price provider " + item.Owner + "/" + item.Id + " failed: " + ex.Message);
                    }
                }
            }
            finally { evaluating = false; }
            // An unregistered direct writer has no comparable registration order. Preserve its
            // absolute cell as the final fixed value; only an explicit After factor may alter it.
            if (foreign.HasValue)
            {
                if (!after) return new MarketPriceResolution(foreign.Value, false);
                values.Add(ValueModifierContribution.FixedValue(foreign.Value));
            }
            if (!effect) return new MarketPriceResolution(foreign ?? baseline, false);
            // HUD 25-good buy preview multiplies the Int16 lot price by five;
            // sell uses division before multiplication: floor(base / 5) * 25.
            int maximum = buying ? short.MaxValue / 5 : (short.MaxValue / 25) * 5 + 4;
            int price = (int)ValueModifierCalculator.Calculate(baseline, values, 0, maximum);
            return new MarketPriceResolution(price, true);
        }

        // Runs in the original-call slot, after all legacy Pre vetoes. The original native
        // helper executes exactly once, with its own arithmetic and unchanged arguments.
        internal static int InvokeOriginal(MarketPriceDirection direction, IntPtr manager, int playerId,
            int good, int amount, MarketPriceNativeRuntime.PriceCall original)
        {
            if (!sessionActive || !contextualPrices || manager == IntPtr.Zero || playerId < 1 || playerId > GamePlayerManagerAPI.MAX_PLAYERS ||
                good < 0 || good >= (int)eGoods.Count)
                return original(manager, playerId, good, amount);
            lock (engineMonitor)
            {
                if (!sessionActive || !contextualPrices || !GamePlayerManagerAPI.Instance.IsAIPlayer(playerId))
                    return original(manager, playerId, good, amount);
                bool buying = direction == MarketPriceDirection.Buy;
                // The displacement comes from the complete hash-validated native helper.
                // Avoid SE GetTradeBasePrice's verbose pointer-string allocation on every AI query.
                int offset = BitConverter.ToInt32(buying ? MarketPriceNativeRuntime.BuyBytes : MarketPriceNativeRuntime.SellBytes, 6);
                IntPtr cell = IntPtr.Add(manager, offset + good * 8);
                int current = Marshal.ReadInt32(cell);
                if (current != Table.AppliedPrice(good, buying)) { RefreshLocked(); current = Marshal.ReadInt32(cell); }
                int desired = Table.AIPrice(good, buying);
                if (current == desired) return original(manager, playerId, good, amount);
                try { Marshal.WriteInt32(cell, desired); return original(manager, playerId, good, amount); }
                finally { Marshal.WriteInt32(cell, current); }
            }
        }

        private static int Read(int good, bool buying)
        { var value = GamePlayerManagerAPI.Instance.GetTradeBasePrice((eGoods)good); return buying ? value.BuyPrice : value.SellPrice; }
        private static int ReadDefault(int good, bool buying)
        { var value = GamePlayerManagerAPI.Instance.GetDefaultTradeBasePrice((eGoods)good); return buying ? value.BuyPrice : value.SellPrice; }
        private static void Write(int good, bool buying, int value)
        {
            var current = GamePlayerManagerAPI.Instance.GetTradeBasePrice((eGoods)good);
            GamePlayerManagerAPI.Instance.SetTradeBasePrice((eGoods)good,
                new PackedGoodPrice(buying ? value : current.BuyPrice, buying ? current.SellPrice : value));
        }
        private static void Report(string message)
        { try { Log.LogWarning("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] " + message); } catch { } }
    }
}

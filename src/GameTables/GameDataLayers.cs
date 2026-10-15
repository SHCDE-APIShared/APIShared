using System;
using APIShared.Savegames;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.Interop;

namespace APIShared.GameTables
{
    /// <summary>Ordered layers over Script Extender data tables, composed from the game's own values into one
    /// write per phase. Register a layer instead of writing the table yourself.</summary>
    /// <remarks>
    /// <para><b>Composition.</b> Every cell starts from its Base, the game's own value read once per process
    /// before APIShared first writes the table. Then every active layer applies in layer order
    /// (<see cref="GameDataLayer"/>: Additive, Multiplier, Override, Clamp), within a layer in ascending order,
    /// ordinal owner GUID and ordinal registration ID. Integer arithmetic only.</para>
    /// <para><b>Phases.</b> APIShared composes each table at every mission start (new game and loaded save,
    /// after the native initialization; not in the editor) and when an owner calls <see cref="Reapply"/>.
    /// A row is written only when one of its composed cells differs from the live value, so a layer that
    /// changes nothing (for example a factor of 1.0) writes nothing.</para>
    /// <para><b>Other writers.</b> A cell whose live value is neither its Base nor the value of this mission's
    /// last composition was written by another mod. It is reported once per mission in APIShared's log and
    /// counted in <see cref="ForeignCells"/>, and left as that mod wrote it: the service never overwrites a
    /// direct write.</para>
    /// <para><b>Reload.</b> The composed values are kept in the versioned save record
    /// <c>APIShared.GameData</c>. After a load the table may hold them or the game's values; both count as
    /// this service's own, and the new composition starts from Base (restore, then apply), so nothing
    /// compounds.</para>
    /// <para><b>Thread and multiplayer.</b> Compositions serialize on one lock and run on the Unity thread
    /// (the mission lifecycle publisher, or the caller of <see cref="Reapply"/>). Every peer composes from its
    /// own Base with the same layers at the same phase; layer inputs (settings) must be identical on every
    /// peer before the phase, for example host-synchronized before the mission starts.</para>
    /// <para><b>Cost.</b> Nothing is subscribed and no table is touched before the first registration.
    /// A composition allocates nothing.</para>
    /// <para>Contract, consumer notes and measurements: docs/GAME_DATA.md.</para>
    /// </remarks>
    public static class GameDataLayers
    {
        internal const string TradeBasePricesEntry = "trade-prices";
        private static readonly object sync = new object();
        private static ManualLogSource log;
        private static bool saveRegistered, missionRegistered;
        internal static readonly GameDataTableComposer TradeBasePrices =
            new GameDataTableComposer(GameDataTable.TradeBasePrices, new TradeBasePriceStore(), Activate, Report);
        private static readonly GameDataTableComposer[] Tables = { TradeBasePrices };
        private static readonly string[] EntryNames = { TradeBasePricesEntry };

        /// <summary>Registers a layer for <paramref name="table"/>.</summary>
        /// <remarks>Any thread; register from <c>Awake</c> or the Extender's <c>LibraryLoaded</c>, on every
        /// multiplayer peer. The first registration subscribes APIShared to the mission lifecycle and registers
        /// the save record, both for the process lifetime. A registration takes effect at the next phase.</remarks>
        /// <param name="ownerGuid">Your plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID and table.</param>
        /// <param name="table">The table.</param>
        /// <param name="layer">The layer of every contribution this handler returns.</param>
        /// <param name="order">Ascending order within the layer.</param>
        /// <param name="handler">Decides the contribution per cell; deterministic.</param>
        /// <param name="registration">Handle on success; dispose it to unregister.</param>
        /// <param name="reason">Empty on success; otherwise why the registration was rejected.</param>
        /// <returns>False for missing arguments, an unknown table or layer, a duplicate owner/ID pair, or when
        /// the mission lifecycle or the save record is unavailable (a later registration retries).</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, GameDataTable table, GameDataLayer layer, int order,
            GameDataLayerHandler handler, out GameDataRegistration registration, out string reason)
        {
            if (table != GameDataTable.TradeBasePrices) { registration = null; reason = "Unknown table " + table + "."; return false; }
            return TradeBasePrices.TryAdd(ownerGuid, registrationId, layer, order, handler, out registration, out reason);
        }

        /// <summary>Composes <paramref name="table"/> now, for example after host-synchronized layer inputs
        /// changed during a mission. Call it on the Unity thread at a point that is the same on every peer.
        /// Returns the number of rows written.</summary>
        public static int Reapply(GameDataTable table) =>
            table == GameDataTable.TradeBasePrices ? TradeBasePrices.Apply() : 0;

        /// <summary>Isolated handler exceptions and rejected contributions.</summary>
        public static long CallbackFailures => TradeBasePrices.CallbackFailures;

        /// <summary>Cells found written by another mod, counted once per cell and mission.</summary>
        public static long ForeignCells => TradeBasePrices.ForeignCells;

        /// <summary>Rows written by compositions. Diagnostic only.</summary>
        public static long RowWrites => TradeBasePrices.RowWrites;

        internal static void Initialize(ManualLogSource logger) { lock (sync) log = logger; }

        // Under the composer's registration lock. The save record comes first, so no table is ever written
        // without a record of what was composed.
        private static string Activate()
        {
            lock (sync)
            {
                // The Script Extender members the trade price table binds, checked once before anything runs.
                string mismatch;
                try { mismatch = TradeBasePriceStore.MemberContract().FindMismatch(); }
                catch (Exception error) { mismatch = error.GetType().Name + ": " + error.Message; }
                if (mismatch != null) return "Script Extender trade price members changed: " + mismatch;
                string reason;
                if (!saveRegistered)
                {
                    var record = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, GameDataSaveRecord.Identifier, 1, 1,
                        EntryNames, writer => GameDataSaveRecord.Capture(writer, Tables, EntryNames),
                        load => GameDataSaveRecord.Loaded(load, Tables, EntryNames));
                    if (!VersionedSaveData.TryRegister(record, out _, out reason)) return "GameData save record unavailable: " + reason;
                    saveRegistered = true;
                }
                if (!missionRegistered)
                {
                    if (!ApiSharedRuntime.ProcessInstance.TryGetMissionLifecycle(APISharedPlugin.PluginGuid, out IMissionLifecycleCapability missions, out NativeCapabilityDiagnostic diagnostic) ||
                        !missions.TryRegisterObserver("GameDataLayers", OnMissionStart, null, null, out diagnostic))
                        return "Mission lifecycle unavailable: " + diagnostic?.Reason;
                    missionRegistered = true;
                }
                return null;
            }
        }

        private static void OnMissionStart(MissionLifecycleNotification notification)
        {
            MissionStartKind kind = notification.Context?.StartKind ?? MissionStartKind.EditorCreated;
            if (kind != MissionStartKind.NewGame && kind != MissionStartKind.LoadedSave) return;
            foreach (GameDataTableComposer composer in Tables)
            {
                try { composer.BeginMission(); composer.Apply(); }
                catch (Exception ex) { Report(composer.Table + " composition failed: " + ex.Message); }
            }
        }

        private static void Report(string message)
        {
            try { NativeApiLog.Warning(log, "GAMEDATA: " + message); } catch { }
        }
    }

    // SE 2.14 GamePlayerManagerAPI: the current table at player manager + 0x1817B8 and the default table
    // in the image that every map load copies from. Rows are eGoods values 0..Count-1.
    internal sealed class TradeBasePriceStore : IGameDataTableStore
    {
        public int Rows => (int)eGoods.Count;
        public int Columns => 2;

        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Method(typeof(GamePlayerManagerAPI), nameof(GamePlayerManagerAPI.GetTradeBasePrice), typeof(PackedGoodPrice), typeof(eGoods))
            .Method(typeof(GamePlayerManagerAPI), nameof(GamePlayerManagerAPI.SetTradeBasePrice), typeof(void), typeof(eGoods), typeof(PackedGoodPrice))
            .Method(typeof(GamePlayerManagerAPI), nameof(GamePlayerManagerAPI.GetDefaultTradeBasePrice), typeof(PackedGoodPrice), typeof(eGoods))
            .Field(typeof(PackedGoodPrice), nameof(PackedGoodPrice.BuyPrice), typeof(int))
            .Field(typeof(PackedGoodPrice), nameof(PackedGoodPrice.SellPrice), typeof(int));

        public bool TryReadBase(int row, int[] values)
        {
            try { PackedGoodPrice price = GamePlayerManagerAPI.Instance.GetDefaultTradeBasePrice((eGoods)row); values[0] = price.BuyPrice; values[1] = price.SellPrice; return true; }
            catch { return false; }
        }

        public bool TryReadLive(int row, int[] values)
        {
            try { PackedGoodPrice price = GamePlayerManagerAPI.Instance.GetTradeBasePrice((eGoods)row); values[0] = price.BuyPrice; values[1] = price.SellPrice; return true; }
            catch { return false; }
        }

        public void WriteRow(int row, int[] values)
        {
            try { GamePlayerManagerAPI.Instance.SetTradeBasePrice((eGoods)row, new PackedGoodPrice(values[0], values[1])); }
            catch { }
        }
    }
}

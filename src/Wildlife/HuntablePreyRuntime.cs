using System;
using System.Threading;
using APIShared.Economy;
using APIShared.Internal;
using APIShared.Simulation;
using BepInEx.Logging;
using R3;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;

namespace APIShared.Wildlife
{
    // Process-owned. Two rooted, permanently installed paths do the work, both dormant (one volatile
    // read) while no registered type is active: the Extender's Hunter-query event and a simulation tick.
    // The mission owner (WorkerProductionRuntime) freezes, activates and ends the catalog together with
    // the weapon-workshop catalog, because both share its multiplayer peer agreement.
    internal static unsafe class HuntablePreyRuntime
    {
        // Vanilla's Hunter lease word (manager form +0xAA4). The Hunter writes 2 on each of its own
        // updates while it holds the prey (0x12FE63, 0x130044, 0x130259, 0x130338, 0x13064F); the
        // query (0x18AFFD) and the corpse search (0x18B6ED) only accept 0. Hash-bound, see
        // HuntablePreyNativeContract.
        internal const int ReservationOffset = HuntablePreyNativeContract.PublicReservationOffset;
        // Unit slots visited per map tick. A held lease is renewed every tick and so never reaches 0;
        // a released one reaches 0 after two visits (two passes over the unit array).
        internal const int SlotsPerTick = 256;

        private static readonly object Gate = new object();
        private static HuntablePreyRegistration[] registrations = Array.Empty<HuntablePreyRegistration>();
        private static HuntablePreyRegistration[] preparedRegistrations;
        private static int version, preparedVersion = -1;
        private static HuntablePreyCatalog prepared = HuntablePreyCatalogBuilder.VanillaOnly;
        private static HuntablePreyCatalog frozen = HuntablePreyCatalogBuilder.VanillaOnly;
        private static HuntablePreyCatalog active = HuntablePreyCatalogBuilder.VanillaOnly;
        private static byte[] demand;
        private static int worldActive, available, lastTick;
        private static bool hasLastTick;
        private static ManualLogSource log;
        private static IDisposable querySubscription;
        private static string unavailable = "The huntable-prey service has not been initialized.";

        internal static bool IsAvailable => Volatile.Read(ref available) != 0;
        internal static HuntablePreyCatalog Current => Volatile.Read(ref worldActive) != 0 ? Volatile.Read(ref active) : PrepareCatalog();

        internal static void Initialize(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (IsAvailable) return;
            log = logger;
            try
            {
                if (!HuntablePreyNativeContract.TryValidate(context, hash, out string invalid))
                    throw new InvalidOperationException(invalid);
                // Rooted for the process lifetime; never disposed by plugin or map teardown.
                querySubscription = UnitR3EventHooks.OnUnitHunterQueryTarget.Observable.Subscribe(OnHunterQuery);
                if (!SimulationTickEvents.TryRegister(APISharedPlugin.PluginGuid, "wildlife/hunter-reservation", Release,
                        out string reason, int.MinValue))
                    throw new InvalidOperationException(reason);
                Volatile.Write(ref available, 1);
                NativeApiLog.Info(log, "Huntable prey ready: Hunter-query admission and reservation release stay dormant until a registered type is active.");
            }
            catch (Exception error)
            {
                Volatile.Write(ref unavailable, error.Message);
                NativeApiLog.Error(log, "Huntable prey unavailable; registered types stay unhuntable: " + error);
            }
        }

        internal static bool TryRegister(string owner, string id, HuntablePreyDefinition definition, int priority,
            out HuntablePreyRegistration registration, out string reason)
        {
            registration = null;
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Huntable prey must register on the initialized Unity thread."; return false; }
            if (!IsAvailable) { reason = Volatile.Read(ref unavailable); return false; }
            if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || owner.Length > 128 || id.Length > 128 ||
                StringComparer.Ordinal.Equals(owner, APISharedPlugin.PluginGuid))
            { reason = "A valid consumer GUID and registration ID are required."; return false; }
            if (definition == null || definition.UnitType < 1 || definition.UnitType > ushort.MaxValue || definition.HandlingCost < 0)
            { reason = "The unit type must be from 1 through 65535 and the handling cost non-negative."; return false; }

            lock (Gate)
            {
                foreach (var old in registrations)
                    if (StringComparer.Ordinal.Equals(old.OwnerGuid, owner) && StringComparer.Ordinal.Equals(old.RegistrationId, id))
                    { reason = "This owner already registered that huntable-prey ID."; return false; }
                registration = new HuntablePreyRegistration(owner, id, definition, priority,
                    HuntablePreyCatalogBuilder.UnsupportedReason(definition.UnitType));
                var next = new HuntablePreyRegistration[registrations.Length + 1];
                Array.Copy(registrations, next, registrations.Length);
                next[next.Length - 1] = registration;
                registrations = next;
            }
            reason = "";
            NativeApiLog.Info(log, "Huntable prey registered: owner=" + owner + ", id=" + id + ", type=" + definition.UnitType +
                (registration.UnsupportedReason.Length != 0 ? ", unsupported: " + registration.UnsupportedReason : "."));
            WorkerProductionAgreement.RequestRefresh();
            return true;
        }

        internal static bool TrySetEnabled(HuntablePreyRegistration registration, bool value, out string reason)
        {
            if (registration == null) { reason = "The registration is required."; return false; }
            if (!UnityMainThreadDispatch.IsMainThread)
            { reason = "Huntable prey changes require the initialized Unity thread."; return false; }
            reason = "";
            if (!registration.SetEnabledFlag(value)) return true;
            Interlocked.Increment(ref version);
            WorkerProductionAgreement.RequestRefresh();
            return true;
        }

        internal static HuntablePreyCatalog PrepareCatalog()
        {
            lock (Gate)
            {
                int currentVersion = Volatile.Read(ref version);
                if (!ReferenceEquals(preparedRegistrations, registrations) || preparedVersion != currentVersion)
                {
                    prepared = HuntablePreyCatalogBuilder.Compile(registrations);
                    preparedRegistrations = registrations;
                    preparedVersion = currentVersion;
                }
                return prepared;
            }
        }

        // Called by the mission owner before native loading; the catalog stays inactive until Activate.
        internal static void BeginWorld(HuntablePreyCatalog catalog)
        {
            EndWorld();
            frozen = catalog ?? HuntablePreyCatalogBuilder.VanillaOnly;
        }

        // Idempotent. The mission owner calls it whenever its own activation succeeds.
        internal static void Activate(bool allowed)
        {
            var catalog = allowed && IsAvailable ? frozen : HuntablePreyCatalogBuilder.VanillaOnly;
            bool changed = !ReferenceEquals(Volatile.Read(ref active), catalog) || Volatile.Read(ref worldActive) == 0;
            Volatile.Write(ref active, catalog);
            Volatile.Write(ref demand, HuntablePreyCatalogBuilder.BuildDemandTable(catalog));
            Volatile.Write(ref worldActive, 1);
            if (changed && catalog.HasRegistrations)
                NativeApiLog.Info(log, "Huntable prey active for this mission: " + Describe(catalog) + ", digest=" + catalog.Digest + ".");
        }

        internal static void EndWorld()
        {
            Volatile.Write(ref demand, null);
            Volatile.Write(ref worldActive, 0);
            Volatile.Write(ref active, HuntablePreyCatalogBuilder.VanillaOnly);
            frozen = HuntablePreyCatalogBuilder.VanillaOnly;
            hasLastTick = false;
        }

        private static string Describe(HuntablePreyCatalog catalog)
        {
            var parts = new string[catalog.Entries.Count];
            for (int i = 0; i < parts.Length; i++)
            {
                var entry = catalog.Entries[i];
                parts[i] = entry.UnitType + (entry.IsVanilla ? "/vanilla" : "/" + entry.OwnerGuid + ":" + entry.RegistrationId);
            }
            return "types=[" + string.Join(", ", parts) + "]";
        }

        // The Extender raises this Pre event for every candidate after Vanilla's own type test (deer/goat),
        // directly before the reservation test at 0x18AFFD. APIShared subscribes before consumers, which
        // may still refine the decision afterwards.
        private static void OnHunterQuery(UnitHunterQueryTargetEventArgs args)
        {
            byte[] table = Volatile.Read(ref demand);
            if (table == null || args.Phase != EventHookPhase.Pre) return;
            if (UnitAccess.TryGetById(args.QueryUnitId, out GameUnit* unit, out _) && table[(ushort)unit->r_UnitChimp] != 0)
                args.IsValidTarget = true;
        }

        // Vanilla's deer/goat update decrements a positive lease by one; registered types get the same
        // decrement here, at most once per slot and map tick. Slots follow the map tick, so peers that run
        // the same ticks visit the same slots. No allocation, no per-unit hook.
        private static void Release(int tick)
        {
            byte[] table = Volatile.Read(ref demand);
            if (table == null) { hasLastTick = false; return; }
            GameUnitManagerAPI manager = GameUnitManagerAPI.Instance;
            Span<GameUnit> units = manager.GetUnitsAsSpan();
            // Vanilla updates and queries only IDs below the manager's next ID (loop at 0x184E76), so
            // only that prefix can hold a lease; the pass is as short as the game's actual unit range.
            // Span index = ID - 1. The value is simulation state and equal on every peer.
            int used = Math.Min(units.Length, (int)Math.Min(manager.GetUnitManager().Pointer->r_NextUnitId, int.MaxValue) - 1);
            long ticks = hasLastTick ? unchecked((uint)(tick - lastTick)) : 1;
            if (ticks == 0) return;
            lastTick = tick;
            hasLastTick = true;
            if (used <= 0) return;
            fixed (GameUnit* first = units) ReleaseSlots(first, used, table, tick, ticks);
        }

        internal static void ReleaseSlots(GameUnit* first, int length, byte[] table, int tick, long ticks)
        {
            // Cover every tick since the previous publication; a gap of a whole pass or more visits each slot once.
            int count;
            long startTick;
            if (ticks * SlotsPerTick >= length) { count = length; startTick = tick; }
            else { count = (int)(ticks * SlotsPerTick); startTick = (long)tick - ticks + 1; }
            int slot = (int)((startTick * SlotsPerTick % length + length) % length);
            for (int visited = 0; visited < count; visited++)
            {
                GameUnit* unit = first + slot;
                if (table[(ushort)unit->r_UnitChimp] != 0)
                {
                    short* lease = (short*)((byte*)unit + ReservationOffset);
                    if (*lease > 0) *lease = (short)(*lease - 1);
                }
                if (++slot == length) slot = 0;
            }
        }
    }
}

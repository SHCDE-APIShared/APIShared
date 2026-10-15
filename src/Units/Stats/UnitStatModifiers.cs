using System;
using APIShared.Savegames;
using SHCDESE.Interop;

namespace APIShared.Units
{
    /// <summary>Ordered per-unit stat modifiers, composed into one write when a unit settles. Use it instead
    /// of writing a settled unit's maximum health from your own <see cref="UnitSettledEvents"/> callback.</summary>
    /// <remarks>
    /// <para><b>Composition.</b> For each settled unit, the contributions of every active registration run in
    /// layer order (<see cref="UnitStatLayer"/>: Base, Additive, Multiplier, Override, Clamp), within a layer in
    /// ascending order, then ordinal owner GUID, then ordinal registration ID, starting from
    /// <see cref="UnitStatSubject.NativeValue"/>. Integer arithmetic only; a multiplier rounds half away
    /// from zero after each step. The result is at least 1.</para>
    /// <para><b>Write.</b> One write per unit per settlement, only when the result differs from the live
    /// value: maximum health, current health rescaled to keep the unit's share (a living unit keeps at least
    /// 1), health percentage and health-bar blocks. It happens in APIShared's own UnitSettled callback at order
    /// <see cref="int.MinValue"/>, so it runs before every UnitSettled callback with a higher order, on the
    /// simulation thread, and every peer writes the same value at the same point.</para>
    /// <para><b>Applied marker.</b> APIShared records, per unit, the native value it started from and the value
    /// it wrote, and keeps that record in the save (versioned save record <c>APIShared.UnitStats</c>). A unit
    /// that settles again with the same identity, type and written value is composed from the recorded native
    /// value, so a save, reload or second report never applies a modifier twice. A unit whose type changed
    /// (recruitment, assignment, disband) starts from its new live value.</para>
    /// <para><b>Already-settled units are not re-applied.</b> Registering, disposing or changing a modifier
    /// affects units that settle afterwards; units already in the world keep their values. Re-applying at an
    /// arbitrary registration time would write at a point that differs between multiplayer peers.
    /// Register from <c>Awake</c> or the Extender's <c>LibraryLoaded</c>, before any mission.</para>
    /// <para><b>Cost.</b> Nothing is subscribed before the first registration. With no active registration the
    /// settle callback is one volatile read; a settlement allocates nothing.</para>
    /// <para><b>Foreign writers.</b> A mod that keeps writing maximum health itself after settlement is not
    /// coordinated: if it changes a value this service wrote, the next settlement of that unit treats the
    /// changed value as native. Move such writes into a Base or Multiplier registration.</para>
    /// <para>Contract, consumer notes and measurements: docs/UNIT_STATS.md.</para>
    /// </remarks>
    public static class UnitStatModifiers
    {
        internal const string SettledRegistrationId = "UnitStatModifiers";
        internal static readonly UnitStatComposer Composer = new UnitStatComposer(new GameUnitHealthStore(), Activate);
        private static bool saveRegistered, settledRegistered;

        /// <summary>Registers a modifier for <paramref name="stat"/> in <paramref name="layer"/>.</summary>
        /// <remarks>Any thread. The first successful registration subscribes APIShared to
        /// <see cref="UnitSettledEvents"/> and registers the applied-marker save record; both stay for the
        /// process lifetime. See the class remarks for order, timing and the already-settled rule.</remarks>
        /// <param name="ownerGuid">Your plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within your owner GUID and stat.</param>
        /// <param name="stat">The stat to modify.</param>
        /// <param name="layer">The layer of every contribution this modifier returns.</param>
        /// <param name="order">Ascending order within the layer.</param>
        /// <param name="modifier">Decides the contribution per unit; deterministic, simulation thread.</param>
        /// <param name="registration">Handle on success; dispose it to unregister.</param>
        /// <param name="reason">Empty on success; otherwise why the registration was rejected.</param>
        /// <returns>False for missing arguments, an unknown stat or layer, a duplicate owner/ID pair, or when unit
        /// settlement or the save record is unavailable (a later registration retries).</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, UnitStat stat, UnitStatLayer layer, int order,
            UnitStatModifier modifier, out UnitStatRegistration registration, out string reason) =>
            Composer.TryAdd(ownerGuid, registrationId, stat, layer, order, modifier, out registration, out reason);

        /// <summary>Isolated modifier exceptions and rejected contributions; the unit still gets the others.</summary>
        public static long CallbackFailures => Composer.CallbackFailures;

        /// <summary>Settled units whose value was written. Diagnostic only; not reset per mission.</summary>
        public static long Applications => Composer.Applications;

        // Under the composer's registration lock. Each step runs once; the save record comes first, so no
        // unit is ever written without a marker that survives a save.
        private static string Activate()
        {
            string reason;
            if (!saveRegistered)
            {
                var record = new VersionedSaveDataRegistration(APISharedPlugin.PluginGuid, UnitStatComposer.SaveIdentifier, 1, 1,
                    new[] { UnitStatComposer.MaxHealthEntry }, Composer.Capture, Composer.Loaded);
                if (!VersionedSaveData.TryRegister(record, out _, out reason)) return "Unit stat save record unavailable: " + reason;
                saveRegistered = true;
            }
            if (!settledRegistered)
            {
                if (!UnitSettledEvents.TryRegister(APISharedPlugin.PluginGuid, SettledRegistrationId, Composer.OnSettled, out reason, int.MinValue))
                    return "Unit settlement unavailable: " + reason;
                settledRegistered = true;
            }
            return null;
        }
    }

    // Interop fields read and written here are audited by tools/Validation/Verify-Interop.ps1.
    internal sealed unsafe class GameUnitHealthStore : IUnitHealthStore
    {
        public bool TryRead(int unitId, out int globalId, out eChimps unitType, out uint current, out uint max)
        {
            globalId = 0; unitType = default; current = max = 0;
            try
            {
                if (!UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit == null) return false;
                globalId = unchecked((int)unit->r_GlobalId); unitType = unit->r_UnitChimp;
                current = unit->r_CurrentHealth; max = unit->r_MaxHealth;
                return true;
            }
            catch { return false; }
        }

        public void Write(int unitId, uint current, uint max, ushort percent, uint healthBarBlocks)
        {
            try
            {
                if (!UnitAccess.TryGetById(unitId, out GameUnit* unit, out _) || unit == null) return;
                unit->r_MaxHealth = max;
                unit->r_CurrentHealth = current;
                unit->r_CurrentHealthPercentage = percent;
                unit->r_HealthBarBlocks = healthBarBlocks;
            }
            catch { }
        }
    }
}

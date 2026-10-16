using System;
using System.Threading;
using SHCDESE.Interop;

namespace APIShared.Units
{
    /// <summary>A per-unit value that <see cref="UnitStatModifiers"/> composes.</summary>
    public enum UnitStat
    {
        /// <summary>Maximum health (<c>GameUnit.r_MaxHealth</c>). Current health is rescaled with it so the
        /// unit keeps its share of health; health percentage and health-bar blocks follow.</summary>
        MaxHealth = 0,
    }

    /// <summary>Composition layers, applied in this order. Within a layer, contributions apply in ascending
    /// order, then ordinal owner GUID, then ordinal registration ID.</summary>
    public enum UnitStatLayer
    {
        /// <summary>Replaces the starting value (the unit's native value). The last contribution wins.</summary>
        Base = 0,
        /// <summary>Adds a signed amount. All contributions add up.</summary>
        Additive = 1,
        /// <summary>Multiplies by a ratio, each in turn, rounding half away from zero after each step.</summary>
        Multiplier = 2,
        /// <summary>Replaces the result so far. The last contribution wins.</summary>
        Override = 3,
        /// <summary>Limits the result to a range, each range in turn.</summary>
        Clamp = 4,
    }

    /// <summary>The unit a modifier is asked about: a settled unit and the native value its composition
    /// starts from. A value copy; the unit is the one <see cref="UnitSettledEvents"/> reports.</summary>
    public readonly struct UnitStatSubject
    {
        /// <summary>Creates a subject, for example for consumer tests.</summary>
        public UnitStatSubject(UnitStat stat, int unitId, int globalId, int playerId, eChimps unitType,
            UnitSettledCause cause, int simulationTick, int nativeValue)
        {
            Stat = stat; UnitId = unitId; GlobalId = globalId; PlayerId = playerId; UnitType = unitType;
            Cause = cause; SimulationTick = simulationTick; NativeValue = nativeValue;
        }
        /// <summary>The stat being composed.</summary>
        public UnitStat Stat { get; }
        /// <summary>One-based unit game ID.</summary>
        public int UnitId { get; }
        /// <summary>The unit's GlobalId at settlement.</summary>
        public int GlobalId { get; }
        /// <summary>One-based controlling player ID.</summary>
        public int PlayerId { get; }
        /// <summary>Unit type at settlement.</summary>
        public eChimps UnitType { get; }
        /// <summary>Why the unit settled (created, recruited, assigned, disbanded).</summary>
        public UnitSettledCause Cause { get; }
        /// <summary>Simulation tick of the settlement.</summary>
        public int SimulationTick { get; }
        /// <summary>The native value the composition starts from: the unit's value when it first settled with
        /// its current type and identity, never a value this service already wrote.</summary>
        public int NativeValue { get; }
    }

    /// <summary>One modifier's contribution for one unit. Create it with the factory that matches the
    /// registration's layer; a contribution of another kind is ignored and counted as a failure.</summary>
    public readonly struct UnitStatContribution
    {
        private UnitStatContribution(UnitStatLayer layer, int value, int second)
        { Layer = layer; Value = value; Second = second; }
        internal UnitStatLayer Layer { get; }
        internal int Value { get; }
        internal int Second { get; }
        /// <summary>For <see cref="UnitStatLayer.Base"/> and <see cref="UnitStatLayer.Override"/>: use <paramref name="value"/>.</summary>
        public static UnitStatContribution Set(UnitStatLayer layer, int value) => new UnitStatContribution(layer, value, 0);
        /// <summary>For <see cref="UnitStatLayer.Additive"/>: add <paramref name="amount"/> (may be negative).</summary>
        public static UnitStatContribution Add(int amount) => new UnitStatContribution(UnitStatLayer.Additive, amount, 0);
        /// <summary>For <see cref="UnitStatLayer.Multiplier"/>: multiply by <paramref name="numerator"/> /
        /// <paramref name="denominator"/>; numerator at least 0, denominator at least 1.</summary>
        public static UnitStatContribution Scale(int numerator, int denominator) => new UnitStatContribution(UnitStatLayer.Multiplier, numerator, denominator);
        /// <summary>For <see cref="UnitStatLayer.Multiplier"/>: multiply by <paramref name="percent"/> / 100.</summary>
        public static UnitStatContribution Percent(int percent) => new UnitStatContribution(UnitStatLayer.Multiplier, percent, 100);
        /// <summary>For <see cref="UnitStatLayer.Clamp"/>: limit to [<paramref name="minimum"/>, <paramref name="maximum"/>].</summary>
        public static UnitStatContribution Range(int minimum, int maximum) => new UnitStatContribution(UnitStatLayer.Clamp, minimum, maximum);
    }

    /// <summary>Decides one modifier's contribution for one unit. Return false to leave the unit alone.</summary>
    /// <remarks>Runs on the simulation thread inside unit settlement, for every settled unit, and must be
    /// deterministic: the same inputs give the same answer on every multiplayer peer. No local-player,
    /// Unity, clock or unsynchronized-setting decisions, no blocking, no logging per call.</remarks>
    public delegate bool UnitStatModifier(UnitStatSubject unit, out UnitStatContribution contribution);

    /// <summary>Handle of one stat modifier registration. Dispose it to unregister.</summary>
    /// <remarks><see cref="Dispose"/> may be called from any thread, any number of times, including from a
    /// modifier. Units that settle afterwards no longer get this contribution; units that already settled
    /// keep their values (see <see cref="UnitStatModifiers.TryRegister"/>).</remarks>
    public sealed class UnitStatRegistration : IDisposable
    {
        internal int ActiveValue = 1;
        private readonly Action<UnitStatRegistration> remove;
        internal UnitStatRegistration(string ownerGuid, string registrationId, UnitStat stat, UnitStatLayer layer, int order,
            Action<UnitStatRegistration> remove)
        {
            OwnerGuid = ownerGuid; RegistrationId = registrationId; Stat = stat; Layer = layer; Order = order; this.remove = remove;
        }
        /// <summary>Owning plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Registration ID, unique per owner and stat.</summary>
        public string RegistrationId { get; }
        /// <summary>The composed stat.</summary>
        public UnitStat Stat { get; }
        /// <summary>The layer the contributions belong to.</summary>
        public UnitStatLayer Layer { get; }
        /// <summary>Ascending order within the layer.</summary>
        public int Order { get; }
        /// <summary>False once <see cref="Dispose"/> has been called.</summary>
        public bool IsActive => Volatile.Read(ref ActiveValue) != 0;
        /// <summary>Unregisters. Idempotent and thread-safe.</summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref ActiveValue, 0) != 0)
                remove(this);
        }
        /// <inheritdoc/>
        public override string ToString() => OwnerGuid + "/" + RegistrationId + " (" + Stat + " " + Layer + ", order " + Order + ")";
    }
}

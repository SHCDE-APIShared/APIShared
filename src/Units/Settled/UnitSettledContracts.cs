using SHCDESE.Interop;

namespace APIShared.Units
{
    /// <summary>The Script Extender event that started tracking a unit until it settled.</summary>
    public enum UnitSettledCause
    {
        /// <summary>The game created the unit through its spawn function (SE <c>OnUnitCreate</c> Post with
        /// a positive returned unit ID). Barracks and mercenary recruitment do not use this path.</summary>
        Created = 0,
        /// <summary>Recruitment at a mercenary post (SE <c>OnUnitTransition</c>, source MercenaryOutpost).</summary>
        MercenaryOutpost = 1,
        /// <summary>Recruitment at a European barracks (SE <c>OnUnitTransition</c>, source EuropeanBarracks).</summary>
        EuropeanBarracks = 2,
        /// <summary>A peasant assigned to a workplace (SE <c>OnUnitTransition</c>, source Worker).</summary>
        Worker = 3,
        /// <summary>A disbanded unit (SE <c>OnUnitTransition</c>, source Disband).</summary>
        Disband = 4,
        /// <summary>An SE transition source that this APIShared build does not name.</summary>
        OtherTransition = 5,
    }

    /// <summary>Immutable value describing one unit that finished its creation or type transition.</summary>
    /// <remarks>Values are copied when read; a callback may retain the notification itself. The unit ID and
    /// state are a momentary simulation view: revalidate identity (unit ID plus <see cref="GlobalId"/>)
    /// before acting on the unit after the callback has returned. See
    /// <see cref="UnitSettledEvents.TryRegister"/> for when a unit counts as settled.</remarks>
    public readonly struct UnitSettledNotification
    {
        /// <summary>Creates a notification, for example for consumer tests.</summary>
        public UnitSettledNotification(int unitId, int globalId, int playerId, eChimps unitType,
            eChimps previousUnitType, UnitSettledCause cause, int simulationTick, int ticksObserved)
        {
            UnitId = unitId;
            GlobalId = globalId;
            PlayerId = playerId;
            UnitType = unitType;
            PreviousUnitType = previousUnitType;
            Cause = cause;
            SimulationTick = simulationTick;
            TicksObserved = ticksObserved;
        }

        /// <summary>Gets the one-based unit game ID. Recruitment keeps the recruited peasant's ID.</summary>
        public int UnitId { get; }
        /// <summary>Gets the unit's nonzero GlobalId at settlement; together with <see cref="UnitId"/> it
        /// identifies this unit while the slot is not reused.</summary>
        public int GlobalId { get; }
        /// <summary>Gets the one-based player ID that controls the unit. It equals the owner at the trigger;
        /// an owner change before settlement drops the unit instead.</summary>
        public int PlayerId { get; }
        /// <summary>Gets the unit type at settlement (the recruited, assigned or disbanded result type).</summary>
        public eChimps UnitType { get; }
        /// <summary>Gets the slot's unit type when SE raised the transition, for example the peasant of a
        /// recruit; <c>CHIMP_TYPE_NULL</c> for <see cref="UnitSettledCause.Created"/>.</summary>
        public eChimps PreviousUnitType { get; }
        /// <summary>Gets the Script Extender event that started tracking this unit.</summary>
        public UnitSettledCause Cause { get; }
        /// <summary>Gets the simulation tick passed by SE <c>OnTick</c> at settlement.</summary>
        public int SimulationTick { get; }
        /// <summary>Gets how many SE ticks examined this unit, including the settling tick (at least 1).</summary>
        public int TicksObserved { get; }
        /// <summary>Gets whether the unit was recruited at a mercenary post or European barracks.</summary>
        public bool IsRecruitment => Cause == UnitSettledCause.MercenaryOutpost || Cause == UnitSettledCause.EuropeanBarracks;
    }
}

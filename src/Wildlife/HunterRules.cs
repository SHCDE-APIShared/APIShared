using System;

namespace APIShared.Wildlife
{
    /// <summary>One candidate of a Vanilla Hunter target query, as seen by a target policy.</summary>
    /// <remarks>Only candidates that are alive, no corpse and owned by no player reach policies; Vanilla
    /// rejects the others before. The reservation, distance and sight tests follow after the decision.</remarks>
    public readonly struct HunterTargetCandidate
    {
        internal HunterTargetCandidate(int hunterUnitId, int candidateUnitId, int candidateType, bool isHuntable, bool isSimulationQuery)
        {
            HunterUnitId = hunterUnitId;
            CandidateUnitId = candidateUnitId;
            CandidateType = candidateType;
            IsHuntable = isHuntable;
            IsSimulationQuery = isSimulationQuery;
        }

        /// <summary>One-based id of the querying Hunter (the native query argument, not the Extender's value).</summary>
        public int HunterUnitId { get; }
        /// <summary>One-based id of the candidate.</summary>
        public int CandidateUnitId { get; }
        /// <summary>Raw native unit type of the candidate.</summary>
        public int CandidateType { get; }
        /// <summary>Whether the candidate's type is in the active <see cref="HuntablePrey"/> catalog.</summary>
        public bool IsHuntable { get; }
        /// <summary>False only for the local, presentation-only query Vanilla runs for the selected
        /// Hunter's idle speech. Change simulation state only when this is true.</summary>
        public bool IsSimulationQuery { get; }
    }

    /// <summary>A target policy's answer for one candidate.</summary>
    public enum HunterTargetDecision
    {
        /// <summary>No opinion; the catalog decides.</summary>
        Default,
        /// <summary>Admit the candidate. Ignored for types outside the huntable-prey catalog, because only
        /// catalog types get their Hunter reservation released.</summary>
        Allow,
        /// <summary>Refuse the candidate. Any refusal wins over every allowance.</summary>
        Refuse
    }

    /// <summary>Decides one candidate. Runs on the simulation thread inside Vanilla's query; it must be
    /// fast, must not block and must decide identically on every multiplayer peer.</summary>
    public delegate HunterTargetDecision HunterTargetPolicy(in HunterTargetCandidate candidate);

    /// <summary>Shared rules that admit or refuse candidates of Vanilla's Hunter target query.</summary>
    /// <remarks>
    /// Register on the initialized Unity thread during startup; registrations live for the process. Policies
    /// run for every candidate in order (ascending <c>order</c>, then ordinal owner GUID and ID); any
    /// <see cref="HunterTargetDecision.Refuse"/> refuses, otherwise any <see cref="HunterTargetDecision.Allow"/>
    /// admits a catalog type, otherwise the catalog decides. An exception counts as Default and is logged once
    /// per registration. Policies receive the correct Hunter id even though the Script Extender's own event
    /// reports a wrong one. Without registrations nothing runs.
    /// </remarks>
    public static class HunterTargetPolicies
    {
        /// <summary>Registers a policy. Returns false for invalid arguments, a duplicate identity, the wrong
        /// thread or unavailable Hunter query sites.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, HunterTargetPolicy policy,
            out string reason, int order = 0) =>
            HuntablePreyRuntime.TryRegisterPolicy(ownerGuid, registrationId, policy, order, out reason);
    }

    /// <summary>The base meat of one Hunter yield, changeable by handlers.</summary>
    public sealed class HunterMeatYield
    {
        internal HunterMeatYield(int hunterUnitId, int preyType, int vanillaBaseAmount, int baseAmount)
        {
            HunterUnitId = hunterUnitId;
            PreyType = preyType;
            VanillaBaseAmount = vanillaBaseAmount;
            BaseAmount = baseAmount;
        }

        /// <summary>One-based id of the Hunter taking the meat from his hut.</summary>
        public int HunterUnitId { get; }
        /// <summary>Raw native type of the animal he butchered for this meat.</summary>
        public int PreyType { get; }
        /// <summary>The base Vanilla passes for every prey (6).</summary>
        public int VanillaBaseAmount { get; }
        /// <summary>The base before Vanilla's bonus, from 0 through 1000. Starts with the catalog entry's
        /// <see cref="HuntablePreyDefinition.BaseMeat"/> or the Vanilla base; values outside are clamped.</summary>
        public int BaseAmount { get; set; }
    }

    /// <summary>Changes the base meat of Vanilla Hunter yields per butchered animal.</summary>
    /// <remarks>
    /// Register on the initialized Unity thread during startup; registrations live for the process. Handlers run
    /// in order (ascending <c>order</c>, then ordinal owner GUID and ID) on the simulation thread when a Hunter
    /// takes his meat from the hut, before Vanilla adds its bonus; they must decide identically on every
    /// multiplayer peer. APIShared records the butchered animal natively at the corpse and keeps that record in
    /// saves. A yield whose animal is unknown (for example a save written without this record) keeps Vanilla's
    /// base and raises no event. An exception is logged once per registration and leaves the value as it was.
    /// </remarks>
    public static class HunterMeatYieldEvents
    {
        /// <summary>Registers a handler. Returns false for invalid arguments, a duplicate identity, the wrong
        /// thread or unavailable Hunter sites.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, Action<HunterMeatYield> handler,
            out string reason, int order = 0) =>
            HuntablePreyRuntime.TryRegisterMeatHandler(ownerGuid, registrationId, handler, order, out reason);
    }
}

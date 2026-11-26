using System;
using System.Collections.Generic;

namespace APIShared.Modifiers
{
    /// <summary>A reusable, typed request created by the APIShared service that owns the value.</summary>
    /// <remarks>The creating service defines units, thread access, identity and availability.
    /// The request selects a value each time it is read. Only the returned status is a snapshot.</remarks>
    public abstract class ValueModifierStatusQuery
    {
        internal ValueModifierStatusQuery() { }
        internal abstract bool TryRead(out ValueModifierStatus status, out string reason);
    }

    /// <summary>Reads status for scalar values published by APIShared services.</summary>
    public static class ValueModifiers
    {
        /// <summary>Reads a detached status. False returns a null status and a diagnostic.</summary>
        /// <remarks>Uses the creating service's thread and availability contract. Reading does not
        /// initialize the service, evaluate providers, or apply changes.</remarks>
        public static bool TryGetStatus(ValueModifierStatusQuery query, out ValueModifierStatus status, out string reason)
        {
            if (query != null) return query.TryRead(out status, out reason);
            status = null;
            reason = "A value modifier status query is required.";
            return false;
        }
    }

    /// <summary>One detached contribution in the service's evaluation order.</summary>
    public readonly struct ValueModifierContributionStatus
    {
        internal ValueModifierContributionStatus(string owner, string id, int order,
            ValueModifierContribution contribution, bool failed, bool superseded)
        {
            OwnerGuid = owner; RegistrationId = id; Order = order;
            Contribution = contribution; ProviderFailed = failed; SupersededByFixedValue = superseded;
        }
        /// <summary>Provider's ordinal owner GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Provider's ordinal owner-local registration ID.</summary>
        public string RegistrationId { get; }
        /// <summary>Registration priority within the publishing service.</summary>
        public int Order { get; }
        /// <summary>Recorded operation, value and stage. A failed provider has no contribution.</summary>
        public ValueModifierContribution Contribution { get; }
        /// <summary>True when the service isolated a provider failure for this evaluation.</summary>
        public bool ProviderFailed { get; }
        /// <summary>True when the selected fixed value replaced this earlier fixed value or Before factor.</summary>
        public bool SupersededByFixedValue { get; }
    }

    /// <summary>Inclusive bounds used for a resolved value, in the service's units.</summary>
    public readonly struct ValueModifierBounds
    {
        internal ValueModifierBounds(decimal minimum, decimal maximum, bool wasClamped)
        { Minimum = minimum; Maximum = maximum; WasClamped = wasClamped; }
        /// <summary>Inclusive lower bound.</summary>
        public decimal Minimum { get; }
        /// <summary>Inclusive upper bound.</summary>
        public decimal Maximum { get; }
        /// <summary>True if applying a bound changed the intermediate result.</summary>
        public bool WasClamped { get; }
    }

    /// <summary>A separately observed value and the reference supplied by its service.</summary>
    public readonly struct ValueModifierObservation
    {
        internal ValueModifierObservation(decimal value, decimal referenceValue)
        { Value = value; ReferenceValue = referenceValue; }
        /// <summary>The separately sampled value.</summary>
        public decimal Value { get; }
        /// <summary>The comparable value from the recorded evaluation. It can differ from the selected projection.</summary>
        public decimal ReferenceValue { get; }
        /// <summary>True when the sampled value differs from the reference.</summary>
        public bool DiffersFromReference => Value != ReferenceValue;
    }

    /// <summary>Immutable explanation of a scalar result in the creating service's documented units.</summary>
    public sealed class ValueModifierStatus
    {
        // The caller transfers ownership of the detached array.
        internal ValueModifierStatus(decimal baselineValue, decimal resolvedValue, decimal? externalFixedValue,
            ValueModifierBounds? bounds, ValueModifierObservation? observation, ValueModifierContributionStatus[] contributions)
        {
            BaselineValue = baselineValue; ResolvedValue = resolvedValue; ExternalFixedValue = externalFixedValue;
            Bounds = bounds; Observation = observation; Contributions = Array.AsReadOnly(contributions);
        }
        /// <summary>The original value used by the recorded evaluation.</summary>
        public decimal BaselineValue { get; }
        /// <summary>The result produced by the recorded evaluation.</summary>
        public decimal ResolvedValue { get; }
        /// <summary>An absolute input recorded from outside the contribution list, when applicable.</summary>
        public decimal? ExternalFixedValue { get; }
        /// <summary>The inclusive bounds used by the service, if it applied bounds.</summary>
        public ValueModifierBounds? Bounds { get; }
        /// <summary>A separate observation when the service provides one.</summary>
        public ValueModifierObservation? Observation { get; }
        /// <summary>Detached results in the service's evaluation order, including failures and empty contributions.</summary>
        public IReadOnlyList<ValueModifierContributionStatus> Contributions { get; }
    }
}

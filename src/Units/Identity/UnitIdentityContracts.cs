using System;
using System.Collections.Generic;

namespace APIShared.Units
{
    /// <summary>Process-lived registration for one owner's variant on an existing native carrier.</summary>
    /// <remarks>No hooks are removed and no native unit is created, converted or purchased. Each exact unit may have one variant;
    /// independent metadata belongs to consumer records and does not reserve a variant. A transforming mod explicitly removes
    /// its own old identity and marks its own replacement; no foreign identities or data are copied automatically.</remarks>
    public sealed class UnitVariantRegistration
    {
        private readonly UnitIdentityRuntime runtime;
        internal readonly UnitVariantDefinition Definition;
        internal UnitVariantRegistration(UnitIdentityRuntime runtime, UnitVariantDefinition definition)
        { this.runtime = runtime; Definition = definition; }
        /// <summary>Registering plugin GUID.</summary>
        public string OwnerGuid => Definition.Owner;
        /// <summary>Stable owner-local variant ID.</summary>
        public string VariantId => Definition.Id;
        /// <summary>Existing native carrier type.</summary>
        public int NativeType => Definition.NativeType;
        /// <summary>Marks a live exact unit, or a live peasant natively transforming into this carrier. Idempotent for the same registration.</summary>
        /// <remarks>The caller must already hold the actual engine monitor. Real multiplayer additionally requires a shared native
        /// tick or authenticated Chore callback; identical registrations and deterministic decisions on every peer remain mandatory.
        /// Offline/editor callers may mark under the monitor in a confirmed world. No dispatch, purchase authorization or command is issued.
        /// False preserves every existing mark and reports invalid context, stale identity, wrong carrier or conflicting owner/variant.</remarks>
        public bool TryMark(int unitId, uint globalId, out string reason) => runtime.Mark(Definition, unitId, globalId, out reason);
        /// <summary>Removes only this variant's exact mark. Missing marks succeed; stale slots and foreign marks are never removed.</summary>
        /// <remarks>Uses the same mutation-context requirements as TryMark. No native unit state is changed.</remarks>
        public bool TryUnmark(int unitId, uint globalId, out string reason) => runtime.Unmark(Definition, unitId, globalId, out reason);
        /// <summary>Returns a detached, read-only member snapshot ordered by unit slot; retainable, possibly stale after return.</summary>
        /// <remarks>Allocation-free lookup without the engine lock. The list contains only this registered variant, including pending
        /// recruits. It grants no native liveness, thread or gameplay authority; revalidate exact live identity before simulation work.</remarks>
        public IReadOnlyList<UnitIdentitySnapshot> GetMembers() => runtime.Members(Definition);
    }

    /// <summary>Owner-bound registration and lock-free presentation reads for shared troop variant identities.</summary>
    public sealed class UnitIdentityClient
    {
        private readonly string owner;
        internal UnitIdentityClient(string ownerGuid) { owner = ownerGuid; }
        /// <summary>Registers during initialized Unity startup, before a native world exists. IDs and owners accept 1–128 nonblank characters.</summary>
        /// <remarks>Registrations last for the process, reject duplicate owner/ID pairs and accept carrier values 0–65535. Register
        /// unconditionally on every simulation peer. First registration starts lifecycle, marked-unit reconciliation and one versioned
        /// simulation-relevant SaveGamesAndMaps record. Existing consumer save formats are not imported automatically. Check every result.</remarks>
        public bool TryRegisterVariant(string variantId, int nativeType, out UnitVariantRegistration registration, out string reason) =>
            UnitIdentityRuntime.Instance.Register(owner, variantId, nativeType, out registration, out reason);
        /// <summary>Whether a valid identity snapshot is published for the current native world. Lock-free and nonmutating.</summary>
        /// <remarks>False before startup/restoration, after invalidation, or for an unreadable record. True also for a valid empty world.
        /// An earlier NativeLoaded observer may see false; use a later observer/native tick or an engine-protected TryGetLive to apply staged data.
        /// This point-in-time readiness grants no simulation authority and can change immediately after return.</remarks>
        public bool IsCurrentWorldReady => UnitIdentityRuntime.Instance.IsCurrentWorldReady;
        /// <summary>Reads this owner's exact current native identity while the caller holds the engine monitor.</summary>
        /// <remarks>Use for simulation decisions. Applies this native world's staged identity restore first, then revalidates liveness,
        /// GlobalId, carrier and pending transformation; no native pointer escapes and no gameplay command is issued.
        /// A successful read is no purchase/command authorization and remains valid only within the caller's protected boundary.</remarks>
        public bool TryGetLive(int unitId, uint globalId, out UnitIdentitySnapshot identity) =>
            UnitIdentityRuntime.Instance.TryGetLive(owner, unitId, globalId, out identity);
        /// <summary>Reads this owner's published variant for an exact slot and GlobalId, without locks, allocations or native reads.</summary>
        /// <remarks>Publication follows explicit mutations and marked-only simulation-tick reconciliation. It is a momentary presentation
        /// view, not live simulation authority. Invalidated/replaced worlds return false immediately. Unknown variants stay dormant and
        /// are not exposed. Pass the GlobalId observed at the caller's own valid boundary; a slot alone is insufficient.</remarks>
        public bool TryGet(int unitId, uint globalId, out UnitIdentitySnapshot identity) =>
            UnitIdentityRuntime.Instance.TryGet(owner, unitId, globalId, out identity);
    }
}

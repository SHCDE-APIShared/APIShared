using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Wildlife
{
    /// <summary>Describes one unit type that Vanilla Hunters may select as prey.</summary>
    /// <remarks>
    /// The type is a raw native unit type (the value of <c>GameUnit.r_UnitChimp</c>), so types outside
    /// <c>eChimps</c> can be registered as well. How such units enter the world is the registering mod's
    /// business. APIShared admits the type to Vanilla's Hunter target query and releases the native Hunter
    /// reservation for it, and applies <see cref="BaseMeat"/> when set. Shooting, corpse handling and fleeing
    /// follow Vanilla for that type until a consumer changes them.
    /// </remarks>
    public sealed class HuntablePreyDefinition
    {
        /// <summary>Creates a definition. Registration validates the values.</summary>
        /// <param name="unitType">Native unit type from 1 through 65535.</param>
        /// <param name="handlingCost">Non-negative cost hint in map ticks for skinning and handling one
        /// animal. APIShared does not use it; target-ranking consumers may read it from the catalog.</param>
        public HuntablePreyDefinition(int unitType, int handlingCost = 0)
        { UnitType = unitType; HandlingCost = handlingCost; }

        private HuntablePreyDefinition(int unitType, int handlingCost, int? baseMeat)
            : this(unitType, handlingCost) { BaseMeat = baseMeat; }

        /// <summary>Copies the definition with a base meat for the Hunter's yield of this animal, from 0
        /// through 1000 before Vanilla's bonus. Null keeps Vanilla's base of 6.</summary>
        public HuntablePreyDefinition WithBaseMeat(int? baseMeat) => new HuntablePreyDefinition(UnitType, HandlingCost, baseMeat);

        /// <summary>Raw native unit type.</summary>
        public int UnitType { get; }
        /// <summary>Cost hint in map ticks for target-ranking consumers.</summary>
        public int HandlingCost { get; }
        /// <summary>Base meat of the Hunter's yield, or null for Vanilla's base. See <see cref="HunterMeatYieldEvents"/>.</summary>
        public int? BaseMeat { get; }
    }

    /// <summary>Result of the most recent catalog preparation for a registration.</summary>
    public enum HuntablePreyRegistrationStatus
    {
        /// <summary>The registration has not taken part in a catalog preparation yet.</summary>
        Pending,
        /// <summary>The registration defines its type in the prepared catalog.</summary>
        Admitted,
        /// <summary>Another enabled registration for the same type has a higher priority.</summary>
        ExcludedByPriority,
        /// <summary>The type can never be admitted; <see cref="HuntablePreyRegistration.Diagnostic"/> says why.</summary>
        Unsupported,
        /// <summary>The owner disabled the registration.</summary>
        Disabled
    }

    /// <summary>Process-held registration of one huntable type. Keep it for the process lifetime.</summary>
    public sealed class HuntablePreyRegistration
    {
        private int status;
        private int enabled = 1;

        internal HuntablePreyRegistration(string ownerGuid, string registrationId, HuntablePreyDefinition definition,
            int priority, string unsupportedReason)
        {
            OwnerGuid = ownerGuid;
            RegistrationId = registrationId;
            Definition = definition;
            Priority = priority;
            UnsupportedReason = unsupportedReason ?? "";
            if (UnsupportedReason.Length != 0) status = (int)HuntablePreyRegistrationStatus.Unsupported;
        }

        /// <summary>Registering plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable ID within the owner.</summary>
        public string RegistrationId { get; }
        /// <summary>The registered type and its data.</summary>
        public HuntablePreyDefinition Definition { get; }
        /// <summary>Higher values win a type registered by several mods; ties sort by ordinal owner GUID, then ID.</summary>
        public int Priority { get; }
        /// <summary>Whether the owner currently wants this type to be huntable.</summary>
        public bool IsEnabled => Volatile.Read(ref enabled) != 0;
        /// <summary>Result of the most recent catalog preparation.</summary>
        public HuntablePreyRegistrationStatus Status => (HuntablePreyRegistrationStatus)Volatile.Read(ref status);
        /// <summary>Reason for an unsupported or excluded registration; otherwise empty.</summary>
        public string Diagnostic => Status == HuntablePreyRegistrationStatus.Unsupported ? UnsupportedReason
            : Status == HuntablePreyRegistrationStatus.ExcludedByPriority ? "Another registration for this unit type has a higher priority."
            : "";

        internal string UnsupportedReason { get; }

        /// <summary>Changes the owner's demand for this type. Call on the initialized Unity thread.</summary>
        /// <remarks>The change takes part in the next catalog preparation: a running mission keeps its frozen
        /// catalog, and lobbies re-run their peer agreement. Change it before or after a mission, like any
        /// gameplay setting. Returns false with a reason on the wrong thread.</remarks>
        public bool TrySetEnabled(bool value, out string reason) => HuntablePreyRuntime.TrySetEnabled(this, value, out reason);

        internal void SetStatus(HuntablePreyRegistrationStatus value) => Volatile.Write(ref status, (int)value);
        internal bool SetEnabledFlag(bool value) => Interlocked.Exchange(ref enabled, value ? 1 : 0) != (value ? 1 : 0);
    }

    /// <summary>Immutable entry of a huntable-prey catalog.</summary>
    public sealed class HuntablePreyEntry
    {
        internal HuntablePreyEntry(int unitType, int handlingCost, int? baseMeat, string ownerGuid, string registrationId, bool isVanilla)
        {
            UnitType = unitType;
            HandlingCost = handlingCost;
            BaseMeat = baseMeat;
            OwnerGuid = ownerGuid;
            RegistrationId = registrationId;
            IsVanilla = isVanilla;
        }

        /// <summary>Raw native unit type.</summary>
        public int UnitType { get; }
        /// <summary>Cost hint in map ticks; zero for the built-in deer and goat entries.</summary>
        public int HandlingCost { get; }
        /// <summary>Base meat of the Hunter's yield, or null for Vanilla's base of 6.</summary>
        public int? BaseMeat { get; }
        /// <summary>Owner of the admitted registration; APIShared's GUID for a built-in entry.</summary>
        public string OwnerGuid { get; }
        /// <summary>Admitted registration ID; empty for a built-in entry.</summary>
        public string RegistrationId { get; }
        /// <summary>True for Vanilla's deer and goat entries that no registration replaced.</summary>
        public bool IsVanilla { get; }
    }

    /// <summary>Immutable, type-ordered catalog of huntable unit types.</summary>
    public sealed class HuntablePreyCatalog
    {
        private readonly Dictionary<int, HuntablePreyEntry> byType;

        internal HuntablePreyCatalog(HuntablePreyEntry[] entries, string digest, bool hasRegistrations)
        {
            Entries = Array.AsReadOnly(entries);
            Digest = digest;
            HasRegistrations = hasRegistrations;
            byType = new Dictionary<int, HuntablePreyEntry>(entries.Length);
            foreach (var entry in entries) byType.Add(entry.UnitType, entry);
        }

        /// <summary>All huntable types in ascending type order. Always contains deer and goat.</summary>
        public IReadOnlyList<HuntablePreyEntry> Entries { get; }
        /// <summary>Uppercase SHA-256 of the gameplay-relevant entries; equal on peers with equal catalogs.</summary>
        public string Digest { get; }
        /// <summary>True when at least one registration was admitted.</summary>
        public bool HasRegistrations { get; }
        /// <summary>Whether Vanilla Hunters may select this type, before consumer target policies.</summary>
        public bool IsHuntable(int unitType) => byType.ContainsKey(unitType);
        /// <summary>Reads the entry for a type.</summary>
        public bool TryGet(int unitType, out HuntablePreyEntry entry) => byType.TryGetValue(unitType, out entry);
    }

    /// <summary>Shared registry of unit types that Vanilla Hunters may hunt.</summary>
    /// <remarks>
    /// Register on the initialized Unity thread after APIShared's native initialization, normally during plugin
    /// startup. Each mission freezes the catalog when it is initialized; registrations and enabled changes apply
    /// to the next mission. Custom entries are active only where the mission mode allows custom gameplay mods
    /// (including the map editor); elsewhere the catalog holds only Vanilla's deer and goat. In a real
    /// multiplayer lobby the catalog digest is part of the existing production peer agreement, so peers with
    /// different catalogs cannot start. Registrations are never removed; use
    /// <see cref="HuntablePreyRegistration.TrySetEnabled"/> to withdraw a type logically.
    /// </remarks>
    public static class HuntablePrey
    {
        /// <summary>Registers one unit type as huntable prey.</summary>
        /// <param name="ownerGuid">Your stable plugin GUID.</param>
        /// <param name="registrationId">Stable ID, unique within the owner.</param>
        /// <param name="definition">The type and its data.</param>
        /// <param name="registration">The process-held registration, also for an unsupported type.</param>
        /// <param name="reason">Empty on success; otherwise why nothing was registered.</param>
        /// <param name="priority">Higher values win a type registered by several mods.</param>
        /// <returns>False for invalid arguments, a duplicate identity, the wrong thread or an unavailable
        /// service. A type that can never be admitted still registers, with status Unsupported.</returns>
        public static bool TryRegister(string ownerGuid, string registrationId, HuntablePreyDefinition definition,
            out HuntablePreyRegistration registration, out string reason, int priority = 0) =>
            HuntablePreyRuntime.TryRegister(ownerGuid, registrationId, definition, priority, out registration, out reason);

        /// <summary>Whether the reservation release and target admission are installed.</summary>
        public static bool IsAvailable => HuntablePreyRuntime.IsAvailable;

        /// <summary>The frozen catalog of the running mission; outside a mission, the catalog the next
        /// mission would freeze (before its mode is known). Never null.</summary>
        public static HuntablePreyCatalog Current => HuntablePreyRuntime.Current;
    }
}

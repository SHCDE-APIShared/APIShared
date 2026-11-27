using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Networking;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Stable recipe identity. HUD position and packet table index are mission-local.</summary>
    public readonly struct WeaponProductionOptionId : IEquatable<WeaponProductionOptionId>
    {
        /// <summary>Constructs an ordinal, owner-local recipe key.</summary>
        public WeaponProductionOptionId(string ownerGuid, string optionId)
        { OwnerGuid = ownerGuid; OptionId = optionId; }

        /// <summary>Registering plugin GUID.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable ID within the owner.</summary>
        public string OptionId { get; }
        /// <inheritdoc/>
        public bool Equals(WeaponProductionOptionId other) =>
            StringComparer.Ordinal.Equals(OwnerGuid, other.OwnerGuid) &&
            StringComparer.Ordinal.Equals(OptionId, other.OptionId);
        /// <inheritdoc/>
        public override bool Equals(object obj) => obj is WeaponProductionOptionId other && Equals(other);
        /// <inheritdoc/>
        public override int GetHashCode() =>
            unchecked((OwnerGuid == null ? 0 : StringComparer.Ordinal.GetHashCode(OwnerGuid)) * 397 ^
                (OptionId == null ? 0 : StringComparer.Ordinal.GetHashCode(OptionId)));
        /// <inheritdoc/>
        public override string ToString() => OwnerGuid + "/" + OptionId;
    }

    /// <summary>An additional recipe using one of this workshop's native work and ingredient sequences.</summary>
    public sealed class WeaponProductionOptionDefinition
    {
        /// <summary>Describes one workshop recipe. Registration validates the values together.</summary>
        /// <param name="buildingType">One of the five weapon workshops.</param>
        /// <param name="templateGood">One of that workshop's Vanilla products, selecting its default ingredient cost and animation.</param>
        /// <param name="outputGood">An existing weapon or armour good.</param>
        /// <param name="displayName">Localized, nonempty name. Presentation only.</param>
        /// <param name="baseAmount">Whole output base from 0 through 100, before existing yield modifiers. Null uses the template's base.</param>
        /// <param name="workTicks">Active manufacturing updates from 1 through 65535. Null uses the template's native completion. Transport and setup are separate.</param>
        /// <param name="priority">Higher values win the available additional slots. Ties sort by ordinal owner and option ID.</param>
        public WeaponProductionOptionDefinition(eStructs buildingType, eGoods templateGood, eGoods outputGood,
            string displayName, int? baseAmount = null, int? workTicks = null, int priority = 0)
            : this(buildingType, templateGood, outputGood, displayName, baseAmount, workTicks, priority, null)
        { }

        private WeaponProductionOptionDefinition(eStructs buildingType, eGoods templateGood, eGoods outputGood,
            string displayName, int? baseAmount, int? workTicks, int priority, int? ingredientAmount)
        {
            BuildingType = buildingType;
            TemplateGood = templateGood;
            OutputGood = outputGood;
            DisplayName = displayName;
            BaseAmount = baseAmount;
            WorkTicks = workTicks;
            Priority = priority;
            IngredientAmount = ingredientAmount;
        }

        /// <summary>Copies the recipe with a new cost per charge in the workshop's native ingredient. Null uses the template cost.</summary>
        public WeaponProductionOptionDefinition WithIngredientAmount(int? ingredientAmount) =>
            new WeaponProductionOptionDefinition(BuildingType, TemplateGood, OutputGood, DisplayName, BaseAmount,
                WorkTicks, Priority, ingredientAmount);

        /// <summary>The workshop using this recipe.</summary>
        public eStructs BuildingType { get; }
        /// <summary>The native ingredient and animation template.</summary>
        public eGoods TemplateGood { get; }
        /// <summary>The existing weapon stored at delivery.</summary>
        public eGoods OutputGood { get; }
        /// <summary>The caller's localized recipe name.</summary>
        public string DisplayName { get; }
        /// <summary>Override for the template's integer output base.</summary>
        public int? BaseAmount { get; }
        /// <summary>Override for the active manufacturing update count.</summary>
        public int? WorkTicks { get; }
        /// <summary>Optional units of the workshop's native ingredient consumed per charge.</summary>
        public int? IngredientAmount { get; }
        /// <summary>Priority for the remaining slots after the built-in recipes.</summary>
        public int Priority { get; }
    }

    /// <summary>Immutable entry in an admitted mission's production catalogue.</summary>
    public sealed class WeaponProductionOption
    {
        internal WeaponProductionOption(WeaponProductionOptionId id, WeaponProductionOptionDefinition definition, bool vanilla)
        { Id = id; Definition = definition; IsVanilla = vanilla; }

        internal WeaponProductionOptionDefinition Definition { get; }
        /// <summary>The recipe's stable public identity.</summary>
        public WeaponProductionOptionId Id { get; }
        /// <summary>Display label for the recipe.</summary>
        public string DisplayName => Definition.DisplayName;
        /// <summary>Compatible workshop type.</summary>
        public eStructs BuildingType => Definition.BuildingType;
        /// <summary>Native work template.</summary>
        public eGoods TemplateGood => Definition.TemplateGood;
        /// <summary>Weapon delivered by the recipe.</summary>
        public eGoods OutputGood => Definition.OutputGood;
        /// <summary>Optional integer base before yield modifiers.</summary>
        public int? BaseAmount => Definition.BaseAmount;
        /// <summary>Optional active manufacturing duration.</summary>
        public int? WorkTicks => Definition.WorkTicks;
        /// <summary>Optional units of the workshop's native ingredient consumed per charge.</summary>
        public int? IngredientAmount => Definition.IngredientAmount;
        /// <summary>True for a reserved built-in recipe.</summary>
        public bool IsVanilla { get; }
    }

    /// <summary>Result of applying backend and capacity checks during catalogue preparation.</summary>
    public enum WeaponProductionRegistrationStatus
    {
        /// <summary>The registration has not participated in preparation yet.</summary>
        Pending,
        /// <summary>The recipe has a slot in the prepared catalogue.</summary>
        Admitted,
        /// <summary>Other registrations took all remaining slots.</summary>
        ExcludedByPriority,
        /// <summary>The required native adapter cannot execute the recipe.</summary>
        AdapterUnavailable
    }

    /// <summary>Process-held registration. Status reports the most recent catalogue preparation.</summary>
    public sealed class WeaponProductionRegistration
    {
        private int status;
        internal WeaponProductionRegistration(WeaponProductionOption option) { Option = option; }
        /// <summary>The registered recipe and its stable key.</summary>
        public WeaponProductionOption Option { get; }
        /// <summary>Result of the last catalogue preparation.</summary>
        public WeaponProductionRegistrationStatus Status => (WeaponProductionRegistrationStatus)Volatile.Read(ref status);
        /// <summary>Reason for a recipe excluded during preparation.</summary>
        public string Diagnostic => Status == WeaponProductionRegistrationStatus.ExcludedByPriority
            ? "Four workshop options are already reserved or admitted."
            : Status == WeaponProductionRegistrationStatus.AdapterUnavailable
                ? WeaponProductionRuntime.AvailabilityReason(Option)
                : "";
        internal void SetStatus(WeaponProductionRegistrationStatus value) => Volatile.Write(ref status, (int)value);
    }

    /// <summary>Detached snapshot for one building identity.</summary>
    public sealed class WeaponProductionSelection
    {
        internal WeaponProductionSelection(WeaponProductionOption current, WeaponProductionOption next)
        { Current = current; Next = next; }

        /// <summary>Latched production, or null if the native record has no current product.</summary>
        public WeaponProductionOption Current { get; }
        /// <summary>Requested production for the next charge.</summary>
        public WeaponProductionOption Next { get; }
    }

    /// <summary>Shared weapon-workshop catalogue and deterministic selection.</summary>
    public static class WeaponProduction
    {
        /// <summary>Register on the initialized Unity thread during startup. The next mission preparation picks up new registrations.</summary>
        public static bool TryRegister(string ownerGuid, string optionId, WeaponProductionOptionDefinition definition,
            out WeaponProductionRegistration registration, out string reason) =>
            WeaponProductionRuntime.TryRegister(ownerGuid, optionId, definition, out registration, out reason);

        /// <summary>Stable, ordered snapshot for the active mission. Includes reserved Vanilla slots even when the map restricts them.</summary>
        public static IReadOnlyList<WeaponProductionOption> GetOptions(eStructs buildingType) =>
            WeaponProductionRuntime.GetOptions(buildingType);

        /// <summary>Reads the active world under its engine monitor. Building IDs are one-based and paired with their Global ID.</summary>
        public static bool TryGetSelection(int buildingId, uint globalId, out WeaponProductionSelection selection) =>
            WeaponProductionRuntime.TryGetSelection(buildingId, globalId, out selection);

        /// <summary>Submit on Unity as the real local participant. A group request resolves own live workshops of that type at execution.</summary>
        public static NetworkSendResult RequestSelection(int buildingId, uint globalId, WeaponProductionOptionId optionId,
            bool allOfType = false) => WeaponProductionRuntime.RequestSelection(buildingId, globalId, optionId, allOfType);

        /// <summary>Set the next recipe of an AI-owned workshop from a deterministic simulation callback run on every peer.</summary>
        public static bool TrySetAiNextOption(int buildingId, uint globalId, WeaponProductionOptionId optionId) =>
            WeaponProductionRuntime.TrySetAiNextOption(buildingId, globalId, optionId);
    }
}

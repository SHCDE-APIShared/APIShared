using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal sealed class WeaponProductionCatalog
    {
        private static readonly WeaponProductionOption[] Vanilla =
        {
            BuiltIn(eStructs.STRUCT_FLETCHERS_WORKSHOP, eGoods.STORED_BOWS, "Bows"),
            BuiltIn(eStructs.STRUCT_FLETCHERS_WORKSHOP, eGoods.STORED_CROSSBOWS, "Crossbows"),
            BuiltIn(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, eGoods.STORED_MACES, "Maces"),
            BuiltIn(eStructs.STRUCT_BLACKSMITHS_WORKSHOP, eGoods.STORED_SWORDS, "Swords"),
            BuiltIn(eStructs.STRUCT_POLETURNERS_WORKSHOP, eGoods.STORED_SPEARS, "Spears"),
            BuiltIn(eStructs.STRUCT_POLETURNERS_WORKSHOP, eGoods.STORED_PIKES, "Pikes"),
            BuiltIn(eStructs.STRUCT_ARMOURERS_WORKSHOP, eGoods.STORED_METAL_ARMOUR, "Metal armour"),
            BuiltIn(eStructs.STRUCT_TANNERS_WORKSHOP, eGoods.STORED_LEATHER_ARMOUR, "Leather armour")
        };

        internal static readonly WeaponProductionCatalog Empty = new WeaponProductionCatalog(Array.Empty<WeaponProductionOption>(), "");
        private readonly Dictionary<eStructs, IReadOnlyList<WeaponProductionOption>> byType;
        private readonly WeaponProductionOption[] entries;
        internal string Digest { get; }
        internal bool HasOptions => entries.Length != 0;
        internal bool HasStoredOutputs { get; }
        internal bool HasTannerIngredientOverrides { get; }

        private WeaponProductionCatalog(WeaponProductionOption[] entries, string digest)
        {
            this.entries = entries;
            Digest = digest;
            HasStoredOutputs = entries.Any(option => option.StoredOutput != null);
            HasTannerIngredientOverrides = entries.Any(option =>
                option.BuildingType == eStructs.STRUCT_TANNERS_WORKSHOP && option.IngredientAmount > 1);
            byType = entries.GroupBy(option => option.BuildingType)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<WeaponProductionOption>)Array.AsReadOnly(group.ToArray()));
        }

        internal static bool IsWeapon(eGoods good) => Vanilla.Any(option => option.OutputGood == good);

        internal static eGoods IngredientGood(eStructs type)
        {
            switch (type)
            {
                case eStructs.STRUCT_FLETCHERS_WORKSHOP:
                case eStructs.STRUCT_POLETURNERS_WORKSHOP:
                    return eGoods.STORED_WOOD_PLANKS;
                case eStructs.STRUCT_BLACKSMITHS_WORKSHOP:
                case eStructs.STRUCT_ARMOURERS_WORKSHOP:
                    return eGoods.STORED_IRON_INGOTS;
                case eStructs.STRUCT_TANNERS_WORKSHOP:
                    return eGoods.STORED_COW_HIDES;
                default:
                    throw new ArgumentOutOfRangeException(nameof(type));
            }
        }

        internal static int IngredientLimit(eStructs type) => type == eStructs.STRUCT_TANNERS_WORKSHOP ? 3 : 48;

        internal static WeaponProductionOption FindVanilla(eStructs type, eGoods good)
        {
            foreach (var option in Vanilla)
                if (option.BuildingType == type && option.OutputGood == good) return option;
            return null;
        }

        internal IReadOnlyList<WeaponProductionOption> For(eStructs type) =>
            byType.TryGetValue(type, out var options) ? options : Array.Empty<WeaponProductionOption>();

        internal WeaponProductionOption At(int index) => index > 0 && index <= entries.Length ? entries[index - 1] : null;

        internal int IndexOf(WeaponProductionOptionId id)
        {
            for (int index = 0; index < entries.Length; index++)
                if (entries[index].Id.Equals(id)) return index + 1;
            return 0;
        }

        internal static WeaponProductionCatalog Compile(IEnumerable<WeaponProductionRegistration> registrations,
            Func<eStructs, bool> isAvailable, Func<WeaponProductionOption, bool> isRecipeAvailable = null)
        {
            var admitted = new List<WeaponProductionOption>();
            foreach (var group in registrations.GroupBy(registration => registration.Option.BuildingType).OrderBy(group => group.Key))
            {
                var baseline = Vanilla.Where(option => option.BuildingType == group.Key).ToArray();
                bool available = isAvailable(group.Key);
                int capacity = 4 - baseline.Length;
                int free = capacity;
                foreach (var registration in group.OrderByDescending(item => item.Option.Definition.Priority)
                    .ThenBy(item => item.Option.Id.OwnerGuid, StringComparer.Ordinal)
                    .ThenBy(item => item.Option.Id.OptionId, StringComparer.Ordinal))
                {
                    if (available && isRecipeAvailable != null && !isRecipeAvailable(registration.Option))
                        registration.SetStatus(WeaponProductionRegistrationStatus.AdapterUnavailable);
                    else if (free <= 0)
                        registration.SetStatus(WeaponProductionRegistrationStatus.ExcludedByPriority);
                    else if (!available)
                    {
                        free--;
                        registration.SetStatus(WeaponProductionRegistrationStatus.AdapterUnavailable);
                    }
                    else
                    {
                        if (free == capacity) admitted.AddRange(baseline);
                        free--;
                        admitted.Add(registration.Option);
                        registration.SetStatus(WeaponProductionRegistrationStatus.Admitted);
                    }
                }
            }
            if (admitted.Count == 0) return Empty;
            var options = admitted.ToArray();
            return new WeaponProductionCatalog(options, DigestOptions(options));
        }

        private static string DigestOptions(WeaponProductionOption[] options)
        {
            using (var data = new MemoryStream())
            using (var writer = new BinaryWriter(data))
            {
                bool hasStoredOutputs = options.Any(option => option.StoredOutput != null);
                bool hasIngredientAmounts = options.Any(option => option.IngredientAmount.HasValue);
                // Native-only catalogues retain the exact established /1 and /2 byte encodings.
                writer.Write(hasStoredOutputs ? "weapon-workshop-options/3" :
                    hasIngredientAmounts ? "weapon-workshop-options/2" : "weapon-workshop-options/1");
                writer.Write(options.Length);
                foreach (var option in options)
                {
                    writer.Write(option.Id.OwnerGuid);
                    writer.Write(option.Id.OptionId);
                    writer.Write((ushort)option.BuildingType);
                    writer.Write((short)option.TemplateGood);
                    writer.Write((short)option.OutputGood);
                    writer.Write(option.BaseAmount ?? -1);
                    writer.Write(option.WorkTicks ?? -1);
                    if (hasStoredOutputs || hasIngredientAmounts) writer.Write(option.IngredientAmount ?? -1);
                    if (hasStoredOutputs)
                    {
                        NativeStoredGood output = option.StoredOutput;
                        writer.Write((byte)(output == null ? 0 : 1));
                        if (output != null)
                        {
                            writer.Write(output.OwnerGuid); writer.Write(output.GoodId); writer.Write((byte)output.Pool);
                        }
                    }
                }
                writer.Flush();
                return Sha256Hex.Of(data.GetBuffer(), 0, checked((int)data.Length));
            }
        }

        private static WeaponProductionOption BuiltIn(eStructs type, eGoods good, string label) =>
            new WeaponProductionOption(new WeaponProductionOptionId(APISharedPlugin.PluginGuid, "weapon-workshop/" + good),
                new WeaponProductionOptionDefinition(type, good, good, label), true);
    }
}

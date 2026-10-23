using System;
using System.Collections.Generic;
using SHCDESE.GameGlobals;

namespace APIShared.ScriptExtenderFixes
{
    // Owns only a reusable UI-thread snapshot, never SE overrides. Read the effective
    // stack tops on every GUI check: foreign mods can push/pop without notifying us.
    internal sealed class PopularityParameters
    {
        private sealed class Entry
        {
            internal readonly string Name;
            internal readonly int Vanilla;
            internal readonly Func<int> Read;
            internal Entry(string name, int vanilla, Func<int> read) { Name=name; Vanilla=vanilla; Read=read; }
        }
        private readonly Entry[] entries;
        private readonly GameGlobalsManager globals;
        private readonly Dictionary<string, int> changed = new Dictionary<string, int>(41, StringComparer.Ordinal);
        internal PopularityParameters(GameGlobalsManager globals)
        {
            this.globals = globals;
            entries = new[] {
                new Entry("RationsPopModifierNone", -200, () => globals.RationsPopModifierNone.GetValue()),
                new Entry("RationsPopModifierHalf", -100, () => globals.RationsPopModifierHalf.GetValue()),
                new Entry("RationsPopModifierFull", 0, () => globals.RationsPopModifierFull.GetValue()),
                new Entry("RationsPopModifierExtra", 100, () => globals.RationsPopModifierExtra.GetValue()),
                new Entry("RationsPopModifierDouble", 200, () => globals.RationsPopModifierDouble.GetValue()),
                new Entry("RationsPopModifierNoPreferredFood", -200, () => globals.RationsPopModifierNoPreferredFood.GetValue()),
                new Entry("RationsPopModifierNoPopulation", 200, () => globals.RationsPopModifierNoPopulation.GetValue()),
                new Entry("FoodVarietyPopModifierTwoTypes", 25, () => globals.FoodVarietyPopModifierTwoTypes.GetValue()),
                new Entry("FoodVarietyPopModifierThreeTypes", 50, () => globals.FoodVarietyPopModifierThreeTypes.GetValue()),
                new Entry("FoodVarietyPopModifierFourTypes", 75, () => globals.FoodVarietyPopModifierFourTypes.GetValue()),
                new Entry("OverpopulationPopModifierTierOne", -50, () => globals.OverpopulationPopModifierTierOne.GetValue()),
                new Entry("OverpopulationPopModifierTierTwo", -100, () => globals.OverpopulationPopModifierTierTwo.GetValue()),
                new Entry("OverpopulationPopModifierTierThree", -150, () => globals.OverpopulationPopModifierTierThree.GetValue()),
                new Entry("OverpopulationPopModifierTierFour", -200, () => globals.OverpopulationPopModifierTierFour.GetValue()),
                new Entry("OverpopulationPopModifierTierFive", -250, () => globals.OverpopulationPopModifierTierFive.GetValue()),
                new Entry("TaxPopModifierHugeDonation", 175, () => globals.TaxPopModifierHugeDonation.GetValue()),
                new Entry("TaxPopModifierBigDonation", 125, () => globals.TaxPopModifierBigDonation.GetValue()),
                new Entry("TaxPopModifierSmallDonation", 75, () => globals.TaxPopModifierSmallDonation.GetValue()),
                new Entry("TaxPopModifierNone", 25, () => globals.TaxPopModifierNone.GetValue()),
                new Entry("TaxPopModifierLow", -50, () => globals.TaxPopModifierLow.GetValue()),
                new Entry("TaxPopModifierModerate", -100, () => globals.TaxPopModifierModerate.GetValue()),
                new Entry("TaxPopModifierHigh", -150, () => globals.TaxPopModifierHigh.GetValue()),
                new Entry("TaxPopModifierMean", -200, () => globals.TaxPopModifierMean.GetValue()),
                new Entry("TaxPopModifierUsurious", -300, () => globals.TaxPopModifierUsurious.GetValue()),
                new Entry("TaxPopModifierCruel", -400, () => globals.TaxPopModifierCruel.GetValue()),
                new Entry("TaxPopModifierMostCruel", -500, () => globals.TaxPopModifierMostCruel.GetValue()),
                new Entry("TaxPopModifierCruellest", -600, () => globals.TaxPopModifierCruellest.GetValue()),
                new Entry("TaxPopModifierUnaffordableDonation", 25, () => globals.TaxPopModifierUnaffordableDonation.GetValue()),
                new Entry("TaxPopModifierUnknownMode", -600, () => globals.TaxPopModifierUnknownMode.GetValue()),
                new Entry("ReligionPopModifierTierOne", 50, () => globals.ReligionPopModifierTierOne.GetValue()),
                new Entry("ReligionPopModifierTierTwo", 100, () => globals.ReligionPopModifierTierTwo.GetValue()),
                new Entry("ReligionPopModifierTierThree", 150, () => globals.ReligionPopModifierTierThree.GetValue()),
                new Entry("ReligionPopModifierTierFour", 200, () => globals.ReligionPopModifierTierFour.GetValue()),
                new Entry("ChurchPopModifier", 25, () => globals.ChurchPopModifier.GetValue()),
                new Entry("CathedralPopModifier", 50, () => globals.CathedralPopModifier.GetValue()),
                new Entry("AlePopModifierTierOne", 50, () => globals.AlePopModifierTierOne.GetValue()),
                new Entry("AlePopModifierTierTwo", 100, () => globals.AlePopModifierTierTwo.GetValue()),
                new Entry("AlePopModifierTierThree", 150, () => globals.AlePopModifierTierThree.GetValue()),
                new Entry("AlePopModifierTierFour", 200, () => globals.AlePopModifierTierFour.GetValue()),
                new Entry("FairsPopularityEventModifier", 400, () => globals.FairsPopularityEventModifier.GetValue()),
                new Entry("GoodBadThingPopModifierMultiplier", 25, () => globals.GoodBadThingPopModifierMultiplier.GetValue()),
            };
        }
        internal IReadOnlyDictionary<string, int> Refresh()
        {
            changed.Clear();
            foreach (var entry in entries)
            {
                int value = entry.Read();
                if (value != entry.Vanilla) changed.Add(entry.Name, value);
            }
            if (Economy.FearPopularity.HasActive)
                changed["GoodBadThingPopModifierMultiplier"] = globals.GoodBadThingPopModifierMultiplier.GetValue();
            return changed;
        }
    }
}

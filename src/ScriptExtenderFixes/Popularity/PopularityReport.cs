using System.Collections.Generic;
using SHCDESE.Interop.Enums;

namespace APIShared.ScriptExtenderFixes
{
    // A detached presentation snapshot. Never carries native pointers or writes PlayState.
    internal struct PopularityReport
    {
        internal int Population, FoodAvailable, FoodTypes, RationMode, TaxMode, Gold;
        internal int BlessedPercent, AlePercent, FearLevel;
        internal int Rations, Variety, Food, Tax, Crowding, Religion, Buildings, Ale, Fear, Fairs, Events, Total;

        internal PopularityReport Correct(IReadOnlyDictionary<string, int> active)
        {
            checked
            {
            var result = this;
            string ration = Population <= 0 ? "RationsPopModifierNoPopulation" : FoodAvailable <= 0 ? "RationsPopModifierNoPreferredFood" :
                RationName((RationsMode)RationMode);
            result.Rations = Value(active, ration, Rations);
            string variety = FoodTypes == 2 ? "FoodVarietyPopModifierTwoTypes" : FoodTypes == 3 ? "FoodVarietyPopModifierThreeTypes" : FoodTypes == 4 ? "FoodVarietyPopModifierFourTypes" : null;
            result.Variety = Value(active, variety, Variety);
            result.Food = Food + result.Rations - Rations + result.Variety - Variety;
            string tax = TaxMode < (int)TaxesMode.None && Gold <= 0 ? "TaxPopModifierUnaffordableDonation" : TaxName((TaxesMode)TaxMode);
            result.Tax = Value(active, tax, Tax);
            // Native report encodes the selected crowding tier, including the zero-population gate.
            string crowd = Crowding == -50 ? "OverpopulationPopModifierTierOne" : Crowding == -100 ? "OverpopulationPopModifierTierTwo" : Crowding == -150 ? "OverpopulationPopModifierTierThree" :
                Crowding == -200 ? "OverpopulationPopModifierTierFour" : Crowding == -250 ? "OverpopulationPopModifierTierFive" : null;
            result.Crowding = Value(active, crowd, Crowding);
            string religion = Tier(BlessedPercent, 95, ReligionKeys);
            result.Religion = Value(active, religion, Religion);
            // Vanilla's 25/50/75 is also its church/cathedral identity marker. Do not overwrite that sbyte.
            if (Buildings == 25 || Buildings == 75) result.Buildings += Value(active, "ChurchPopModifier", 25) - 25;
            if (Buildings == 50 || Buildings == 75) result.Buildings += Value(active, "CathedralPopModifier", 50) - 50;
            string ale = Tier(AlePercent, 100, AleKeys);
            result.Ale = Value(active, ale, Ale);
            if (active.TryGetValue("GoodBadThingPopModifierMultiplier", out int fear)) result.Fear = checked(fear * FearLevel);
            if (Fairs != 0) result.Fairs = Value(active, "FairsPopularityEventModifier", Fairs);
            result.Events = Events + result.Fairs - Fairs;
            result.Total = checked(Total + result.Food - Food + result.Tax - Tax + result.Crowding - Crowding +
                result.Religion - Religion + result.Buildings - Buildings + result.Ale - Ale + result.Fear - Fear + result.Fairs - Fairs);
            return result;
            }
        }
        private static int Value(IReadOnlyDictionary<string, int> values, string key, int original)
            => key != null && values.TryGetValue(key, out int value) ? value : original;
        private static readonly string[] ReligionKeys = { "ReligionPopModifierTierOne", "ReligionPopModifierTierTwo", "ReligionPopModifierTierThree", "ReligionPopModifierTierFour" };
        private static readonly string[] AleKeys = { "AlePopModifierTierOne", "AlePopModifierTierTwo", "AlePopModifierTierThree", "AlePopModifierTierFour" };
        private static string Tier(int percent, int top, string[] keys) => percent < 25 ? null : keys[percent < 50 ? 0 : percent < 75 ? 1 : percent < top ? 2 : 3];
        private static string RationName(RationsMode mode)
        {
            switch (mode)
            {
                case RationsMode.None: return "RationsPopModifierNone";
                case RationsMode.Half: return "RationsPopModifierHalf";
                case RationsMode.Full: return "RationsPopModifierFull";
                case RationsMode.Extra: return "RationsPopModifierExtra";
                case RationsMode.Double: return "RationsPopModifierDouble";
                default: return null;
            }
        }
        private static string TaxName(TaxesMode mode)
        {
            switch (mode)
            {
                case TaxesMode.HugeDonation: return "TaxPopModifierHugeDonation";
                case TaxesMode.BigDonation: return "TaxPopModifierBigDonation";
                case TaxesMode.SmallDonation: return "TaxPopModifierSmallDonation";
                case TaxesMode.None: return "TaxPopModifierNone";
                case TaxesMode.Low: return "TaxPopModifierLow";
                case TaxesMode.Moderate: return "TaxPopModifierModerate";
                case TaxesMode.High: return "TaxPopModifierHigh";
                case TaxesMode.Mean: return "TaxPopModifierMean";
                case TaxesMode.Usurious: return "TaxPopModifierUsurious";
                case TaxesMode.Cruel: return "TaxPopModifierCruel";
                case TaxesMode.MostCruel: return "TaxPopModifierMostCruel";
                case TaxesMode.Cruellest: return "TaxPopModifierCruellest";
                default: return "TaxPopModifierUnknownMode";
            }
        }
    }
}

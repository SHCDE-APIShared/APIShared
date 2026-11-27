namespace SHCDESE.Interop.Enums
{
    public enum RationsMode { None, Half, Full, Extra, Double }
    public enum TaxesMode { HugeDonation, BigDonation, SmallDonation, None, Low, Moderate, High, Mean, Usurious, Cruel, MostCruel, Cruellest }
}
namespace SHCDESE.GameGlobals
{
    public sealed class ManagedInt { public int ValueNumber; public int Value { get => ValueNumber; set => ValueNumber=value; } public ManagedInt(int value) { ValueNumber=value; } public int GetValue() => ValueNumber; }
    public sealed class GameGlobalsManager
    {
        public ManagedInt RationsPopModifierNone = new ManagedInt(-200);
        public ManagedInt RationsPopModifierHalf = new ManagedInt(-100);
        public ManagedInt RationsPopModifierFull = new ManagedInt(0);
        public ManagedInt RationsPopModifierExtra = new ManagedInt(100);
        public ManagedInt RationsPopModifierDouble = new ManagedInt(200);
        public ManagedInt RationsPopModifierNoPreferredFood = new ManagedInt(-200);
        public ManagedInt RationsPopModifierNoPopulation = new ManagedInt(200);
        public ManagedInt FoodVarietyPopModifierTwoTypes = new ManagedInt(25);
        public ManagedInt FoodVarietyPopModifierThreeTypes = new ManagedInt(50);
        public ManagedInt FoodVarietyPopModifierFourTypes = new ManagedInt(75);
        public ManagedInt OverpopulationPopModifierTierOne = new ManagedInt(-50);
        public ManagedInt OverpopulationPopModifierTierTwo = new ManagedInt(-100);
        public ManagedInt OverpopulationPopModifierTierThree = new ManagedInt(-150);
        public ManagedInt OverpopulationPopModifierTierFour = new ManagedInt(-200);
        public ManagedInt OverpopulationPopModifierTierFive = new ManagedInt(-250);
        public ManagedInt TaxPopModifierHugeDonation = new ManagedInt(175);
        public ManagedInt TaxPopModifierBigDonation = new ManagedInt(125);
        public ManagedInt TaxPopModifierSmallDonation = new ManagedInt(75);
        public ManagedInt TaxPopModifierNone = new ManagedInt(25);
        public ManagedInt TaxPopModifierLow = new ManagedInt(-50);
        public ManagedInt TaxPopModifierModerate = new ManagedInt(-100);
        public ManagedInt TaxPopModifierHigh = new ManagedInt(-150);
        public ManagedInt TaxPopModifierMean = new ManagedInt(-200);
        public ManagedInt TaxPopModifierUsurious = new ManagedInt(-300);
        public ManagedInt TaxPopModifierCruel = new ManagedInt(-400);
        public ManagedInt TaxPopModifierMostCruel = new ManagedInt(-500);
        public ManagedInt TaxPopModifierCruellest = new ManagedInt(-600);
        public ManagedInt TaxPopModifierUnaffordableDonation = new ManagedInt(25);
        public ManagedInt TaxPopModifierUnknownMode = new ManagedInt(-600);
        public ManagedInt ReligionPopModifierTierOne = new ManagedInt(50);
        public ManagedInt ReligionPopModifierTierTwo = new ManagedInt(100);
        public ManagedInt ReligionPopModifierTierThree = new ManagedInt(150);
        public ManagedInt ReligionPopModifierTierFour = new ManagedInt(200);
        public ManagedInt ChurchPopModifier = new ManagedInt(25);
        public ManagedInt CathedralPopModifier = new ManagedInt(50);
        public ManagedInt AlePopModifierTierOne = new ManagedInt(50);
        public ManagedInt AlePopModifierTierTwo = new ManagedInt(100);
        public ManagedInt AlePopModifierTierThree = new ManagedInt(150);
        public ManagedInt AlePopModifierTierFour = new ManagedInt(200);
        public ManagedInt FairsPopularityEventModifier = new ManagedInt(400);
        public ManagedInt GoodBadThingPopModifierMultiplier = new ManagedInt(25);
    }
}

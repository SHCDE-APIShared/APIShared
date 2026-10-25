using System;
using System.Collections.Generic;
using System.Threading;

namespace APIShared.Economy
{
    /// <summary>Ordinary SE popularity sources. Values identify the documented parameter, not a game array index.</summary>
    public enum PopularitySource
    {
        /// <summary>Override the SE RationsPopModifierNone parameter in whole report points.</summary>
        RationsPopModifierNone = 0,
        /// <summary>Override the SE RationsPopModifierHalf parameter in whole report points.</summary>
        RationsPopModifierHalf = 1,
        /// <summary>Override the SE RationsPopModifierFull parameter in whole report points.</summary>
        RationsPopModifierFull = 2,
        /// <summary>Override the SE RationsPopModifierExtra parameter in whole report points.</summary>
        RationsPopModifierExtra = 3,
        /// <summary>Override the SE RationsPopModifierDouble parameter in whole report points.</summary>
        RationsPopModifierDouble = 4,
        /// <summary>Override the SE RationsPopModifierNoPreferredFood parameter in whole report points.</summary>
        RationsPopModifierNoPreferredFood = 5,
        /// <summary>Override the SE RationsPopModifierNoPopulation parameter in whole report points.</summary>
        RationsPopModifierNoPopulation = 6,
        /// <summary>Override the SE FoodVarietyPopModifierTwoTypes parameter in whole report points.</summary>
        FoodVarietyPopModifierTwoTypes = 7,
        /// <summary>Override the SE FoodVarietyPopModifierThreeTypes parameter in whole report points.</summary>
        FoodVarietyPopModifierThreeTypes = 8,
        /// <summary>Override the SE FoodVarietyPopModifierFourTypes parameter in whole report points.</summary>
        FoodVarietyPopModifierFourTypes = 9,
        /// <summary>Override the SE OverpopulationPopModifierTierOne parameter in whole report points.</summary>
        OverpopulationPopModifierTierOne = 10,
        /// <summary>Override the SE OverpopulationPopModifierTierTwo parameter in whole report points.</summary>
        OverpopulationPopModifierTierTwo = 11,
        /// <summary>Override the SE OverpopulationPopModifierTierThree parameter in whole report points.</summary>
        OverpopulationPopModifierTierThree = 12,
        /// <summary>Override the SE OverpopulationPopModifierTierFour parameter in whole report points.</summary>
        OverpopulationPopModifierTierFour = 13,
        /// <summary>Override the SE OverpopulationPopModifierTierFive parameter in whole report points.</summary>
        OverpopulationPopModifierTierFive = 14,
        /// <summary>Override the SE TaxPopModifierHugeDonation parameter in whole report points.</summary>
        TaxPopModifierHugeDonation = 15,
        /// <summary>Override the SE TaxPopModifierBigDonation parameter in whole report points.</summary>
        TaxPopModifierBigDonation = 16,
        /// <summary>Override the SE TaxPopModifierSmallDonation parameter in whole report points.</summary>
        TaxPopModifierSmallDonation = 17,
        /// <summary>Override the SE TaxPopModifierNone parameter in whole report points.</summary>
        TaxPopModifierNone = 18,
        /// <summary>Override the SE TaxPopModifierLow parameter in whole report points.</summary>
        TaxPopModifierLow = 19,
        /// <summary>Override the SE TaxPopModifierModerate parameter in whole report points.</summary>
        TaxPopModifierModerate = 20,
        /// <summary>Override the SE TaxPopModifierHigh parameter in whole report points.</summary>
        TaxPopModifierHigh = 21,
        /// <summary>Override the SE TaxPopModifierMean parameter in whole report points.</summary>
        TaxPopModifierMean = 22,
        /// <summary>Override the SE TaxPopModifierUsurious parameter in whole report points.</summary>
        TaxPopModifierUsurious = 23,
        /// <summary>Override the SE TaxPopModifierCruel parameter in whole report points.</summary>
        TaxPopModifierCruel = 24,
        /// <summary>Override the SE TaxPopModifierMostCruel parameter in whole report points.</summary>
        TaxPopModifierMostCruel = 25,
        /// <summary>Override the SE TaxPopModifierCruellest parameter in whole report points.</summary>
        TaxPopModifierCruellest = 26,
        /// <summary>Override the SE TaxPopModifierUnaffordableDonation parameter in whole report points.</summary>
        TaxPopModifierUnaffordableDonation = 27,
        /// <summary>Override the SE TaxPopModifierUnknownMode parameter in whole report points.</summary>
        TaxPopModifierUnknownMode = 28,
        /// <summary>Override the SE ReligionPopModifierTierOne parameter in whole report points.</summary>
        ReligionPopModifierTierOne = 29,
        /// <summary>Override the SE ReligionPopModifierTierTwo parameter in whole report points.</summary>
        ReligionPopModifierTierTwo = 30,
        /// <summary>Override the SE ReligionPopModifierTierThree parameter in whole report points.</summary>
        ReligionPopModifierTierThree = 31,
        /// <summary>Override the SE ReligionPopModifierTierFour parameter in whole report points.</summary>
        ReligionPopModifierTierFour = 32,
        /// <summary>Override the SE ChurchPopModifier parameter in whole report points.</summary>
        ChurchPopModifier = 33,
        /// <summary>Override the SE CathedralPopModifier parameter in whole report points.</summary>
        CathedralPopModifier = 34,
        /// <summary>Override the SE AlePopModifierTierOne parameter in whole report points.</summary>
        AlePopModifierTierOne = 35,
        /// <summary>Override the SE AlePopModifierTierTwo parameter in whole report points.</summary>
        AlePopModifierTierTwo = 36,
        /// <summary>Override the SE AlePopModifierTierThree parameter in whole report points.</summary>
        AlePopModifierTierThree = 37,
        /// <summary>Override the SE AlePopModifierTierFour parameter in whole report points.</summary>
        AlePopModifierTierFour = 38,
        /// <summary>Override the SE FairsPopularityEventModifier parameter in whole report points.</summary>
        FairsPopularityEventModifier = 39,
    }
    /// <summary>Absolute whole report points by controller type. Null leaves that controller type untouched.</summary>
    public readonly struct PopularityOverrideValues
    {
        /// <summary>Creates independent human and AI values; conversion is validated atomically by the registration setter.</summary>
        public PopularityOverrideValues(int? humanPoints, int? aiPoints) { HumanPoints=humanPoints; AIPoints=aiPoints; }
        /// <summary>Points for every human participant, or null for no override.</summary>
        public int? HumanPoints { get; }
        /// <summary>Points for allied and enemy AI participants, or null for no override.</summary>
        public int? AIPoints { get; }
    }
    /// <summary>Process-lifetime, owner-bound ordinary popularity policies, selected before SE component arithmetic.</summary>
    /// <remarks>Register after native initialization. Settings and mission activation belong to the consumer.
    /// Registrations sort by ascending order, ordinal owner GUID and local ID; last matching non-null value wins.
    /// With no matching value SE's effective global value, including foreign overrides, is preserved.
    /// Readers use immutable snapshots without locks or allocation; setters may run on any thread.</remarks>
    public static class PopularityOverrides
    {
        private static readonly object sync=new object();
        private static readonly List<PopularityOverrideRegistration> entries=new List<PopularityOverrideRegistration>();
        internal static int SourceCount => Enum.GetValues(typeof(PopularitySource)).Length;
        /// <summary>Creates an inactive registration; duplicate owner/local IDs are rejected. No unregister is needed.</summary>
        public static bool TryRegister(string ownerGuid,string registrationId,out PopularityOverrideRegistration registration,
            out string diagnostic,int order=0)
        {
            registration=null;
            if(string.IsNullOrWhiteSpace(ownerGuid)||string.IsNullOrWhiteSpace(registrationId)) { diagnostic="Owner GUID and registration ID are required."; return false; }
            if(!FearPopularityRuntime.TargetedAvailable) { diagnostic="Player-aware popularity unavailable: "+(FearPopularityRuntime.TargetedDiagnostic??FearPopularityRuntime.Diagnostic); return false; }
            lock(sync) {
                foreach(var item in entries) if(item.Owner==ownerGuid&&item.Id==registrationId) { diagnostic="Duplicate owner/registration ID."; return false; }
                registration=new PopularityOverrideRegistration(ownerGuid,registrationId,order); entries.Add(registration);
                entries.Sort((a,b)=>{int c=a.Order.CompareTo(b.Order);if(c==0)c=StringComparer.Ordinal.Compare(a.Owner,b.Owner);return c!=0?c:StringComparer.Ordinal.Compare(a.Id,b.Id);});
            }
            diagnostic=null; return true;
        }
        internal static void Set(PopularityOverrideRegistration registration,IReadOnlyDictionary<PopularitySource,PopularityOverrideValues> values)
        {
            var copied=new Dictionary<PopularitySource,PopularityOverrideValues>();
            if(values!=null) foreach(var pair in values) {
                if(!Enum.IsDefined(typeof(PopularitySource),pair.Key)) throw new ArgumentException("Unknown popularity source.",nameof(values));
                copied.Add(pair.Key,new PopularityOverrideValues(ToUnits(pair.Value.HumanPoints),ToUnits(pair.Value.AIPoints)));
            }
            lock(sync) {
                int count=SourceCount; var human=new int?[count]; var ai=new int?[count];
                foreach(var item in entries) foreach(var pair in ReferenceEquals(item,registration)?copied:item.Values) {
                    if(pair.Value.HumanPoints.HasValue) human[(int)pair.Key]=pair.Value.HumanPoints;
                    if(pair.Value.AIPoints.HasValue) ai[(int)pair.Key]=pair.Value.AIPoints;
                }
                registration.Values=copied;
                PopularitySelection.SetSources(human,ai);
            }
        }
        internal static int? ToUnits(int? points) => points.HasValue?(int?)checked(points.Value*25):null;
    }
    /// <summary>A retained ordinary-popularity owner registration; use an empty configuration to deactivate.</summary>
    public sealed class PopularityOverrideRegistration
    {
        internal readonly string Owner,Id; internal readonly int Order;
        internal Dictionary<PopularitySource,PopularityOverrideValues> Values=new Dictionary<PopularitySource,PopularityOverrideValues>();
        internal PopularityOverrideRegistration(string owner,string id,int order){Owner=owner;Id=id;Order=order;}
        /// <summary>Atomically replaces all source values; null/empty disables this registration.</summary>
        /// <remarks>Copies inputs. Unknown source keys or points*25 overflow throw without changing active state.
        /// Null role values permit earlier applicable policies or the original SE value; zero is an explicit override.</remarks>
        public void SetContributions(IReadOnlyDictionary<PopularitySource,PopularityOverrideValues> contributions) => PopularityOverrides.Set(this,contributions);
    }
    // Combined publication is shared by simulation and UI. Each role object is built
    // on writes, never per calculation; one capture fixes both sources and fear.
    internal sealed class PopularitySelection
    {
        internal readonly int?[] Sources;
        internal readonly FearPopularity.Selection Fear;
        internal PopularitySelection(int?[] sources,FearPopularity.Selection fear){Sources=sources;Fear=fear;}
        private sealed class State {
            internal readonly PopularitySelection Human,AI,Legacy;
            internal readonly bool Targeted;
            internal State(int?[] h,int?[] a,FearPopularity.Selection fh,FearPopularity.Selection fa,FearPopularity.Selection fl) {
                Human=new PopularitySelection(h,fh);AI=new PopularitySelection(a,fa);Legacy=new PopularitySelection(null,fl);
                Targeted=Has(h)||Has(a)||!ReferenceEquals(fh,fl)||!ReferenceEquals(fa,fl);
            }
            private static bool Has(int?[] values){if(values!=null)foreach(var v in values)if(v.HasValue)return true;return false;}
        }
        private static readonly object sync=new object();
        private static int?[] human,ai;
        private static FearPopularity.Selection fearHuman,fearAI,fearLegacy;
        private static State current=new State(null,null,null,null,null);
        internal static void SetSources(int?[] h,int?[] a){lock(sync){human=h;ai=a;Volatile.Write(ref current,new State(h,a,fearHuman,fearAI,fearLegacy));}}
        internal static void SetFear(FearPopularity.Selection h,FearPopularity.Selection a,FearPopularity.Selection legacy){lock(sync){fearHuman=h;fearAI=a;fearLegacy=legacy;Volatile.Write(ref current,new State(human,ai,h,a,legacy));}}
        internal static PopularitySelection Legacy => Volatile.Read(ref current).Legacy;
        internal static PopularitySelection Capture(int playerId,IntPtr resources){var state=Volatile.Read(ref current);if(!state.Targeted)return state.Legacy;int kind=PopularityPlayerRuntime.Classify(playerId,resources);return kind==1?state.Human:kind==2?state.AI:state.Legacy;}
        internal static PopularitySelection ForKind(int kind){var s=Volatile.Read(ref current);return kind==1?s.Human:kind==2?s.AI:s.Legacy;}
        internal static bool NeedsPlayer => Volatile.Read(ref current).Targeted;
        internal static int Resolve(int original,PopularitySelection selection,int source) => selection?.Sources!=null&&selection.Sources[source].HasValue?selection.Sources[source].Value:original;
    }
}

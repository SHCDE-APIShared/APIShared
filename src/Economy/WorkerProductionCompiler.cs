using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using APIShared.GameModes;
using APIShared.Modifiers;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal readonly struct WorkerProductionCompiledEntry
    {
        internal readonly uint Whole;
        internal readonly ulong Numerator, Denominator;
        internal WorkerProductionCompiledEntry(uint whole, ulong numerator, ulong denominator)
        { Whole=whole; Numerator=numerator; Denominator=denominator; }
    }

    internal sealed class WorkerProductionCompiledRules
    {
        private readonly WorkerProductionCompiledEntry[] entries;
        internal WorkerProductionCompiledRules(int bonus, bool hasBase, ulong denominator, WorkerProductionCompiledEntry[] entries)
        {
            FixedBonusPercent=bonus; HasBaseContributions=hasBase; CommonDenominator=denominator;
            this.entries=(WorkerProductionCompiledEntry[])entries.Clone();
            Entries=Array.AsReadOnly(this.entries); Digest=ComputeDigest();
        }
        internal int FixedBonusPercent { get; }
        internal bool HasBaseContributions { get; }
        internal ulong CommonDenominator { get; }
        internal string Digest { get; }
        internal IReadOnlyList<eChimps> WorkerTypes => WorkerProductionCompiler.WorkerTypes;
        internal IReadOnlyList<WorkerProductionCompiledEntry> Entries { get; }
        internal WorkerProductionCompiledEntry GetEntry(eChimps type, bool ai, int input)
        {
            int worker=WorkerProductionCompiler.WorkerIndex(type);
            if(worker<0 || input<0 || input>WorkerProductionCompiler.MaximumInput) throw new ArgumentOutOfRangeException(nameof(input));
            return entries[(worker*2+(ai?1:0))*WorkerProductionCompiler.InputCount+input];
        }
        private string ComputeDigest()
        {
            // Explicit wire order; owner names and registration implementation details are not arithmetic.
            using(var data=new MemoryStream()) using(var writer=new BinaryWriter(data))
            {
                writer.Write("APIShared.WorkerProduction/2/actual-output/miller-one-no-productivity/exact-credit/native-bonus");
                writer.Write(WorkerProductionCompiler.MaximumInput); writer.Write(FixedBonusPercent);
                writer.Write(HasBaseContributions); writer.Write(CommonDenominator);
                foreach(var type in WorkerTypes) writer.Write((int)type);
                foreach(var entry in entries) { writer.Write(entry.Whole); writer.Write(entry.Numerator); writer.Write(entry.Denominator); }
                writer.Flush(); return Sha256Hex.Of(data.ToArray());
            }
        }
    }

    internal readonly struct WorkerProductionProfileContribution
    {
        internal readonly string Owner, Id;
        internal readonly WorkerProductionProfile Profile;
        internal WorkerProductionProfileContribution(string owner, string id, WorkerProductionProfile profile)
        { Owner=owner; Id=id; Profile=profile; }
    }

    internal sealed class WorkerProductionCompilation
    {
        internal WorkerProductionCompiledRules Rules;
        internal readonly List<WorkerProductionIssue> Issues=new List<WorkerProductionIssue>();
        internal bool HasBaseContributions, HasActiveContributions;
        internal int Bonus=50;
    }

    // All BigInteger/decimal work belongs here, on rare desired/preflight writes. Native readers receive
    // a bounded 17*2*101 lookup and one common denominator; no hot rational multiplication is required.
    internal static class WorkerProductionCompiler
    {
        internal const int MaximumInput=100, InputCount=MaximumInput+1;
        internal static readonly IReadOnlyList<eChimps> WorkerTypes=Array.AsReadOnly(new[] {
            eChimps.CHIMP_TYPE_WOODCUTTER,eChimps.CHIMP_TYPE_FLETCHER,eChimps.CHIMP_TYPE_HUNTER,
            eChimps.CHIMP_TYPE_QUARRY_GRUNT,eChimps.CHIMP_TYPE_PITCHMAN,eChimps.CHIMP_TYPE_FARMER_WHEAT,
            eChimps.CHIMP_TYPE_FARMER_HOPS,eChimps.CHIMP_TYPE_FARMER_APPLE,eChimps.CHIMP_TYPE_FARMER_CATTLE,
            eChimps.CHIMP_TYPE_MILLER,eChimps.CHIMP_TYPE_BAKER,eChimps.CHIMP_TYPE_BREWER,
            eChimps.CHIMP_TYPE_POLETURNER,eChimps.CHIMP_TYPE_BLACKSMITH,eChimps.CHIMP_TYPE_ARMOURER,
            eChimps.CHIMP_TYPE_TANNER,eChimps.CHIMP_TYPE_MINER2 });
        internal static int WorkerIndex(eChimps type)
        { for(int n=0;n<WorkerTypes.Count;n++) if(WorkerTypes[n]==type) return n; return -1; }
        internal static bool IsWorker(eChimps type) => WorkerIndex(type)>=0;
        internal static int MaximumBase(int bonus, eChimps type) => type==eChimps.CHIMP_TYPE_MILLER
            ? short.MaxValue : 3276700/(150+Math.Max(0,bonus));

        internal static WorkerProductionCompilation Compile(IReadOnlyList<WorkerProductionProfileContribution> ordered,
            GameModeSnapshot mode, IEnumerable<ulong> savedDenominators)
        {
            var result=new WorkerProductionCompilation();
            var active=new List<WorkerProductionProfileContribution>();
            var bonuses=new List<ValueModifierContribution>();
            foreach(var item in ordered)
            {
                if(item.Profile==null) continue;
                bool hasContribution=item.Profile.FixedBonusPercent.Kind!=ValueModifierKind.None;
                foreach(var values in item.Profile.BaseAmounts.Values)
                    hasContribution|=values.Human.Kind!=ValueModifierKind.None || values.AI.Kind!=ValueModifierKind.None;
                // None is inert even before a mission's mode is known. Still validate role keys below.
                if(!hasContribution)
                {
                    foreach(var pair in item.Profile.BaseAmounts) if(!IsWorker(pair.Key))
                        result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.UnsupportedWorker,
                            "No audited actual-output role exists for this worker type.",item.Owner,item.Id,pair.Key));
                    continue;
                }
                if(item.Profile.Activation.HasValue)
                {
                    if(mode.Kind==GameModeKind.Unknown)
                    { result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.ActivationUnknown,
                        "A mode-dependent production profile has no prepared mission mode.",item.Owner,item.Id)); continue; }
                    if(!GameplayModModePolicy.IsAllowed(item.Profile.Activation.Value,mode,out _)) continue;
                }
                active.Add(item);
                if(item.Profile.FixedBonusPercent.Kind!=ValueModifierKind.None)
                { bonuses.Add(item.Profile.FixedBonusPercent); result.HasActiveContributions=true; }
                foreach(var pair in item.Profile.BaseAmounts)
                {
                    if(!IsWorker(pair.Key)) result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.UnsupportedWorker,
                        "No audited actual-output role exists for this worker type.",item.Owner,item.Id,pair.Key));
                    if(pair.Value.Human.Kind!=ValueModifierKind.None || pair.Value.AI.Kind!=ValueModifierKind.None)
                    { result.HasBaseContributions=true; result.HasActiveContributions=true; }
                }
            }
            var bonusPlan=ValueModifierPlan.Create(bonuses);
            bonusPlan.GetExactRatio(50,out BigInteger bonusNumerator,out BigInteger bonusDenominator);
            BigInteger rounded=BigInteger.DivRem(bonusNumerator,bonusDenominator,out BigInteger bonusRest);
            if(BigInteger.Abs(bonusRest)*2>=bonusDenominator) rounded+=bonusNumerator.Sign;
            if(rounded < -100 || rounded > 1000)
                result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.FixedBonusOutOfRange,
                    "The composed fixed production bonus is outside -100..1000 after one midpoint-away rounding.",
                    actual:Text(rounded),limit:"-100..1000"));
            else result.Bonus=(int)rounded;

            var whole=new uint[WorkerTypes.Count*2*InputCount];
            var numerators=new BigInteger[whole.Length]; var denominators=new BigInteger[whole.Length];
            BigInteger common=BigInteger.One;
            bool precisionIssue=false;
            if(savedDenominators!=null) foreach(ulong denominator in savedDenominators)
            {
                if(denominator==0 || denominator>long.MaxValue)
                { result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.SavedDenominatorInvalid,
                    "A saved production denominator is outside 1..Int64.MaxValue.",actual:denominator.ToString(CultureInfo.InvariantCulture),limit:"1..9223372036854775807")); continue; }
                IncludeDenominator(ref common,denominator,result,ref precisionIssue);
            }
            for(int worker=0;worker<WorkerTypes.Count;worker++) for(int category=0;category<2;category++)
            {
                var contributions=new List<ValueModifierContribution>();
                foreach(var owner in active) if(owner.Profile.BaseAmounts.TryGetValue(WorkerTypes[worker],out var values))
                    contributions.Add(category==0 ? values.Human : values.AI);
                var plan=ValueModifierPlan.Create(contributions);
                int maximum=MaximumBase(result.Bonus,WorkerTypes[worker]);
                bool negativeReported=false, largeReported=false;
                for(int input=0;input<InputCount;input++)
                {
                    int index=(worker*2+category)*InputCount+input;
                    // Miller's actual-output site has the fixed Vanilla baseline one and no SE
                    // Pre parameter. Other slots are reserved identity entries, not admissible
                    // hypothetical Miller inputs; this also keeps identity-mask inspection exact.
                    if(WorkerTypes[worker]==eChimps.CHIMP_TYPE_MILLER && input!=1)
                    { whole[index]=(uint)input; denominators[index]=BigInteger.One; continue; }
                    plan.GetExactRatio(input,out BigInteger numerator,out BigInteger denominator);
                    if(numerator<0)
                    {
                        if(!negativeReported) result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.NegativeBaseAmount,
                            "The exact base-output result is negative.",worker:WorkerTypes[worker],ai:category!=0,input:input,
                            actual:Ratio(numerator,denominator),limit:"0"));
                        negativeReported=true; continue;
                    }
                    BigInteger integer=BigInteger.DivRem(numerator,denominator,out BigInteger fraction);
                    BigInteger upper=integer+(fraction.IsZero ? 0 : 1);
                    if(upper>maximum)
                    {
                        if(!largeReported) result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.BaseAmountOutOfRange,
                            "A base output including an accumulated fractional unit exceeds the original native arithmetic domain.",
                            worker:WorkerTypes[worker],ai:category!=0,input:input,actual:Ratio(numerator,denominator),
                            limit:maximum.ToString(CultureInfo.InvariantCulture)));
                        largeReported=true; continue;
                    }
                    whole[index]=(uint)integer;
                    // A zero fraction has no precision requirement, even when the unreduced expression was large.
                    if(fraction.IsZero) denominator=BigInteger.One;
                    numerators[index]=fraction; denominators[index]=denominator;
                    IncludeDenominator(ref common,denominator,result,ref precisionIssue);
                }
            }
            if(result.Issues.Count!=0) return result;
            var entries=new WorkerProductionCompiledEntry[whole.Length];
            for(int n=0;n<entries.Length;n++)
                entries[n]=new WorkerProductionCompiledEntry(whole[n],(ulong)(numerators[n]*(common/denominators[n])),(ulong)common);
            result.Rules=new WorkerProductionCompiledRules(result.Bonus,result.HasBaseContributions,(ulong)common,entries);
            return result;
        }

        private static void IncludeDenominator(ref BigInteger common,BigInteger denominator,WorkerProductionCompilation result,ref bool reported)
        {
            BigInteger next=(common/BigInteger.GreatestCommonDivisor(common,denominator))*denominator;
            if(next>long.MaxValue)
            {
                if(!reported) result.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.PrecisionNotRepresentable,
                    "The exact common denominator of active rules and saved credits exceeds Int64.MaxValue.",
                    actual:Text(next),limit:"9223372036854775807"));
                reported=true;
            }
            else common=next;
        }
        private static string Text(BigInteger value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Ratio(BigInteger numerator,BigInteger denominator) => denominator.IsOne ? Text(numerator) : Text(numerator)+"/"+Text(denominator);
    }
}

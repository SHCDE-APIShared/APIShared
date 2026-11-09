using System;
using System.Collections.Generic;
using System.Linq;
using APIShared.Economy;
using APIShared.GameModes;
using APIShared.Modifiers;
using SHCDESE.Interop;

internal static partial class Program
{
    private static WorkerProductionProfile BaseProfile(ValueModifierContribution human,ValueModifierContribution ai=default,
        ValueModifierContribution bonus=default,eChimps worker=eChimps.CHIMP_TYPE_WOODCUTTER) =>
        new WorkerProductionProfile(new Dictionary<eChimps,WorkerProductionValues> { [worker]=new WorkerProductionValues(human,ai) },bonus);
    private static WorkerProductionCompilation CompileProfiles(params WorkerProductionProfile[] profiles) =>
        WorkerProductionCompiler.Compile(profiles.Select((profile,index)=>new WorkerProductionProfileContribution("owner",index.ToString(),profile)).ToArray(),default,null);
    private static void TestCompilerAndProfiles()
    {
        var exact=CompileProfiles(BaseProfile(ValueModifierContribution.Factor(0.1m)),BaseProfile(ValueModifierContribution.Factor(3m)));
        Check(exact.Issues.Count==0,"Exact decimal composition compiles without quantizing factors separately.");
        var third=exact.Rules.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,false,1);
        Check(third.Whole==0 && third.Numerator==3 && third.Denominator==10,"Base fractions retain exact tenths for per-unit accumulation.");
        var ai=exact.Rules.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,true,1);
        Check(ai.Whole==1 && ai.Numerator==0,"Human and AI contributions remain independent.");
        var cancelled=CompileProfiles(BaseProfile(ValueModifierContribution.Factor(10000000000000000000000000000m)),
            BaseProfile(ValueModifierContribution.Factor(0.0000000000000000000000000001m)));
        Check(cancelled.Issues.Count==0 && cancelled.Rules.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,false,100).Whole==100,
            "Large intermediate factors cancel exactly before representability and amount validation.");
        Check(CompileProfiles(BaseProfile(ValueModifierContribution.Factor(0.00000000000000000001m))).Issues.Any(i=>i.Code==WorkerProductionIssueCode.PrecisionNotRepresentable),
            "Unrepresentable exact precision is visible instead of rounded away.");
        Check(CompileProfiles(BaseProfile(ValueModifierContribution.FixedValue(-0.001m))).Issues.Any(i=>i.Code==WorkerProductionIssueCode.NegativeBaseAmount),
            "A tiny negative exact amount is rejected before integer quantization.");
        var atLimit=CompileProfiles(BaseProfile(ValueModifierContribution.Factor(28.49m),bonus:ValueModifierContribution.FixedValue(1000)));
        Check(atLimit.Issues.Count==0 && atLimit.Rules.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,false,100).Whole==2849,
            "Largest all-mode base at bonus1000 is admitted.");
        var tooLarge=CompileProfiles(BaseProfile(ValueModifierContribution.Factor(28.5m),bonus:ValueModifierContribution.FixedValue(1000)));
        Check(tooLarge.Rules==null && tooLarge.Issues.Any(i=>i.Code==WorkerProductionIssueCode.BaseAmountOutOfRange && i.Limit=="2849"),
            "Next oversized base is rejected with the actual applicable limit.");
        Check(WorkerProductionCompiler.MaximumBase(-100,eChimps.CHIMP_TYPE_WOODCUTTER)==21844 &&
            WorkerProductionCompiler.MaximumBase(50,eChimps.CHIMP_TYPE_WOODCUTTER)==16383 &&
            WorkerProductionCompiler.MaximumBase(1000,eChimps.CHIMP_TYPE_MILLER)==32767,
            "Bounds include ineligible/modus0 productivity and Miller's actual-output exception.");
        var millerLimit=CompileProfiles(BaseProfile(ValueModifierContribution.Factor(32767),worker:eChimps.CHIMP_TYPE_MILLER));
        Check(millerLimit.Issues.Count==0 && millerLimit.Rules.GetEntry(eChimps.CHIMP_TYPE_MILLER,false,1).Whole==32767,
            "Miller validates its real baseline one rather than an unreachable hypothetical input100.");
        Check(CompileProfiles(BaseProfile(ValueModifierContribution.FixedValue(32767.01m),worker:eChimps.CHIMP_TYPE_MILLER))
            .Issues.Any(i=>i.Code==WorkerProductionIssueCode.BaseAmountOutOfRange),"Miller's accumulated next fractional unit must also fit signed Carry.");
        Check(CompileProfiles(BaseProfile(ValueModifierContribution.FixedValue(-0.1m),worker:eChimps.CHIMP_TYPE_MILLER))
            .Issues.Any(i=>i.Code==WorkerProductionIssueCode.NegativeBaseAmount && i.InputAmount==1),"Miller still rejects a negative exact output at its actual input1.");
        foreach(var bad in new[] { 1000.5m,-100.5m })
            Check(CompileProfiles(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(bad))).Issues.Any(i=>i.Code==WorkerProductionIssueCode.FixedBonusOutOfRange),
                "Bonus midpoint-away rounding occurs before range rejection.");
        Check(CompileProfiles(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(1000.49m))).Bonus==1000,
            "A representable rounded percentage is retained without clamping.");
        var saved=WorkerProductionCompiler.Compile(new[] { new WorkerProductionProfileContribution("a","b",BaseProfile(ValueModifierContribution.Factor(0.1m))) },default,new ulong[] { 3 });
        Check(saved.Rules.CommonDenominator==30 && saved.Rules.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,false,1).Numerator==3,
            "Saved precision participates in the exact common denominator.");
        Check(WorkerProductionCompiler.Compile(Array.Empty<WorkerProductionProfileContribution>(),default,new ulong[] { (ulong)long.MaxValue,2 }).Issues.Any(i=>i.Code==WorkerProductionIssueCode.PrecisionNotRepresentable),
            "The combined saved denominator bound is checked after exact LCM cancellation.");
        Check(WorkerProductionCompiler.Compile(Array.Empty<WorkerProductionProfileContribution>(),default,new ulong[] { 0 }).Issues.Any(i=>i.Code==WorkerProductionIssueCode.SavedDenominatorInvalid),
            "Invalid saved denominator has a structured issue.");

        bool available=false; int published=0;
        var registry=new WorkerProductionModifierRegistry(()=>available,()=>"fixture unavailable",_=>published++);
        Check(registry.TryRegister("any.foreign.owner","production",out var registration,out _),"Desired registration succeeds before native availability.");
        var source=new Dictionary<eChimps,WorkerProductionValues> { [eChimps.CHIMP_TYPE_WOODCUTTER]=new WorkerProductionValues(ValueModifierContribution.Factor(2),default) };
        var detached=new WorkerProductionProfile(source);
        source.Clear(); registration.SetProfile(detached);
        Check(registry.Status.HasDesiredProfiles && !registry.Status.CanStart && registry.Status.Issues.Any(i=>i.Code==WorkerProductionIssueCode.NativeUnavailable),
            "Unavailable desired effects remain registered and block admission.");
        available=true; registry.Prepare(default);
        Check(registry.Status.CanStart && registry.CurrentPrepared.GetEntry(eChimps.CHIMP_TYPE_WOODCUTTER,false,12).Whole==24 && published==0,
            "Detached desired profile prepares successfully but never mutates native state.");
        var prepared=registry.CurrentPrepared;
        Check(registry.ActivatePrepared(prepared,out _) && published==1,"Only explicit activation publishes the admitted immutable object.");
        long revision=registry.Status.Revision; string digest=registry.Status.Digest; int observed=0;
        registry.TryRegisterObserver("observer","throws",_=>throw new Exception("isolated"),out _,replayCurrent:false);
        registry.TryRegisterObserver("observer","works",_=>observed++,out _,replayCurrent:false);
        registry.UpdatePeerReadiness("Pending","fixture peer");
        Check(observed==1 && registry.Status.Revision==revision && registry.Status.Digest==digest && ReferenceEquals(prepared,registry.CurrentPrepared),
            "Peer presentation changes isolate observers and preserve the arithmetic admission object.");
        registry.NotifyRejectedLocalLaunch("Unavailable");
        Check(registry.Status.RejectedLocalLaunchSequence==1 && registry.Status.RejectedLocalLaunchCode=="Unavailable" &&
            registry.Status.Revision==revision && registry.Status.Digest==digest && ReferenceEquals(prepared,registry.CurrentPrepared),
            "An actual rejected local launch advances only presentation state.");
        registration.SetProfile(BaseProfile(ValueModifierContribution.Factor(3)));
        Check(!registry.ActivatePrepared(prepared,out _) && published==1,"A stale prepared reference cannot publish a newer request.");
        registration.SetProfile(BaseProfile(ValueModifierContribution.FixedValue(-1)));
        Check(registry.Status.HasDesiredProfiles && !registry.Status.CanStart && registry.Status.Digest==string.Empty && published==1,
            "Invalid desired state remains visible and cannot silently fall back to the previous plan.");
        registration.SetProfile(null);
        Check(registry.Status.CanStart && !registry.Status.HasDesiredProfiles,"Explicit null removes an invalid desired profile.");

        var profile=new GameplayModActivationProfile("any.foreign.owner","fixture",GameplayModAllowedContext.CustomGame);
        registration.SetProfile(new WorkerProductionProfile(activation:profile));
        Check(registry.Status.CanStart,"An empty profile is inert even before a mode has been prepared.");
        registration.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(0),activation:profile));
        var bundle=registry.GetLaunchBundle();
        Check(registry.Status.CanStart && registry.CurrentPrepared==null && bundle.CanStart && bundle.Digest.Length!=0,
            "An unknown real mode admits a finite launch bundle without activating its preview.");
        registry.Prepare(new GameModeSnapshot(GameModeKind.Campaign));
        Check(registry.Status.CanStart && registry.Status.ResolvedFixedBonusPercent==50,"A recognized disallowed context leaves Vanilla arithmetic.");
        registry.Prepare(new GameModeSnapshot(GameModeKind.CustomGame));
        Check(registry.Status.CanStart && registry.Status.ResolvedFixedBonusPercent==0,"A permitted context contributes its desired percentage.");
        Check(bundle.Contains(new GameModeSnapshot(GameModeKind.CustomGame),registry.CurrentPrepared),
            "The actual mission selects exactly the arithmetic committed by its frozen bundle member.");
        string admittedBundle=bundle.Digest;
        registration.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(10),activation:profile));
        Check(registry.GetLaunchBundle().Digest!=admittedBundle && !bundle.Contains(new GameModeSnapshot(GameModeKind.CustomGame),registry.CurrentPrepared),
            "A desired policy/value change invalidates the admitted launch commitment.");
        registry.TryRegister("global.owner","tutorial",out var tutorial,out _);
        tutorial.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(20)));
        var tutorialBundle=registry.GetLaunchBundle();
        registry.Prepare(new GameModeSnapshot(GameModeKind.Tutorial));
        Check(registry.Status.ResolvedFixedBonusPercent==20 && tutorialBundle.Contains(new GameModeSnapshot(GameModeKind.Tutorial),registry.CurrentPrepared),
            "Tutorial remains a bundle member for unconditional owners while optional policies deny it.");

        var common=new WorkerProductionModifierRegistry(()=>true,()=>"",_=> { });
        common.TryRegister("a.owner","factor",out var factor,out _);
        factor.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.Factor(2)));
        var legacy=new WorkerProductionBonusRegistry(common);
        legacy.TryRegister("z.owner","fixed",out var fixedValue,out _);
        fixedValue.SetPercent(50);
        Check(common.Status.ResolvedFixedBonusPercent==50,"Legacy explicit50 is a fixed contribution in the shared registry.");
        fixedValue.SetPercent(null);
        Check(common.Status.ResolvedFixedBonusPercent==100,"Legacy null reveals another owner's factor over Vanilla50.");

        var partial=new WorkerProductionModifierRegistry(()=>false,()=>"base cohort failed",_=> { },legacyAvailable:()=>true);
        partial.TryRegister("partial","production",out var partialOwner,out _);
        partialOwner.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(0)));
        Check(partial.Status.CanStart && partial.Status.IsAvailable,"A fixed-only request uses the independent ready bonus cohort.");
        partialOwner.SetProfile(BaseProfile(ValueModifierContribution.Factor(2)));
        Check(!partial.Status.CanStart && !partial.Status.IsAvailable,"A base contribution requires the complete base cohort.");
        partialOwner.SetProfile(null); partial.SetSavedDenominatorProvider(()=>new ulong[] { 1 });
        Check(!partial.TryRefreshSavedDenominators(out _) && !partial.Status.CanStart,
            "Saved denominator1 still represents required zero/load provenance and requires the full cohort.");

        var engineGate=new object(); bool armed=false,providerLocked=false,publisherLocked=false;
        var monitored=new WorkerProductionModifierRegistry(()=>true,()=>"",_=> { });
        monitored.SetSavedDenominatorProvider(()=> { if(armed) providerLocked=System.Threading.Monitor.IsEntered(engineGate); return Array.Empty<ulong>(); });
        monitored.ConfigureBackend(()=>true,()=>"",_=> { },()=>"fixture",legacyPublisher:_=>publisherLocked=System.Threading.Monitor.IsEntered(engineGate),
            legacyPublicationMonitor:engineGate);
        var monitoredLegacy=new WorkerProductionBonusRegistry(monitored);
        monitoredLegacy.TryRegister("monitor","legacy",out var monitoredHandle,out _);
        armed=true; monitoredHandle.SetPercent(25);
        Check(providerLocked && publisherLocked,"Legacy publication enters the injected real engine monitor before provider capture or publishing.");
        TestDetachedStatusSnapshots();
        TestSavedProviderRebinding();
        TestFearProductionProfiles();
    }

    private static WorkerProductionProfile FearProfile(int level, ValueModifierContribution contribution,
        ValueModifierContribution fixedBonus = default, GameplayModActivationProfile? activation = null) =>
        new WorkerProductionProfile(null, fixedBonus, activation, new Dictionary<int, ValueModifierContribution> { [level] = contribution });

    private static void TestFearProductionProfiles()
    {
        var vanilla = CompileProfiles().Rules;
        Check(vanilla.FearBonusByLevel.SequenceEqual(new[] { 50, 40, 30, 20, 10, 0, 0, 0, 0, 0, 0 }),
            "Every signed native fear stage has the correct Vanilla output-bonus baseline.");
        var identity = CompileProfiles(FearProfile(-3, ValueModifierContribution.Factor(1)),
            FearProfile(3, ValueModifierContribution.Factor(2))).Rules;
        Check(identity.Digest == vanilla.Digest && !identity.HasFearChanges && !identity.RequiresExtendedWire,
            "An unchanged resolved fear table preserves the native-wire Vanilla arithmetic digest.");
        var doubled = CompileProfiles(FearProfile(-3, ValueModifierContribution.Factor(2))).Rules;
        Check(doubled.FearBonusByLevel[2] == 60 && doubled.HasFearChanges && !doubled.RequiresExtendedWire && doubled.Digest != vanilla.Digest,
            "A changed positive fear bonus affects launch agreement without requiring extended rows.");

        foreach (int level in new[] { int.MinValue, -6, 0, 6, int.MaxValue })
            Check(CompileProfiles(FearProfile(level, ValueModifierContribution.FixedValue(20))).Issues.Any(issue =>
                issue.Code == WorkerProductionIssueCode.InvalidFearLevel && issue.FearLevel == level),
                "The neutral stage and out-of-domain inputs report the exact unsupported level.");
        foreach (decimal value in new[] { -100.5m, 1000.5m })
            Check(CompileProfiles(FearProfile(2, ValueModifierContribution.FixedValue(value))).Issues.Any(issue =>
                issue.Code == WorkerProductionIssueCode.FearBonusOutOfRange && issue.FearLevel == 2),
                "One midpoint-away rounding precedes signed fear range validation.");
        var limits = CompileProfiles(FearProfile(2, ValueModifierContribution.FixedValue(-99.5m), ValueModifierContribution.FixedValue(0)),
            FearProfile(-2, ValueModifierContribution.FixedValue(1000.49m))).Rules;
        Check(limits.FearBonusByLevel[7] == -100 && limits.FearBonusByLevel[3] == 1000 && limits.HasNegativeFearBonus && limits.RequiresExtendedWire,
            "The inclusive signed limits are retained and negative output requires the extended row contract.");
        Check(CompileProfiles(FearProfile(3, ValueModifierContribution.FixedValue(-50), ValueModifierContribution.FixedValue(-50))).Rules != null &&
            CompileProfiles(FearProfile(3, ValueModifierContribution.FixedValue(-51), ValueModifierContribution.FixedValue(-50))).Issues.Any(issue =>
                issue.Code == WorkerProductionIssueCode.CombinedBonusOutOfRange && issue.FearLevel == 3 && issue.ActualValue == "-101"),
            "A combined penalty can reach zero yield but cannot make native cargo negative.");
        var peak = FearProfile(-2, ValueModifierContribution.FixedValue(1000), ValueModifierContribution.FixedValue(1000));
        Check(CompileProfiles(peak, BaseProfile(ValueModifierContribution.FixedValue(1560))).Rules != null &&
            CompileProfiles(peak, BaseProfile(ValueModifierContribution.FixedValue(1560.01m))).Issues.Any(issue =>
                issue.Code == WorkerProductionIssueCode.BaseAmountOutOfRange && issue.Limit == "1560"),
            "The worst combined bonus limits even the next whole unit from a carried base fraction.");

        bool fullReady = true;
        var registry = new WorkerProductionModifierRegistry(() => fullReady, () => "full backend unavailable", _ => { }, legacyAvailable: () => true);
        var custom = new GameModeSnapshot(GameModeKind.CustomGame, multiplayer: true);
        registry.Prepare(custom);
        registry.TryRegister("z.owner", "fixed", out var later, out _, order: 1);
        registry.TryRegister("a.owner", "after", out var after, out _, order: -1);
        registry.TryRegister("a.owner", "before", out var before, out _);
        registry.TryRegister("b.owner", "fixed", out var earlier, out _);
        var caller = new Dictionary<int, ValueModifierContribution> { [3] = ValueModifierContribution.FixedValue(-20) };
        later.SetProfile(new WorkerProductionProfile(null, default, null, caller));
        caller[3] = ValueModifierContribution.FixedValue(99);
        after.SetProfile(FearProfile(3, ValueModifierContribution.Factor(1.25m, ValueModifierStage.AfterFixedValue)));
        before.SetProfile(FearProfile(3, ValueModifierContribution.Factor(10)));
        earlier.SetProfile(FearProfile(3, ValueModifierContribution.FixedValue(-10)));
        var negativeStatus = registry.Status;
        var negativeBundle = registry.GetLaunchBundle();
        Check(negativeStatus.TryGetResolvedFearBonusPercent(3, out int resolved) && resolved == -25 &&
            negativeStatus.TryGetResolvedFearBonusPercent(0, out int neutral) && neutral == 0 &&
            !negativeStatus.TryGetResolvedFearBonusPercent(6, out _) && negativeBundle.Contains(custom, registry.CurrentPrepared),
            "A detached signed fixed winner replaces ordinary factors, then receives the explicit after-factor.");
        fullReady = false; registry.Prepare(custom);
        Check(!registry.Status.CanStart && registry.Status.Issues.Any(issue => issue.Code == WorkerProductionIssueCode.NativeUnavailable),
            "The legacy-only backend cannot admit an active signed fear table.");
        fullReady = true; later.SetProfile(null);
        Check(registry.Status.TryGetResolvedFearBonusPercent(3, out resolved) && resolved == -13 &&
            negativeStatus.TryGetResolvedFearBonusPercent(3, out int previous) && previous == -25 &&
            negativeBundle.Digest != registry.GetLaunchBundle().Digest && !negativeBundle.Contains(custom, registry.CurrentPrepared),
            "Withdrawal reveals the earlier fixed value and invalidates the previous peer agreement without changing old snapshots.");

        var policy = new GameplayModActivationProfile("a.owner", "fear", GameplayModAllowedContext.Campaign);
        registry.Prepare(default);
        earlier.SetProfile(FearProfile(3, ValueModifierContribution.FixedValue(20), activation: policy));
        Check(!registry.Status.TryGetResolvedFearBonusPercent(3, out _),
            "An unknown launch mode exposes its agreement bundle without claiming a resolved fear value.");
        registry.Prepare(custom);
        Check(registry.Status.TryGetResolvedFearBonusPercent(3, out resolved) && resolved == 0,
            "A mode-inactive owner contributes no fear change.");
        registry.Prepare(new GameModeSnapshot(GameModeKind.Campaign));
        Check(registry.Status.TryGetResolvedFearBonusPercent(3, out resolved) && resolved == 25,
            "A permitted mode can resolve an absolute boost for a fear stage whose Vanilla baseline is zero.");
    }

    private static void TestSavedProviderRebinding()
    {
        int published=0;
        var registry=new WorkerProductionModifierRegistry(()=>true,()=>"",_=>published++);
        registry.TryRegister("restore.fixture","production",out var owner,out _);
        owner.SetProfile(BaseProfile(ValueModifierContribution.Factor(0.25m),bonus:ValueModifierContribution.FixedValue(0)));
        var editor=new GameModeSnapshot(GameModeKind.MapEditor);
        registry.Prepare(editor);
        var oldCandidate=registry.CurrentPrepared;var oldStatus=registry.Status;
        Check(oldCandidate!=null && oldCandidate.CommonDenominator==4 && registry.GetLaunchBundle().Contains(editor,oldCandidate),
            "A created editor has an admitted quarter-output calculation before archive selection.");
        // The exact editor reload regression: the selected record contains a quarter,
        // whose denominator changes neither the existing profile's CD4 nor its digest.
        registry.SetSavedDenominatorProvider(()=>new ulong[] {4});
        Check(registry.CurrentPrepared==null,"Selecting saved precision invalidates the prior admission candidate immediately.");
        registry.Prepare(default);
        var frozen=registry.GetLaunchBundle();
        registry.Prepare(editor);
        var restoredCandidate=registry.CurrentPrepared;
        Check(registry.Status.CanStart && restoredCandidate!=null && restoredCandidate.Digest==oldCandidate.Digest &&
            !ReferenceEquals(restoredCandidate,oldCandidate) && registry.Status.Revision>oldStatus.Revision,
            "Equal saved denominator and arithmetic still rebuild the cleared candidate and advance its admission revision.");
        Check(frozen.Contains(editor,restoredCandidate) && registry.GetLaunchBundle().Digest==frozen.Digest,
            "The reloaded actual editor selects its exact newly frozen saved-precision bundle member.");
        Check(!registry.ActivatePrepared(oldCandidate,out _) && published==0 &&
            registry.ActivatePrepared(restoredCandidate,out _) && published==1,
            "The stale pre-selection candidate stays forbidden while the valid rebuilt candidate can activate.");
        registry.SetSavedDenominatorProvider(()=>new ulong[] {0}); registry.Prepare(editor);
        Check(!registry.Status.CanStart && registry.CurrentPrepared==null,
            "Provider invalidation never retains the last valid arithmetic when the new saved precision is invalid.");
        registry.SetSavedDenominatorProvider(()=>new ulong[] {4});registry.Prepare(editor);
        Check(registry.Status.CanStart && registry.CurrentPrepared!=null && registry.CurrentPrepared.Digest==oldCandidate.Digest,
            "A corrected saved precision provider can rebuild the same exact quarter plan without falling back to an old object.");
        var quarterCandidate=registry.CurrentPrepared;
        registry.SetSavedDenominatorProvider(()=>new ulong[] {3});registry.Prepare(editor);
        Check(registry.CurrentPrepared.CommonDenominator==12 && !frozen.Contains(editor,registry.CurrentPrepared) &&
            !registry.ActivatePrepared(quarterCandidate,out _),
            "A genuinely different combined saved precision still changes the bundle and rejects earlier admission identities.");
    }

    private static void TestDetachedStatusSnapshots()
    {
        var registry=new WorkerProductionModifierRegistry(()=>true,()=>"",_=> { });
        registry.SetSavedDenominatorProvider(()=>new ulong[] {3});
        registry.TryRegister("disabled.owner","none",out _,out _,order:-1);
        registry.TryRegister("b.owner","later",out var later,out _,order:1);
        registry.TryRegister("a.owner","earlier",out var earlier,out _);
        Check(registry.Status.Registrations.Count==3 && registry.Status.Registrations[0].DesiredProfile==null &&
            registry.Status.Registrations[1].OwnerGuid=="a.owner" && registry.Status.Registrations[2].Order==1,
            "Status includes detached inactive registrations in deterministic composition order immediately after registration.");
        var caller=new Dictionary<eChimps,WorkerProductionValues> {
            [eChimps.CHIMP_TYPE_WOODCUTTER]=new WorkerProductionValues(ValueModifierContribution.Factor(0.5m),ValueModifierContribution.Factor(2)) };
        var first=new WorkerProductionProfile(caller);
        earlier.SetProfile(first); caller.Clear();
        later.SetProfile(BaseProfile(ValueModifierContribution.Factor(0.5m),ValueModifierContribution.FixedValue(3.25m)));
        registry.TryRegister("inactive.owner","campaign",out var inactive,out _,order:2);
        inactive.SetProfile(new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(100),
            activation:new GameplayModActivationProfile("inactive.owner","fixture",GameplayModAllowedContext.Campaign)));
        registry.Prepare(new GameModeSnapshot(GameModeKind.CustomGame));
        var snapshot=registry.Status;
        Check(snapshot.Registrations.Count==4 && ReferenceEquals(snapshot.Registrations[1].DesiredProfile,first) &&
            snapshot.Registrations[3].DesiredProfile!=null && snapshot.ResolvedFixedBonusPercent==50,
            "Caller collection mutation cannot change detached profiles and mode-inactive owners remain visible.");
        Check(snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out var quarter) &&
            quarter.Whole==0 && quarter.FractionNumerator==1 && quarter.FractionDenominator==4 && registry.CurrentPrepared.CommonDenominator==12,
            "Public resolved base exposes canonical quarter without leaking the table's saved-denominator scaling.");
        Check(snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,true,1,out var ai) &&
            ai.Whole==3 && ai.FractionNumerator==1 && ai.FractionDenominator==4,
            "Resolved role amounts distinguish AI fixed composition from the human factor chain.");
        Check(snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,4,out var integer) &&
            integer.Whole==1 && integer.FractionNumerator==0 && integer.FractionDenominator==1,
            "Integral resolved amounts use canonical fraction zero over one.");
        Check(!snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_QUARRY_OX,false,1,out var absent) &&
            absent.Whole==0 && absent.FractionNumerator==0 && absent.FractionDenominator==1 &&
            !snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,-1,out _) &&
            !snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,101,out _) &&
            !snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_MILLER,false,2,out _) &&
            snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_MILLER,false,1,out _),
            "Only supported actual-output roles and inputs have resolved values; absent values are canonical zero.");
        bool readOnly=false;
        try { ((IList<WorkerProductionRegistrationSnapshot>)snapshot.Registrations)[0]=snapshot.Registrations[1]; }
        catch(NotSupportedException) { readOnly=true; }
        Check(readOnly,"Public registration lists cannot be mutated by consumers.");
        registry.UpdatePeerReadiness("Pending","fixture"); registry.NotifyRejectedLocalLaunch("Unavailable");
        Check(ReferenceEquals(snapshot.Registrations,registry.Status.Registrations) &&
            registry.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out var copied) && copied.FractionDenominator==4,
            "Peer and local-launch status clones preserve registration and compiled-value snapshots.");
        earlier.SetProfile(null);
        Check(registry.Status.Registrations[1].DesiredProfile==null && snapshot.Registrations[1].DesiredProfile!=null &&
            snapshot.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out var retained) && retained.FractionDenominator==4 &&
            registry.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out var changed) && changed.FractionDenominator==2,
            "An old status keeps both desired owners and exact values after handle mutation.");
        var invalid=BaseProfile(ValueModifierContribution.FixedValue(-1)); earlier.SetProfile(invalid);
        Check(ReferenceEquals(registry.Status.Registrations[1].DesiredProfile,invalid) &&
            !registry.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out _),
            "Invalid desired profiles remain inspectable without presenting earlier valid resolved values.");
        earlier.SetProfile(first); registry.ReportRuntimeFault("fixture runtime failure");
        Check(registry.Status.Registrations.Count==4 && ReferenceEquals(registry.Status.Registrations[1].DesiredProfile,first) &&
            !registry.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out _),
            "Runtime-fault status preserves desired registrations and rejects executable-value queries.");
        registry.ClearRuntimeFault();
        registry.ConfigureBackend(()=>true,()=>"",_=>throw new InvalidOperationException("publication fixture"),()=>"fixture");
        var beforeFailure=registry.Status;
        Check(!registry.ActivatePrepared(registry.CurrentPrepared,out _) &&
            ReferenceEquals(beforeFailure.Registrations,registry.Status.Registrations) &&
            !registry.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out _),
            "Publication-failure clones retain desired snapshots without claiming a valid resolved request.");

        var unknown=new WorkerProductionModifierRegistry(()=>true,()=>"",_=> { });
        unknown.TryRegister("policy.owner","mode",out var policy,out _);
        policy.SetProfile(new WorkerProductionProfile(new Dictionary<eChimps,WorkerProductionValues> {
            [eChimps.CHIMP_TYPE_WOODCUTTER]=new WorkerProductionValues(ValueModifierContribution.Factor(0.25m),default) },
            activation:new GameplayModActivationProfile("policy.owner","fixture",GameplayModAllowedContext.CustomGame)));
        Check(unknown.Status.CanStart && unknown.Status.Registrations[0].DesiredProfile!=null &&
            !unknown.Status.TryGetResolvedBaseAmount(eChimps.CHIMP_TYPE_WOODCUTTER,false,1,out _),
            "A valid finite unknown-mode bundle never masquerades as one actual resolved base table.");
    }
}

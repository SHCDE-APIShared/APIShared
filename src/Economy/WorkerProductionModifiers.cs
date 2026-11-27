using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using APIShared.GameModes;
using APIShared.Modifiers;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>Separate contributions for human-owned and AI-owned output of one supported worker role.</summary>
    public readonly struct WorkerProductionValues
    {
        /// <summary>Creates immutable role contributions. None leaves that owner category unchanged.</summary>
        public WorkerProductionValues(ValueModifierContribution human, ValueModifierContribution ai)
        { Human = human; AI = ai; }
        /// <summary>Contribution for human-owned workers.</summary>
        public ValueModifierContribution Human { get; }
        /// <summary>Contribution for AI-owned workers.</summary>
        public ValueModifierContribution AI { get; }
    }

    /// <summary>A detached, immutable desired production profile. Quantities describe actual output, never ingredient withdrawal.</summary>
    /// <remarks>Supported roles are the 17 producer types in WorkerProductionModifiers.SupportedWorkers. Quarry Grunt
    /// means its Ox output. Both apple paths share one role. Baker means bread, not flour pickup; Miller means the
    /// actual Vanilla flour output of one, without its discarded wheat-pickup productivity calculation.
    /// Contributions compose by order/ordinal owner/registration ID using ValueModifierStage. The fixed bonus channel
    /// starts at 50, rounds once away from zero and must remain in -100..1000; it retains Vanilla's mode/flag rules.
    /// Base fractions accumulate exactly in production-owned saved state. Unsupported workers, negative or oversized
    /// results, and unrepresentable combined precision are reported as blocking issues; nothing is silently capped.
    /// An optional activation profile is evaluated against the prepared mission mode by the shared service.</remarks>
    public sealed class WorkerProductionProfile
    {
        /// <summary>Copies caller-owned inputs. Null baseAmounts means no base contribution; activation null permits every mode.</summary>
        public WorkerProductionProfile(IDictionary<eChimps, WorkerProductionValues> baseAmounts = null,
            ValueModifierContribution fixedBonusPercent = default, GameplayModActivationProfile? activation = null)
            : this(baseAmounts, fixedBonusPercent, activation, null) { }

        /// <summary>Copies the supplied base amounts and per-stage fear contributions into a detached profile.</summary>
        /// <remarks>Fear uses the native signed stages -5..-1 and 1..5. Negative stages have a Vanilla bonus
        /// of ten percentage points per step from neutral. Positive stages have a Vanilla bonus of zero.
        /// Neutral stays at zero. Values use the same ordered factor/fixed-value stages as the fixed bonus.
        /// A null map leaves the fear channel unchanged; invalid requests are reported during preparation.</remarks>
        public WorkerProductionProfile(IDictionary<eChimps, WorkerProductionValues> baseAmounts,
            ValueModifierContribution fixedBonusPercent, GameplayModActivationProfile? activation,
            IReadOnlyDictionary<int, ValueModifierContribution> fearBonusPercentByLevel)
        {
            var copy = new Dictionary<eChimps, WorkerProductionValues>();
            if (baseAmounts != null) foreach (var pair in baseAmounts) copy.Add(pair.Key, pair.Value);
            BaseAmounts = new ReadOnlyDictionary<eChimps, WorkerProductionValues>(copy);
            var fearCopy = new Dictionary<int, ValueModifierContribution>();
            if (fearBonusPercentByLevel != null)
                foreach (var pair in fearBonusPercentByLevel) fearCopy.Add(pair.Key, pair.Value);
            FearBonusPercentByLevel = new ReadOnlyDictionary<int, ValueModifierContribution>(fearCopy);
            FixedBonusPercent = fixedBonusPercent; Activation = activation;
        }
        /// <summary>Detached role contributions, indexed by producer type rather than unit ID.</summary>
        public IReadOnlyDictionary<eChimps, WorkerProductionValues> BaseAmounts { get; }
        /// <summary>Factor or absolute percentage points of the original fixed 50-percent contribution.</summary>
        public ValueModifierContribution FixedBonusPercent { get; }
        /// <summary>Detached desired factors or absolute fear bonus points, indexed by native fear stage.</summary>
        public IReadOnlyDictionary<int, ValueModifierContribution> FearBonusPercentByLevel { get; }
        internal bool HasFearContributions
        {
            get
            {
                foreach (var pair in FearBonusPercentByLevel)
                    if (!WorkerProductionCompiler.IsConfigurableFearLevel(pair.Key) || pair.Value.Kind != ValueModifierKind.None)
                        return true;
                return false;
            }
        }
        /// <summary>Optional caller-defined mission permissions; this never restricts another owner.</summary>
        public GameplayModActivationProfile? Activation { get; }
    }

    /// <summary>Stable machine-readable production admission failures.</summary>
    public enum WorkerProductionIssueCode
    {
        /// <summary>Required native protection has not initialized.</summary>
        NativeUnavailable,
        /// <summary>A requested producer type has no audited output contract.</summary>
        UnsupportedWorker,
        /// <summary>A base profile produces a negative exact amount.</summary>
        NegativeBaseAmount,
        /// <summary>A base profile exceeds the safe original native arithmetic domain.</summary>
        BaseAmountOutOfRange,
        /// <summary>The rounded fixed percentage is outside -100..1000.</summary>
        FixedBonusOutOfRange,
        /// <summary>Combined exact precision cannot be represented by the bounded native remainder format.</summary>
        PrecisionNotRepresentable,
        /// <summary>Saved production precision is invalid.</summary>
        SavedDenominatorInvalid,
        /// <summary>A mode-dependent profile needs a recognized prepared mode.</summary>
        ActivationUnknown,
        /// <summary>The native publication boundary rejected a prepared rule set.</summary>
        PublicationFailed,
        /// <summary>The world has encountered an out-of-contract runtime value.</summary>
        RuntimeFault,
        /// <summary>A fear stage is outside the configurable nonneutral stages.</summary>
        InvalidFearLevel,
        /// <summary>A composed fear bonus rounds outside -100..1000 percentage points.</summary>
        FearBonusOutOfRange,
        /// <summary>A fear bonus and an applicable fixed bonus can total less than -100 percentage points.</summary>
        CombinedBonusOutOfRange
    }

    /// <summary>Issue impact. Errors block admission; warnings are informational.</summary>
    public enum WorkerProductionIssueSeverity
    {
        /// <summary>An informational issue.</summary>
        Warning,
        /// <summary>The requested production state must not start.</summary>
        Error
    }

    /// <summary>Immutable structured issue. Consumers localize LocalizationCode and its typed/value arguments.</summary>
    public sealed class WorkerProductionIssue
    {
        internal WorkerProductionIssue(WorkerProductionIssueCode code, string diagnostic, string owner = null, string id = null,
            eChimps? worker = null, bool? ai = null, int? input = null, string actual = null, string limit = null,
            int? fearLevel = null)
        { Code=code; Diagnostic=diagnostic ?? string.Empty; OwnerGuid=owner ?? string.Empty; RegistrationId=id ?? string.Empty;
          WorkerType=worker; IsAIPlayer=ai; InputAmount=input; ActualValue=actual ?? string.Empty; Limit=limit ?? string.Empty;
          FearLevel=fearLevel; }
        /// <summary>Stable failure category.</summary>
        public WorkerProductionIssueCode Code { get; }
        /// <summary>All current validation failures are admission-blocking errors.</summary>
        public WorkerProductionIssueSeverity Severity => WorkerProductionIssueSeverity.Error;
        /// <summary>Stable localization key: WorkerProduction followed by the issue code.</summary>
        public string LocalizationCode => "WorkerProduction." + Code;
        /// <summary>Responsible owner, or empty for a combined/service failure.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local identity, or empty for a combined/service failure.</summary>
        public string RegistrationId { get; }
        /// <summary>Affected producer role, when applicable.</summary>
        public eChimps? WorkerType { get; }
        /// <summary>Affected ownership category, when applicable.</summary>
        public bool? IsAIPlayer { get; }
        /// <summary>Supported pre-transform input that demonstrates the failure.</summary>
        public int? InputAmount { get; }
        /// <summary>The native signed fear stage associated with this issue, when applicable.</summary>
        public int? FearLevel { get; }
        /// <summary>Exact invariant-format offending value, when applicable.</summary>
        public string ActualValue { get; }
        /// <summary>Invariant-format required bound, when applicable.</summary>
        public string Limit { get; }
        /// <summary>Technical detail for a diagnostic view; ordinary player text should use LocalizationCode.</summary>
        public string Diagnostic { get; }
    }

    /// <summary>Detached desired state of one process-held registration at a status publication.</summary>
    public sealed class WorkerProductionRegistrationSnapshot
    {
        internal WorkerProductionRegistrationSnapshot(string owner, string id, int order, WorkerProductionProfile profile)
        { OwnerGuid=owner; RegistrationId=id; Order=order; DesiredProfile=profile; }
        /// <summary>Arbitrary mod GUID that owns this registration.</summary>
        public string OwnerGuid { get; }
        /// <summary>Owner-local registration identity.</summary>
        public string RegistrationId { get; }
        /// <summary>Composition priority, followed by ordinal owner GUID and registration ID.</summary>
        public int Order { get; }
        /// <summary>Immutable desired profile, including invalid or mode-inactive requests; null means disabled.</summary>
        public WorkerProductionProfile DesiredProfile { get; }
    }

    /// <summary>Exact resolved output base: Whole plus a canonical proper fraction, before native productivity/bonus.</summary>
    public readonly struct WorkerProductionResolvedBaseAmount
    {
        private readonly ulong denominator;
        internal WorkerProductionResolvedBaseAmount(uint whole, ulong numerator, ulong denominator)
        { Whole=whole; FractionNumerator=numerator; this.denominator=denominator; }
        /// <summary>Whole output units before accumulated per-unit fractional credit.</summary>
        public uint Whole { get; }
        /// <summary>Reduced nonnegative numerator, smaller than FractionDenominator.</summary>
        public ulong FractionNumerator { get; }
        /// <summary>Positive reduced denominator; integral amounts, including the default value, use 1.</summary>
        public ulong FractionDenominator => denominator==0 ? 1 : denominator;
    }

    /// <summary>Immutable desired-state/preflight status. This is not an acknowledgement that a mission has started.</summary>
    public sealed class WorkerProductionStatus
    {
        internal WorkerProductionStatus(long revision, bool available, bool desired, bool hasBase, int bonus,
            string digest, string key, IEnumerable<WorkerProductionIssue> issues, string peerCode=null, string peerDiagnostic=null,
            long rejectedLocalSequence=0,string rejectedLocalCode=null,
            IReadOnlyList<WorkerProductionRegistrationSnapshot> registrations=null,WorkerProductionCompiledRules resolvedRules=null)
        { Revision=revision; IsAvailable=available; HasDesiredProfiles=desired; HasBaseContributions=hasBase;
          ResolvedFixedBonusPercent=bonus; Digest=digest ?? string.Empty; ServiceKey=key ?? string.Empty;
          Issues=Array.AsReadOnly(new List<WorkerProductionIssue>(issues).ToArray());
          PeerReadinessCode=peerCode ?? string.Empty; PeerReadinessDiagnostic=peerDiagnostic ?? string.Empty;
          RejectedLocalLaunchSequence=rejectedLocalSequence; RejectedLocalLaunchCode=rejectedLocalCode ?? string.Empty;
          Registrations=registrations ?? Array.AsReadOnly(Array.Empty<WorkerProductionRegistrationSnapshot>());
          ResolvedRules=resolvedRules; }
        internal WorkerProductionCompiledRules ResolvedRules { get; }
        /// <summary>Monotonic status revision; an admitted prepared object remains bound to this revision.</summary>
        public long Revision { get; }
        /// <summary>Whether the backend required by this request is available, including the complete production
        /// cohort for base rules, effective fear rules or retained production state.</summary>
        public bool IsAvailable { get; }
        /// <summary>True when the prepared request has no blocking issue. Multiplayer agreement is separate.</summary>
        public bool CanStart => Issues.Count == 0;
        /// <summary>Whether any owner retains a non-null desired profile, including an invalid one.</summary>
        public bool HasDesiredProfiles { get; }
        /// <summary>Whether the resolved request includes any base contribution, including identity factors.</summary>
        public bool HasBaseContributions { get; }
        /// <summary>Rounded resolved fixed bonus; an invalid bonus retains 50 here and exposes its failure in Issues.
        /// Other channel or availability failures do not replace a successfully resolved bonus value.</summary>
        public int ResolvedFixedBonusPercent { get; }
        /// <summary>Canonical digest of the executable prepared arithmetic, or empty when invalid.</summary>
        public string Digest { get; }
        /// <summary>Backend compatibility identity, or empty while unavailable.</summary>
        public string ServiceKey { get; }
        /// <summary>Detached structured failures; no failure is only logged.</summary>
        public IReadOnlyList<WorkerProductionIssue> Issues { get; }
        /// <summary>Detached registration snapshots in composition order, including disabled and mode-inactive owners.
        /// Later handle/profile changes never modify an earlier status or its desired profiles.</summary>
        public IReadOnlyList<WorkerProductionRegistrationSnapshot> Registrations { get; }
        /// <summary>Reads the prepared fear bonus, in whole percentage points, for a native stage -5..5.
        /// Returns false with zero for an invalid request, unresolved mode or unsupported stage. A successful
        /// lookup includes Vanilla values for stages with no active contribution and zero for the neutral stage.
        /// This describes the prepared rules, independently of a mission's already active rules.</summary>
        public bool TryGetResolvedFearBonusPercent(int fearLevel, out int percent)
        {
            percent=0;
            if (!CanStart || ResolvedRules==null || fearLevel < -WorkerProductionCompiler.MaximumFearLevel ||
                fearLevel > WorkerProductionCompiler.MaximumFearLevel) return false;
            percent=ResolvedRules.FearBonusByLevel[fearLevel+WorkerProductionCompiler.MaximumFearLevel];
            return true;
        }
        /// <summary>Reads the exact prepared base before productivity, fixed bonus and per-unit credits; no caller
        /// composition is required. Returns false for an invalid status, unresolved mode-dependent request,
        /// unsupported worker or input outside 0..100. Miller supports only its actual baseline input 1.
        /// A failed lookup returns the default zero amount. This does not describe a mission's already active rules.</summary>
        public bool TryGetResolvedBaseAmount(eChimps worker, bool isAIPlayer, int inputAmount,
            out WorkerProductionResolvedBaseAmount amount)
        {
            amount=default;
            if(!CanStart || ResolvedRules==null || !WorkerProductionCompiler.IsWorker(worker) || inputAmount<0 ||
                inputAmount>WorkerProductionCompiler.MaximumInput || worker==eChimps.CHIMP_TYPE_MILLER && inputAmount!=1) return false;
            var entry=ResolvedRules.GetEntry(worker,isAIPlayer,inputAmount);
            ulong numerator=entry.Numerator, denominator=entry.Denominator;
            if(numerator==0) denominator=1;
            else
            {
                ulong a=numerator,b=denominator;
                while(b!=0) { ulong next=a%b; a=b; b=next; }
                numerator/=a; denominator/=a;
            }
            amount=new WorkerProductionResolvedBaseAmount(entry.Whole,numerator,denominator); return true;
        }
        /// <summary>Independent peer-admission presentation code; empty means no outstanding peer warning.</summary>
        public string PeerReadinessCode { get; }
        /// <summary>Technical peer-readiness detail. Peer changes do not invalidate the prepared arithmetic revision.</summary>
        public string PeerReadinessDiagnostic { get; }
        /// <summary>Monotonic local rejected-launch notification sequence, independent of the arithmetic revision.</summary>
        public long RejectedLocalLaunchSequence { get; }
        /// <summary>Localization/admission code of the latest actual local launch rejection; empty before the first rejection.</summary>
        public string RejectedLocalLaunchCode { get; }
    }

    /// <summary>Process-held desired profile. Registrations are never disposed or unpatched.</summary>
    public sealed class WorkerProductionModifierRegistration
    {
        internal readonly WorkerProductionModifierRegistry Registry;
        internal readonly int Order;
        internal WorkerProductionProfile Profile;
        internal WorkerProductionModifierRegistration(WorkerProductionModifierRegistry registry, string owner, string id, int order)
        { Registry=registry; OwnerGuid=owner; RegistrationId=id; Order=order; }
        /// <summary>Stable arbitrary mod GUID, ordered ordinally after priority.</summary>
        public string OwnerGuid { get; }
        /// <summary>Stable owner-local identity.</summary>
        public string RegistrationId { get; }
        /// <summary>Replaces desired state; null disables this owner's contribution. Invalid desired profiles remain visible
        /// and block start rather than revealing an earlier valid profile. Call at a synchronized settings/mission boundary.
        /// This cold operation never activates native rules or dispatches UI work.</summary>
        public void SetProfile(WorkerProductionProfile profile) => Registry.SetProfile(this, profile);
    }

    /// <summary>Shared owner of exact output-base and fixed-bonus channels, with explicit mission admission.</summary>
    /// <remarks>Registering desired state is allowed before native initialization. Preparation composes immutable rules;
    /// the mission coordinator activates exactly that prepared revision after availability/save/peer checks. No per-worker
    /// managed callback, configuration lock, decimal arithmetic or UI work is added to production. Status observers run
    /// outside registry locks on the caller thread, are exception-isolated, and must dispatch their own UI updates.</remarks>
    public static class WorkerProductionModifiers
    {
        internal static readonly WorkerProductionModifierRegistry Registry = new WorkerProductionModifierRegistry(
            () => false, () => "The complete production backend has not initialized.", _ => {
                throw new InvalidOperationException("The complete production backend has not initialized.");
            }, legacyAvailable:()=>WorkerProductionBonusNativeRuntime.IsAvailable,
            legacyPublish:rules=> {
                if(rules.NeedsFullBackend) throw new InvalidOperationException("The complete production backend is required for base or fear contributions.");
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);
            });
        /// <summary>Audited output-role types, including Quarry Grunt rather than its transport Ox.</summary>
        public static IReadOnlyList<eChimps> SupportedWorkers => WorkerProductionCompiler.WorkerTypes;
        /// <summary>Latest immutable desired-state status.</summary>
        public static WorkerProductionStatus Status => Registry.Status;
        /// <summary>Registers initially inactive desired state. Only invalid/duplicate identities fail; unavailable
        /// protection is visible in Status after a profile is supplied.</summary>
        public static bool TryRegister(string ownerGuid, string registrationId, out WorkerProductionModifierRegistration registration,
            out string diagnostic, int order=0) => Registry.TryRegister(ownerGuid, registrationId, out registration, out diagnostic, order);
        /// <summary>Registers a process-lived status observer. Optional replay is synchronous on this caller thread;
        /// later calls run on the preparation/mutation thread. Exceptions are isolated. Duplicate identities fail.</summary>
        public static bool TryRegisterObserver(string ownerGuid, string registrationId, Action<WorkerProductionStatus> observer,
            out string diagnostic, int order=0, bool replayCurrent=true) =>
            Registry.TryRegisterObserver(ownerGuid, registrationId, observer, out diagnostic, order, replayCurrent);

        internal static WorkerProductionCompiledRules CurrentPrepared => Registry.CurrentPrepared;
        internal static WorkerProductionLaunchBundle GetLaunchBundle() => Registry.GetLaunchBundle();
        internal static WorkerProductionLaunchBundle PrepareLaunchBundle() => Registry.GetLaunchBundle();
        internal static bool IsCandidateInBundle(GameModeSnapshot actualMode,WorkerProductionCompiledRules candidate) =>
            Registry.GetLaunchBundle().Contains(actualMode,candidate);
        internal static bool IsCandidateInBundle(WorkerProductionLaunchBundle admitted,GameModeSnapshot actualMode,
            WorkerProductionCompiledRules candidate) => admitted!=null && admitted.Digest==Registry.GetLaunchBundle().Digest && admitted.Contains(actualMode,candidate);
        internal static bool DesiredHasBaseContributions => Registry.DesiredHasBaseContributions;
        internal static WorkerProductionStatus Prepare(GameModeSnapshot mode) => Registry.Prepare(mode);
        internal static bool ActivatePrepared(WorkerProductionCompiledRules expected, out string diagnostic) => Registry.ActivatePrepared(expected, out diagnostic);
        internal static void ConfigureBackend(Func<bool> available, Func<string> diagnostic, Action<WorkerProductionCompiledRules> publish,
            Func<string> serviceKey, Func<bool> legacyAvailable=null, Action<WorkerProductionCompiledRules> legacyPublish=null,
            object legacyPublicationMonitor=null) =>
            Registry.ConfigureBackend(available, diagnostic, publish, serviceKey, legacyAvailable, legacyPublish,legacyPublicationMonitor);
        internal static void SetSavedDenominatorProvider(Func<IEnumerable<ulong>> provider) => Registry.SetSavedDenominatorProvider(provider);
        internal static bool TryRefreshSavedDenominators(out string diagnostic) => Registry.TryRefreshSavedDenominators(out diagnostic);
        internal static void ReportRuntimeFault(string diagnostic) => Registry.ReportRuntimeFault(diagnostic);
        internal static void ClearRuntimeFault() => Registry.ClearRuntimeFault();
        internal static void UpdatePeerReadiness(string code,string diagnostic) => Registry.UpdatePeerReadiness(code,diagnostic);
        internal static void NotifyRejectedLocalLaunch(string code) => Registry.NotifyRejectedLocalLaunch(code);
    }
}

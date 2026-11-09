using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using APIShared.Events;
using APIShared.GameModes;
using APIShared.Modifiers;

namespace APIShared.Economy
{
    internal sealed class WorkerProductionModifierRegistry
    {
        private sealed class Observer
        {
            internal string Owner,Id; internal int Order; internal Action<WorkerProductionStatus> Callback;
            internal static readonly Func<Observer,RegistrationKey> Key=entry=>new RegistrationKey(entry.Owner,entry.Id,entry.Order);
        }
        private readonly object sync=new object();
        private object legacyPublication=new object();
        private readonly List<WorkerProductionModifierRegistration> registrations=new List<WorkerProductionModifierRegistration>();
        private readonly List<Observer> observers=new List<Observer>();
        private Func<bool> available,legacyAvailable;
        private Func<string> diagnostic,serviceKey;
        private Action<WorkerProductionCompiledRules> publish,legacyPublish;
        private Func<IEnumerable<ulong>> savedDenominators=()=>Array.Empty<ulong>();
        private GameModeSnapshot mode;
        private WorkerProductionStatus status;
        private WorkerProductionCompiledRules prepared;
        private string signature, runtimeFault;
        private long revision;
        private long desiredRevision;
        private bool hasSavedPrecision,requiresBackend;
        private WorkerProductionLaunchBundle launchBundle;
        private string bundleSignature;

        internal WorkerProductionModifierRegistry(Func<bool> available,Func<string> diagnostic,
            Action<WorkerProductionCompiledRules> publish,Func<string> serviceKey=null,
            Func<bool> legacyAvailable=null,Action<WorkerProductionCompiledRules> legacyPublish=null)
        {
            this.available=available ?? throw new ArgumentNullException(nameof(available));
            this.diagnostic=diagnostic ?? throw new ArgumentNullException(nameof(diagnostic));
            this.publish=publish ?? throw new ArgumentNullException(nameof(publish));
            this.serviceKey=serviceKey ?? (()=>string.Empty);
            this.legacyAvailable=legacyAvailable ?? available; this.legacyPublish=legacyPublish ?? publish;
            status=new WorkerProductionStatus(0,false,false,false,50,string.Empty,string.Empty,new List<WorkerProductionIssue>());
        }
        internal WorkerProductionStatus Status => Volatile.Read(ref status);
        internal WorkerProductionCompiledRules CurrentPrepared { get { lock(sync) return prepared; } }
        internal bool DesiredHasBaseContributions
        {
            get { lock(sync) foreach(var registration in registrations) if(registration.Profile!=null)
                foreach(var values in registration.Profile.BaseAmounts.Values)
                    if(values.Human.Kind!=ValueModifierKind.None || values.AI.Kind!=ValueModifierKind.None) return true;
                return false; }
        }
        internal bool LegacyAvailable { get { try { return legacyAvailable(); } catch { return false; } } }
        internal string LegacyDiagnostic { get { try { return diagnostic(); } catch(Exception error) { return error.Message; } } }

        internal bool TryRegister(string owner,string id,out WorkerProductionModifierRegistration registration,out string reason,int order=0)
        {
            registration=null;
            if(string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id))
            { reason="Owner GUID and registration ID are required."; return false; }
            GameModeSnapshot current;
            lock(sync)
            {
                foreach(var item in registrations) if(item.OwnerGuid==owner && item.RegistrationId==id)
                { reason="Duplicate worker-production owner/registration ID."; return false; }
                registration=new WorkerProductionModifierRegistration(this,owner,id,order); registrations.Add(registration);
                registrations.Sort(Compare);
                desiredRevision++; prepared=null; launchBundle=null; bundleSignature=null; current=mode;
            }
            Prepare(current);
            reason=string.Empty; return true;
        }
        private IReadOnlyList<WorkerProductionRegistrationSnapshot> CaptureRegistrations()
        {
            // Caller holds sync. Profiles have already copied caller-owned collections.
            var values=new WorkerProductionRegistrationSnapshot[registrations.Count];
            for(int i=0;i<values.Length;i++)
            {
                var item=registrations[i];
                values[i]=new WorkerProductionRegistrationSnapshot(item.OwnerGuid,item.RegistrationId,item.Order,item.Profile);
            }
            return Array.AsReadOnly(values);
        }
        private static int Compare(WorkerProductionModifierRegistration a,WorkerProductionModifierRegistration b) =>
            RegistrationKey.Compare(new RegistrationKey(a.OwnerGuid,a.RegistrationId,a.Order),new RegistrationKey(b.OwnerGuid,b.RegistrationId,b.Order));
        internal bool TryRegisterObserver(string owner,string id,Action<WorkerProductionStatus> observer,out string reason,
            int order=0,bool replayCurrent=true)
        {
            if(string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(id) || observer==null)
            { reason="An observer owner, registration ID and callback are required."; return false; }
            lock(sync)
            {
                foreach(var item in observers) if(item.Owner==owner && item.Id==id)
                { reason="Duplicate worker-production observer identity."; return false; }
                observers.Add(new Observer { Owner=owner,Id=id,Order=order,Callback=observer });
                observers.Sort((a,b)=>RegistrationKey.Compare(Observer.Key(a),Observer.Key(b)));
            }
            if(replayCurrent) Invoke(observer,Status);
            reason=string.Empty; return true;
        }
        internal void SetProfile(WorkerProductionModifierRegistration registration,WorkerProductionProfile profile)
        {
            CheckOwner(registration);
            GameModeSnapshot current;
            lock(sync)
            {
                registration.Profile=profile; current=mode; desiredRevision++;
                prepared=null; launchBundle=null; bundleSignature=null;
            }
            Prepare(current);
        }

        // Capture a state-provider snapshot before the registry lock: sidecar ownership may use the
        // engine monitor, whose order must never be reversed by a cold UI/configuration writer.
        internal WorkerProductionStatus Prepare(GameModeSnapshot snapshot)
        {
            List<ulong> saved=null; WorkerProductionIssue providerIssue=null;
            Func<IEnumerable<ulong>> provider; lock(sync) provider=savedDenominators;
            try { saved=new List<ulong>(provider() ?? throw new InvalidOperationException("Saved denominator provider returned null.")); }
            catch(Exception error) { providerIssue=new WorkerProductionIssue(WorkerProductionIssueCode.SavedDenominatorInvalid,error.Message); }
            WorkerProductionStatus next; Observer[] listeners=null;
            lock(sync)
            {
                mode=snapshot;
                var contributions=new List<WorkerProductionProfileContribution>(); bool desired=false;
                foreach(var item in registrations)
                { desired|=item.Profile!=null; contributions.Add(new WorkerProductionProfileContribution(item.OwnerGuid,item.RegistrationId,item.Profile)); }
                bool modeDependent=false;
                foreach(var item in contributions) modeDependent|=item.Profile?.Activation.HasValue==true;
                var allModes=GetLaunchBundleLocked(contributions,saved,providerIssue);
                WorkerProductionLaunchBundle bundle=snapshot.Kind==GameModeKind.Unknown && modeDependent
                    ? allModes : null;
                var compilation=bundle==null ? WorkerProductionCompiler.Compile(contributions,snapshot,saved) :
                    new WorkerProductionCompilation { HasBaseContributions=bundle.HasBaseContributions,
                        HasActiveContributions=bundle.HasActiveContributions,Bonus=bundle.ResolvedFixedBonusPercentPreview };
                if(bundle!=null) compilation.Issues.AddRange(bundle.Issues);
                if(providerIssue!=null && bundle==null) compilation.Issues.Add(providerIssue);
                hasSavedPrecision=saved!=null && saved.Count!=0;
                requiresBackend=compilation.HasActiveContributions || hasSavedPrecision;
                bool ready=false; string key=string.Empty,unavailable=string.Empty;
                try
                {
                    bool needsFull=compilation.HasBaseContributions || compilation.Rules?.NeedsFullBackend==true ||
                        bundle?.NeedsFullBackend==true || hasSavedPrecision;
                    ready=needsFull ? available() : legacyAvailable();
                    if(ready) key=serviceKey() ?? string.Empty; else unavailable=diagnostic();
                }
                catch(Exception error) { unavailable=error.Message; }
                if(!ready && requiresBackend && bundle==null)
                    compilation.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.NativeUnavailable,
                        "Worker production protection is unavailable: "+unavailable));
                if(runtimeFault!=null && bundle==null) compilation.Issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.RuntimeFault,runtimeFault));
                string digest=bundle!=null ? bundle.Digest : compilation.Issues.Count==0 && compilation.Rules!=null ? compilation.Rules.Digest : string.Empty;
                string nextSignature=Signature(ready,desired,compilation.HasBaseContributions,compilation.Bonus,
                    bundle?.Digest ?? compilation.Rules?.Digest ?? string.Empty,key,compilation.Issues)+"|desired:"+desiredRevision;
                if(signature==nextSignature) return status;
                signature=nextSignature; revision++;
                prepared=compilation.Rules;
                next=new WorkerProductionStatus(revision,ready,desired,compilation.HasBaseContributions,compilation.Bonus,digest,key,
                    compilation.Issues,status.PeerReadinessCode,status.PeerReadinessDiagnostic,status.RejectedLocalLaunchSequence,status.RejectedLocalLaunchCode,
                    CaptureRegistrations(),compilation.Rules);
                Volatile.Write(ref status,next); listeners=observers.ToArray();
            }
            foreach(var listener in listeners) Invoke(listener.Callback,next);
            return next;
        }

        internal bool ActivatePrepared(WorkerProductionCompiledRules expected,out string reason)
        {
            Action<WorkerProductionCompiledRules> publisher;
            lock(sync)
            {
                if(expected==null || !ReferenceEquals(expected,prepared) || !status.CanStart)
                { reason="The production request is invalid, unavailable, or no longer the prepared revision."; return false; }
                if(!status.IsAvailable && !requiresBackend) { reason=string.Empty; return true; }
                publisher=publish;
            }
            try { publisher(expected); reason=string.Empty; return true; }
            catch(Exception error) { PublishFailure(error.Message); reason=error.Message; return false; }
        }

        internal WorkerProductionLaunchBundle GetLaunchBundle()
        {
            Func<IEnumerable<ulong>> provider;
            lock(sync) { if(launchBundle!=null) return launchBundle; provider=savedDenominators; }
            List<ulong> saved=null; WorkerProductionIssue issue=null;
            try { saved=new List<ulong>(provider() ?? throw new InvalidOperationException("Saved denominator provider returned null.")); }
            catch(Exception error) { issue=new WorkerProductionIssue(WorkerProductionIssueCode.SavedDenominatorInvalid,error.Message); }
            lock(sync)
            {
                var values=new List<WorkerProductionProfileContribution>();
                foreach(var item in registrations) values.Add(new WorkerProductionProfileContribution(item.OwnerGuid,item.RegistrationId,item.Profile));
                return GetLaunchBundleLocked(values,saved,issue);
            }
        }
        private WorkerProductionLaunchBundle GetLaunchBundleLocked(IReadOnlyList<WorkerProductionProfileContribution> values,
            List<ulong> saved,WorkerProductionIssue providerIssue)
        {
            bool full=false,fixedReady=false; string key=string.Empty,detail=string.Empty;
            try { full=available(); fixedReady=legacyAvailable(); key=serviceKey() ?? string.Empty; detail=diagnostic() ?? string.Empty; }
            catch(Exception error) { detail=error.Message; }
            if(saved!=null) saved.Sort();
            string cache=desiredRevision+"|"+full+"|"+fixedReady+"|"+key+"|"+detail+"|"+runtimeFault+"|"+providerIssue?.Diagnostic;
            if(saved!=null) foreach(ulong denominator in saved) cache+="/"+denominator;
            if(launchBundle!=null && bundleSignature==cache) return launchBundle;
            var result=WorkerProductionLaunchBundleCompiler.Compile(values,saved,full,()=>fixedReady,key,detail,providerIssue);
            if(runtimeFault!=null)
            {
                var issues=new List<WorkerProductionIssue>(result.Issues) { new WorkerProductionIssue(WorkerProductionIssueCode.RuntimeFault,runtimeFault) };
                result=new WorkerProductionLaunchBundle(result.Members,issues,
                    result.HasBaseContributions,result.HasActiveContributions,result.HasDesiredProfiles,result.IsAvailable,
                    result.ServiceKey,result.ResolvedFixedBonusPercentPreview);
            }
            bundleSignature=cache; launchBundle=result; return result;
        }

        internal void ConfigureBackend(Func<bool> isAvailable,Func<string> getDiagnostic,Action<WorkerProductionCompiledRules> publisher,
            Func<string> getServiceKey,Func<bool> isLegacyAvailable=null,Action<WorkerProductionCompiledRules> legacyPublisher=null,
            object legacyPublicationMonitor=null)
        {
            if(isAvailable==null || getDiagnostic==null || publisher==null || getServiceKey==null) throw new ArgumentNullException(nameof(publisher));
            GameModeSnapshot current;
            lock(sync)
            {
                available=isAvailable; diagnostic=getDiagnostic; publish=publisher; serviceKey=getServiceKey;
                if(isLegacyAvailable!=null) legacyAvailable=isLegacyAvailable;
                if(legacyPublisher!=null) legacyPublish=legacyPublisher;
                // The real runtime supplies its existing engine monitor. Legacy callers then
                // acquire Engine -> Registry, including provider capture and publication, never
                // a private serialization lock followed by Engine. Pure fixtures need no engine.
                if(legacyPublicationMonitor!=null) Volatile.Write(ref legacyPublication,legacyPublicationMonitor);
                launchBundle=null; bundleSignature=null;
                current=mode;
            }
            Prepare(current);
        }
        internal void SetSavedDenominatorProvider(Func<IEnumerable<ulong>> provider)
        { if(provider==null) throw new ArgumentNullException(nameof(provider)); lock(sync)
            {
                savedDenominators=provider; prepared=null; launchBundle=null; bundleSignature=null;
                // A new selected archive invalidates admission even when its denominators
                // leave arithmetic unchanged. Prepare must rebuild the candidate instead
                // of returning an equal old status with the candidate still cleared.
                signature=null;
            } }
        internal bool TryRefreshSavedDenominators(out string reason)
        {
            GameModeSnapshot current; lock(sync) current=mode;
            var value=Prepare(current);
            reason=value.CanStart ? string.Empty : JoinIssues(value.Issues);
            return value.CanStart;
        }
        internal void ReportRuntimeFault(string detail)
        { GameModeSnapshot current; lock(sync) { runtimeFault=detail ?? "Production runtime fault."; current=mode; } Prepare(current); }
        internal void ClearRuntimeFault()
        { GameModeSnapshot current; lock(sync) { runtimeFault=null; current=mode; } Prepare(current); }
        internal void UpdatePeerReadiness(string code,string detail)
        {
            code=code ?? string.Empty; detail=detail ?? string.Empty;
            WorkerProductionStatus next; Observer[] listeners;
            lock(sync)
            {
                if(status.PeerReadinessCode==code && status.PeerReadinessDiagnostic==detail) return;
                // Peer receipts are presentation/admission state, not a new arithmetic preparation.
                next=new WorkerProductionStatus(status.Revision,status.IsAvailable,status.HasDesiredProfiles,
                    status.HasBaseContributions,status.ResolvedFixedBonusPercent,status.Digest,status.ServiceKey,
                    status.Issues,code,detail,status.RejectedLocalLaunchSequence,status.RejectedLocalLaunchCode,status.Registrations,status.ResolvedRules);
                Volatile.Write(ref status,next); listeners=observers.ToArray();
            }
            foreach(var listener in listeners) Invoke(listener.Callback,next);
        }
        internal void NotifyRejectedLocalLaunch(string code)
        {
            WorkerProductionStatus next; Observer[] listeners;
            lock(sync)
            {
                long sequence=status.RejectedLocalLaunchSequence==long.MaxValue ? long.MaxValue : status.RejectedLocalLaunchSequence+1;
                next=new WorkerProductionStatus(status.Revision,status.IsAvailable,status.HasDesiredProfiles,
                    status.HasBaseContributions,status.ResolvedFixedBonusPercent,status.Digest,status.ServiceKey,status.Issues,
                    status.PeerReadinessCode,status.PeerReadinessDiagnostic,sequence,code,status.Registrations,status.ResolvedRules);
                Volatile.Write(ref status,next); listeners=observers.ToArray();
            }
            foreach(var listener in listeners) Invoke(listener.Callback,next);
        }

        // The compatibility facade retains its old immediate-publication and failure rollback contract.
        // It still contributes one ordered absolute fixed value, including an explicit value of 50.
        internal void SetLegacy(WorkerProductionModifierRegistration registration,int? percent)
        {
            CheckOwner(registration);
            lock(Volatile.Read(ref legacyPublication))
            {
                if(!LegacyAvailable) throw new InvalidOperationException("Worker production bonus unavailable: "+LegacyDiagnostic);
                WorkerProductionProfile old; GameModeSnapshot current;
                lock(sync) { old=registration.Profile; registration.Profile=percent.HasValue
                    ? new WorkerProductionProfile(fixedBonusPercent:ValueModifierContribution.FixedValue(percent.Value)) : null;
                    current=mode; desiredRevision++; prepared=null; launchBundle=null; bundleSignature=null; }
                try
                {
                    var value=Prepare(current); WorkerProductionCompiledRules candidate; Action<WorkerProductionCompiledRules> publisher;
                    bool onlyLegacyAvailabilityFailure=true;
                    foreach(var issue in value.Issues) if(issue.Code!=WorkerProductionIssueCode.NativeUnavailable) onlyLegacyAvailabilityFailure=false;
                    lock(sync)
                    {
                        candidate=prepared; publisher=legacyPublish;
                        // No mode-dependent plan is activated in a lobby. Its exact finite bundle is
                        // already committed as desired state; the real load must select its member.
                        if(candidate==null && current.Kind==GameModeKind.Unknown && launchBundle?.CanStart==true) return;
                        if(candidate==null || (!value.CanStart && (!onlyLegacyAvailabilityFailure || candidate.NeedsFullBackend || hasSavedPrecision)))
                            throw new InvalidOperationException(JoinIssues(value.Issues));
                    }
                    publisher(candidate);
                }
                catch
                {
                    lock(sync) { registration.Profile=old; desiredRevision++; launchBundle=null; bundleSignature=null; }
                    Prepare(current); throw;
                }
            }
        }
        private void CheckOwner(WorkerProductionModifierRegistration registration)
        { if(registration==null || !ReferenceEquals(registration.Registry,this)) throw new ArgumentException("Registration belongs to another production registry."); }
        private void PublishFailure(string detail)
        {
            WorkerProductionStatus next; Observer[] listeners;
            lock(sync)
            {
                var issues=new List<WorkerProductionIssue>(status.Issues) { new WorkerProductionIssue(WorkerProductionIssueCode.PublicationFailed,detail) };
                signature=null; revision++;
                next=new WorkerProductionStatus(revision,status.IsAvailable,status.HasDesiredProfiles,status.HasBaseContributions,
                    status.ResolvedFixedBonusPercent,string.Empty,status.ServiceKey,issues,status.PeerReadinessCode,status.PeerReadinessDiagnostic,
                    status.RejectedLocalLaunchSequence,status.RejectedLocalLaunchCode,status.Registrations,status.ResolvedRules);
                Volatile.Write(ref status,next); listeners=observers.ToArray();
            }
            foreach(var listener in listeners) Invoke(listener.Callback,next);
        }
        private static void Invoke(Action<WorkerProductionStatus> observer,WorkerProductionStatus value)
        { try { observer(value); } catch { /* Consumer UI/diagnostics must not prevent admission diagnostics. */ } }
        private static string JoinIssues(IReadOnlyList<WorkerProductionIssue> issues)
        { var text=new StringBuilder(); foreach(var issue in issues) { if(text.Length!=0) text.Append("; "); text.Append(issue.Diagnostic); } return text.ToString(); }
        private static string Signature(bool ready,bool desired,bool hasBase,int bonus,string digest,string key,IReadOnlyList<WorkerProductionIssue> issues)
        {
            var value=new StringBuilder().Append(ready).Append('|').Append(desired).Append('|').Append(hasBase).Append('|').Append(bonus)
                .Append('|').Append(digest).Append('|').Append(key);
            foreach(var issue in issues) value.Append('|').Append(issue.Code).Append(':').Append(issue.OwnerGuid).Append(':').Append(issue.RegistrationId)
                .Append(':').Append(issue.WorkerType).Append(':').Append(issue.IsAIPlayer).Append(':').Append(issue.InputAmount).Append(':').Append(issue.FearLevel)
                .Append(':').Append(issue.ActualValue).Append(':').Append(issue.Limit).Append(':').Append(issue.Diagnostic);
            return value.ToString();
        }
    }
}

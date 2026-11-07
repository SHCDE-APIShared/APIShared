using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.GameModes;
using APIShared.Internal;
using BepInEx.Logging;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    // The single mission owner connects cold desired rules to permanent native readers.
    // Lobby receipts commit a finite rule bundle; real mode evidence selects its member.
    internal static unsafe class WorkerProductionRuntime
    {
        private static readonly IntPtr configurationSlot=AllocateSlot();
        private static readonly Dictionary<string,IntPtr> nativePlans=new Dictionary<string,IntPtr>(StringComparer.Ordinal);
        private static readonly WorkerProductionStateRequirement noSavedState=new WorkerProductionStateRequirement(true,false,0,"","");
        private static ManualLogSource log;
        private static IMissionLifecycleCapability missions;
        private static WorkerProductionLaunchBundle frozenBundle;
        private static WorkerProductionLaunchBundle observedBundleReference;
        private static WorkerProductionAgreementDescriptor frozenDescriptor;
        private static WorkerProductionCompiledRules activeRules;
        private static WorkerProductionStateRequirement selectedRequirement=noSavedState;
        private static ulong[] selectedDenominators=Array.Empty<ulong>();
        private static string selectedPath,frozenSelectionPath,observedBundle="";
        private static long session;
        private static IntPtr auditedImage;
        private static bool initialized,restored,worldNativeLoaded;
        internal static ulong CurrentCommonDenominator => Volatile.Read(ref activeRules)?.CommonDenominator ?? SavedCommonDenominator();
        internal static bool BaseAvailable => WorkerProductionBonusNativeRuntime.IsAvailable && WorkerProductionYieldContext.IsAvailable && WorkerProductionOutputNativeRuntime.IsAvailable &&
            WorkerProductionStateRuntime.IsAvailable && WorkerProductionWorldSafety.IsAvailable;
        private static bool BonusAvailable => WorkerProductionBonusNativeRuntime.IsAvailable && WorkerProductionWorldSafety.IsAvailable;
        internal static WorkerProductionHotAddresses HotAddresses(ulong image)
        {
            // Called by the hash-bound native cohort while preparing its generators.
            // The restore reader uses this only after that cohort reports available.
            auditedImage=new IntPtr(unchecked((long)image));
            return new WorkerProductionHotAddresses(
            unchecked((ulong)configurationSlot.ToInt64()),unchecked((ulong)WorkerProductionStateStore.RowsAddress.ToInt64()),
            unchecked((ulong)WorkerProductionStateStore.ControlAddress.ToInt64()),
            unchecked((ulong)WorkerProductionWorldSafety.NativeFaultAddress.ToInt64()),image+0x8574C44,
            WorkerProductionYieldContext.OriginalUnitAddress);
        }

        private static IntPtr AllocateSlot() { IntPtr p=Marshal.AllocHGlobal(8);Marshal.WriteInt64(p,0);return p; }
        internal static void Initialize(ManualLogSource logger)
        {
            if(initialized)return;log=logger;
            WorkerProductionModifiers.SetSavedDenominatorProvider(SavedDenominators);
            WorkerProductionModifiers.ConfigureBackend(()=>BaseAvailable,BackendDiagnostic,PublishRules,ServiceKey,
                ()=>BonusAvailable,PublishLegacy,legacyPublicationMonitor:WorkerProductionWorldSafety.EngineGate);
            WorkerProductionModifiers.TryRegisterObserver(APISharedPlugin.PluginGuid,"worker-production/admission-inputs",ObserveStatus,out _);
            WorkerProductionStateRuntime.ConfigurationChanged+=StateChanged;
            WorkerProductionWorldSafety.Faulted+=WorkerProductionModifiers.ReportRuntimeFault;
            // Channel identity/order must exist even when some native cohort is unavailable.
            WorkerProductionAgreement.Initialize(logger,CaptureDescriptor,SelectSave);
            WorkerProductionAgreement.Changed+=AgreementChanged;
            if(!ApiShared.ForMod(APISharedPlugin.PluginGuid).TryGetMissionLifecycle(out missions,out var diagnostic) ||
                !missions.TryRegisterObserver("worker-production/world",Started,Ended,Initializing,out diagnostic))
                throw new InvalidOperationException("Production lifecycle unavailable: "+diagnostic);
            WorkerProductionStateRuntime.CompleteIntegration();
            initialized=true;WorkerProductionModifiers.Prepare(default);
        }
        internal static string ValidateNativeAdmission(MissionContext context)
        {
            if(context==null)return null;
            if(initialized)
                return WorkerProductionWorldSafety.IsFaulted ? "Production initialization failed before native loading." : null;
            // The lifecycle calls this independently of a successful production startup.
            // A failed optional coordinator must leave Vanilla alone, but may never silently
            // execute a requested custom calculation without its world owner.
            var status=WorkerProductionModifiers.Status;
            bool needsData=!string.IsNullOrEmpty(context.FilePath) && File.Exists(context.FilePath) &&
                WorkerProductionStateRuntime.InspectSelectedSaveRequirement(context.FilePath).RequiresExtended;
            if(!status.HasDesiredProfiles && !needsData)return null;
            status=WorkerProductionModifiers.Prepare(context.Mode);
            if(!status.CanStart || status.HasBaseContributions || status.ResolvedFixedBonusPercent!=50 || needsData)
            {
                WorkerProductionModifiers.NotifyRejectedLocalLaunch("Unavailable");
                return "The requested production rules have no initialized mission/admission owner.";
            }
            return null;
        }
        private static string ServiceKey() => WorkerProductionBonusNativeRuntime.IsAvailable
            ? "worker-production-core/2/"+WorkerProductionBonusNativeDefinition.ReferenceHash : "";
        private static string BackendDiagnostic() => !WorkerProductionBonusNativeRuntime.IsAvailable ? WorkerProductionBonusNativeRuntime.Diagnostic :
            !WorkerProductionWorldSafety.IsAvailable ? "Production run safety is unavailable." :
            !WorkerProductionYieldContext.IsAvailable ? WorkerProductionYieldContext.Diagnostic :
            !WorkerProductionOutputNativeRuntime.IsAvailable ? WorkerProductionOutputNativeRuntime.Diagnostic : WorkerProductionStateRuntime.Diagnostic;

        private static IEnumerable<ulong> SavedDenominators()
        {
            if(selectedPath!=null && !restored)return Volatile.Read(ref selectedDenominators);
            return WorkerProductionStateStore.CaptureDenominators();
        }
        private static ulong SavedCommonDenominator()
        {
            ulong common=1;
            foreach(ulong d in selectedDenominators)
            {
                if(d==0)return 0;ulong factor=d/WorkerProductionStateStore.Gcd(common,d);
                if(common>(ulong)long.MaxValue/factor)return 0;common*=factor;
            }
            return common;
        }
        private static void SelectSave(string path)
        {
            // Save/map coordinators retire their transaction after GO. That cleanup must
            // not withdraw the descriptor of the world which has just consumed it.
            if(session!=0 && frozenDescriptor!=null && string.IsNullOrEmpty(path))return;
            selectedPath=string.IsNullOrEmpty(path)?null:path;restored=false;
            var requirement=selectedPath==null?noSavedState:WorkerProductionStateRuntime.InspectSelectedSaveRequirement(selectedPath);
            ulong[] denominators=Array.Empty<ulong>();
            if(requirement.IsValid && selectedPath!=null)
                try { denominators=WorkerProductionStateRuntime.InspectSelectedSaveDenominators(selectedPath); }
                catch(Exception error) { requirement=new WorkerProductionStateRequirement(false,true,1,"",error.Message); }
            Volatile.Write(ref selectedDenominators,denominators);Volatile.Write(ref selectedRequirement,requirement);
            WorkerProductionModifiers.SetSavedDenominatorProvider(SavedDenominators);
            WorkerProductionModifiers.Prepare(default);
        }
        private static WorkerProductionAgreementDescriptor CaptureDescriptor()
        {
            if(session!=0 && frozenDescriptor!=null &&
                string.Equals(selectedPath,frozenSelectionPath,StringComparison.OrdinalIgnoreCase))return frozenDescriptor;
            return Describe(WorkerProductionModifiers.GetLaunchBundle(),Volatile.Read(ref selectedRequirement));
        }
        private static WorkerProductionAgreementDescriptor Describe(WorkerProductionLaunchBundle bundle,WorkerProductionStateRequirement saved)
        {
            bool extended=bundle.HasBaseContributions || saved.RequiresExtended;
            return new WorkerProductionAgreementDescriptor(ServiceKey(),bundle.Digest,extended?"extended-row32/1":"native",
                extended && BaseAvailable?WorkerProductionStateRuntime.CapabilityKey:"",extended?1:0,
                extended?Math.Max(1,saved.MinimumReaderSchema):0,
                bundle.CanStart && (extended?BaseAvailable:BonusAvailable),bundle.HasActiveContributions || extended,saved.IsValid,saved.Digest);
        }
        private static void ObserveStatus(WorkerProductionStatus status)
        {
            var bundle=WorkerProductionModifiers.GetLaunchBundle();
            string digest=bundle.Digest+"/"+ServiceKey()+"/"+BaseAvailable;
            if(ReferenceEquals(bundle,observedBundleReference) && digest==observedBundle)return;
            observedBundleReference=bundle;observedBundle=digest;
            // Legacy setters may first publish at an existing synchronized native-load
            // boundary. Before the first admitted run, recommit their arithmetic without
            // upgrading the already selected unit-wire protocol.
            object gate=WorkerProductionWorldSafety.EngineGate;
            if(gate!=null)lock(gate)
                if(session!=0 && frozenDescriptor!=null && !WorkerProductionWorldSafety.IsAdmitted && !WorkerProductionWorldSafety.IsFaulted)
                {
                    var next=Describe(bundle,selectedRequirement);
                    if(next.WireMode!=frozenDescriptor.WireMode || next.SavedRecordDigest!=frozenDescriptor.SavedRecordDigest)
                        WorkerProductionWorldSafety.Fault("Production data requirements changed after native-world admission.");
                    else { frozenBundle=bundle;frozenDescriptor=next; }
                }
            WorkerProductionAgreement.RequestRefresh();
        }
        private static void AgreementChanged()
        {
            if(session==0 || WorkerProductionWorldSafety.IsAdmitted || WorkerProductionWorldSafety.IsFaulted)return;
            lock(WorkerProductionWorldSafety.EngineGate)
            {
                var context=MissionLifecycleService.ActiveContext;
                if(context==null || context.SessionId!=session || context.Mode.Kind==GameModeKind.Unknown)return;
                // Network completion does not release physics before NativeLoaded/Started.
                if(!worldNativeLoaded)return;
                if(!TryActivate(context.Mode,true,out string reason) && reason!=null)WorkerProductionWorldSafety.Fault(reason);
            }
        }
        private static void StateChanged()
        {
            // A restored record is verified synchronously by ValidateRestoredState. Its
            // notification does not revoke the receipt for that already committed file.
            if(session==0)WorkerProductionAgreement.RequestRefresh();
        }
        private static void Initializing(MissionLifecycleNotification note)
        {
            if(!note.IsBeforeInitialization && note.Phase!=MissionInitializationPhase.NativeLoaded)return;
            object gate=WorkerProductionWorldSafety.EngineGate;
            if(gate==null)return;
            lock(gate)
            {
                try
                {
                    if(session!=note.Context.SessionId)Begin(note.Context);
                    if(WorkerProductionWorldSafety.IsFaulted)return;
                    if(note.Phase==MissionInitializationPhase.NativeLoaded)worldNativeLoaded=true;
                    TryActivate(note.Context.Mode,worldNativeLoaded,out string reason);
                    if(reason!=null)WorkerProductionWorldSafety.Fault(reason);
                }
                catch(Exception error) { WorkerProductionWorldSafety.Fault("Production initialization rejected: "+error.Message); }
            }
        }
        private static void Begin(MissionContext context)
        {
            session=context.SessionId;
            worldNativeLoaded=false;
            WorkerProductionWorldSafety.Admit(session,true);
            WorkerProductionModifiers.ClearRuntimeFault();
            // Local save/editor routes have no Steam preparation. Read their requirement
            // at the same pre-native boundary; never infer history from current settings.
            if(!string.IsNullOrEmpty(context.FilePath) && File.Exists(context.FilePath) &&
                !string.Equals(selectedPath,context.FilePath,StringComparison.OrdinalIgnoreCase))SelectSave(context.FilePath);
            else if(string.IsNullOrEmpty(context.FilePath) && !context.IsSave &&
                context.StartKind!=MissionStartKind.EditorLoaded && selectedPath!=null)SelectSave(null);
            frozenDescriptor=null;frozenBundle=WorkerProductionModifiers.PrepareLaunchBundle();
            frozenDescriptor=CaptureDescriptor();frozenSelectionPath=selectedPath;
            if(!frozenBundle.CanStart || frozenDescriptor.LocalFailure!=null)
                throw new InvalidOperationException("Production rules/save requirements are invalid: "+frozenDescriptor.LocalFailure);
            bool extended=frozenBundle.HasBaseContributions || selectedRequirement.RequiresExtended;
            if(extended && !BaseAvailable)throw new InvalidOperationException(BackendDiagnostic());
            Volatile.Write(ref activeRules,null);Volatile.Write(ref *(long*)configurationSlot,0);
            if(WorkerProductionBonusNativeRuntime.IsAvailable)WorkerProductionBonusNativeRuntime.PublishPercent(50);
            if(WorkerProductionStateRuntime.IsAvailable)WorkerProductionStateRuntime.BeginWorld(session,extended);
            else { WorkerProductionStateStore.Clear();WorkerProductionStateStore.Admit(WorkerProductionWorldMode.Legacy); }
            restored=false;
        }
        private static bool TryActivate(GameModeSnapshot mode,bool requireKnown,out string reason)
        {
            reason=null;
            if(mode.Kind==GameModeKind.Unknown)
            { if(requireKnown)reason="The loaded production mission mode is unknown.";return false; }
            var status=WorkerProductionModifiers.Prepare(mode);var candidate=WorkerProductionModifiers.CurrentPrepared;
            if(!status.CanStart || candidate==null || !WorkerProductionModifiers.IsCandidateInBundle(frozenBundle,mode,candidate))
            { reason="The actual production rules do not match the prepared launch bundle.";return false; }
            if(mode.IsRealMultiplayer)
            {
                string receipt=WorkerProductionAgreement.ApprovedDescriptorDigest;
                if(receipt.Length==0 || frozenDescriptor==null || receipt!=frozenDescriptor.Digest())
                {
                    string code=WorkerProductionAgreement.Current.Code;
                    if(code=="Pending" || code=="Roster" || code=="Unavailable" || code.Length==0)
                    {WorkerProductionAgreement.RequestRefresh();return false;}
                    reason="The current multiplayer production rules have no matching peer receipt: "+code;return false;
                }
            }
            if(!WorkerProductionModifiers.ActivatePrepared(candidate,out reason))return false;
            reason=null;
            if(requireKnown)WorkerProductionWorldSafety.ConfirmPrepared(candidate.FixedBonusPercent!=50 ||
                WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Extended);
            return true;
        }
        internal static bool ValidateRestoredState(out string reason)
        {
            reason=null;
            if(session==0 || frozenBundle==null){reason="No production world was admitted before native restore.";return false;}
            if(!BonusAvailable && HasHistoricalNegativeProduction())
            {reason="Saved reduced production needs the complete native completion guards.";return false;}
            if(WorkerProductionStateRuntime.LoadedRequiresExtended && !BaseAvailable){reason=BackendDiagnostic();return false;}
            if(selectedRequirement.Digest!=WorkerProductionStateRuntime.SavedRecordDigest)
            {reason="The production save record changed after launch preparation.";return false;}
            restored=true;
            // Membership includes the saved denominators; no credit is rebased or rounded.
            var mode=MissionLifecycleService.ActiveContext?.Mode ?? default;
            if(mode.Kind==GameModeKind.Unknown)
            {
                bool needsArithmetic=WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Extended ||
                    WorkerProductionStateRuntime.LoadedRequiresExtended || RequiresUnknownModeArithmetic(frozenBundle) ||
                    HasHistoricalNegativeProduction();
                int scene=-1;
                if(needsArithmetic && WorkerProductionStateRuntime.PreparedThroughSaveGameEntry && BonusAvailable)
                {
                    // Hash-bound FBCB9319 contract:853F0 calls87D10 (scene40) before
                    // Raw87D0. Neither the archive section table nor87D0 restores this
                    // scene;71670/CDE60 permits182B00 only when7E50 accepts12/14/16.
                    // Other map entries and initflag-1 have no such counterproof.
                    IntPtr image=auditedImage;
                    if(image!=IntPtr.Zero)scene=Marshal.ReadInt32(image+0x3665ED8);
                }
                if(CanDeferUnknownRestore(needsArithmetic,WorkerProductionStateRuntime.PreparedThroughSaveGameEntry,scene))return true;
                reason="The production mode is unknown before a native loader that may already dispatch workers.";
                return false;
            }
            bool activated=TryActivate(mode,false,out reason);
            return activated || reason==null; // A pending peer receipt holds runs; it does not invalidate restored bytes.
        }
        private static bool RequiresUnknownModeArithmetic(WorkerProductionLaunchBundle bundle)
        {
            if(bundle.HasBaseContributions)return true;
            foreach(var member in bundle.Members.Values)
                if(member.FixedBonusPercent!=50 || member.HasBaseContributions)return true;
            return false;
        }
        internal static bool CanDeferUnknownRestore(bool needsArithmetic,bool saveGameEntry,int nativeScene) =>
            !needsArithmetic || saveGameEntry && nativeScene==40;
        private static bool HasHistoricalNegativeProduction()
        {
            // Cold raw-restore validation only. A saved Vanilla-only request may still
            // contain old reduced batches, including paused workers; no live unit polling.
            var manager=GameUnitManagerAPI.Instance;
            for(int id=1;id<WorkerProductionStateStore.RowCapacity;id++)
                if(UnitAccess.TryGetById(manager,id,out GameUnit* unit,out _) && unit->r_GlobalId!=0 &&
                    unit->r_AliveState!=AliveState.None && unit->r_AliveState!=AliveState.MarkedForDeletion &&
                    (WorkerProductionCompiler.IsWorker(unit->r_UnitChimp) || unit->r_UnitChimp==eChimps.CHIMP_TYPE_QUARRY_OX) &&
                    unchecked((short)unit->r_CarryBonusYieldAmount)<0)return true;
            return false;
        }
        private static void Started(MissionLifecycleNotification note)
        {
            if(session!=note.Context.SessionId)return;
            lock(WorkerProductionWorldSafety.EngineGate)
            {
                if(WorkerProductionStateRuntime.IsAvailable)WorkerProductionStateRuntime.ValidateStarted(session);
                worldNativeLoaded=true;
                if(WorkerProductionWorldSafety.IsAdmitted)return;
                if(!WorkerProductionWorldSafety.IsFaulted && !TryActivate(note.Context.Mode,true,out string reason) && reason!=null)
                    WorkerProductionWorldSafety.Fault(reason);
            }
        }
        private static void Ended(MissionLifecycleNotification note)
        {
            if(session!=note.Context.SessionId)return;
            lock(WorkerProductionWorldSafety.EngineGate)
            {
                bool nextPrepared=selectedPath!=null && !restored &&
                    !string.Equals(selectedPath,frozenSelectionPath,StringComparison.OrdinalIgnoreCase);
                WorkerProductionWorldSafety.Retire();
                if(WorkerProductionStateRuntime.IsAvailable)WorkerProductionStateRuntime.EndWorld(session);
                else WorkerProductionStateStore.Clear();
                session=0;restored=false;
                Volatile.Write(ref activeRules,null);Volatile.Write(ref *(long*)configurationSlot,0);
                if(WorkerProductionBonusNativeRuntime.IsAvailable)WorkerProductionBonusNativeRuntime.PublishPercent(50);
                frozenBundle=null;frozenDescriptor=null;frozenSelectionPath=null;
                if(!nextPrepared)
                {selectedPath=null;selectedRequirement=noSavedState;selectedDenominators=Array.Empty<ulong>();}
                WorkerProductionModifiers.ClearRuntimeFault();
                WorkerProductionModifiers.Prepare(default);
                if(!nextPrepared)WorkerProductionAgreement.RequestRefresh();
            }
        }
        private static void PublishLegacy(WorkerProductionCompiledRules rules)
        {
            if(session==0)
            {
                // Desired base profiles in a lobby are not executable yet. A legacy
                // percentage write must neither activate them nor demand a wire upgrade.
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);return;
            }
            if(!rules.HasBaseContributions && WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Legacy)
            {
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);
                Volatile.Write(ref activeRules,rules);
                if(session!=0)
                {
                    if(rules.FixedBonusPercent!=50)WorkerProductionWorldSafety.RequireGuard();
                    else if(WorkerProductionWorldSafety.IsAdmitted)WorkerProductionWorldSafety.ConfirmPrepared(false);
                }
                return;
            }
            var existing=Volatile.Read(ref activeRules);
            if(existing==null)
            {WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);WorkerProductionWorldSafety.RequireGuard();return;}
            // SetProfile is desired-only. A legacy bonus setter never makes pending typed
            // base edits live; keep the currently admitted base arithmetic and its credits.
            var entries=new WorkerProductionCompiledEntry[existing.Entries.Count];
            int maximum=3276700/(150+Math.Max(rules.FixedBonusPercent,0));
            for(int role=0;role<existing.WorkerTypes.Count*2;role++)
            for(int input=0;input<WorkerProductionCompiler.InputCount;input++)
            {
                int index=role*WorkerProductionCompiler.InputCount+input;var entry=existing.Entries[index];
                uint ceiling=entry.Whole+(entry.Numerator==0?0U:1U);
                int limit=existing.WorkerTypes[role/2]==eChimps.CHIMP_TYPE_MILLER?32767:maximum;
                if(ceiling>limit)throw new InvalidOperationException("The legacy bonus exceeds the admitted active base arithmetic bounds.");
                entries[index]=entry;
            }
            PublishRules(new WorkerProductionCompiledRules(rules.FixedBonusPercent,existing.HasBaseContributions,existing.CommonDenominator,entries));
        }
        private static void PublishRules(WorkerProductionCompiledRules rules)
        {
            if(rules==null)throw new ArgumentNullException(nameof(rules));
            object gate=WorkerProductionWorldSafety.EngineGate ?? throw new InvalidOperationException("Production engine boundary unavailable.");
            lock(gate)
            {
                if(WorkerProductionWorldSafety.IsFaulted)throw new InvalidOperationException("The production world is faulted.");
                bool extended=WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Extended;
                if((rules.HasBaseContributions || extended) && !BaseAvailable)throw new InvalidOperationException(BackendDiagnostic());
                if(rules.HasBaseContributions && !extended)throw new InvalidOperationException("Base rules cannot upgrade a running native-wire world.");
                IntPtr plan=IntPtr.Zero;
                if(extended && !nativePlans.TryGetValue(rules.Digest,out plan))
                {
                    int count=rules.Entries.Count;IntPtr table=Marshal.AllocHGlobal(checked(count*WorkerProductionHotAssembly.EntrySize));
                    ulong activeMask=0;
                    for(int role=0;role<rules.WorkerTypes.Count*2;role++)
                    for(int input=0;input<WorkerProductionHotAssembly.EntriesPerRole;input++)
                    {
                        int index=role*WorkerProductionHotAssembly.EntriesPerRole+input;var entry=rules.Entries[index];
                        if(entry.Denominator!=rules.CommonDenominator)throw new InvalidOperationException("Production table denominator differs.");
                        Marshal.WriteInt64(table,index*24,entry.Whole);Marshal.WriteInt64(table,index*24+8,unchecked((long)entry.Numerator));
                        Marshal.WriteInt64(table,index*24+16,unchecked((long)entry.Denominator));
                        if(entry.Whole!=(uint)input || entry.Numerator!=0)activeMask|=1UL<<role;
                    }
                    plan=Marshal.AllocHGlobal(WorkerProductionHotAssembly.HeaderSize);
                    Marshal.WriteInt64(plan,0,unchecked((long)rules.CommonDenominator));Marshal.WriteIntPtr(plan,8,table);
                    Marshal.WriteInt64(plan,16,unchecked((long)activeMask));nativePlans.Add(rules.Digest,plan);
                }
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);
                Volatile.Write(ref *(long*)configurationSlot,plan.ToInt64());Volatile.Write(ref activeRules,rules);
            }
        }
    }
}

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
        private static readonly Dictionary<(string Digest,bool Extended),IntPtr> nativePlans=
            new Dictionary<(string Digest,bool Extended),IntPtr>();
        private static readonly WorkerProductionStateRequirement noSavedState=new WorkerProductionStateRequirement(true,false,0,"","");
        private static ManualLogSource log;
        private static IMissionLifecycleCapability missions;
        private static WorkerProductionLaunchBundle frozenBundle;
        private static WorkerProductionLaunchBundle observedBundleReference;
        private static WorkerProductionAgreementDescriptor frozenDescriptor;
        private static WeaponProductionCatalog frozenWeaponCatalog;
        // Huntable prey shares this mission owner and its peer agreement; it has no save record.
        private static Wildlife.HuntablePreyCatalog frozenHuntingCatalog;
        private static byte[] selectedWeaponSave;
        private static string selectedWeaponDigest = "", selectedWeaponError;
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
        internal static string StoredOutputMultiplayerDiagnostic => WeaponProductionRuntime.StoredOutputModeDiagnostic(
            frozenWeaponCatalog ?? WeaponProductionRuntime.PrepareCatalog(), realMultiplayer: true);

        internal static string ValidateNativeAdmission(MissionContext context)
        {
            if(context==null)return null;
            if(context.Mode.IsRealMultiplayer)
            {
                string storedOutputReason = StoredOutputMultiplayerDiagnostic;
                if(storedOutputReason != null)return storedOutputReason;
            }
            if(initialized)
                return WorkerProductionWorldSafety.IsFaulted ? "Production initialization failed before native loading." : null;
            // The lifecycle calls this independently of a successful production startup.
            // A failed optional coordinator must leave Vanilla alone, but may never silently
            // execute a requested custom calculation without its world owner.
            var status=WorkerProductionModifiers.Status;
            bool needsData=!string.IsNullOrEmpty(context.FilePath) && File.Exists(context.FilePath) &&
                WorkerProductionStateRuntime.InspectSelectedSaveRequirement(context.FilePath).RequiresExtended;
            if (!string.IsNullOrEmpty(context.FilePath) && File.Exists(context.FilePath))
                try { needsData |= WeaponProductionRuntime.InspectSave(context.FilePath) != null; }
                catch { needsData = true; }
            if(!status.HasDesiredProfiles && !needsData)return null;
            status=WorkerProductionModifiers.Prepare(context.Mode);
            if(!status.CanStart || status.HasBaseContributions || status.ResolvedRules?.HasFearChanges==true ||
                (context.Mode.Kind==GameModeKind.Unknown && RequiresUnknownModeArithmetic(WorkerProductionModifiers.GetLaunchBundle())) ||
                status.ResolvedFixedBonusPercent!=50 || needsData)
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
            // Without the engine gate no world is ever admitted (Initializing returns), so no row exists;
            // capturing would lock a null monitor and report a bogus saved-denominator issue.
            if(WorkerProductionWorldSafety.EngineGate==null)return Array.Empty<ulong>();
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
            selectedWeaponSave = null;
            selectedWeaponDigest = "";
            selectedWeaponError = null;
            if (selectedPath != null)
                try
                {
                    selectedWeaponSave = WeaponProductionRuntime.InspectSave(selectedPath);
                    selectedWeaponDigest = WeaponProductionRuntime.SavedDigest(selectedWeaponSave);
                }
                catch (Exception error) { selectedWeaponError = error.Message; }
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
            var weapons = frozenWeaponCatalog ?? WeaponProductionRuntime.PrepareCatalog();
            bool extended=bundle.RequiresExtendedWire || saved.RequiresExtended || weapons.HasOptions;
            bool needsFull=extended || bundle.NeedsFullBackend;
            bool valid = saved.IsValid && selectedWeaponError == null &&
                WeaponProductionRuntime.MatchesSavedCatalog(selectedWeaponSave, weapons);
            string rulesDigest = weapons.HasOptions ? WeaponProductionRuntime.CombineDigest(bundle.Digest, weapons.Digest) : bundle.Digest;
            // Without registrations the descriptor stays byte-identical to builds without huntable prey.
            var hunting = frozenHuntingCatalog ?? Wildlife.HuntablePreyRuntime.PrepareCatalog();
            if (hunting.HasRegistrations) rulesDigest = WeaponProductionRuntime.CombineDigest(rulesDigest, "huntable-prey:" + hunting.Digest);
            string saveDigest = selectedWeaponSave != null ? WeaponProductionRuntime.CombineDigest(saved.Digest, selectedWeaponDigest) : saved.Digest;
            string wire = weapons.HasOptions ? "extended-row32+weapon-workshop/1" : extended ? "extended-row32/1" : "native";
            return new WorkerProductionAgreementDescriptor(ServiceKey(),rulesDigest,wire,
                extended && BaseAvailable?WorkerProductionStateRuntime.CapabilityKey:"",extended?1:0,
                extended?Math.Max(1,saved.MinimumReaderSchema):0,
                bundle.CanStart && (needsFull?BaseAvailable:BonusAvailable),bundle.HasActiveContributions || extended,valid,saveDigest);
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
            frozenDescriptor=null;
            frozenWeaponCatalog = context.Mode.Kind == GameModeKind.Unknown || context.Mode.IsMapEditor || context.Mode.AllowsCustomGameMods
                ? WeaponProductionRuntime.PrepareCatalog()
                : WeaponProductionCatalog.Empty;
            WeaponProductionRuntime.BeginWorld(frozenWeaponCatalog, session);
            frozenHuntingCatalog = context.Mode.Kind == GameModeKind.Unknown || context.Mode.IsMapEditor || context.Mode.AllowsCustomGameMods
                ? Wildlife.HuntablePreyRuntime.PrepareCatalog()
                : Wildlife.HuntablePreyCatalogBuilder.VanillaOnly;
            Wildlife.HuntablePreyRuntime.BeginWorld(frozenHuntingCatalog);
            frozenBundle=WorkerProductionModifiers.PrepareLaunchBundle();
            frozenDescriptor=CaptureDescriptor();frozenSelectionPath=selectedPath;
            if(!frozenBundle.CanStart || frozenDescriptor.LocalFailure!=null)
                throw new InvalidOperationException("Production rules/save requirements are invalid: "+frozenDescriptor.LocalFailure);
            bool extended=frozenBundle.RequiresExtendedWire || selectedRequirement.RequiresExtended || frozenWeaponCatalog.HasOptions;
            if((extended || frozenBundle.NeedsFullBackend) && !BaseAvailable)throw new InvalidOperationException(BackendDiagnostic());
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
            string storedOutputReason = WeaponProductionRuntime.StoredOutputModeDiagnostic(frozenWeaponCatalog, mode.IsRealMultiplayer);
            if(storedOutputReason != null) { reason=storedOutputReason;return false; }
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
            if (!WeaponProductionRuntime.Activate(mode.IsMapEditor || mode.AllowsCustomGameMods, out reason)) return false;
            Wildlife.HuntablePreyRuntime.Activate(mode.IsMapEditor || mode.AllowsCustomGameMods);
            reason=null;
            if(requireKnown)WorkerProductionWorldSafety.ConfirmPrepared(candidate.FixedBonusPercent!=50 || candidate.HasFearChanges ||
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
            if (!WeaponProductionRuntime.RestoreVerified(selectedWeaponSave))
            { reason = "The weapon-workshop save record changed or was not restored."; return false; }
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
            if(bundle.NeedsFullBackend)return true;
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
                WeaponProductionRuntime.EndWorld();
                Wildlife.HuntablePreyRuntime.EndWorld();
                if(WorkerProductionStateRuntime.IsAvailable)WorkerProductionStateRuntime.EndWorld(session);
                else WorkerProductionStateStore.Clear();
                session=0;restored=false;
                Volatile.Write(ref activeRules,null);Volatile.Write(ref *(long*)configurationSlot,0);
                if(WorkerProductionBonusNativeRuntime.IsAvailable)WorkerProductionBonusNativeRuntime.PublishPercent(50);
                frozenBundle=null;frozenDescriptor=null;frozenSelectionPath=null;frozenWeaponCatalog=null;frozenHuntingCatalog=null;
                if(!nextPrepared)
                {
                    selectedPath=null;selectedRequirement=noSavedState;selectedDenominators=Array.Empty<ulong>();
                    selectedWeaponSave=null;selectedWeaponDigest="";selectedWeaponError=null;
                }
                WorkerProductionModifiers.ClearRuntimeFault();
                WorkerProductionModifiers.Prepare(default);
                if(!nextPrepared)WorkerProductionAgreement.RequestRefresh();
            }
        }
        private static void PublishLegacy(WorkerProductionCompiledRules rules)
        {
            if(session==0)
            {
                // Desired profiles in a lobby are not executable yet. A legacy
                // percentage write must neither activate them nor demand a wire upgrade.
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);return;
            }
            var existing=Volatile.Read(ref activeRules);
            if(existing==null)
            {WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);WorkerProductionWorldSafety.RequireGuard();return;}
            // SetProfile is desired-only. A legacy bonus setter never makes pending typed
            // base/fear edits live. Validate the new bonus against the admitted arithmetic.
            foreach (int fear in existing.FearBonusByLevel)
                if (fear+Math.Min(0,rules.FixedBonusPercent)<-100)
                    throw new InvalidOperationException("The legacy bonus and the admitted fear rules can total less than -100 percent.");
            var entries=new WorkerProductionCompiledEntry[existing.Entries.Count];
            int maximumFear=WorkerProductionCompiler.MaximumFearBonus(existing.FearBonusByLevel);
            for(int role=0;role<existing.WorkerTypes.Count*2;role++)
            {
                int limit=WorkerProductionCompiler.MaximumBase(rules.FixedBonusPercent,existing.WorkerTypes[role/2],maximumFear);
                for(int input=0;input<WorkerProductionCompiler.InputCount;input++)
                {
                    int index=role*WorkerProductionCompiler.InputCount+input;var entry=existing.Entries[index];
                    uint ceiling=entry.Whole+(entry.Numerator==0?0U:1U);
                    if(ceiling>limit)throw new InvalidOperationException("The legacy bonus exceeds the admitted active base arithmetic bounds.");
                    entries[index]=entry;
                }
            }
            PublishRules(new WorkerProductionCompiledRules(rules.FixedBonusPercent,existing.HasBaseContributions,
                existing.CommonDenominator,entries,existing.FearBonusByLevel));
            if(rules.FixedBonusPercent!=50 || existing.HasFearChanges ||
                WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Extended) WorkerProductionWorldSafety.RequireGuard();
            else if(WorkerProductionWorldSafety.IsAdmitted)WorkerProductionWorldSafety.ConfirmPrepared(false);
        }
        private static void PublishRules(WorkerProductionCompiledRules rules)
        {
            if(rules==null)throw new ArgumentNullException(nameof(rules));
            object gate=WorkerProductionWorldSafety.EngineGate ?? throw new InvalidOperationException("Production engine boundary unavailable.");
            lock(gate)
            {
                if(WorkerProductionWorldSafety.IsFaulted)throw new InvalidOperationException("The production world is faulted.");
                bool extended=WorkerProductionStateStore.WorldMode==WorkerProductionWorldMode.Extended;
                if((rules.NeedsFullBackend || extended) && !BaseAvailable)throw new InvalidOperationException(BackendDiagnostic());
                if(rules.RequiresExtendedWire && !extended)throw new InvalidOperationException("Production rules cannot upgrade a running native-wire world.");
                IntPtr plan=IntPtr.Zero;
                var planKey=(rules.Digest,extended);
                if((extended || rules.HasFearChanges) && !nativePlans.TryGetValue(planKey,out plan))
                {
                    IntPtr table=extended ? Marshal.AllocHGlobal(checked(rules.Entries.Count*WorkerProductionHotAssembly.EntrySize)) : IntPtr.Zero;
                    ulong activeMask=0;
                    for(int role=0;extended && role<rules.WorkerTypes.Count*2;role++)
                    for(int input=0;input<WorkerProductionHotAssembly.EntriesPerRole;input++)
                    {
                        int index=role*WorkerProductionHotAssembly.EntriesPerRole+input;var entry=rules.Entries[index];
                        if(entry.Denominator!=rules.CommonDenominator)throw new InvalidOperationException("Production table denominator differs.");
                        // WorkerProductionHotAssembly reads whole, numerator and denominator at +0, +8, +16.
                        int entryOffset=index*WorkerProductionHotAssembly.EntrySize;
                        Marshal.WriteInt64(table,entryOffset,entry.Whole);Marshal.WriteInt64(table,entryOffset+8,unchecked((long)entry.Numerator));
                        Marshal.WriteInt64(table,entryOffset+16,unchecked((long)entry.Denominator));
                        if(entry.Whole!=(uint)input || entry.Numerator!=0)activeMask|=1UL<<role;
                    }
                    IntPtr fearTable=IntPtr.Zero;
                    if (rules.HasFearChanges)
                    {
                        fearTable=Marshal.AllocHGlobal(WorkerProductionCompiler.FearLevelCount*sizeof(int));
                        for (int index=0;index<rules.FearBonusByLevel.Count;index++)
                            Marshal.WriteInt32(fearTable,index*sizeof(int),rules.FearBonusByLevel[index]);
                    }
                    plan=Marshal.AllocHGlobal(WorkerProductionHotAssembly.HeaderSize);
                    Marshal.WriteInt64(plan,WorkerProductionHotAssembly.CommonDenominatorOffset,unchecked((long)rules.CommonDenominator));
                    Marshal.WriteIntPtr(plan,WorkerProductionHotAssembly.TableOffset,table);
                    Marshal.WriteInt64(plan,WorkerProductionHotAssembly.ActiveRoleMaskOffset,unchecked((long)activeMask));
                    Marshal.WriteIntPtr(plan,WorkerProductionHotAssembly.FearTableOffset,fearTable);
                    Marshal.WriteInt64(plan,WorkerProductionHotAssembly.FearHasNegativeOffset,rules.HasNegativeFearBonus ? 1L : 0L);
                    nativePlans.Add(planKey,plan);
                }
                WorkerProductionBonusNativeRuntime.PublishPercent(rules.FixedBonusPercent);
                Volatile.Write(ref *(long*)configurationSlot,plan.ToInt64());Volatile.Write(ref activeRules,rules);
            }
        }
    }
}

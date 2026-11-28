using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Threading;

namespace APIShared.DependencyBootstrap
{
    /// <summary>Outcome of one process-lifetime early registration. No state promises successful plugin Awake.</summary>
    public enum EarlyPluginActivationState
    {
        Pending, AwaitingTarget, Running, Completed, CallbackFailed,
        OwnerNotSelected, OwnerFailed, TargetNotSelected, TargetSkipped, Unavailable
    }

    /// <summary>Immutable metadata of the actual Chainloader winner; not a checksum of its assets or a future Awake guarantee.</summary>
    public sealed class EarlyPluginIdentity
    {
        internal EarlyPluginIdentity(string guid,string version,string assemblyPath,string entryTypeName)
        {
            if(string.IsNullOrWhiteSpace(guid) || string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(assemblyPath) || string.IsNullOrWhiteSpace(entryTypeName))
                throw new ArgumentException("Incomplete selected plugin identity.");
            Guid=guid;Version=version;AssemblyPath=Path.GetFullPath(assemblyPath);EntryTypeName=entryTypeName;
        }
        public string Guid { get; }
        public string Version { get; }
        public string AssemblyPath { get; }
        public string EntryTypeName { get; }
    }

    /// <summary>The selected owner/target and actual loaded entry immediately before the target's AddComponent.</summary>
    /// <remarks>Callback is synchronous on the Chainloader caller, before construction/Awake. Retainable immutable metadata;
    /// LoadedType/its Assembly remain process-wide CLR identities. Selected plugins may subsequently fail or be skipped.
    /// A loaded assembly MVID identifies that module; it is not a native hash or complete package-integrity proof.</remarks>
    public sealed class EarlyPluginActivationContext
    {
        internal EarlyPluginActivationContext(EarlyPluginIdentity owner,EarlyPluginIdentity target,
            IReadOnlyList<EarlyPluginIdentity> selected,Type loadedType)
        {
            Owner=owner;Target=target;SelectedPlugins=selected;LoadedType=loadedType;
            LoadedAssemblyName=loadedType.Assembly.FullName;LoadedModuleVersionId=loadedType.Module.ModuleVersionId;
        }
        public EarlyPluginIdentity Owner { get; }
        public EarlyPluginIdentity Target { get; }
        public IReadOnlyList<EarlyPluginIdentity> SelectedPlugins { get; }
        public Type LoadedType { get; }
        public string LoadedAssemblyName { get; }
        public Guid LoadedModuleVersionId { get; }
    }

    /// <summary>Permanent rooted registration; no unsubscribe, retry, veto, or replay.</summary>
    public sealed class EarlyPluginActivationRegistration
    {
        private int state;
        private string reason="Waiting for the selected plugin snapshot.";
        internal readonly Action<EarlyPluginActivationContext> Callback;
        internal Action<EarlyPluginActivationContext,ISet<string>> ProviderCallback;
        internal EarlyPluginActivationRegistration(string owner,string id,string target,int order,Action<EarlyPluginActivationContext> callback)
        {OwnerGuid=owner;Id=id;TargetGuid=target;Order=order;Callback=callback;}
        public string OwnerGuid { get; }
        public string Id { get; }
        public string TargetGuid { get; }
        public int Order { get; }
        public EarlyPluginActivationState State => (EarlyPluginActivationState)Volatile.Read(ref state);
        public string Reason => Volatile.Read(ref reason);
        internal void Set(EarlyPluginActivationState value,string diagnostic)
        {Volatile.Write(ref reason,diagnostic);Volatile.Write(ref state,(int)value);}
    }

    /// <summary>Early, generic observation supplied by APIShared.DependencyBootstrap.dll, not APIShared.dll.</summary>
    /// <remarks>Call from a preloader companion Finish, before Chainloader selects plugins. Referencing this assembly does
    /// not initialize Unity, APIShared's plugin, MonoMod or native hooks. A verified bootstrap/runtime pair and supported
    /// Chainloader are required. Accepted registration is pending, not a claim of availability: inspect its final State.
    /// Registration closes at the selected-winner snapshot, even if the target has not run. No late registration/replay.
    /// Dispatch orders by Order, then ordinal OwnerGuid, then ordinal Id. Exceptions are contained per observer; the
    /// original AddComponent always remains the loader's responsibility. Observer code must not reorder/instantiate
    /// plugins, alter loader state, perform UI work or undo published hooks. This service grants no native authority.</remarks>
    public static class EarlyPluginActivation
    {
        private static readonly EarlyPluginActivationBroker broker=new EarlyPluginActivationBroker(ProbeTrace.Error);
        public static bool TryRegister(string ownerGuid,string id,string targetGuid,Action<EarlyPluginActivationContext> callback,
            out EarlyPluginActivationRegistration registration,out string reason,int order=0) =>
            broker.TryRegister(ownerGuid,id,targetGuid,callback,out registration,out reason,order);
        internal static bool HasRegistrations => broker.HasRegistrations;
        internal static bool BeginSelection() => broker.BeginSelection();
        internal static void BindSelected(IEnumerable<EarlyPluginIdentity> identities,EarlyProviderCatalog providers=null)
        {providers?.Register(broker);broker.BindSelected(identities);}
        internal static void BeforePlugin(Type type,ISet<string> failed,bool validPair) => broker.BeforePlugin(type,failed,validPair);
        internal static void Unavailable(string reason) => broker.Unavailable(reason);
        internal static void CompleteStartup() => broker.CompleteStartup();
    }

    // Same coordinator is used by the production facade and isolated fixtures; callbacks never execute under sync.
    internal sealed class EarlyPluginActivationBroker
    {
        private readonly object sync=new object();
        private readonly Action<string> report;
        private readonly List<EarlyPluginActivationRegistration> registrations=new List<EarlyPluginActivationRegistration>();
        private EarlyPluginActivationRegistration[] ordered=Array.Empty<EarlyPluginActivationRegistration>();
        private Dictionary<string,EarlyPluginIdentity> selected;
        private IReadOnlyList<EarlyPluginIdentity> selectedView;
        private readonly HashSet<string> passed=new HashSet<string>(StringComparer.Ordinal);
        private bool closed,finished;
        private string unavailable;
        internal EarlyPluginActivationBroker(Action<string> report){this.report=report;}
        internal bool HasRegistrations {get{lock(sync)return registrations.Count!=0;}}
        internal bool TryRegister(string owner,string id,string target,Action<EarlyPluginActivationContext> callback,
            out EarlyPluginActivationRegistration registration,out string reason,int order=0)
        {
            registration=null;
            if(!Valid(owner) || !Valid(id) || !Valid(target) || callback==null)
            {reason="Owner, ID, target and callback are required (nonblank, at most96 characters).";return false;}
            lock(sync)
            {
                if(closed || unavailable!=null){reason=unavailable??"Selected-winner boundary passed; no late registration or replay.";return false;}
                foreach(var item in registrations)if(item.OwnerGuid==owner && item.Id==id)
                {reason="Duplicate owner-local registration ID.";return false;}
                registration=new EarlyPluginActivationRegistration(owner,id,target,order,callback);
                registrations.Add(registration);reason=string.Empty;return true;
            }
        }
        // Only the immutable selected-metadata adapter may append declarations after external registration closes.
        // Existing preloader registrations win owner-local ID conflicts and retain their original callbacks/order.
        internal bool AddProvider(string owner,string id,string target,int order,
            Action<EarlyPluginActivationContext,ISet<string>> callback)
        {
            lock(sync)
            {
                if(selected!=null || finished || unavailable!=null)return false;
                foreach(var item in registrations)if(item.OwnerGuid==owner && item.Id==id)return false;
                var registration=new EarlyPluginActivationRegistration(owner,id,target,order,null);
                registration.ProviderCallback=callback;registrations.Add(registration);return true;
            }
        }
        internal bool BeginSelection()
        {lock(sync){if(closed)return false;closed=true;return registrations.Count!=0;}}
        internal void BindSelected(IEnumerable<EarlyPluginIdentity> identities)
        {
            lock(sync)
            {
                if(selected!=null || finished || unavailable!=null)return;
                closed=true;
                try
                {
                    selected=new Dictionary<string,EarlyPluginIdentity>(StringComparer.Ordinal);
                    var values=new List<EarlyPluginIdentity>();
                    foreach(var identity in identities)
                    {if(identity==null || selected.ContainsKey(identity.Guid))throw new ArgumentException();selected.Add(identity.Guid,identity);values.Add(identity);}
                    values.Sort((a,b)=>StringComparer.Ordinal.Compare(a.Guid,b.Guid));
                    selectedView=new ReadOnlyCollection<EarlyPluginIdentity>(values);
                    ordered=registrations.ToArray();
                    Array.Sort(ordered,(a,b)=>{int c=a.Order.CompareTo(b.Order);if(c!=0)return c;
                        c=StringComparer.Ordinal.Compare(a.OwnerGuid,b.OwnerGuid);return c!=0?c:StringComparer.Ordinal.Compare(a.Id,b.Id);});
                    foreach(var item in ordered)
                        item.Set(!selected.ContainsKey(item.OwnerGuid)?EarlyPluginActivationState.OwnerNotSelected:
                            !selected.ContainsKey(item.TargetGuid)?EarlyPluginActivationState.TargetNotSelected:EarlyPluginActivationState.AwaitingTarget,
                            !selected.ContainsKey(item.OwnerGuid)?"Owner was not selected.":
                            !selected.ContainsKey(item.TargetGuid)?"Target was not selected.":"Waiting for the selected target's AddComponent boundary.");
                }
                catch {SetUnavailable("Invalid selected-winner snapshot.");}
            }
        }
        internal void BeforePlugin(Type type,ISet<string> failed,bool validPair)
        {
            EarlyPluginActivationRegistration[] snapshot;EarlyPluginIdentity target=null;
            lock(sync)
            {
                if(registrations.Count==0 || finished || unavailable!=null)return;
                if(!validPair || selected==null){SetUnavailable("Bootstrap pair or selection unavailable before activation.");return;}
                try
                {
                    if(type==null || failed==null)throw new ArgumentException();
                    string actual=Path.GetFullPath(type.Assembly.Location);
                    foreach(var identity in selected.Values)
                        if(identity.EntryTypeName==type.FullName && PathEquals(identity.AssemblyPath,actual))
                        {if(target!=null)throw new ArgumentException();target=identity;}
                    if(target==null)return; // No selected entry, so no authority to notify a consumer.
                    if(!passed.Add(target.Guid))return;
                    snapshot=ordered;
                }
                catch {SetUnavailable("Loaded plugin identity cannot be matched to the selected entry.");return;}
            }
            foreach(var item in snapshot)
            {
                EarlyPluginActivationContext context;
                lock(sync)
                {
                    if(finished || unavailable!=null)return;
                    if(item.State!=EarlyPluginActivationState.AwaitingTarget || item.TargetGuid!=target.Guid)continue;
                    if(failed.Contains(item.OwnerGuid))
                    {item.Set(EarlyPluginActivationState.OwnerFailed,"Owner had already failed in the Chainloader.");continue;}
                    if(failed.Contains(target.Guid))
                    {item.Set(EarlyPluginActivationState.TargetSkipped,"Target had already failed in the Chainloader.");continue;}
                    try {context=new EarlyPluginActivationContext(selected[item.OwnerGuid],target,selectedView,type);}
                    catch {item.Set(EarlyPluginActivationState.Unavailable,"Loaded module identity unavailable.");continue;}
                    item.Set(EarlyPluginActivationState.Running,"Callback is running before AddComponent.");
                }
                try {if(item.ProviderCallback!=null)item.ProviderCallback(context,failed);else item.Callback(context);item.Set(EarlyPluginActivationState.Completed,"Callback returned; plugin Awake outcome is unknown.");}
                catch(EarlyProviderRejectedException rejected)
                {
                    item.Set(EarlyPluginActivationState.Unavailable,rejected.Reason);
                    try {report?.Invoke("Early provider refused: "+item.OwnerGuid+"/"+item.Id+": "+rejected.Reason);}catch{}
                }
                catch
                {
                    item.Set(EarlyPluginActivationState.CallbackFailed,"Observer threw; original loader and other observers continue.");
                    // Never format a foreign exception, including Message/ToString.
                    try {report?.Invoke("Early plugin observer failed: "+item.OwnerGuid+"/"+item.Id);}catch{}
                }
            }
        }
        internal void CompleteStartup()
        {
            lock(sync)
            {
                finished=true;closed=true;
                foreach(var item in registrations)
                    if(item.State==EarlyPluginActivationState.Pending)item.Set(EarlyPluginActivationState.Unavailable,"Selected-winner boundary was not observed.");
                    else if(item.State==EarlyPluginActivationState.AwaitingTarget)item.Set(EarlyPluginActivationState.TargetSkipped,"Selected target never reached AddComponent.");
            }
        }
        internal void Unavailable(string reason){lock(sync)SetUnavailable(reason);}
        private void SetUnavailable(string reason)
        {
            unavailable=reason??"Early boundary unavailable.";closed=true;
            foreach(var item in registrations)
                if(item.State==EarlyPluginActivationState.Pending || item.State==EarlyPluginActivationState.AwaitingTarget)
                    item.Set(EarlyPluginActivationState.Unavailable,unavailable);
        }
        private static bool Valid(string value)=>!string.IsNullOrWhiteSpace(value) && value.Length<=96;
        private static bool PathEquals(string a,string b)=>string.Equals(a,b,
            Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    }
}

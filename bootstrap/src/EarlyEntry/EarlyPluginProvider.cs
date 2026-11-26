using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Mono.Cecil;

namespace APIShared.DependencyBootstrap
{
    /// <summary>Declares one deferred early observer in the normal selected consumer DLL.</summary>
    /// <remarks>Read as Cecil metadata, without loading consumer code, at the winner snapshot. Owner must be a selected
    /// plugin entry in this same DLL; target must also be selected. ID is owner-local and shares the TryRegister namespace.
    /// Provider is a declared, nongeneric public static void method taking exactly EarlyPluginActivationContext on a
    /// nongeneric type in this DLL. No instance is created. Type/method names are CLR full names, not assembly-qualified.
    /// Binding occurs once at BeforePlugin(target), in ordinary observer order, outside locks. A verified bootstrap pair
    /// is required; loading the DLL does not run its plugin Awake. Module initializers in the owner DLL are refused.
    /// All selected hard dependency versions/closure and known failures are checked, but future Awake success/readiness
    /// is not promised. Provider and its complete call graph must be early-safe: no Unity/plugin initialization, loader
    /// mutation or dependency on a future Awake. Validate feature-specific contracts before publishing any permanent hook.
    /// Normal resolver behavior is retained. Selected assemblies already loaded or resolved during binding must match
    /// their selected path/MVID. Arbitrary later loads inside provider code are not preauthorized by this declaration.
    /// No native authority, replay, retry, cancellation or permission to replace an existing hook is conferred.</remarks>
    [AttributeUsage(AttributeTargets.Assembly,AllowMultiple=true)]
    public sealed class EarlyPluginActivationProviderAttribute : Attribute
    {
        public EarlyPluginActivationProviderAttribute(string ownerGuid,string id,string targetGuid,
            string providerTypeName,string methodName,int order=0)
        {OwnerGuid=ownerGuid;Id=id;TargetGuid=targetGuid;ProviderTypeName=providerTypeName;MethodName=methodName;Order=order;}
        public string OwnerGuid { get; }
        public string Id { get; }
        public string TargetGuid { get; }
        public string ProviderTypeName { get; }
        public string MethodName { get; }
        public int Order { get; }
    }

    internal sealed class EarlyProviderRejectedException : Exception
    {
        internal readonly string Reason;
        internal EarlyProviderRejectedException(string reason){Reason=reason;}
    }
    internal sealed class EarlyProviderDependency
    {
        internal readonly string Guid;internal readonly Version Minimum;
        internal EarlyProviderDependency(string guid,Version minimum){Guid=guid;Minimum=minimum;}
    }
    internal sealed class EarlyProviderPlugin
    {
        internal readonly EarlyPluginIdentity Identity;
        internal readonly EarlyProviderDependency[] Dependencies;
        internal EarlyProviderPlugin(EarlyPluginIdentity identity,EarlyProviderDependency[] dependencies)
        {Identity=identity;Dependencies=dependencies;}
    }
    internal sealed class EarlyProviderModule
    {
        internal string Path,FullName,SimpleName;internal Guid Mvid;internal bool HasInitializer;
        internal readonly List<EarlyProviderDeclaration> Declarations=new List<EarlyProviderDeclaration>();
    }
    internal sealed class EarlyProviderDeclaration
    {
        internal string Owner,Id,Target,Type,Method;internal int Order;internal EarlyProviderModule Module;
    }

    // Startup-only metadata. Never scans directories, calls GetCustomAttributes, resolves Cecil references,
    // instantiates a plugin, or installs an AssemblyResolve/AssemblyLoad callback. The broker roots this catalog.
    internal sealed class EarlyProviderCatalog
    {
        private readonly Dictionary<string,EarlyProviderPlugin> selected=new Dictionary<string,EarlyProviderPlugin>(StringComparer.Ordinal);
        private readonly List<EarlyProviderModule> modules=new List<EarlyProviderModule>();
        private readonly Action<string> report;
        internal EarlyProviderCatalog(IEnumerable<EarlyProviderPlugin> plugins,Action<string> report)
        {
            this.report=report;
            foreach(var plugin in plugins)selected.Add(plugin.Identity.Guid,plugin);
            foreach(var plugin in selected.Values)
            {
                bool exists=false;foreach(var known in modules)if(SamePath(known.Path,plugin.Identity.AssemblyPath)){exists=true;break;}
                if(exists)continue;
                try{modules.Add(ReadModule(plugin.Identity.AssemblyPath));}
                catch{Report("Early provider metadata unreadable for selected plugin: "+plugin.Identity.Guid);}
            }
        }
        private static EarlyProviderModule ReadModule(string path)
        {
            using(var assembly=AssemblyDefinition.ReadAssembly(path,new ReaderParameters{ReadSymbols=false,ReadingMode=ReadingMode.Deferred}))
            {
                var module=assembly.MainModule;
                var result=new EarlyProviderModule{Path=System.IO.Path.GetFullPath(path),Mvid=module.Mvid,
                    FullName=assembly.Name.FullName,SimpleName=assembly.Name.Name};
                foreach(var type in module.Types)if(type.Name=="<Module>")foreach(var method in type.Methods)
                    if(method.Name==".cctor")result.HasInitializer=true;
                foreach(var attribute in assembly.CustomAttributes)
                {
                    if(attribute.AttributeType.FullName!=typeof(EarlyPluginActivationProviderAttribute).FullName)continue;
                    // Reject a lookalike attribute from a different assembly; no type resolution is necessary.
                    if(!(attribute.AttributeType.Scope is AssemblyNameReference scope) ||
                        scope.FullName!=typeof(EarlyPluginActivationProviderAttribute).Assembly.GetName().FullName)continue;
                    try
                    {
                        if(attribute.ConstructorArguments.Count!=6 || attribute.HasFields || attribute.HasProperties)continue;
                        var args=attribute.ConstructorArguments;
                        var d=new EarlyProviderDeclaration{Owner=(string)args[0].Value,Id=(string)args[1].Value,Target=(string)args[2].Value,
                            Type=(string)args[3].Value,Method=(string)args[4].Value,Order=(int)args[5].Value,Module=result};
                        if(Valid(d.Owner,96) && Valid(d.Id,96) && Valid(d.Target,96) && Valid(d.Type,512) && Valid(d.Method,128))
                            result.Declarations.Add(d);
                    }
                    catch{/* Invalid declaration cannot authorize code or disable another consumer. */}
                }
                return result;
            }
        }
        internal void Register(EarlyPluginActivationBroker broker)
        {
            var declarations=new List<EarlyProviderDeclaration>();
            // Invalid cross-DLL owner claims must not poison another DLL's legitimate duplicate group.
            foreach(var module in modules)foreach(var declaration in module.Declarations)
            {
                if(!selected.TryGetValue(declaration.Owner,out var owner) || !SamePath(owner.Identity.AssemblyPath,module.Path))
                {Report("Early provider declaration has no same-DLL selected owner.");continue;}
                declarations.Add(declaration);
            }
            declarations.Sort((a,b)=>{int c=a.Order.CompareTo(b.Order);if(c!=0)return c;
                c=StringComparer.Ordinal.Compare(a.Owner,b.Owner);if(c!=0)return c;
                c=StringComparer.Ordinal.Compare(a.Id,b.Id);if(c!=0)return c;
                c=StringComparer.Ordinal.Compare(a.Type,b.Type);return c!=0?c:StringComparer.Ordinal.Compare(a.Method,b.Method);});
            // Duplicate declarations are all rejected instead of letting Cecil attribute order select a winner.
            var counts=new Dictionary<string,int>(StringComparer.Ordinal);
            foreach(var d in declarations){string key=d.Owner+"\n"+d.Id;counts.TryGetValue(key,out int count);counts[key]=count+1;}
            foreach(var d in declarations)
            {
                if(counts[d.Owner+"\n"+d.Id]!=1){Report("Early provider declaration has no unique owner-local ID.");continue;}
                if(!broker.AddProvider(d.Owner,d.Id,d.Target,d.Order,(context,failed)=>Invoke(d,context,failed)))
                    Report("Early provider owner-local ID already registered: "+d.Owner+"/"+d.Id);
            }
        }
        internal bool CheckClosure(string owner,ISet<string> failed,out string reason)
        {
            if(failed==null){reason="Loader failure set unavailable.";return false;}
            var pending=new Stack<Tuple<string,bool>>();var active=new HashSet<string>(StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.Ordinal);pending.Push(Tuple.Create(owner,false));
            while(pending.Count!=0)
            {
                var step=pending.Pop();string guid=step.Item1;
                if(step.Item2){active.Remove(guid);seen.Add(guid);continue;}
                if(seen.Contains(guid))continue;
                if(!active.Add(guid)){reason="Selected hard dependency cycle.";return false;}
                pending.Push(Tuple.Create(guid,true));
                if(!selected.TryGetValue(guid,out var plugin) || failed.Contains(guid) || FindModule(plugin.Identity.AssemblyPath)==null)
                {reason="Selected owner/dependency metadata absent or already failed: "+guid;return false;}
                foreach(var dependency in plugin.Dependencies)
                {
                    if(!selected.TryGetValue(dependency.Guid,out var required) || !Version.TryParse(required.Identity.Version,out var actual) ||
                        dependency.Minimum==null || actual<dependency.Minimum)
                    {reason="Selected hard dependency absent or below its minimum: "+dependency.Guid;return false;}
                    pending.Push(Tuple.Create(dependency.Guid,false));
                }
            }
            reason=string.Empty;return true;
        }
        private EarlyProviderModule FindModule(string path)
        {foreach(var module in modules)if(SamePath(module.Path,path))return module;return null;}
        private void CheckLoaded(string owner,string target)
        {
            // Only owner/target and selected hard closure names can affect this provider. A selected unrelated
            // plugin must not veto it merely by having another path/version with the same assembly simple name.
            var relevant=new HashSet<string>(StringComparer.Ordinal);
            var visited=new HashSet<string>(StringComparer.Ordinal);var pending=new Stack<string>();
            pending.Push(owner);pending.Push(target);
            while(pending.Count!=0)
            {
                string guid=pending.Pop();if(!visited.Add(guid) || !selected.TryGetValue(guid,out var plugin))continue;
                var module=FindModule(plugin.Identity.AssemblyPath);if(module!=null)relevant.Add(module.SimpleName);
                foreach(var dependency in plugin.Dependencies)pending.Push(dependency.Guid);
            }
            foreach(var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if(!relevant.Contains(loaded.GetName().Name))continue;
                bool matched=false;
                foreach(var module in modules)
                    if(loaded.FullName==module.FullName && SamePath(loaded.Location,module.Path) &&
                        loaded.ManifestModule.ModuleVersionId==module.Mvid){matched=true;break;}
                if(!matched)
                    throw new EarlyProviderRejectedException("Loaded relevant assembly has no matching selected path/identity/MVID: "+loaded.GetName().Name);
            }
        }
        private void Invoke(EarlyProviderDeclaration declaration,EarlyPluginActivationContext context,ISet<string> failed)
        {
            if(!CheckClosure(declaration.Owner,failed,out string reason))throw new EarlyProviderRejectedException(reason);
            if(declaration.Module.HasInitializer)throw new EarlyProviderRejectedException("Consumer module initializer is not early-safe.");
            CheckDisk(declaration.Module);CheckLoaded(declaration.Owner,declaration.Target);
            // This is the selected normal consumer, loaded only now. Never instantiate its BepInEx entry.
            var assembly=Assembly.LoadFile(declaration.Module.Path);
            if(!SamePath(assembly.Location,declaration.Module.Path) || assembly.FullName!=declaration.Module.FullName ||
                assembly.ManifestModule.ModuleVersionId!=declaration.Module.Mvid)
                throw new EarlyProviderRejectedException("Resolved provider is not the selected consumer module.");
            var type=assembly.GetType(declaration.Type,false);
            if(!IsManifestProviderType(type,assembly,declaration.Module) || type.ContainsGenericParameters || type.IsGenericType)
                throw new EarlyProviderRejectedException("Declared provider type is unavailable, generic or outside the inspected manifest DLL.");
            var method=type.GetMethod(declaration.Method,BindingFlags.Public|BindingFlags.Static|BindingFlags.DeclaredOnly,
                null,new[]{typeof(EarlyPluginActivationContext)},null);
            if(method==null || method.ReturnType!=typeof(void) || method.ContainsGenericParameters || method.IsGenericMethod ||
                method.GetMethodBody()==null)
                throw new EarlyProviderRejectedException("Provider must declare public static void(EarlyPluginActivationContext).");
            var callback=(Action<EarlyPluginActivationContext>)Delegate.CreateDelegate(typeof(Action<EarlyPluginActivationContext>),method);
            RuntimeHelpers.PrepareDelegate(callback);
            CheckDisk(declaration.Module);CheckLoaded(declaration.Owner,declaration.Target);
            if(!CheckClosure(declaration.Owner,failed,out reason))throw new EarlyProviderRejectedException(reason);
            try{callback(context);}finally{CheckLoaded(declaration.Owner,declaration.Target);}
        }
        // Assembly membership alone also includes types in linked netmodules. The audited metadata, path and
        // initializer guard belong to this manifest DLL only; no additional module may supply provider code.
        internal static bool IsManifestProviderType(Type type,Assembly assembly,EarlyProviderModule expected)=>
            type!=null && assembly!=null && expected!=null && type.Assembly==assembly &&
            type.Module==assembly.ManifestModule && type.Module.ModuleVersionId==expected.Mvid &&
            SamePath(type.Module.FullyQualifiedName,expected.Path);
        private static void CheckDisk(EarlyProviderModule expected)
        {
            EarlyProviderModule actual;
            try{actual=ReadModule(expected.Path);}catch{throw new EarlyProviderRejectedException("Selected provider module became unreadable.");}
            if(actual.Mvid!=expected.Mvid || actual.FullName!=expected.FullName || actual.HasInitializer)
                throw new EarlyProviderRejectedException("Selected provider disk identity changed after discovery.");
        }
        private void Report(string message){try{report?.Invoke(message);}catch{}}
        private static bool Valid(string value,int maximum)=>!string.IsNullOrWhiteSpace(value) && value.Length<=maximum && value.IndexOf('\n')<0 && value.IndexOf('\r')<0;
        private static bool SamePath(string a,string b)=>string.Equals(System.IO.Path.GetFullPath(a),System.IO.Path.GetFullPath(b),
            System.IO.Path.DirectorySeparatorChar=='\\'?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal);
    }
}

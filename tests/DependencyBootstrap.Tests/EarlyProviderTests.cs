using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace APIShared.DependencyBootstrap
{
    public static class EarlyProviderFixtureSupport
    {
        public static void Throw(){throw new Unformattable();}
        private sealed class Unformattable : Exception
        {public override string Message=>throw new Exception();public override string ToString()=>throw new Exception();}
    }
    internal static class EarlyProviderTests
    {
        private static int checks;
        private static void Check(bool value,string reason){checks++;if(!value)throw new Exception(reason);}
        private static EarlyPluginIdentity Identity(string guid,string path,string type="Consumer.Entry",string version="1.2.3")=>
            new EarlyPluginIdentity(guid,version,path,type);
        private static EarlyProviderPlugin Plugin(string guid,string path,params EarlyProviderDependency[] dependencies)=>
            new EarlyProviderPlugin(Identity(guid,path),dependencies);
        private static EarlyProviderCatalog Catalog(string consumer,string future=null,EarlyProviderDependency[] dependencies=null)
        {
            var entries=new List<EarlyProviderPlugin>{new EarlyProviderPlugin(Identity("owner",consumer),dependencies??Array.Empty<EarlyProviderDependency>()),
                new EarlyProviderPlugin(Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName),Array.Empty<EarlyProviderDependency>())};
            if(future!=null)entries.Add(Plugin("future",future));
            return new EarlyProviderCatalog(entries,_=>{throw new Exception("diagnostic sink failure");});
        }
        private static EarlyPluginActivationBroker Broker(EarlyProviderCatalog catalog,string consumer,Action<EarlyPluginActivationContext> existing=null,params EarlyPluginIdentity[] additional)
        {
            var broker=new EarlyPluginActivationBroker(_=>{});
            if(existing!=null)Check(broker.TryRegister("owner","declared","target",existing,out _,out _),"Existing registration refused");
            broker.BeginSelection();catalog.Register(broker);
            var identities=new List<EarlyPluginIdentity>{Identity("owner",consumer),Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName)};
            identities.AddRange(additional);broker.BindSelected(identities);
            return broker;
        }
        // Emits a real external consumer with the production attribute and static delegate signature.
        // The normal test process executes the actual production LoadFile/binding path; no fake invoker is substituted.
        private static string Emit(string root,string owner="owner",bool initializer=false,bool wrongReturn=false,bool duplicate=false,bool throwing=false,string assemblyName=null,Version version=null)
        {
            Directory.CreateDirectory(root);
            string name=assemblyName??("EarlyProviderFixture"+Guid.NewGuid().ToString("N"));string path=Path.Combine(root,name+".dll");
            using(var assembly=AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name,version??new Version(1,0)),name,ModuleKind.Dll))
            {
                var module=assembly.MainModule;
                var type=new TypeDefinition("Consumer","Entry",Mono.Cecil.TypeAttributes.Public|Mono.Cecil.TypeAttributes.Class,module.TypeSystem.Object);
                module.Types.Add(type);
                var calls=new FieldDefinition("Calls",Mono.Cecil.FieldAttributes.Public|Mono.Cecil.FieldAttributes.Static,module.TypeSystem.Int32);type.Fields.Add(calls);
                var method=new MethodDefinition("Observe",Mono.Cecil.MethodAttributes.Public|Mono.Cecil.MethodAttributes.Static,
                    wrongReturn?module.TypeSystem.Int32:module.TypeSystem.Void);
                method.Parameters.Add(new ParameterDefinition(module.ImportReference(typeof(EarlyPluginActivationContext))));type.Methods.Add(method);
                var il=method.Body.GetILProcessor();il.Emit(OpCodes.Ldsfld,calls);il.Emit(OpCodes.Ldc_I4_1);il.Emit(OpCodes.Add);il.Emit(OpCodes.Stsfld,calls);
                if(throwing)il.Emit(OpCodes.Call,module.ImportReference(typeof(EarlyProviderFixtureSupport).GetMethod("Throw")));
                if(wrongReturn)il.Emit(OpCodes.Ldc_I4_0);il.Emit(OpCodes.Ret);
                var constructor=typeof(EarlyPluginActivationProviderAttribute).GetConstructor(new[]{typeof(string),typeof(string),typeof(string),typeof(string),typeof(string),typeof(int)});
                for(int i=0;i<(duplicate?2:1);i++)
                {
                    var attribute=new CustomAttribute(module.ImportReference(constructor));
                    foreach(string value in new[]{owner,"declared","target","Consumer.Entry","Observe"})
                        attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.String,value));
                    attribute.ConstructorArguments.Add(new CustomAttributeArgument(module.TypeSystem.Int32,-10));assembly.CustomAttributes.Add(attribute);
                }
                if(initializer)
                {
                    var init=new MethodDefinition(".cctor",Mono.Cecil.MethodAttributes.Private|Mono.Cecil.MethodAttributes.Static|
                        Mono.Cecil.MethodAttributes.SpecialName|Mono.Cecil.MethodAttributes.RTSpecialName,module.TypeSystem.Void);
                    module.Types[0].Methods.Add(init);init.Body.GetILProcessor().Emit(OpCodes.Ret);
                }
                assembly.Write(path);
            }
            return path;
        }
        internal static void Run()
        {
            var usage=(AttributeUsageAttribute)Attribute.GetCustomAttribute(typeof(EarlyPluginActivationProviderAttribute),typeof(AttributeUsageAttribute));
            Check(usage.ValidOn==AttributeTargets.Assembly && usage.AllowMultiple,"Public declaration target/multiplicity changed");
            var constructor=typeof(EarlyPluginActivationProviderAttribute).GetConstructors().Single();
            Check(constructor.GetParameters().Length==6 && constructor.GetParameters()[5].IsOptional &&
                (int)constructor.GetParameters()[5].DefaultValue==0,"Public optional order contract changed");
            string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"early-provider-fixtures");Directory.CreateDirectory(root);
            // Loaded .NET Framework DLLs may stay locked until process exit. The existing test driver owns final cleanup.
            string good=Emit(root),future=Emit(root,owner:"unselected-future-owner");
            string simple=Path.GetFileNameWithoutExtension(good),futureSimple=Path.GetFileNameWithoutExtension(future);
            var catalog=Catalog(good,future,new[]{new EarlyProviderDependency("future",new Version(1,0))});
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==simple),"Metadata discovery loaded consumer");
            Check(catalog.CheckClosure("owner",new HashSet<string>(),out _),"Valid future dependency selection refused");
            var unversioned=new BepInEx.BepInDependency("future",BepInEx.BepInDependency.DependencyFlags.HardDependency);
            Check(unversioned.MinimumVersion!=null && unversioned.MinimumVersion==new Version(),"Actual unversioned constructor no longer supplies0.0");
            Check(Catalog(good,future,new[]{new EarlyProviderDependency("future",unversioned.MinimumVersion)}).CheckClosure("owner",new HashSet<string>(),out _),
                "Legitimate unversioned hard dependency refused");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==futureSimple),"Closure check loaded future dependency/Awake");
            Check(!catalog.CheckClosure("owner",new HashSet<string>{"future"},out _),"Known transitive failure accepted");
            Check(!Catalog(good,null,new[]{new EarlyProviderDependency("missing",new Version(1,0))}).CheckClosure("owner",new HashSet<string>(),out _),"Missing hard dependency accepted");
            Check(!Catalog(good,future,new[]{new EarlyProviderDependency("future",new Version(9,0))}).CheckClosure("owner",new HashSet<string>(),out _),"Below-minimum dependency accepted");
            var cyclic=new EarlyProviderCatalog(new[]{new EarlyProviderPlugin(Identity("owner",good),new[]{new EarlyProviderDependency("future",new Version(1,0))}),
                new EarlyProviderPlugin(Identity("future",future),new[]{new EarlyProviderDependency("owner",new Version(1,0))})},null);
            Check(!cyclic.CheckClosure("owner",new HashSet<string>(),out _),"Hard dependency cycle accepted");

            // Legacy preloader callback wins a duplicate owner-local ID; declaration cannot replace it.
            int legacy=0;var conflict=Broker(Catalog(good),good,_=>legacy++);
            conflict.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check(legacy==1 && !AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==simple),"Declaration replaced preloader callback or loaded eagerly");
            foreach(string rejected in new[]{Emit(root,owner:"not-selected"),Emit(root,initializer:true),Emit(root,wrongReturn:true),Emit(root,duplicate:true)})
            {
                var denied=Broker(Catalog(rejected),rejected);denied.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
                var loaded=AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(rejected));
                Check(loaded==null || (int)loaded.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==0,"Invalid declaration/signature/module initializer invoked");
            }
            string foreignOwner=Emit(root);
            var sameDllGuard=new EarlyProviderCatalog(new[]{Plugin("owner",typeof(EarlyProviderTests).Assembly.Location),
                new EarlyProviderPlugin(Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName),Array.Empty<EarlyProviderDependency>()),
                Plugin("other",foreignOwner)},null);
            var crossOwner=Broker(sameDllGuard,typeof(EarlyProviderTests).Assembly.Location,null,Identity("other",foreignOwner));
            crossOwner.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(foreignOwner)),"Declaration borrowed selected owner from another DLL");
            string legitimate=Emit(root),borrowed=Emit(root); // Both declare owner/declared; borrowed DLL owns only another GUID.
            var coexistCatalog=new EarlyProviderCatalog(new[]{Plugin("owner",legitimate),Plugin("other",borrowed),
                new EarlyProviderPlugin(Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName),Array.Empty<EarlyProviderDependency>())},null);
            var coexist=Broker(coexistCatalog,legitimate,null,Identity("other",borrowed));
            coexist.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            var legitAssembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(legitimate));
            Check((int)legitAssembly.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==1,"Invalid cross-DLL duplicate poisoned legitimate declaration");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(borrowed)),"Borrowed duplicate DLL loaded");

            string shared="EarlyProviderShared"+Guid.NewGuid().ToString("N");
            string v1=Emit(Path.Combine(root,"v1"),assemblyName:shared,version:new Version(1,0));
            string v2=Emit(Path.Combine(root,"v2"),owner:"not-selected-other",assemblyName:shared,version:new Version(2,0));
            var actualV2=Assembly.LoadFile(v2);
            var versions=new EarlyProviderCatalog(new[]{Plugin("owner",v1),Plugin("other",v2),
                new EarlyProviderPlugin(Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName),Array.Empty<EarlyProviderDependency>())},null);
            // Test one target/provider only; the second selected GUID is an unrelated, already loaded module.
            var versionBroker=Broker(versions,v1,null,Identity("other",v2));
            versionBroker.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            var actualV1=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name==shared && a.GetName().Version.Major==1);
            Check((int)actualV1.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==1 &&
                (int)actualV2.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==0,"Known selected same-simple-name versions rejected or unrelated provider executed");

            string isolatedOwner=Emit(root),unrelated=Emit(root,owner:"unselected");
            string unrelatedCopy=Path.Combine(root,"elsewhere-"+Path.GetFileName(unrelated));File.Copy(unrelated,unrelatedCopy);
            Assembly.LoadFile(unrelatedCopy);
            var independent=new EarlyProviderCatalog(new[]{Plugin("owner",isolatedOwner),Plugin("unrelated",unrelated),
                new EarlyProviderPlugin(Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName),Array.Empty<EarlyProviderDependency>())},null);
            var independentBroker=Broker(independent,isolatedOwner,null,Identity("unrelated",unrelated));
            independentBroker.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            var independentAssembly=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(isolatedOwner));
            Check((int)independentAssembly.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==1,"Unrelated bad load globally disabled provider");
            string changed=Emit(root);var stale=Broker(Catalog(changed),changed);
            string replacement=changed+".new";
            using(var assembly=AssemblyDefinition.ReadAssembly(changed)){assembly.MainModule.Mvid=Guid.NewGuid();assembly.Write(replacement);}
            File.Copy(replacement,changed,true);File.Delete(replacement);
            stale.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(changed)),"Changed MVID loaded after snapshot");
            var failed=Broker(Catalog(good,future,new[]{new EarlyProviderDependency("future",new Version(1,0))}),good,null,Identity("future",future));
            failed.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>{"future"},true);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==simple),"Known failure did not prevent provider load");

            string wrongPath=Emit(root);string copy=Path.Combine(root,"copy-"+Path.GetFileName(wrongPath));File.Copy(wrongPath,copy);
            var alien=Assembly.LoadFile(copy);var misplaced=Broker(Catalog(wrongPath),wrongPath);
            misplaced.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check((int)alien.GetType("Consumer.Entry").GetField("Calls").GetValue(null)==0,"Wrong-path loaded identity was authorized");
            string throwing=Emit(root,throwing:true);var isolated=Broker(Catalog(throwing),throwing);int sibling=0;
            // Add sibling before selected-boundary closure via an independent broker.
            isolated=new EarlyPluginActivationBroker(_=>{throw new Exception("broken error sink");});
            Check(isolated.TryRegister("owner","sibling","target",_=>sibling++,out var siblingReceipt,out _),"Sibling registration failed");
            isolated.BeginSelection();Catalog(throwing).Register(isolated);
            isolated.BindSelected(new[]{Identity("owner",throwing),Identity("target",typeof(EarlyProviderTests).Assembly.Location,typeof(EarlyProviderTests).FullName)});
            isolated.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check(sibling==1 && siblingReceipt.State==EarlyPluginActivationState.Completed,"Provider exception/diagnostic blocked sibling");
            string closureOwner=Emit(root),closureDependency=Emit(root,owner:"unselected-dependency");
            string closureCopy=Path.Combine(root,"elsewhere-"+Path.GetFileName(closureDependency));File.Copy(closureDependency,closureCopy);
            Assembly.LoadFile(closureCopy);
            var wrongClosure=Broker(Catalog(closureOwner,closureDependency,new[]{new EarlyProviderDependency("future",new Version(1,0))}),
                closureOwner,null,Identity("future",closureDependency));
            wrongClosure.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==Path.GetFileNameWithoutExtension(closureOwner)),"Wrong-path required closure module authorized provider load");
            var actual=Broker(catalog,good,null,Identity("future",future));actual.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);
            var consumer=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetName().Name==simple);
            var count=consumer.GetType("Consumer.Entry").GetField("Calls");Check((int)count.GetValue(null)==1,"Actual bound provider did not run exactly once");
            actual.BeforePlugin(typeof(EarlyProviderTests),new HashSet<string>(),true);Check((int)count.GetValue(null)==1,"Provider replayed");
            Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a=>a.GetName().Name==futureSimple),"Provider binding required future dependency to be loaded");
            var fileProvider=consumer.GetType("Consumer.Entry");
            var expectedFile=new EarlyProviderModule{Path=good,Mvid=consumer.ManifestModule.ModuleVersionId};
            Check(EarlyProviderCatalog.IsManifestProviderType(fileProvider,consumer,expectedFile),"Actual file-backed manifest provider refused");
            // Desktop Framework can represent additional modules. This tests the real Type.Module/Assembly boundary;
            // it does not claim Unity-Mono accepts or executes multi-module consumer assemblies.
            var dynamicAssembly=AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("EarlyProviderModules"+Guid.NewGuid().ToString("N")),
                System.Reflection.Emit.AssemblyBuilderAccess.Run);
            dynamicAssembly.DefineDynamicModule("Manifest");
            var additional=dynamicAssembly.DefineDynamicModule("Additional");
            var linkedType=additional.DefineType("Linked.Provider",System.Reflection.TypeAttributes.Public).CreateType();
            var linkedAssembly=linkedType.Assembly;
            Check(linkedType.Assembly==linkedAssembly && linkedType.Module!=linkedAssembly.ManifestModule,"Multi-module counterexample precondition absent");
            var manifest=new EarlyProviderModule{Path=linkedAssembly.ManifestModule.FullyQualifiedName,Mvid=linkedAssembly.ManifestModule.ModuleVersionId};
            Check(!EarlyProviderCatalog.IsManifestProviderType(linkedType,linkedAssembly,manifest),"Same-assembly linked module escaped manifest-only provider contract");
            Console.WriteLine("PASS: early declared providers "+checks+" checks; actual Cecil discovery/broker/LoadFile/delegate; no plugin Awake or game invocation.");
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace APIShared.DependencyBootstrap
{
    internal static class EarlyPluginActivationTests
    {
        private static int checks;
        private static void Check(bool condition,string reason){checks++;if(!condition)throw new Exception(reason);}
        private static EarlyPluginIdentity Identity(string guid,string path=null,string type=null)=>
            new EarlyPluginIdentity(guid,"1.2.3",path??typeof(EarlyPluginActivationTests).Assembly.Location,type??typeof(EarlyPluginActivationTests).FullName);
        private static EarlyPluginActivationRegistration Add(EarlyPluginActivationBroker broker,string owner,string id,string target,
            Action<EarlyPluginActivationContext> callback,int order=0)
        {
            Check(broker.TryRegister(owner,id,target,callback,out var receipt,out string reason,order),reason);
            return receipt;
        }
        internal static void Run()
        {
            // Enum ordinals are public compiled-consumer identities, not an implementation-count test.
            foreach(var value in new[]{"Pending=0","AwaitingTarget=1","Running=2","Completed=3","CallbackFailed=4",
                "OwnerNotSelected=5","OwnerFailed=6","TargetNotSelected=7","TargetSkipped=8","Unavailable=9"})
            {
                var parts=value.Split('=');Check((int)Enum.Parse(typeof(EarlyPluginActivationState),parts[0])==int.Parse(parts[1]),"Public outcome ordinal changed");
            }
            var calls=new List<string>();
            int reports=0;
            var broker=new EarlyPluginActivationBroker(_=>{reports++;throw new InvalidOperationException("broken diagnostic sink");});
            EarlyPluginIdentity ownerA=Identity("a-owner",type:"Owner.A"),ownerZ=Identity("z-owner",type:"Owner.Z"),target=Identity("target");
            var identities=new List<EarlyPluginIdentity>{ownerZ,target,ownerA};
            var z=Add(broker,"z-owner","z","target",c=>{
                calls.Add("z");Check(c.Owner.Guid=="z-owner" && ReferenceEquals(c.Target,target),"Exact selected identities lost");
                Check(c.LoadedType==typeof(EarlyPluginActivationTests) && c.LoadedModuleVersionId==typeof(EarlyPluginActivationTests).Module.ModuleVersionId,
                    "Actual loaded type/MVID missing");
                Check(c.LoadedAssemblyName==typeof(EarlyPluginActivationTests).Assembly.FullName,"Actual assembly identity missing");
                Check(c.SelectedPlugins.Count==3 && c.Target.Version=="1.2.3" && Path.IsPathRooted(c.Target.AssemblyPath),"Package metadata lost");
            });
            var second=Add(broker,"a-owner","b","target",c=>calls.Add("b"));
            var first=Add(broker,"a-owner","a","target",c=>{
                calls.Add("a");
                var independent=Task.Run(()=>broker.TryRegister("a-owner","late","target",_=>{},out _,out _));
                Check(independent.Wait(2000) && !independent.Result,"Callback ran under registry lock or allowed late registration");
                throw new UnformattableException();
            });
            var priority=Add(broker,"z-owner","priority","target",c=>calls.Add("priority"),-1);
            Check(!broker.TryRegister("a-owner","a","target",_=>{},out _,out _),"Duplicate owner-local ID accepted");
            Check(broker.BeginSelection(),"Registered demand lost");
            broker.BindSelected(identities);identities.Clear();
            Check(first.State==EarlyPluginActivationState.AwaitingTarget,"Selection did not arm target");
            broker.BeforePlugin(typeof(EarlyPluginActivationTests),new HashSet<string>(),true);
            Check(calls.SequenceEqual(new[]{"priority","a","b","z"}),"Nondeterministic observer order or exception blocked siblings");
            Check(first.State==EarlyPluginActivationState.CallbackFailed && second.State==EarlyPluginActivationState.Completed &&
                z.State==EarlyPluginActivationState.Completed && priority.State==EarlyPluginActivationState.Completed && reports==1,"Completion/error isolation failed");
            broker.BeforePlugin(typeof(EarlyPluginActivationTests),new HashSet<string>(),true);
            Check(calls.Count==4,"Boundary replayed");
            broker.CompleteStartup();Check(z.State==EarlyPluginActivationState.Completed,"Completion overwritten at end");

            var missing=new EarlyPluginActivationBroker(null);
            var absentOwner=Add(missing,"absent","a","target",_=>{throw new Exception();});
            var absentTarget=Add(missing,"owner","b","absent-target",_=>{throw new Exception();});
            var skipped=Add(missing,"owner","c","target",_=>{throw new Exception();});
            missing.BindSelected(new[]{Identity("owner",type:"Owner"),Identity("target")});
            Check(absentOwner.State==EarlyPluginActivationState.OwnerNotSelected && absentTarget.State==EarlyPluginActivationState.TargetNotSelected,
                "Unselected owner or target accepted");
            missing.CompleteStartup();Check(skipped.State==EarlyPluginActivationState.TargetSkipped,"Skipped target not explicit");

            var failed=new EarlyPluginActivationBroker(null);
            var deadOwner=Add(failed,"owner","a","target",_=>{throw new Exception();});
            failed.BindSelected(new[]{Identity("owner",type:"Owner"),Identity("target")});
            failed.BeforePlugin(typeof(EarlyPluginActivationTests),new HashSet<string>{"owner"},true);
            Check(deadOwner.State==EarlyPluginActivationState.OwnerFailed,"Failed selected owner called");

            var mismatch=new EarlyPluginActivationBroker(null);int wrongCalls=0;
            var wrong=Add(mismatch,"owner","a","target",_=>wrongCalls++);
            mismatch.BindSelected(new[]{Identity("owner",type:"Owner"),Identity("target",path:Path.Combine(Path.GetTempPath(),"other.dll"))});
            mismatch.BeforePlugin(typeof(EarlyPluginActivationTests),new HashSet<string>(),true);
            mismatch.CompleteStartup();Check(wrongCalls==0 && wrong.State==EarlyPluginActivationState.TargetSkipped,"Wrong loaded assembly path accepted");

            var invalidPair=new EarlyPluginActivationBroker(null);
            var refused=Add(invalidPair,"owner","a","target",_=>{throw new Exception();});
            invalidPair.BindSelected(new[]{Identity("owner",type:"Owner"),Identity("target")});
            invalidPair.BeforePlugin(typeof(EarlyPluginActivationTests),new HashSet<string>(),false);
            Check(refused.State==EarlyPluginActivationState.Unavailable,"Unverified bootstrap pair authorized callback");

            var empty=new EarlyPluginActivationBroker(null);
            Check(!empty.BeginSelection(),"Empty registry invented demand");
            Check(!empty.TryRegister("owner","late","target",_=>{},out _,out string lateReason) && lateReason.Contains("boundary"),
                "First late registration after zero-demand snapshot accepted");
            var unavailable=new EarlyPluginActivationBroker(null);
            var pending=Add(unavailable,"owner","a","target",_=>{});
            unavailable.Unavailable("Loader unavailable");
            Check(pending.State==EarlyPluginActivationState.Unavailable && !unavailable.TryRegister("owner","b","target",_=>{},out _,out _),
                "Unavailable loader accepted new work");
            var noBoundary=new EarlyPluginActivationBroker(null);
            var never=Add(noBoundary,"owner","a","target",_=>{});noBoundary.CompleteStartup();
            Check(never.State==EarlyPluginActivationState.Unavailable,"Missing selection boundary not explicit");
            Console.WriteLine("PASS: early plugin activation "+checks+" checks; actual coordinator/type/MVID, identity/order/lifetime/late-boundary/error contracts; no plugin Awake or game invocation.");
        }
        private sealed class UnformattableException : Exception
        {
            public override string Message=>throw new Exception("Message must not be read");
            public override string ToString()=>throw new Exception("ToString must not be called");
        }
    }
}

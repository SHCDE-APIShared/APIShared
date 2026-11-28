using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using APIShared;
using APIShared.Units;
using APIShared.Units.AiMilitary.Orders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.Detours;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Units;
using SHCDESE.Interop;
using RedBird.Core.Memory;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using RedBird.X64.Assembly;
using System.Runtime.InteropServices;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass,DoNotParallelize]
    public sealed class UnitMoveEventsTests
    {
        private static readonly BulkUnitDetours.c_game_unit_issueorder_movehere_delegate Stub=Native;
        private static Func<int,int,int,int,long> native;
        private static Action<UnitMoveHereEventArgs> raise;
        private static long Native(NativePointer<GameUnitManager> manager,int unit,int x,int y,int extra)=>native(unit,x,y,extra);
        private static BulkUnitDetours.c_game_unit_issueorder_movehere_delegate Original()=>Stub;
        private static void Raise(object hook,UnitMoveHereEventArgs args)=>raise?.Invoke(args);
        private sealed class Observer:IUnitMoveObserver,IUnitMoveInvocation
        {
            internal readonly List<UnitMoveContext> Begins=new List<UnitMoveContext>();
            internal readonly List<UnitMoveCompletion> Ends=new List<UnitMoveCompletion>();
            internal Action<UnitMoveContext> Before;
            internal Action<UnitMoveCompletion> After;
            public IUnitMoveInvocation TryBegin(UnitMoveContext context){Begins.Add(context);Before?.Invoke(context);return this;}
            public void Complete(UnitMoveCompletion completion){Ends.Add(completion);After?.Invoke(completion);}
        }
        private sealed class Harness:IDisposable
        {
            private readonly FieldInfo field=typeof(UnitMoveRuntime).GetField("broker",BindingFlags.NonPublic|BindingFlags.Static);
            private readonly object previous;
            internal long Session=9,Generation=3;
            internal int Calls;
            internal long NativeResult=1;
            internal readonly List<int> NativeIds=new List<int>();
            internal readonly UnitMoveBroker Broker;
            internal readonly BulkUnitDetours.c_game_unit_issueorder_movehere_delegate Wrapper;
            internal Harness(Action<Exception> report=null,Func<bool> adapterAvailable=null)
            {
                previous=field.GetValue(null);Broker=new UnitMoveBroker(()=>new UnitMoveWorld(Session,Generation),report,adapterAvailable);Broker.SetAvailable(true);field.SetValue(null,Broker);
                native=(unit,x,y,extra)=>{Calls++;NativeIds.Add(unit);return NativeResult;};raise=null;
                Wrapper=BuildWrapper();
            }
            internal long Call(int id=7,int x=10,int y=20,int extra=0)=>Wrapper(default,id,x,y,extra);
            internal Observer Observe(string id="observer",int order=0,Func<bool> enabled=null)
            {var observer=new Observer();Assert.IsTrue(UnitMoveEvents.TryRegister("tests",id,observer,out _,order,enabled));return observer;}
            internal bool Run(out UnitMoveRunResult result,Func<long> invoke=null,Func<bool> identity=null,int id=7,int x=10,int y=20)
                =>UnitMoveEvents.TryRun("rescue",id,x,y,0,identity??(()=>true),invoke??(()=>Call(id,x,y)),out result,out _);
            public void Dispose(){field.SetValue(null,previous);raise=null;native=null;}
        }
        private static BulkUnitDetours.c_game_unit_issueorder_movehere_delegate BuildWrapper()
        {
            var method=typeof(BulkUnitDetours).GetMethod("c_game_unit_issueorder_movehere_hook_impl");
            using(var definition=new DynamicMethodDefinition(method))
            using(var il=new ILContext(definition.Definition){ReferenceBag=RuntimeILReferenceBag.Instance})
            {
                AiMilitaryOrderBroker.TransformUnitMove(il);
                // Replace only external boundaries in the actual installed SE body. Production
                // Original decoration, effective Pre locals, Post arguments and finally stay intact.
                var getter=il.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="get_Original" && m.DeclaringType.FullName.Contains("DetourHandle"));
                Assert.IsTrue(getter.Previous.OpCode==OpCodes.Ldsfld||getter.Previous.OpCode==OpCodes.Ldsflda);
                getter.Previous.OpCode=OpCodes.Nop;getter.Previous.Operand=null;
                getter.OpCode=OpCodes.Call;getter.Operand=il.Import(typeof(UnitMoveEventsTests).GetMethod(nameof(Original),BindingFlags.NonPublic|BindingFlags.Static));
                int publications=0;
                foreach(var instruction in il.Body.Instructions)
                    if(instruction.Operand is MethodReference m && m.Name=="Raise" && m.DeclaringType.FullName.Contains("R3EventHook"))
                    {instruction.OpCode=OpCodes.Call;instruction.Operand=il.Import(typeof(UnitMoveEventsTests).GetMethod(nameof(Raise),BindingFlags.NonPublic|BindingFlags.Static));publications++;}
                Assert.AreEqual(2,publications,"Actual installed wrapper must publish distinct Pre and Post.");
                var wrapper=(BulkUnitDetours.c_game_unit_issueorder_movehere_delegate)definition.Generate().CreateDelegate(typeof(BulkUnitDetours.c_game_unit_issueorder_movehere_delegate));
                System.Runtime.CompilerServices.RuntimeHelpers.PrepareDelegate(wrapper);return wrapper;
            }
        }
        [TestMethod]
        public void ActualEffectivePreArgumentsAndOriginalResultsIgnoreLegacyPostArgumentsAndReturn()
        {
            using(var h=new Harness())
            {
                var observer=h.Observe();int postId=0;
                raise=args=>{if(args.Phase==EventHookPhase.Pre){args.UnitId=8;args.TileX=11;args.TileY=21;args.Unknown=4;}
                    else{postId=args.UnitId;args.ReturnValue=0;}};
                Assert.AreEqual(0L,h.Call());Assert.AreEqual(7,postId,"SE Post still publishes its original input ID.");
                var context=observer.Begins.Single();Assert.AreEqual(8,context.UnitId);Assert.AreEqual(11,context.X);Assert.AreEqual(21,context.Y);Assert.AreEqual(4,context.Extra);
                Assert.IsNull(context.OriginOwnerGuid);Assert.AreSame(context,observer.Ends.Single().Context);
                Assert.IsTrue(observer.Ends[0].NativeReturned);Assert.AreEqual(1L,observer.Ends[0].NativeResult);
                Assert.AreEqual(8,h.NativeIds.Single());
            }
            using(var h=new Harness()){h.NativeResult=0;raise=args=>{if(args.Phase==EventHookPhase.Post)args.ReturnValue=1;};
                Assert.IsFalse(h.Run(out var result));Assert.IsTrue(result.NativeReturned);Assert.AreEqual(0L,result.NativeResult);Assert.AreEqual(1L,result.WrapperResult);Assert.AreEqual(UnitMoveRunStatus.NativeRefused,result.Status);}
            using(var h=new Harness()){raise=args=>{if(args.Phase==EventHookPhase.Post)args.ReturnValue=0;};
                Assert.IsTrue(h.Run(out var result));Assert.AreEqual(1L,result.NativeResult);Assert.AreEqual(0L,result.WrapperResult);Assert.AreEqual(UnitMoveRunStatus.NativeSucceeded,result.Status);}
        }
        [TestMethod]
        public void GuardedRootKeepsIndependentNestedPreAndPostCommandsUnmodified()
        {
            using(var h=new Harness())
            {
                var observer=h.Observe();
                raise=args=>{if(args.UnitId!=7)return;if(args.Phase==EventHookPhase.Pre)Assert.AreEqual(1L,h.Call(8,30,40));
                    else Assert.AreEqual(1L,h.Call(9,50,60));};
                Assert.IsTrue(h.Run(out var result));Assert.AreEqual(1L,result.NativeResult);Assert.AreEqual(3,h.Calls);
                CollectionAssert.AreEqual(new[]{8,7,9},h.NativeIds);
                var root=observer.Begins.Single(c=>c.UnitId==7);Assert.AreEqual("rescue",root.OriginOwnerGuid);Assert.AreEqual(0L,root.ParentInvocationId);
                foreach(var child in observer.Begins.Where(c=>c.UnitId!=7)){Assert.IsNull(child.OriginOwnerGuid);Assert.AreEqual(root.InvocationId,child.ParentInvocationId);}
                Assert.AreEqual(3,observer.Ends.Count);
            }
            using(var h=new Harness())
            {
                var observer=h.Observe();
                raise=args=>{if(args.UnitId==7 && args.Phase==EventHookPhase.Pre)
                    Assert.IsTrue(UnitMoveEvents.TryRun("nested",8,30,40,0,()=>true,()=>h.Call(8,30,40),out var inner,out _));};
                Assert.IsTrue(h.Run(out _));Assert.AreEqual(2,h.Calls);
                Assert.AreEqual("nested",observer.Begins.Single(c=>c.UnitId==8).OriginOwnerGuid);
                Assert.AreEqual("rescue",observer.Begins.Single(c=>c.UnitId==7).OriginOwnerGuid);
                raise=null;h.Call(9);Assert.IsNull(observer.Begins.Last().OriginOwnerGuid);Assert.AreEqual(0L,observer.Begins.Last().ParentInvocationId);
            }
        }
        [TestMethod]
        public void RedirectVetoDuplicateAndThrowingPostCannotInventNativeRefusal()
        {
            using(var h=new Harness()){raise=args=>{if(args.Phase==EventHookPhase.Pre)args.UnitId=8;};
                Assert.IsFalse(h.Run(out var result));Assert.AreEqual(UnitMoveRunStatus.Redirected,result.Status);Assert.IsFalse(result.NativeInvoked);Assert.AreEqual(0,h.Calls);}
            using(var h=new Harness()){raise=args=>{if(args.Phase==EventHookPhase.Pre)args.SkipOriginalFunction=true;};
                Assert.IsFalse(h.Run(out var result));Assert.AreEqual(UnitMoveRunStatus.NoOriginal,result.Status);Assert.IsFalse(result.NativeReturned);Assert.AreEqual(0,h.Calls);}
            using(var h=new Harness()){Assert.IsFalse(h.Run(out var result,()=>{h.Call();return h.Call();}));
                Assert.AreEqual(UnitMoveRunStatus.MultipleCalls,result.Status);Assert.AreEqual(1,h.Calls);Assert.IsTrue(result.NativeReturned);Assert.AreEqual(1L,result.NativeResult);}
            using(var h=new Harness()){var failure=new InvalidOperationException("foreign post");raise=args=>{if(args.Phase==EventHookPhase.Post)throw failure;};
                Assert.IsTrue(h.Run(out var result));Assert.IsTrue(result.NativeReturned);Assert.AreEqual(1L,result.NativeResult);Assert.IsFalse(result.WrapperReturned);Assert.AreSame(failure,result.WrapperFailure);Assert.IsNull(result.Failure);}
            using(var h=new Harness()){var failure=new ArgumentException("native");native=(unit,x,y,extra)=>throw failure;
                Assert.IsFalse(h.Run(out var result));Assert.IsTrue(result.NativeInvoked);Assert.IsFalse(result.NativeReturned);Assert.AreSame(failure,result.Failure);Assert.AreSame(failure,result.WrapperFailure);Assert.AreEqual(UnitMoveRunStatus.NativeFailed,result.Status);}
            using(var h=new Harness()){var observer=h.Observe();var failure=new ArgumentException("native");native=(unit,x,y,extra)=>throw failure;
                Assert.AreSame(failure,Assert.ThrowsExactly<ArgumentException>(()=>h.Call()));
                Assert.AreSame(failure,observer.Ends.Single().Failure);native=(unit,x,y,extra)=>1;h.Call(8);
                Assert.AreEqual(0L,observer.Begins.Last().ParentInvocationId);Assert.IsNull(observer.Begins.Last().OriginOwnerGuid);}
        }
        [TestMethod]
        public void WorldAndIdentityAreRevalidatedAfterPreObserversAndPost()
        {
            using(var h=new Harness()){raise=args=>{if(args.Phase==EventHookPhase.Pre)h.Generation++;};
                Assert.IsFalse(h.Run(out var result));Assert.AreEqual(UnitMoveRunStatus.WorldChanged,result.Status);Assert.AreEqual(0,h.Calls);}
            using(var h=new Harness()){var observer=h.Observe();observer.Before=_=>h.Generation++;
                Assert.IsFalse(h.Run(out var result));Assert.AreEqual(0,h.Calls);Assert.AreEqual(UnitMoveRunStatus.WorldChanged,result.Status);
                Assert.IsFalse(observer.Ends.Single().NativeInvoked);Assert.IsFalse(observer.Ends[0].IsCurrentWorld);}
            using(var h=new Harness()){raise=args=>{if(args.Phase==EventHookPhase.Post)h.Generation++;};
                Assert.IsFalse(h.Run(out var result));Assert.IsTrue(result.NativeReturned);Assert.AreEqual(1L,result.NativeResult);Assert.AreEqual(UnitMoveRunStatus.WorldChanged,result.Status);}
            using(var h=new Harness()){bool identity=true;raise=args=>{if(args.Phase==EventHookPhase.Pre)identity=false;};
                Assert.IsFalse(h.Run(out var result,identity:()=>identity));Assert.AreEqual(UnitMoveRunStatus.IdentityChanged,result.Status);Assert.AreEqual(0,h.Calls);}
            using(var h=new Harness()){var failure=new Unformattable();Assert.IsFalse(h.Run(out var result,identity:()=>throw failure));
                Assert.AreEqual(UnitMoveRunStatus.IdentityChanged,result.Status);Assert.AreSame(failure,result.Failure);Assert.AreEqual(0,h.Calls);}
            using(var h=new Harness()){h.Observe();raise=args=>{if(args.Phase==EventHookPhase.Pre)h.Generation++;};
                Assert.AreEqual(0L,h.Call());Assert.AreEqual(0,h.Calls,"An observed legacy wrapper cannot enter a replacement world after Pre.");}
            using(var h=new Harness()){bool returned=false;
                Assert.IsFalse(h.Run(out var result,invoke:()=>{long value=h.Call();returned=true;return value;},identity:()=>{if(returned)h.Generation++;return true;}));
                Assert.AreEqual(UnitMoveRunStatus.WorldChanged,result.Status,"World is rechecked after the final caller identity predicate.");}
        }
        private sealed class Unformattable:Exception{public override string ToString()=>throw new Exception("format");}
        [TestMethod]
        public void ObserverOrderingPrivateCompletionsAndHostileReportersCannotAffectOriginal()
        {
            int reports=0;using(var h=new Harness(error=>{reports++;throw new Exception(error.ToString());}))
            {
                var order=new List<string>();var a=h.Observe("a",-1);var b=h.Observe("b",1);
                a.Before=_=>order.Add("a.pre");b.Before=_=>{order.Add("b.pre");throw new Unformattable();};a.After=_=>{order.Add("a.post");throw new Unformattable();};
                Assert.IsTrue(h.Run(out _));CollectionAssert.AreEqual(new[]{"a.pre","b.pre","a.post"},order);Assert.AreEqual(1,h.Calls);Assert.AreEqual(2,reports);
                Assert.IsTrue(h.Run(out _));Assert.AreEqual(2,reports,"Registration failures are rate-limited and reporters are isolated.");
                Assert.IsFalse(UnitMoveEvents.TryRegister("tests","a",a,out _));
            }
        }
        [TestMethod]
        public void ExistingAiReceiptsRetainNativeResultAndCurrentWorldAdmission()
        {
            using(var h=new Harness())
            {
                h.Observe();raise=args=>{if(args.Phase==EventHookPhase.Post)args.ReturnValue=0;};
                Assert.IsTrue(AiUnitMoveReceipts.TryRun(7,10,20,0,()=>h.Call(),out _));
                h.NativeResult=0;raise=args=>{if(args.Phase==EventHookPhase.Post)args.ReturnValue=1;};
                Assert.IsFalse(AiUnitMoveReceipts.TryRun(7,10,20,0,()=>h.Call(),out _,out bool refused));Assert.IsTrue(refused);
                using(AiRecruitMutationReceipts.Enter(()=>false))
                {Assert.IsFalse(h.Run(out var result));Assert.IsFalse(result.NativeInvoked);Assert.AreEqual(UnitMoveRunStatus.NoOriginal,result.Status);}
                Assert.AreEqual(2,h.Calls);
            }
        }
        [TestMethod]
        public void ActualDormantWrapperAddsZeroAllocationsAndUnknownIlOrHashFailsClosed()
        {
            int guardReads=0;
            using(var h=new Harness(adapterAvailable:()=>{guardReads++;return true;}))
            {
                // SE itself allocates Pre/Post args. Measure the production owner kernel and
                // outer frame boundary directly to isolate the added dormant infrastructure.
                native=(unit,x,y,extra)=>1;
                var invoke=(Func<BulkUnitDetours.c_game_unit_issueorder_movehere_delegate,NativePointer<GameUnitManager>,int,int,int,int,long>)
                    Delegate.CreateDelegate(typeof(Func<BulkUnitDetours.c_game_unit_issueorder_movehere_delegate,NativePointer<GameUnitManager>,int,int,int,int,long>),
                        typeof(AiMilitaryOrderBroker).GetMethod("InvokeUnitMove",BindingFlags.NonPublic|BindingFlags.Static));
                Action pass=()=>{var frame=UnitMoveRuntime.Enter();try{
                    if(invoke(Stub,default,7,10,20,0)!=1)throw new InvalidOperationException("Dormant original changed.");
                }finally{UnitMoveRuntime.Exit(frame);}};
                for(int i=0;i<100;i++)pass();long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;i++)pass();Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
                Assert.AreEqual(0,guardReads,"No-demand calls must not read the native ownership span.");
                h.Observe(enabled:()=>false);int priorReads=guardReads;for(int i=0;i<100;i++)pass();before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;i++)pass();Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
                Assert.AreEqual(priorReads,guardReads,"Disabled observers must not read the native ownership span.");
                Assert.ThrowsExactly<InvalidOperationException>(()=>AiMilitaryOrderBroker.InitializeUnitMove(null,"unknown hash"));
            }
            using(var definition=new DynamicMethodDefinition(typeof(BulkUnitDetours).GetMethod("c_game_unit_issueorder_movehere_hook_impl")))
            using(var il=new ILContext(definition.Definition){ReferenceBag=RuntimeILReferenceBag.Instance})
            {
                var getter=il.Body.Instructions.Single(i=>i.Operand is MethodReference m && m.Name=="get_Original");getter.OpCode=OpCodes.Nop;getter.Operand=null;
                Assert.ThrowsExactly<InvalidOperationException>(()=>AiMilitaryOrderBroker.TransformUnitMove(il));
            }
        }
        [TestMethod]
        public void PublishedSourceAvailabilityIsIndependentOfAiGameplayAndRechecksLiveAdapter()
        {
            bool applied=true;var broker=new UnitMoveBroker(()=>new UnitMoveWorld(1,2),null,()=>applied);broker.SetAvailable(true);
            Assert.IsTrue(broker.IsAvailable);applied=false;bool invoked=false;
            Assert.IsFalse(broker.TryRun("owner",7,10,20,0,()=>true,()=>{invoked=true;return 1;},out var result,out _));
            Assert.AreEqual(UnitMoveRunStatus.Unavailable,result.Status);Assert.IsFalse(invoked);
            var ready=typeof(AiMilitaryOrderBroker).GetField("ready",BindingFlags.Static|BindingFlags.NonPublic);object old=ready.GetValue(null);
            try{ready.SetValue(null,0);using(var h=new Harness()){Assert.IsTrue(UnitMoveEvents.IsAvailable);Assert.IsTrue(h.Run(out _));}}
            finally{ready.SetValue(null,old);}
        }
        [TestMethod]
        public void InvalidOrUnidentifiedGuardedRequestsNeverExecuteCallerAndReceiptsCompleteAfterDisable()
        {
            using(var h=new Harness())
            {
                bool called=false;
                Assert.IsFalse(UnitMoveEvents.TryRun("owner",0,10,20,0,()=>true,()=>{called=true;return 1;},out var invalid,out _));
                Assert.AreEqual(UnitMoveRunStatus.InvalidRequest,invalid.Status);Assert.IsFalse(called);
                h.Session=0;Assert.IsFalse(h.Run(out var unavailable,()=>{called=true;return 1;}));
                Assert.AreEqual(UnitMoveRunStatus.UnidentifiedWorld,unavailable.Status);Assert.IsFalse(called);
                h.Session=9;bool enabled=true;var observer=h.Observe(enabled:()=>enabled);observer.Before=_=>enabled=false;
                Assert.IsTrue(h.Run(out _));Assert.AreEqual(1,observer.Begins.Count);Assert.AreEqual(1,observer.Ends.Count);
                Assert.IsTrue(observer.Ends[0].NativeReturned);
            }
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long PrivateMove(IntPtr manager,int unit,int x,int y,int extra);
        [DllImport("kernel32.dll",SetLastError=true)]
        private static extern bool VirtualProtect(IntPtr address,UIntPtr length,uint protection,out uint previous);
        [TestMethod]
        public void MissingDisabledOrWrongTargetSeNativeOwnerRejectsBeforeInvokingCaller()
        {
            using(var target=ExecutableFunction.Create(a=>{for(int i=0;i<16;i++)a.nop();a.mov(eax,1);a.ret();}))
            {
                NativeDetour<PrivateMove> hook=null;PrivateMove callback=(manager,unit,x,y,extra)=>1;
                try
                {
                    ulong address=unchecked((ulong)target.EntryPoint.ToInt64());
                    var options=new NativeDetourOptions{AllowedSchemes=DetourScheme.Indirect,FollowJumps=false,FunctionScanLimit=32,SuspendThreadsDuringPatch=false};
                    var request=new DetourRequest<PrivateMove>{Name="Private SE move ownership fixture",TargetAddress=address,Callback=callback};
                    hook=(NativeDetour<PrivateMove>)new NativeDetourBackend(options).CreateDetour(in request);
                    var broker=new UnitMoveBroker(()=>new UnitMoveWorld(1,2),null,()=>UnitMoveRuntime.NativeOwnerReady(true,hook,address));broker.SetAvailable(true);
                    bool invoked=false;
                    Assert.IsFalse(broker.TryRun("owner",7,10,20,0,()=>true,()=>{invoked=true;return 1;},out var result,out _));
                    Assert.IsFalse(invoked);Assert.AreEqual(UnitMoveRunStatus.Unavailable,result.Status);
                    Assert.IsFalse(UnitMoveRuntime.NativeOwnerReady<PrivateMove>(true,null,address));
                    hook.Enable();Assert.IsTrue(broker.IsAvailable);
                    Assert.IsFalse(UnitMoveRuntime.NativeOwnerReady(false,hook,address));
                    Assert.IsFalse(UnitMoveRuntime.NativeOwnerReady(true,hook,address+1));
                    hook.Disable();Assert.IsFalse(broker.IsAvailable);
                    Assert.IsFalse(broker.TryRun("owner",7,10,20,0,()=>true,()=>{invoked=true;return 1;},out _,out _));Assert.IsFalse(invoked);
                }
                finally{hook?.Dispose();GC.KeepAlive(callback);} // Only this private fixture can execute these bytes.
            }
        }
        [TestMethod]
        public void ForeignLiveEntryAndJumpSlotWritesDisableBeforeInvokeDespiteInstalledMetadata()
        {
            using(var target=ExecutableFunction.Create(a=>{for(int i=0;i<16;i++)a.nop();a.mov(eax,1);a.ret();}))
            {
                NativeDetour<PrivateMove> hook=null;PrivateMove callback=(manager,unit,x,y,extra)=>1;
                try
                {
                    ulong address=unchecked((ulong)target.EntryPoint.ToInt64());
                    var options=new NativeDetourOptions{AllowedSchemes=DetourScheme.Indirect,FollowJumps=false,FunctionScanLimit=32,SuspendThreadsDuringPatch=false};
                    var request=new DetourRequest<PrivateMove>{Name="Private SE live owner fixture",TargetAddress=address,Callback=callback};
                    hook=(NativeDetour<PrivateMove>)new NativeDetourBackend(options).CreateDetour(in request);hook.Enable();
                    int reports=0;
                    Assert.IsTrue(UnitMoveNativeOwner<PrivateMove>.TryCreate(true,hook,address,_=>reports++,out var owner,out _));
                    var broker=new UnitMoveBroker(()=>new UnitMoveWorld(1,2),null,()=>owner.IsCurrent(true,hook));broker.SetAvailable(true);
                    Assert.IsTrue(broker.IsAvailable);
                    byte original=Marshal.ReadByte(target.EntryPoint);
                    Assert.IsTrue(VirtualProtect(target.EntryPoint,new UIntPtr(1),0x40,out uint old));
                    try
                    {
                        Marshal.WriteByte(target.EntryPoint,(byte)(original^1));
                        Assert.IsTrue(UnitMoveRuntime.NativeOwnerReady(true,hook,address),"Installed metadata intentionally remains true after foreign overwrite.");
                        Assert.IsFalse(UnitMoveNativeOwner<PrivateMove>.TryCreate(true,hook,address,null,out _,out _),"Already overwritten startup bytes cannot approve themselves.");
                        bool invoked=false;
                        Assert.IsFalse(broker.TryRun("owner",7,10,20,0,()=>true,()=>{invoked=true;return 1;},out var result,out _));
                        Assert.IsFalse(invoked);Assert.AreEqual(UnitMoveRunStatus.Unavailable,result.Status);Assert.AreEqual(1,reports);
                    }
                    finally{Marshal.WriteByte(target.EntryPoint,original);VirtualProtect(target.EntryPoint,new UIntPtr(1),old,out _);}
                    Assert.IsFalse(broker.IsAvailable,"Restoring bytes does not silently reactivate a lost source.");Assert.AreEqual(1,reports);
                    Assert.IsTrue(UnitMoveNativeOwner<PrivateMove>.TryCreate(true,hook,address,_=>reports++,out var slotOwner,out _));
                    Assert.AreNotEqual(IntPtr.Zero,hook.PointerSlot);IntPtr prior=Marshal.ReadIntPtr(hook.PointerSlot);
                    try
                    {
                        Marshal.WriteIntPtr(hook.PointerSlot,prior+1);
                        Assert.IsFalse(slotOwner.IsCurrent(true,hook),"Matching entry bytes cannot hide an overwritten indirect jump slot.");
                    }
                    finally{Marshal.WriteIntPtr(hook.PointerSlot,prior);}
                    Assert.AreEqual(2,reports);Assert.IsFalse(slotOwner.IsCurrent(true,hook));
                }
                finally{hook?.Dispose();GC.KeepAlive(callback);} // Private pages only; no thread can be executing the modified bytes.
            }
        }
    }
}

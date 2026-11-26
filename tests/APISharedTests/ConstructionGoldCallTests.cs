using System;
using System.Linq;
using System.Linq.Expressions;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using APIShared.Buildings;
using Iced.Intel;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using RedBird.X64.Assembly;
using SHCDESE.Interop.Enums;
using SHCDESE.Interop;
using static Iced.Intel.AssemblerRegisters;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class ConstructionGoldCallTests
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate long Caller();
        private const BindingFlags PrivateStatic=BindingFlags.Static|BindingFlags.NonPublic;
        private static readonly Type Runtime=typeof(HumanConstructionRuntime);
        private static FieldInfo Field(string name)=>Runtime.GetField(name,PrivateStatic);
        private static MethodInfo Method(string name)=>Runtime.GetMethod(name,PrivateStatic);

        [TestMethod]
        public void NativeE8RelayPreservesArgumentsReturnAndNonvolatileRegistersAndPartialFailureForwards()
        {
            // Only private executable fixtures are modified. All eight Win64 nonvolatile GPRs
            // and ten nonvolatile XMM registers
            // are witnessed across the real reverse-P/Invoke callback; no game code is patched.
            using(var a=MakeCaller()) using(var b=MakeCaller())
            {
                ulong[] sites={FindCall(a),FindCall(b)};
                byte[][] expected=sites.Select(s=>CodePatch.Read(s,5)).ToArray();
                int calls=0;bool arguments=true;
                ConstructionGoldResource original=(manager,player,good,amount)=>{
                    calls++;arguments&=manager==new IntPtr(0x11223344) && player==3 && good==eGoods.STORED_GOLD;
                    return 0x123400000000L+amount;
                };
                // Uses the exact production bridge, dormant without a refund, including its Int64 result.
                ConstructionGoldResource bridge=(m,p,g,n)=>HumanConstructionRuntime.InvokeGoldRefundCall(original,m,p,g,n);
                var patch=ConstructionGoldCallPatch.Prepare(sites,expected,bridge);
                Assert.AreEqual(20L,a.GetDelegate<Caller>()());
                Assert.AreEqual(20L,b.GetDelegate<Caller>()());
                Assert.ThrowsExactly<InvalidOperationException>(()=>patch.Publish(i=>{if(i==1)throw new InvalidOperationException("second write");}));
                Assert.AreEqual(0x123400000014L,a.GetDelegate<Caller>()(),"A published first relay must remain safe while the feature is unavailable.");
                Assert.AreEqual(20L,b.GetDelegate<Caller>()(),"Second site remains original after a pre-write failure.");
                Assert.AreEqual(1,calls);Assert.IsTrue(arguments);
                GC.KeepAlive(patch);GC.KeepAlive(bridge);GC.KeepAlive(original);
            }
            using(var a=MakeCaller()) using(var b=MakeCaller())
            {
                ulong[] sites={FindCall(a),FindCall(b)};byte[][] expected=sites.Select(s=>CodePatch.Read(s,5)).ToArray();
                ConstructionGoldResource bridge=(m,p,g,n)=>n+7;
                var patch=ConstructionGoldCallPatch.Prepare(sites,expected,bridge);
                expected[0][0]=0;expected[1][4]^=0xFF; // Prepared identity owns both byte snapshots.
                patch.Publish();
                Assert.AreEqual(27L,a.GetDelegate<Caller>()());Assert.AreEqual(27L,b.GetDelegate<Caller>()());
                GC.KeepAlive(patch);GC.KeepAlive(bridge);
            }
        }
        [TestMethod]
        public void RelayAllocationFailureLeavesBothCallsUnmodifiedAndRel32BoundsAreExact()
        {
            using(var a=MakeCaller()) using(var b=MakeCaller())
            {
                ulong[] sites={FindCall(a),FindCall(b)};byte[][] expected=sites.Select(s=>CodePatch.Read(s,5)).ToArray();
                ConstructionGoldResource bridge=(m,p,g,n)=>0;
                Assert.ThrowsExactly<InvalidOperationException>(()=>ConstructionGoldCallPatch.Prepare(sites,expected,bridge,(s,n)=>IntPtr.Zero));
                CollectionAssert.AreEqual(expected[0],CodePatch.Read(sites[0],5));CollectionAssert.AreEqual(expected[1],CodePatch.Read(sites[1],5));
            }
            const ulong site=0x200000000;
            Assert.AreEqual(int.MaxValue,BitConverter.ToInt32(ConstructionGoldCallPatch.EncodeCall(site,site+5+int.MaxValue),1));
            Assert.AreEqual(int.MinValue,BitConverter.ToInt32(ConstructionGoldCallPatch.EncodeCall(site,site+5-0x80000000),1));
            Assert.ThrowsExactly<InvalidOperationException>(()=>ConstructionGoldCallPatch.EncodeCall(site,site+5+0x80000000));
        }
        private static ExecutableFunction MakeCaller()=>ExecutableFunction.Create(a=>{
            var target=a.CreateLabel();var failure=a.CreateLabel();var done=a.CreateLabel();
            var registers=new[]{rbx,rbp,rsi,rdi,r12,r13,r14,r15};
            var simd=new[]{xmm6,xmm7,xmm8,xmm9,xmm10,xmm11,xmm12,xmm13,xmm14,xmm15};
            foreach(var register in registers)a.push(register);
            a.sub(rsp,200);
            for(int i=0;i<simd.Length;i++){
                a.movdqu(__xmmword_ptr[rsp+40+i*16],simd[i]);
                a.mov(rax,0x1122334455667700L+i);a.movq(simd[i],rax);
            }
            for(int i=0;i<registers.Length;i++)a.mov(registers[i],0x12345600+i);
            a.mov(rcx,0x11223344);a.mov(edx,3);a.mov(r8d,15);a.mov(r9d,20);
            a.call(target);a.mov(__qword_ptr[rsp+32],rax);
            for(int i=0;i<registers.Length;i++){a.cmp(registers[i],0x12345600+i);a.jne(failure);}
            for(int i=0;i<simd.Length;i++){a.movq(rax,simd[i]);a.mov(r10,0x1122334455667700L+i);a.cmp(rax,r10);a.jne(failure);}
            a.mov(rax,__qword_ptr[rsp+32]);a.jmp(done);
            a.Label(ref failure);a.mov(rax,-1);a.Label(ref done);
            for(int i=0;i<simd.Length;i++)a.movdqu(simd[i],__xmmword_ptr[rsp+40+i*16]);
            a.add(rsp,200);
            for(int i=registers.Length-1;i>=0;i--)a.pop(registers[i]);a.ret();
            a.Label(ref target);a.mov(eax,r9d);a.ret();
        });
        private static ulong FindCall(ExecutableFunction function)
        {
            var decoder=Decoder.Create(64,new ByteArrayCodeReader(function.OriginalBytes));decoder.IP=function.Address;
            while(decoder.IP<function.Address+(uint)function.CodeLength){decoder.Decode(out var i);if(i.Code==Code.Call_rel32_64)return i.IP;}
            throw new InvalidOperationException("Fixture E8 missing.");
        }

        private static Action pre,post;
        private static bool skip;
        private static int contextIdentity=0,contextPercentage=0;
        private static Func<long> original;
        [MethodImpl(MethodImplOptions.NoInlining)] private static long ResourceWrapper()
        { pre?.Invoke();long value=skip?17:original();post?.Invoke();return value; }

        [TestMethod]
        public void WholeWrapperScopeSeparatesPrePostBonusesNestedRefundsAndRestoresOnExceptions()
        {
            var ready=Field("ready");object oldReady=ready.GetValue(null),oldRefund=Field("refund").GetValue(null);
            object oldToken=Field("goldCall").GetValue(null),oldDepth=Field("resourceDepth").GetValue(null);
            object frame=Activator.CreateInstance(Runtime.GetNestedType("RefundFrame",BindingFlags.NonPublic),true);
            object nestedFrame=Activator.CreateInstance(frame.GetType(),true);
            var op=Enum.Parse(Runtime.GetNestedType("Operation",BindingFlags.NonPublic),"AddResource");
            Func<int> enter=()=> (int)Method("EnterMethodScope").Invoke(null,new[]{op});
            Action<int> leave=p=>Method("LeaveMethodScope").Invoke(null,new object[]{op,p});
            bool Match()=> (bool)Method("MatchesGoldRefundCall").Invoke(null,null);
            using(var dmd=new DynamicMethodDefinition(typeof(ConstructionGoldCallTests).GetMethod(nameof(ResourceWrapper),PrivateStatic)))
            using(var il=new ILContext(dmd.Definition){ReferenceBag=RuntimeILReferenceBag.Instance})
            {
                HumanConstructionRuntime.ApplyMethodScope(il,enter,leave);
                var run=(Func<long>)dmd.Generate().CreateDelegate(typeof(Func<long>));
                RuntimeHelpers.PrepareDelegate(run);
                try
                {
                    ready.SetValue(null,true);Field("refund").SetValue(null,frame);
                    int depth=0,bonuses=0,baseCalls=0,nestedCalls=0;
                    original=()=>{if(depth==0){Assert.IsTrue(Match());baseCalls++;}else{Assert.IsFalse(Match());bonuses++;}return 42;};
                    pre=()=>{if(depth!=0)return;depth++;try{run();}finally{depth--;}};
                    post=()=>{if(depth!=0)return;depth++;try{run();}finally{depth--;}};
                    ConstructionGoldResource entry=(m,p,g,n)=>run();
                    Assert.AreEqual(42L,HumanConstructionRuntime.InvokeGoldRefundCall(entry,IntPtr.Zero,1,eGoods.STORED_GOLD,5));
                    Assert.AreEqual(1,baseCalls);Assert.AreEqual(2,bonuses);Assert.IsFalse(Match());
                    pre=()=>{if(depth!=0)return;depth++;Field("refund").SetValue(null,nestedFrame);try{
                        ConstructionGoldResource nested=(m,p,g,n)=>{Assert.IsFalse(Match());return run();};
                        var priorOriginal=original;original=()=>{Assert.IsTrue(Match());nestedCalls++;return 99;};
                        try{Assert.AreEqual(99L,HumanConstructionRuntime.InvokeGoldRefundCall(nested,IntPtr.Zero,1,eGoods.STORED_GOLD,5));}
                        finally{original=priorOriginal;}
                    }finally{Field("refund").SetValue(null,frame);depth--;}};
                    post=null;
                    Assert.AreEqual(42L,HumanConstructionRuntime.InvokeGoldRefundCall(entry,IntPtr.Zero,1,eGoods.STORED_GOLD,5));
                    Assert.AreEqual(1,nestedCalls);
                    // An unmatched native sentinel can still call C4793. The real Refund entry
                    // must hide the outer frame, not manufacture a second token for that receipt.
                    object priorId=Field("refundContextId").GetValue(null),priorPercentage=Field("refundContextPercentage").GetValue(null);
                    try
                    {
                        Field("refundContextId").SetValue(null,typeof(ConstructionGoldCallTests).GetField(nameof(contextIdentity),PrivateStatic));
                        Field("refundContextPercentage").SetValue(null,typeof(ConstructionGoldCallTests).GetField(nameof(contextPercentage),PrivateStatic));
                        int unrelated=0;
                        pre=()=>{
                            if(depth!=0)return;depth++;
                            var savedOriginal=original;original=()=>{Assert.IsFalse(Match());unrelated++;return 12;};
                            try
                            {
                                Action<IntPtr,int,int,int> foreign=(m,b,p,percent)=>{
                                    Assert.IsNull(Field("refund").GetValue(null));
                                    Assert.AreEqual(12L,HumanConstructionRuntime.InvokeGoldRefundCall(entry,m,p,eGoods.STORED_GOLD,9));
                                };
                                Method("Refund").Invoke(null,new object[]{foreign,new object[]{IntPtr.Zero,-3,1,50}});
                                Assert.AreSame(frame,Field("refund").GetValue(null));Assert.IsTrue(Match());
                                Action<IntPtr,int,int,int> fault=(m,b,p,percent)=>throw new InvalidOperationException("foreign refund");
                                Assert.ThrowsExactly<TargetInvocationException>(()=>Method("Refund").Invoke(null,new object[]{fault,new object[]{IntPtr.Zero,-4,1,50}}));
                                Assert.AreSame(frame,Field("refund").GetValue(null));Assert.IsTrue(Match());
                            }
                            finally{original=savedOriginal;depth--;}
                        };
                        Assert.AreEqual(42L,HumanConstructionRuntime.InvokeGoldRefundCall(entry,IntPtr.Zero,1,eGoods.STORED_GOLD,5));
                        Assert.AreEqual(1,unrelated);
                    }
                    finally{Field("refundContextId").SetValue(null,priorId);Field("refundContextPercentage").SetValue(null,priorPercentage);}
                    int posts=0;pre=null;post=()=>posts++;skip=true;
                    original=()=>throw new InvalidOperationException("Skipped Original was entered.");
                    Assert.AreEqual(17L,HumanConstructionRuntime.InvokeGoldRefundCall(entry,IntPtr.Zero,1,eGoods.STORED_GOLD,5));
                    Assert.AreEqual(1,posts);Assert.AreEqual(0,Field("resourceDepth").GetValue(null));skip=false;
                    pre=()=>throw new InvalidOperationException("subscriber");
                    Assert.ThrowsExactly<InvalidOperationException>(()=>HumanConstructionRuntime.InvokeGoldRefundCall(entry,IntPtr.Zero,1,eGoods.STORED_GOLD,5));
                    Assert.AreEqual(0,Field("resourceDepth").GetValue(null));Assert.IsFalse(Match());
                    pre=null;post=null;
                    // Without the native CALL token even a top-level Gold callback is independent.
                    original=()=>{Assert.IsFalse(Match());return 7;};Assert.AreEqual(7L,run());
                }
                finally{pre=null;post=null;original=null;skip=false;ready.SetValue(null,oldReady);Field("refund").SetValue(null,oldRefund);Field("goldCall").SetValue(null,oldToken);Field("resourceDepth").SetValue(null,oldDepth);}
            }
        }

        [TestMethod]
        public void SpawnScopesKeepNestedRawSpawnsSeparateAndFastGuardsAllocateNothing()
        {
            var registrationField=typeof(HumanConstructionCosts).GetField("registrations",PrivateStatic);
            object oldRegistrations=registrationField.GetValue(null),oldReady=Field("ready").GetValue(null),oldBuilding=Field("building").GetValue(null);
            object oldB=Field("buildingSpawnDepth").GetValue(null),oldU=Field("unitSpawnDepth").GetValue(null),oldP=Field("decorationSpawnDepth").GetValue(null);
            var unit=(Func<eChimps,int>)Method("EnterUnitScope").CreateDelegate(typeof(Func<eChimps,int>));
            var decoration=(Func<ProjectileType,int>)Method("EnterDecorationScope").CreateDelegate(typeof(Func<ProjectileType,int>));
            Type operation=Runtime.GetNestedType("Operation",BindingFlags.NonPublic);
            Action<int> Exit(string name){var p=Expression.Parameter(typeof(int));return Expression.Lambda<Action<int>>(
                Expression.Call(Method("LeaveMethodScope"),Expression.Constant(Enum.Parse(operation,name),operation),p),p).Compile();}
            Func<int> Enter(string name)=>Expression.Lambda<Func<int>>(Expression.Call(Method("EnterMethodScope"),Expression.Constant(Enum.Parse(operation,name),operation))).Compile();
            var leaveU=Exit("SpawnUnit");var leaveP=Exit("SpawnDecoration");var enterB=Enter("SpawnBuilding");var leaveB=Exit("SpawnBuilding");
            try
            {
                Field("ready").SetValue(null,true);Field("building").SetValue(null,null);
                Field("unitSpawnDepth").SetValue(null,0);Field("decorationSpawnDepth").SetValue(null,0);Field("buildingSpawnDepth").SetValue(null,0);
                registrationField.SetValue(null,Array.Empty<HumanConstructionCostRegistration>());
                Assert.AreEqual(-1,unit(eChimps.CHIMP_TYPE_BALLISTA));Assert.AreEqual(-1,decoration(ProjectileType.Brazier));
                for(int i=0;i<1000;i++){unit(eChimps.CHIMP_TYPE_BALLISTA);decoration(ProjectileType.Brazier);}
                long before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;i++){unit(eChimps.CHIMP_TYPE_BALLISTA);decoration(ProjectileType.Brazier);}
                Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
                // A configured registration makes depth relevant. It does not initialize game managers.
                var registration=(HumanConstructionCostRegistration)Activator.CreateInstance(typeof(HumanConstructionCostRegistration),BindingFlags.Instance|BindingFlags.NonPublic,null,new object[]{"fixture","scope"},null);
                registration.SetCosts(ConstructionCostTarget.FromStructure(eStructs.STRUCT_BALLISTA),new Dictionary<eGoods,int>{{eGoods.STORED_GOLD,1}});registration.SetActive(true);
                registrationField.SetValue(null,new[]{registration});
                Assert.AreEqual(0,unit(eChimps.CHIMP_TYPE_BALLISTA));
                Assert.AreEqual(1,unit(default));Assert.AreEqual(2,Field("unitSpawnDepth").GetValue(null));leaveU(1);leaveU(0);
                Assert.AreEqual(0,decoration(ProjectileType.Brazier));Assert.AreEqual(1,decoration(default));leaveP(1);leaveP(0);
                // Primary building Pre spawns a raw sibling: only depth1 can claim the parent's receipt.
                Type frameType=Runtime.GetNestedType("BuildFrame",BindingFlags.NonPublic);object outer=Activator.CreateInstance(frameType,true);
                frameType.GetField("ExpectedSpawnDepth",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(outer,1);Field("building").SetValue(null,outer);
                Assert.AreEqual(0,enterB());Assert.IsTrue((bool)Method("IsPrimaryBuildingSpawn").Invoke(null,new[]{outer}));
                Assert.AreEqual(1,enterB());Assert.IsFalse((bool)Method("IsPrimaryBuildingSpawn").Invoke(null,new[]{outer}));
                // A separately paid nested Build owns a separate frame at the next native depth.
                object nested=Activator.CreateInstance(frameType,true);frameType.GetField("ExpectedSpawnDepth",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(nested,3);Field("building").SetValue(null,nested);
                Assert.AreEqual(2,enterB());Assert.IsTrue((bool)Method("IsPrimaryBuildingSpawn").Invoke(null,new[]{nested}));leaveB(2);
                Field("building").SetValue(null,outer);leaveB(1);Assert.IsTrue((bool)Method("IsPrimaryBuildingSpawn").Invoke(null,new[]{outer}));leaveB(0);Field("building").SetValue(null,null);
                // Non-target fast paths bypass demand inspection even while a target is configured.
                for(int i=0;i<1000;i++){unit(default);decoration(default);}
                before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;i++){unit(default);decoration(default);}
                Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
                Field("ready").SetValue(null,false);
                for(int i=0;i<1000;i++){unit(eChimps.CHIMP_TYPE_BALLISTA);decoration(ProjectileType.Brazier);}
                before=GC.GetAllocatedBytesForCurrentThread();
                for(int i=0;i<10000;i++){unit(eChimps.CHIMP_TYPE_BALLISTA);decoration(ProjectileType.Brazier);}
                Assert.AreEqual(0L,GC.GetAllocatedBytesForCurrentThread()-before);
            }
            finally{registrationField.SetValue(null,oldRegistrations);Field("ready").SetValue(null,oldReady);Field("building").SetValue(null,oldBuilding);Field("buildingSpawnDepth").SetValue(null,oldB);Field("unitSpawnDepth").SetValue(null,oldU);Field("decorationSpawnDepth").SetValue(null,oldP);}
        }
    }
}

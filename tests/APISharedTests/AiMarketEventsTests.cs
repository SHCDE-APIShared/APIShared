using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using APIShared.Economy;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;

namespace APISharedTests
{
    [TestClass, DoNotParallelize]
    public sealed class AiMarketEventsTests
    {
        [TestMethod]
        public void ObserverFailureDisableAndReentryCannotUnpairOrSuppressOtherObservers()
        {
            var events=new List<string>();int failures=0,originals=0,depth=0;long outer=0;
            AiMarketRegistration first=null,second=null;
            first=new AiMarketRegistration("a","a",0,c=>{
                events.Add("begin"+depth);if(depth==0)outer=c.InvocationId;
                return 40+depth;
            },(c,token,done,result)=>{Assert.IsTrue(done);Assert.AreEqual(71,result);events.Add("end"+token);},null);
            second=new AiMarketRegistration("b","b",1,c=>throw new InvalidOperationException("diagnostic"),
                (c,t,d,r)=>Assert.Fail("A failed Begin must not get an End."),e=>failures++);
            var all=new[]{first,second};
            AiMarketNativeRuntime.BuyCall original=null;
            original=(root,player,good,amount)=>{
                originals++;Assert.AreEqual(new IntPtr(123),root);Assert.AreEqual(2,player);Assert.AreEqual(17,good);Assert.AreEqual(-3,amount);
                if(depth==0){depth++;AiMarketNativeRuntime.DispatchBuy(all,root,player,good,amount,original);depth--;}
                return 71;
            };
            Assert.AreEqual(71,AiMarketNativeRuntime.DispatchBuy(all,new IntPtr(123),2,17,-3,original));
            Assert.AreEqual(2,originals);Assert.AreEqual(2,failures);Assert.IsTrue(outer>0);
            CollectionAssert.AreEqual(new[]{"begin0","begin1","end41","end40"},events);
        }
        [TestMethod]
        public void OriginalExceptionAndVoidCompletionRemainUnmodified()
        {
            var expected=new InvalidOperationException("native fixture");int end=0;
            var observer=new AiMarketRegistration("a","a",0,c=>0,(c,t,done,result)=>{
                Assert.IsFalse(done);Assert.AreEqual(0,result);end++;
            },null);
            var caught=Assert.ThrowsExactly<InvalidOperationException>(()=>AiMarketNativeRuntime.DispatchPair(
                new[]{observer},AiMarketOperation.Recruitment,IntPtr.Zero,1,(r,p)=>throw expected));
            Assert.AreSame(expected,caught);Assert.AreEqual(1,end);
            bool sale=false;
            observer=new AiMarketRegistration("b","b",0,c=>99,(c,t,done,result)=>{
                Assert.AreEqual(AiMarketOperation.Sell,c.Operation);Assert.AreEqual(-1,c.SignedAmount);
                Assert.IsTrue(done);Assert.AreEqual(99L,t);Assert.AreEqual(0,result);sale=true;
            },null);
            AiMarketNativeRuntime.DispatchSell(new[]{observer},IntPtr.Zero,1,2,-1,(r,p,g,q)=>{});
            Assert.IsTrue(sale);
        }
        [TestMethod]
        public void RegistrySnapshotPreservesEndAfterDisableAndOrdersForeignOwners()
        {
            var notes=new List<string>();string owner="market-test-"+Guid.NewGuid().ToString("N");
            AiMarketRegistration one=null,two=null;
            Assert.IsTrue(AiMarketEvents.TryRegister(owner,"b",c=>{notes.Add("b");one.SetEnabled(false);return 2;},
                (c,t,d,r)=>notes.Add("B"),out two,out _,10));
            Assert.IsTrue(AiMarketEvents.TryRegister(owner,"a",c=>{notes.Add("a");return 1;},
                (c,t,d,r)=>notes.Add("A"),out one,out _,0));
            try{
                one.SetEnabled(true);two.SetEnabled(true);
                AiMarketNativeRuntime.DispatchPair(AiMarketEvents.Snapshot(),AiMarketOperation.PurchaseDecision,IntPtr.Zero,1,(r,p)=>{});
                CollectionAssert.AreEqual(new[]{"a","b","A","B"},notes);
            }finally{one.SetEnabled(false);two.SetEnabled(false);}
        }
        [TestMethod]
        public void WarmPairedDispatchIncludingReentryAllocatesNoManagedMemory()
        {
            long count=0;
            var observers=new[]{new AiMarketRegistration("allocation","observer",0,c=>c.InvocationId,
                (c,t,d,r)=>{if(t==c.InvocationId&&d)count++;},null)};
            AiMarketNativeRuntime.PairCall pair=(r,p)=>{};
            AiMarketNativeRuntime.BuyCall buy=(r,p,g,q)=>{
                AiMarketNativeRuntime.DispatchPair(observers,AiMarketOperation.Recruitment,r,p,pair);return 1;
            };
            AiMarketNativeRuntime.SellCall sell=(r,p,g,q)=>{};
            for(int i=0;i<100;i++)AiMarketNativeRuntime.DispatchBuy(observers,IntPtr.Zero,1,2,3,buy);
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++)
            {
                AiMarketNativeRuntime.DispatchBuy(observers,IntPtr.Zero,1,2,3,buy);
                AiMarketNativeRuntime.DispatchSell(observers,IntPtr.Zero,1,2,3,sell);
            }
            long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.AreEqual(0L,allocated);Assert.AreEqual(30200L,count);
        }
        [DllImport("kernel32.dll")] private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint allocation,uint protection);
        [DllImport("kernel32.dll")] private static extern bool VirtualFree(IntPtr address,UIntPtr size,uint kind);
        [TestMethod]
        public void InstalledBackendReplaysAllFourEntriesIncludingBothSaleBranches()
        {
            for(int index=0;index<4;index++)
            {
                IntPtr copy=VirtualAlloc(IntPtr.Zero,(UIntPtr)65536,0x3000,0x40);
                Assert.AreNotEqual(IntPtr.Zero,copy);
                NativeDetour<AiMarketNativeRuntime.BuyCall> hook=null;
                int calls=0;AiMarketNativeRuntime.BuyCall callback=(r,p,g,q)=>{calls++;return hook.Original(r,p,g,q);};
                try{
                    byte[] entry=AiMarketNativeRuntime.Entries[index];Marshal.Copy(entry,0,copy,entry.Length);
                    byte[] cleanup=index==0?new byte[]{0x41,0x5D,0x41,0x5C,0x5E}:
                        index==1?new byte[]{0x48,0x81,0xC4,0x60,1,0,0,0x41,0x5E,0x5E,0x5D}:Array.Empty<byte>();
                    Marshal.Copy(cleanup,0,copy+entry.Length,cleanup.Length);
                    byte[] ret={0xB8,17,0,0,0,0xC3};Marshal.Copy(ret,0,copy+entry.Length+cleanup.Length,ret.Length);
                    byte[] neg={0xB8,23,0,0,0,0xC3};Marshal.Copy(neg,0,copy+0x9F,neg.Length);
                    var request=new DetourRequest<AiMarketNativeRuntime.BuyCall>{Name="private shared AI market fixture",TargetAddress=unchecked((ulong)copy.ToInt64()),Callback=callback};
                    hook=(NativeDetour<AiMarketNativeRuntime.BuyCall>)AiMarketNativeRuntime.Backend.CreateDetour(in request);
                    AiMarketNativeRuntime.Validate(hook,copy,entry,false);
                    hook.Enable();AiMarketNativeRuntime.Validate(hook,copy,entry,true);
                    var call=Marshal.GetDelegateForFunctionPointer<AiMarketNativeRuntime.BuyCall>(copy);
                    Assert.AreEqual(17,call(IntPtr.Zero,2,17,1));
                    Assert.AreEqual(index==3?23:17,call(IntPtr.Zero,2,17,-1));
                    Assert.AreEqual(2,calls);
                    Marshal.WriteByte(copy,0,0x90);
                    Assert.ThrowsExactly<InvalidOperationException>(()=>AiMarketNativeRuntime.Validate(hook,copy,entry,true));
                    Marshal.WriteByte(copy,0,0xFF);
                }finally{hook?.Dispose();GC.KeepAlive(callback);Assert.IsTrue(VirtualFree(copy,UIntPtr.Zero,0x8000));}
            }
        }
    }
}

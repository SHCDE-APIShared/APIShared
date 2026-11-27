using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using APIShared.Units;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.API.LowLevel;

namespace APISharedTests
{
    [TestClass,DoNotParallelize]
    public sealed class UnitRelocationCommandTests
    {
        private static UnitRelocationRequest Request=>new UnitRelocationRequest(11,22,7,123,eChimps.CHIMP_TYPE_ARCHER,1,10,20);
        [TestMethod]
        public void CommandScopeRequiresCurrentTickMonitorCompletedSourceAndWorld()
        {
            var r=Request;
            Assert.IsTrue(UnitRelocationValidation.Scope(true,true,false,true,11,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(false,true,false,true,11,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,false,false,true,11,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,true,true,true,11,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,true,false,false,11,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,true,false,true,11,23,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,true,false,true,12,22,r));
            Assert.IsFalse(UnitRelocationValidation.Scope(true,true,false,true,0,0,default));
        }
        [TestMethod]
        public void IdentityChangesAfterPoliciesCannotAuthorizeTheOriginal()
        {
            var r=Request;
            Assert.IsTrue(UnitRelocationValidation.Identity(r,7,123,eChimps.CHIMP_TYPE_ARCHER,1,true));
            Assert.IsFalse(UnitRelocationValidation.Identity(r,8,123,eChimps.CHIMP_TYPE_ARCHER,1,true));
            Assert.IsFalse(UnitRelocationValidation.Identity(r,7,124,eChimps.CHIMP_TYPE_ARCHER,1,true));
            Assert.IsFalse(UnitRelocationValidation.Identity(r,7,123,eChimps.CHIMP_TYPE_SPEARMAN,1,true));
            Assert.IsFalse(UnitRelocationValidation.Identity(r,7,123,eChimps.CHIMP_TYPE_ARCHER,2,true));
            Assert.IsFalse(UnitRelocationValidation.Identity(r,7,123,eChimps.CHIMP_TYPE_ARCHER,1,false));
        }
        [TestMethod]
        public void OccupancyPairRejectsSharedSuffixCyclesAndDuplicateActor()
        {
            var nodes=new Dictionary<int,UnitRelocationValidation.Node>{[7]=new UnitRelocationValidation.Node(123,8),[8]=new UnitRelocationValidation.Node(124,0),[9]=new UnitRelocationValidation.Node(125,0)};
            UnitRelocationValidation.Node? Read(int id)=>nodes.TryGetValue(id,out var node)?(UnitRelocationValidation.Node?)node:null;
            Assert.IsTrue(UnitRelocationValidation.Pair(7,9,7,1,0,false,Read));
            nodes[9]=new UnitRelocationValidation.Node(125,8);
            Assert.IsFalse(UnitRelocationValidation.Pair(7,9,7,1,0,false,Read),"Individually acyclic lists sharing a tail must be refused.");
            nodes[9]=new UnitRelocationValidation.Node(125,7);
            Assert.IsFalse(UnitRelocationValidation.Pair(7,9,7,1,0,false,Read));
            nodes[8]=new UnitRelocationValidation.Node(124,7);
            Assert.IsFalse(UnitRelocationValidation.Pair(7,0,7,1,0,false,Read));
            nodes[8]=new UnitRelocationValidation.Node(124,0);
            Assert.IsTrue(UnitRelocationValidation.Pair(7,7,7,1,1,true,Read));
            Assert.IsFalse(UnitRelocationValidation.Pair(7,9,7,1,1,true,Read));
            Assert.IsFalse(UnitRelocationValidation.Pair(10000,0,7,1,0,false,Read));
            Assert.IsFalse(UnitRelocationValidation.Pair(-1,0,7,1,0,false,Read));
            nodes[8]=new UnitRelocationValidation.Node(0,0);
            Assert.IsFalse(UnitRelocationValidation.Pair(7,0,7,1,0,false,Read));
        }
        [TestMethod]
        public void FullPoolChainAndPostRemovalHaveExactMembership()
        {
            UnitRelocationValidation.Node? Read(int id)=>new UnitRelocationValidation.Node((uint)(id+100),id==9999?0:id+1);
            Assert.IsTrue(UnitRelocationValidation.Pair(1,0,7,1,0,false,Read));
            Assert.IsFalse(UnitRelocationValidation.Pair(1,9999,7,1,0,false,Read));
            Assert.IsTrue(UnitRelocationValidation.Pair(0,1,7,0,1,false,Read));
            Assert.IsFalse(UnitRelocationValidation.Pair(0,1,7,1,1,false,Read));
        }
        [TestMethod]
        public void OriginUsesOriginalCallerAndExplicitCorrelationNotWrapperReturn()
        {
            const ulong image=0x10000000;
            Assert.AreEqual(UnitRelocationOrigin.InvalidStandingTerrainRecovery,UnitRelocationValidation.Origin(false,image+0x183014,image));
            Assert.AreEqual(UnitRelocationOrigin.Native,UnitRelocationValidation.Origin(false,image+0x15ABD6,image));
            Assert.AreEqual(UnitRelocationOrigin.ExplicitApiRequest,UnitRelocationValidation.Origin(true,image+0x183014,image));
            var bytes=new byte[5];bytes[0]=0xE8;
            Array.Copy(BitConverter.GetBytes(UnitRelocationNative.EntryRva-UnitRelocationNative.StandingReturnRva),0,bytes,1,4);
            UnitRelocationNative.ValidateStandingCaller(bytes,image);
            bytes[0]=0xE9;Assert.ThrowsExactly<InvalidOperationException>(()=>UnitRelocationNative.ValidateStandingCaller(bytes,image));
            bytes[0]=0xE8;bytes[1]^=1;Assert.ThrowsExactly<InvalidOperationException>(()=>UnitRelocationNative.ValidateStandingCaller(bytes,image));
        }
        [TestMethod]
        public void IntegrityLossIsStickyEvenWhenDiagnosticAlreadyReportedOrBytesRestored()
        {
            bool bytes=true;int reads=0,reports=0;
            var health=new UnitRelocationIntegrity(()=>{reads++;return bytes;},_=>reports++);
            Assert.IsTrue(health.Check());
            bytes=false;Assert.IsFalse(health.Check());Assert.AreEqual(1,reports);
            Assert.IsFalse(health.Check());Assert.AreEqual(2,reads);
            bytes=true;Assert.IsFalse(health.Check());Assert.AreEqual(2,reads);Assert.AreEqual(1,reports);
            var failure=new UnitRelocationIntegrity(()=>throw new InvalidOperationException(),_=>throw new InvalidOperationException());
            Assert.IsFalse(failure.Check());Assert.IsFalse(failure.Check());
        }
        [TestMethod]
        public void InvalidRegistrationDoesNotAcquireAnOwner()
        {
            Assert.IsFalse(UnitRelocationService.TryRegisterPolicy("owner","",null,_=>false,out _,out _));
            Assert.IsFalse(UnitRelocationService.IsAvailable);
        }
        [TestMethod]
        public void FailureRenderingCannotEscapeTheNativeCallbackBoundary()
        {
            var failure=new UnrenderableFailure();
            UnitRelocationRuntime.ReportFailure("callback",failure);
            Assert.AreEqual(1,failure.Renders);
            Assert.AreEqual("Relocation failure details are unavailable.",UnitRelocationRuntime.SafeReason(failure));
            Assert.AreEqual(1,failure.Messages);
        }
        private sealed class UnrenderableFailure:Exception
        {
            internal int Renders,Messages;
            public override string ToString(){Renders++;throw new InvalidOperationException("broken rendering");}
            public override string Message{get{Messages++;throw new InvalidOperationException("broken message");}}
        }
        [TestMethod]
        public void FailedMetadataPreparationCannotReachNativeAcquisitionFromAPartialContext()
        {
            const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Static;
            var context=typeof(UnitRelocationRuntime).GetField("context",flags);
            var failed=typeof(UnitRelocationRuntime).GetField("failed",flags);
            var group=typeof(UnitRelocationRuntime).GetField("group",flags);
            object oldContext=context.GetValue(null),oldFailed=failed.GetValue(null),oldGroup=group.GetValue(null);
            int allocated=0,subscribed=0,disposed=0;
            try
            {
                // Recreate the dangerous historical condition: context assigned, failed still0,
                // but no complete metadata publication. Ensure must stop before touching native data.
                context.SetValue(null,FormatterServices.GetUninitializedObject(typeof(CrusaderLibraryLoadContext)));
                failed.SetValue(null,0);
                foreach(int stage in new[]{0,1,2,3})
                {
                    Assert.ThrowsExactly<InvalidOperationException>(()=>UnitRelocationMetadata.Create(
                        ()=>stage==0?null:new object(),
                        ()=>{allocated++;return stage==1?IntPtr.Zero:Marshal.AllocHGlobal(65536);},
                        ()=>{subscribed++;if(stage==2)throw new InvalidOperationException("subscribe initialization");return new Subscription(()=>disposed++);},
                        ()=>{subscribed++;throw new InvalidOperationException("subscribe start");},null));
                    Assert.IsFalse(UnitRelocationService.EnsureAvailable("test.owner",out string reason));
                    StringAssert.Contains(reason,"initialized LibraryLoaded");
                    Assert.IsFalse(UnitRelocationService.TryRegisterPolicy("test.owner","rule",null,_=>false,out var handle,out _));
                    Assert.IsNull(handle);Assert.AreEqual(0,(int)failed.GetValue(null),"Incomplete metadata must not enter installation failure handling.");
                    Assert.AreSame(oldGroup,group.GetValue(null),"No native candidate may be created from partial metadata.");
                }
                Assert.AreEqual(3,allocated);Assert.AreEqual(3,subscribed);Assert.AreEqual(1,disposed);
            }
            finally{context.SetValue(null,oldContext);failed.SetValue(null,oldFailed);}
        }
        private sealed class Subscription:IDisposable
        {private readonly Action dispose;internal Subscription(Action dispose){this.dispose=dispose;}public void Dispose()=>dispose();}
    }
}

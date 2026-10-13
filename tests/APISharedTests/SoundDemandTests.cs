using System;
using System.Reflection;
using System.Threading.Tasks;
using APIShared.Audio;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class SoundDemandTests
    {
        [TestMethod]
        public void HandlesCountOnlyOnceAndConcurrentTransitionsRemainBalanced()
        {
            var demand=new SoundDemand();var first=new SoundRegistration();first.SetEnabled(false);demand.Attach(first);
            Assert.AreEqual(0,demand.ActiveCount);first.SetEnabled(true);first.SetEnabled(true);Assert.AreEqual(1,demand.ActiveCount);
            first.SetEnabled(false);first.SetEnabled(false);Assert.AreEqual(0,demand.ActiveCount);
            Assert.ThrowsExactly<InvalidOperationException>(()=>demand.Attach(first));Assert.AreEqual(0,demand.ActiveCount);
            var handles=new SoundRegistration[8];for(int i=0;i<handles.Length;i++){handles[i]=new SoundRegistration();demand.Attach(handles[i]);}
            Assert.AreEqual(8,demand.ActiveCount);
            Parallel.For(0,8,index=>{for(int i=0;i<1000;i++){handles[index].SetEnabled(false);handles[index].SetEnabled(true);}handles[index].SetEnabled(false);});
            Assert.AreEqual(0,demand.ActiveCount);Assert.IsFalse(demand.HasObservers);
            Parallel.For(0,8,index=>{for(int i=0;i<1000;i++)first.SetEnabled((i&1)==0);});
            first.SetEnabled(false);Assert.AreEqual(0,demand.ActiveCount);
        }
        [TestMethod]
        public void AttachRacingWithUnpublishedHandleTransitionsCannotLoseDemand()
        {
            for(int run=0;run<50;run++) {
                var demand=new SoundDemand();var handle=new SoundRegistration();
                Parallel.Invoke(()=>demand.Attach(handle),()=>{for(int i=0;i<100;i++)handle.SetEnabled((i&1)==0);});
                Assert.AreEqual(handle.Enabled?1:0,demand.ActiveCount);
                handle.SetEnabled(true);Assert.AreEqual(1,demand.ActiveCount);handle.SetEnabled(false);Assert.AreEqual(0,demand.ActiveCount);
            }
        }
        [TestMethod]
        public void ConservativePositiveDemandDoesNotNotifyDisabledObservers()
        {
            var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>(hasDemand:()=>true);
            int callbacks=0,originals=0;
            registry.TryRegister("fixture","dormant",a=>callbacks++,p=>callbacks++,out _,isActive:()=>false);
            var context=new SoundContext(180,1,0,SoundPlaybackKind.Spatial,SoundSituation.Unknown,1,1);
            SoundEvents.Dispatch(context,false,id=>true,(id,v,p)=>originals++,registry);
            Assert.AreEqual(0,callbacks);Assert.AreEqual(1,originals);
        }
        [TestMethod]
        public void FailedPublicRegistrationsNeverCountAndSuccessfulPublicationDoes()
        {
            var status=typeof(SoundRuntime).GetField("playbackStatus",BindingFlags.Static|BindingFlags.NonPublic);var old=status.GetValue(null);
            int before=SoundEvents.Demand.ActiveCount;SoundRegistration handle=null;
            try {
                // The global broker is already installed by earlier fixtures. Test first-install
                // rejection on a fresh broker rather than inventing a later playback status change.
                var failedDemand=new SoundDemand();var failedRegistry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>(()=>throw new InvalidOperationException("unavailable"),()=>failedDemand.HasObservers);
                var unpublished=new SoundRegistration();bool installed=failedRegistry.TryRegister("fixture","unavailable",a=>{},null,out _);
                if(installed)failedDemand.Attach(unpublished);
                Assert.IsFalse(installed);Assert.AreEqual(0,failedDemand.ActiveCount);Assert.AreEqual(before,SoundEvents.Demand.ActiveCount);
                status.SetValue(null,"Ready");string owner="demand."+Guid.NewGuid();
                Assert.IsFalse(SoundEvents.TryRegister("",owner,a=>{},null,out _,out _));Assert.AreEqual(before,SoundEvents.Demand.ActiveCount);
                Assert.IsTrue(SoundEvents.TryRegister(owner,"main",a=>{},null,out handle,out _));Assert.AreEqual(before+1,SoundEvents.Demand.ActiveCount);
                Assert.IsFalse(SoundEvents.TryRegister(owner,"main",a=>{},null,out _,out _));Assert.AreEqual(before+1,SoundEvents.Demand.ActiveCount);
                handle.SetEnabled(false);Assert.AreEqual(before,SoundEvents.Demand.ActiveCount);
            } finally {handle?.SetEnabled(false);status.SetValue(null,old);}
        }
        [TestMethod]
        public void CachedGuardKeepsActualActivationSnapshotAndOwnerPostState()
        {
            var demand=new SoundDemand();var first=new SoundRegistration();var second=new SoundRegistration();demand.Attach(first);demand.Attach(second);
            var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>(hasDemand:()=>demand.HasObservers);
            int pre=0,post=0;var context=new SoundContext(180,1,0,SoundPlaybackKind.Spatial,SoundSituation.ArabianIdleSwordDraw,1,1);
            registry.TryRegister("a","first",a=>{pre++;a.State="first";first.SetEnabled(false);second.SetEnabled(false);Assert.IsFalse(registry.CanNotify);},p=>{post++;Assert.AreEqual("first",p.State);},out _,isActive:()=>first.Enabled);
            registry.TryRegister("b","second",a=>{pre++;a.State="second";},p=>{post++;Assert.AreEqual("second",p.State);},out _,isActive:()=>second.Enabled);
            SoundEvents.Dispatch(context,false,id=>true,(id,v,p)=>{},registry);
            Assert.AreEqual(2,pre);Assert.AreEqual(2,post);Assert.AreEqual(0,demand.ActiveCount);Assert.IsFalse(registry.HasObservers);
            SoundEvents.Dispatch(context,false,id=>true,(id,v,p)=>{},registry);Assert.AreEqual(2,pre);
        }
        [TestMethod]
        public void AudioCheckpointsAreUnboxedAndReleaseSavedClipAtFreeze()
        {
            var args=new SoundPreEventArgs(new SoundContext(180,1,0,SoundPlaybackKind.Spatial,SoundSituation.ArabianIdleSwordDraw,1,1),false,id=>true);
            var clip=new SoundClipHandle("fixture","rollback",1,false);
            clip.Publish((UnityEngine.AudioClip)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(UnityEngine.AudioClip)));
            Assert.IsTrue(args.TryReplaceClip(clip));Assert.AreSame(args,args.Capture());args.MultiplyVolume(.25f);args.ReplaceSound(179);args.Restore(args);
            Assert.AreEqual(1f,args.VolumeFactor);Assert.AreSame(clip,args.SelectedClip);
            var target1=args.Target;var target2=args.Target;Assert.AreNotSame(target1,target2,"Public target getter identity behavior must remain unchanged.");
            args.Freeze();Assert.AreSame(clip,args.SelectedClip);Assert.IsNull(typeof(SoundPreEventArgs).GetField("savedClip",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(args));
        }
    }
}

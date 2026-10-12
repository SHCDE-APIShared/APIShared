using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using APIShared.Audio;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using UnityEngine;

[TestClass]
public sealed class SoundClipTests
{
    private static AudioClip FakeClip() => (AudioClip)FormatterServices.GetUninitializedObject(typeof(AudioClip));
    private static SoundContext Context() => new SoundContext(180, .8f, .3f, SoundPlaybackKind.Spatial, SoundSituation.ArabianIdleSwordDraw, 1, 2);
    [TestMethod]
    public void DeferredLoadingOwnershipIdCaptureMapEpochAndCompletionIsolation()
    {
        // Inject only Unity adapters; execute the real owner/ID/loading/lifetime coordinator.
        var status = typeof(SoundRuntime).GetField("playbackStatus", BindingFlags.NonPublic | BindingFlags.Static);
        var saved = status.GetValue(null); status.SetValue(null, "Ready");
        try {
            var queue = new Queue<Action>(); var released = new List<AudioClip>(); int loads = 0;
            string owner = "ClipTests." + Guid.NewGuid(); var a = FakeClip(); var b = FakeClip();
            var list = new List<AudioClip> { a, b }; long failures = SoundClips.CallbackFailures;
            Assert.IsTrue(SoundClips.Register(owner,"borrowed",1,true,()=>{ loads++; return list[1]; },false,_=>{throw new Exception("owner");},
                out var borrowed,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            Assert.IsFalse(borrowed.IsReady);Assert.AreEqual(0,loads);
            queue.Dequeue()();Assert.AreSame(b,list[1]);Assert.AreSame(b,borrowed.Clip);
            list.RemoveAt(0);Assert.AreSame(b,borrowed.Clip);Assert.IsTrue(borrowed.IsReady);
            Assert.AreEqual(failures+1,SoundClips.CallbackFailures);
            Assert.IsFalse(SoundClips.Register(owner,"borrowed",1,true,()=>a,false,null,out _,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            Assert.IsTrue(SoundClips.Register(owner+".other","borrowed",1,false,()=>a,true,null,out var process,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            queue.Dequeue()();Assert.IsTrue(process.IsReady);
            Assert.IsTrue(SoundClips.Register(owner,"pending",1,true,()=>{loads++;return a;},true,null,out var pending,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            var detached = SoundClips.DetachMapClips();Assert.IsFalse(borrowed.IsReady);Assert.IsTrue(process.IsReady);
            Assert.AreEqual(0,detached.Count); // borrowed is never destroyed; process resource remains alive
            int before = loads;queue.Dequeue()();Assert.AreEqual(before,loads);Assert.IsFalse(pending.IsReady);
            Assert.IsTrue(SoundClips.Register(owner,"borrowed",1,true,()=>a,false,null,out var rebound,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            Assert.AreSame(borrowed,rebound);queue.Dequeue()();Assert.AreSame(a,rebound.Clip);
            Assert.IsTrue(SoundClips.Register(owner,"owned",1,true,()=>b,true,null,out var owned,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            queue.Dequeue()();detached=SoundClips.DetachMapClips();Assert.AreEqual(1,detached.Count);Assert.AreSame(b,detached[0]);Assert.IsFalse(owned.IsReady);
            Assert.AreEqual(0,released.Count);
            Assert.IsTrue(SoundClips.Register(owner,"decode-failure",1,false,()=>a,true,null,out var failed,out _,queue.Enqueue,_=>false,released.Add,(_,__)=>{}));
            queue.Dequeue()();Assert.IsFalse(failed.IsReady);Assert.AreEqual(1,released.Count);Assert.AreSame(a,released[0]);
            Assert.IsFalse(SoundClips.Register(owner,"invalid",float.NaN,false,()=>a,true,null,out _,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
            Assert.IsFalse(SoundClips.Register(owner+"\0", "invalid",1,false,()=>a,true,null,out _,out _,queue.Enqueue,_=>true,released.Add,(_,__)=>{}));
        } finally { status.SetValue(null,saved); }
    }
    [TestMethod]
    public void TypedClipTargetsComposeRollbackSuppressAndFallBackWithoutEffectIdConfusion()
    {
        var clip = new SoundClipHandle("test","triad",.5f,false); clip.Publish(FakeClip());
        var pending = new SoundClipHandle("test","pending",1,false);
        var registry = new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>(); SoundPostEventArgs result=null;
        registry.TryRegister("a","clip",p=>{Assert.IsFalse(p.TryReplaceClip(pending));Assert.IsTrue(p.TryReplaceClip(clip));p.MultiplyVolume(.25f);p.SetPan(-1);},null,out _);
        registry.TryRegister("b","broken",p=>{p.ReplaceSound(179);p.MultiplyVolume(2);throw new Exception();},p=>result=p,out _);
        int effects=0,clips=0;
        SoundEvents.Dispatch(Context(),false,id=>id==179,(id,v,p)=>effects++,registry,(h,v,p,u)=>{clips++;Assert.AreSame(clip,h);Assert.AreEqual(.2f,v);Assert.AreEqual(-1f,p);return true;});
        Assert.AreEqual(0,effects);Assert.AreEqual(1,clips);Assert.AreEqual(-1,result.SoundId);Assert.IsNull(result.Target.EffectId);Assert.AreSame(clip,result.Target.Clip);
        Assert.AreEqual(1L,registry.CallbackFailures);
        SoundEvents.Dispatch(Context(),false,id=>id==179,(id,v,p)=>{effects++;Assert.AreEqual(180,id);Assert.AreEqual(.2f,v);},registry,(_,__,___,____)=>false);
        Assert.AreEqual(180,result.SoundId);Assert.AreEqual(SoundTargetKind.VanillaEffect,result.Target.Kind);
        clip.Invalidate("destroyed");var pre=new SoundPreEventArgs(Context(),false,id=>true);Assert.IsFalse(pre.TryReplaceClip(clip));Assert.AreEqual(180,pre.SoundId);
        clip.Publish(FakeClip());registry.TryRegister("z","suppress",p=>p.SkipOriginalFunction=true,null,out _);
        SoundEvents.Dispatch(Context(),false,id=>true,(id,v,p)=>Assert.Fail("suppressed effect"),registry,(_,__,___,____)=>{Assert.Fail("suppressed clip");return true;});
        Assert.IsTrue(result.WasSuppressed);
        var huge = new SoundClipHandle("test","huge",float.MaxValue,false);huge.Publish(FakeClip());
        pre=new SoundPreEventArgs(new SoundContext(180,2,0,SoundPlaybackKind.Spatial,SoundSituation.Unknown,1,2),false,id=>true);
        Assert.IsFalse(pre.TryReplaceClip(huge));Assert.AreEqual(180,pre.SoundId);
    }
}

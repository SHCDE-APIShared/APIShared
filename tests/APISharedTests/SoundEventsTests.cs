using System;
using System.Collections.Generic;
using System.Threading;
using APIShared.Audio;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class SoundEventsTests
    {
        private static SoundContext Context(float volume = 1f) => new SoundContext(180, volume, .3f, SoundPlaybackKind.Spatial, SoundSituation.ArabianIdleSwordDraw, 21, 42);
        private static Func<int, bool> Valid = id => id >= 0 && id < 500;
        [TestMethod]
        public void OrderedFactorsReplacementPanAndOwnerStateCompose()
        {
            var registry = new InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs>();
            var order = new List<string>(); SoundPostEventArgs result = null;
            registry.TryRegister("z", "last", a => { order.Add("z"); a.MultiplyVolume(2); a.ReplaceSound(179); a.SetPan(-.5f); a.State = "z"; }, p => { result = p; Assert.AreEqual("z", p.State); }, out _, 1);
            registry.TryRegister("b", "first", a => { order.Add("b"); a.MultiplyVolume(.25f); a.ReplaceSound(145); a.State = "b"; }, p => Assert.AreEqual("b", p.State), out _);
            float volume = 0; int sound = -1; float pan = 0;
            SoundEvents.Dispatch(Context(1.6f), true, Valid, (id,v,p) => { sound=id; volume=v; pan=p; }, registry);
            CollectionAssert.AreEqual(new[] { "b", "z" }, order);
            Assert.AreEqual(.8f, volume); Assert.AreEqual(179,sound); Assert.AreEqual(-.5f,pan);
            Assert.IsFalse(result.WasSuppressed); Assert.IsTrue(result.Unstoppable); Assert.AreEqual(180,result.Context.OriginalSoundId);
        }
        [TestMethod]
        public void StickySuppressionAndThrowingMutationsRollback()
        {
            var registry = new InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs>();
            registry.TryRegister("a", "broken", a => { a.MultiplyVolume(.25f); a.ReplaceSound(179); a.SkipOriginalFunction=true; a.State="broken"; throw new Exception(); }, p => Assert.IsNull(p.State), out _);
            registry.TryRegister("b", "good", a => { Assert.AreEqual(1f,a.VolumeFactor); Assert.AreEqual(180,a.SoundId); Assert.IsFalse(a.SkipOriginalFunction); a.SkipOriginalFunction=true; }, null, out _);
            SoundPostEventArgs result=null;
            registry.TryRegister("c", "later", a => a.SkipOriginalFunction=false, p => result=p, out _);
            int calls=0; SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>calls++,registry);
            Assert.AreEqual(0,calls); Assert.IsTrue(result.WasSuppressed); Assert.AreEqual(1L,registry.CallbackFailures);
        }
        [TestMethod]
        public void InvalidGainReplacementAndPanDoNotLeakPartialChanges()
        {
            foreach (Action<SoundPreEventArgs> invalid in new Action<SoundPreEventArgs>[] {
                a=>a.MultiplyVolume(float.NaN), a=>a.MultiplyVolume(float.PositiveInfinity), a=>a.MultiplyVolume(-1),
                a=>a.MultiplyVolume(float.MaxValue), a=>a.ReplaceSound(-1), a=>a.ReplaceSound(10001),
                a=>a.SetPan(float.NaN), a=>a.SetPan(1.1f) })
            {
                var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>();
                registry.TryRegister("a","bad",a=> { a.ReplaceSound(179); a.SetPan(-1); invalid(a); },null,out _);
                SoundEvents.Dispatch(Context(2),false,Valid,(id,v,p)=> { Assert.AreEqual(180,id); Assert.AreEqual(2f,v); Assert.AreEqual(.3f,p); },registry);
                Assert.AreEqual(1L,registry.CallbackFailures);
            }
        }
        [TestMethod]
        public void ZeroUnityAndQuarterFactorsAndNoObserversPreserveOriginalCallCount()
        {
            foreach(float factor in new[]{0f,1f,.25f})
            {
                var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>();
                registry.TryRegister("a","gain",a=>a.MultiplyVolume(factor),null,out _);
                int calls=0; SoundEvents.Dispatch(Context(1.6f),false,Valid,(id,v,p)=> { calls++; Assert.AreEqual(1.6f*factor,v); },registry);
                Assert.AreEqual(1,calls);
            }
            SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=> { Assert.AreEqual(180,id); Assert.AreEqual(1f,v); Assert.AreEqual(.3f,p); },new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>());
        }
        [TestMethod]
        public void ActivationSnapshotDuplicatesFrozenArgsAndRecursion()
        {
            var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>();
            var handle=new SoundRegistration(); int pres=0,posts=0,calls=0; SoundPreEventArgs held=null;
            Assert.IsTrue(registry.TryRegister("a","toggle",a=> { pres++; held=a; handle.SetEnabled(false); SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>calls++,registry); },p=>posts++,out _,isActive:()=>handle.Enabled));
            Assert.IsFalse(registry.TryRegister("a","toggle",a=>{},null,out _));
            SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>calls++,registry);
            Assert.AreEqual(1,pres); Assert.AreEqual(1,posts); Assert.AreEqual(2,calls);
            Assert.ThrowsExactly<InvalidOperationException>(()=>held.MultiplyVolume(2));
            SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>calls++,registry); Assert.AreEqual(1,pres);
            handle.SetEnabled(true); SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>calls++,registry); Assert.AreEqual(2,pres);
        }
        [TestMethod]
        public void OriginalExceptionPropagatesWithoutPostAndPostFailureIsIsolated()
        {
            var registry=new InterceptionEvent<SoundPreEventArgs,SoundPostEventArgs>(); int posts=0;
            registry.TryRegister("a","observe",null,p=> { posts++; throw new Exception(); },out _);
            registry.TryRegister("b","observe",null,p=>posts++,out _);
            Assert.ThrowsExactly<InvalidOperationException>(()=>SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>throw new InvalidOperationException(),registry));
            Assert.AreEqual(0,posts);
            SoundEvents.Dispatch(Context(),false,Valid,(id,v,p)=>{},registry);Assert.AreEqual(2,posts);Assert.AreEqual(1L,registry.CallbackFailures);
        }
        [TestMethod]
        public void ContextNeverInventsAttributionAndExplicitRecordsIgnoreStaleCoordinates()
        {
            var record=new short[21]; record[0]=16;record[3]=180;record[1]=21;record[2]=42;record[11]=100;record[12]=64;
            var context=SoundContextPolicy.Read(record,0,180,1,0);
            Assert.AreEqual(SoundPlaybackKind.Explicit,context.Kind);Assert.IsNull(context.UnitType);Assert.IsNull(context.TileX);
            record[11]=record[12]=-1;record[13]=1;context=SoundContextPolicy.Read(record,0,180,1,0);
            Assert.AreEqual(SoundPlaybackKind.SpatialUnattenuated,context.Kind);Assert.AreEqual(21,context.TileX.Value);Assert.IsNull(context.UnitType);
        }
    }
}

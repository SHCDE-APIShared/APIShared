using System;
using System.Diagnostics;
using System.Reflection;
using Mono.Cecil.Cil;
using MonoMod.Utils;
using APIShared.Audio;
using APIShared.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class AudioPerformanceTests
    {
        public TestContext TestContext { get; set; }
        private static readonly SoundContext Context = new SoundContext(180, 1, 0, SoundPlaybackKind.Spatial, SoundSituation.ArabianIdleSwordDraw, 21, 42);
        private static readonly Func<int, bool> Valid = id => true;
        private static readonly Action<int, float, float> Original = (id, gain, pan) => { };
        private static readonly Action<SoundPreEventArgs> Pre = args => args.MultiplyVolume(1);
        private static readonly Action<SoundPostEventArgs> Post = args => { };
        private static readonly Func<bool> Disabled = () => false;
        [TestMethod]
        public void WarmDispatchAllocationAndTimingMatrix()
        {
            foreach (int count in new[] { 0, 1, 16 }) foreach (string mode in new[] { "Pre", "Post", "Mixed" }) {
                if (count == 0 && mode != "Pre") continue;
                Measure(count, mode, false);
            }
            Measure(16, "Mixed", true);
        }
        private static Action<SFXManager,int,float,float,bool,short[],int> fixturePlay;
        private static int originals;
        private static void RecordOriginal(SFXManager manager,int sound,float gain,float pan,bool unstoppable) { originals++; }
        private static void Forward(SFXManager manager,int sound,float gain,float pan,bool unstoppable,short[] buffer,int index)
            => fixturePlay(manager,sound,gain,pan,unstoppable,buffer,index);
        [TestMethod]
        public void ActualProductionGuardAndConfirmationAllocateNothingWhenDormant()
        {
            using(var inner=new DynamicMethodDefinition(typeof(SoundEvents).GetMethod("Play",BindingFlags.NonPublic|BindingFlags.Static)))
            using(var outer=new DynamicMethodDefinition(typeof(SoundIlContract).GetMethod("Play",BindingFlags.NonPublic|BindingFlags.Static))) {
                int replacements=0;
                foreach(var instruction in inner.Definition.Body.Instructions)
                    if(instruction.Operand is Mono.Cecil.MethodReference method && method.DeclaringType.FullName=="SFXManager" && method.Name=="playSound") {
                        instruction.OpCode=OpCodes.Call;instruction.Operand=inner.Definition.Module.ImportReference(typeof(AudioPerformanceTests).GetMethod(nameof(RecordOriginal),BindingFlags.NonPublic|BindingFlags.Static));replacements++;
                    }
                Assert.AreEqual(1,replacements);
                foreach(var instruction in outer.Definition.Body.Instructions)
                    if(instruction.Operand is Mono.Cecil.MethodReference method && method.DeclaringType.FullName==typeof(SoundEvents).FullName && method.Name=="Play")
                        instruction.Operand=outer.Definition.Module.ImportReference(typeof(AudioPerformanceTests).GetMethod(nameof(Forward),BindingFlags.NonPublic|BindingFlags.Static));
                fixturePlay=(Action<SFXManager,int,float,float,bool,short[],int>)inner.Generate().CreateDelegate(typeof(Action<SFXManager,int,float,float,bool,short[],int>));
                var play=(Action<SFXManager,int,float,float,bool,short[],int>)outer.Generate().CreateDelegate(typeof(Action<SFXManager,int,float,float,bool,short[],int>));
                Assert.AreEqual(0,SoundEvents.Demand.ActiveCount);
                MeasureProduction(play,"no active consumers");
                var status=typeof(SoundRuntime).GetField("playbackStatus",BindingFlags.Static|BindingFlags.NonPublic);var old=status.GetValue(null);
                try {
                    status.SetValue(null,"Ready");string owner="guard."+Guid.NewGuid();
                    for(int i=0;i<16;i++){Assert.IsTrue(SoundEvents.TryRegister(owner,i.ToString(),Pre,Post,out var handle,out _));handle.SetEnabled(false);}
                    MeasureProduction(play,"16 disabled consumers");
                } finally {status.SetValue(null,old);fixturePlay=null;}
            }
        }
        private void MeasureProduction(Action<SFXManager,int,float,float,bool,short[],int> play,string label)
        {
            for(int i=0;i<2000;i++)play(null,180,1,0,false,null,0);
            int calls=originals;long before=GC.GetAllocatedBytesForCurrentThread();long start=Stopwatch.GetTimestamp();
            for(int i=0;i<20000;i++)play(null,180,1,0,false,null,0);
            long elapsed=Stopwatch.GetTimestamp()-start;long bytes=GC.GetAllocatedBytesForCurrentThread()-before;
            Assert.AreEqual(20000,originals-calls);Assert.AreEqual(0L,bytes);
            TestContext.WriteLine("AUDIO_PRODUCTION_GUARD {0}: bytesPerCall=0,nsPerCall={1:F1}",label,elapsed*(1e9/Stopwatch.Frequency)/20000);
        }
        [TestMethod]
        public void GeneratedProductionFastGuardContainsNoAllocationBeforeOriginalReturn()
        {
            using(var definition=new DynamicMethodDefinition(typeof(SoundEvents).GetMethod("Play",BindingFlags.NonPublic|BindingFlags.Static))) {
                bool found=false;
                foreach(var instruction in definition.Definition.Body.Instructions) {
                    if(instruction.OpCode==OpCodes.Newobj||instruction.OpCode==OpCodes.Newarr||instruction.OpCode==OpCodes.Box)Assert.Fail("Allocation precedes production fast return: "+instruction);
                    if(instruction.Operand is Mono.Cecil.MethodReference method && method.DeclaringType.FullName=="SFXManager" && method.Name=="playSound") {found=true;break;}
                }
                Assert.IsTrue(found);
            }
            using(var definition=new DynamicMethodDefinition(typeof(SoundIlContract).GetMethod("Play",BindingFlags.NonPublic|BindingFlags.Static)))
                foreach(var instruction in definition.Definition.Body.Instructions)
                    Assert.IsFalse(instruction.OpCode==OpCodes.Newobj||instruction.OpCode==OpCodes.Newarr||instruction.OpCode==OpCodes.Box,"Outer wrapper allocates.");
        }
        private void Measure(int count, string mode, bool disabled)
        {
            var registry = new InterceptionEvent<SoundPreEventArgs, SoundPostEventArgs>();
            for (int i=0;i<count;i++) registry.TryRegister("benchmark",i.ToString(),mode=="Post"?null:Pre,mode=="Pre"?null:Post,out _,isActive:disabled?Disabled:null);
            const int iterations=20000;
            for(int i=0;i<2000;i++) SoundEvents.Dispatch(Context,false,Valid,Original,registry);
            var times=new long[5];long bytes=0;
            for(int sample=0;sample<times.Length;sample++) {
                long startBytes=GC.GetAllocatedBytesForCurrentThread();long start=Stopwatch.GetTimestamp();
                for(int i=0;i<iterations;i++) SoundEvents.Dispatch(Context,false,Valid,Original,registry);
                times[sample]=Stopwatch.GetTimestamp()-start;bytes=GC.GetAllocatedBytesForCurrentThread()-startBytes;
                if(count==0||disabled) Assert.AreEqual(0L,bytes,"Dormant dispatch allocates after warm-up.");
            }
            Array.Sort(times);
            TestContext.WriteLine("AUDIO_BENCH registrations={0},mode={1},disabled={2},bytesPerCall={3:F1},nsPerCall={4:F1}",count,mode,disabled,bytes/(double)iterations,times[2]*(1e9/Stopwatch.Frequency)/iterations);
        }
    }
}

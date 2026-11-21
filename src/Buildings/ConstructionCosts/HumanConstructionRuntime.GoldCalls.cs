using System;
using System.Runtime.InteropServices;
using APIShared.Internal;
using APIShared.NativeSites;
using BepInEx.Logging;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Buildings
{
    internal static unsafe partial class HumanConstructionRuntime
    {
        internal const string GoldCallsSite = "Buildings.ConstructionRefundGoldCalls";
        private const string GoldCallsForeignPatchFeature = "Construction gold refund calls";
        internal static readonly int[] GoldCallRvas = { 0xC4793, 0xC496C };
        private static readonly byte[][] GoldCallBytes = { new byte[] { 0xE8,0x18,0x11,0xFF,0xFF }, new byte[] { 0xE8,0x3F,0x0F,0xFF,0xFF } };
        internal static NativeSiteSpan[] GoldCallSpans() => new[] {
            new NativeSiteSpan(GoldCallRvas[0],5,NativeSiteMode.Exclusive), new NativeSiteSpan(GoldCallRvas[1],5,NativeSiteMode.Exclusive) };
        private static readonly NativeHookPublicationState goldPublication = new NativeHookPublicationState();
        private static ConstructionGoldCallPatch goldPatches;
        private static ConstructionGoldResource nativeAddResource, goldCallback;
        private struct GoldCallToken { internal RefundFrame Frame; internal int ExpectedDepth; }
        [ThreadStatic] private static GoldCallToken goldCall;
        private static bool MatchesGoldRefundCall() => refund!=null && ReferenceEquals(goldCall.Frame,refund) && resourceDepth==goldCall.ExpectedDepth;

        private static void InitializeGoldRefundCalls(CrusaderLibraryLoadContext context, string hash, ManualLogSource logger)
        {
            if (!NativeStartupPatchWindow.IsOpen || hash != ApiSharedRuntime.SupportedHash ||
                ApiSharedRuntime.ComputeSha256(context.Memory.Slice(0xC4480,0x528)) != "C1E4DC936740B9D35117AC1539BC0A07D48EF212F61CE57778DC2A82AA8445D7")
                throw new InvalidOperationException("Construction gold refund requires the audited startup function.");
            for (int i=0;i<GoldCallRvas.Length;i++)
                if (!context.Memory.Slice(GoldCallRvas[i],5).SequenceEqual(GoldCallBytes[i]))
                    throw new InvalidOperationException("Construction gold CALL changed.");
            var ownership=ApiSharedRuntime.ProcessInstance.NativeOwnership;
            ApiSharedNativeSites.Claim(ownership,GoldCallsSite,GoldCallSpans(),reason=>new InvalidOperationException(reason));
            try
            {
                // Enter the existing LIVE function, including SE and other compatible entry hooks.
                // Calling its native Original trampoline here would bypass their Pre/Skip/Post.
                nativeAddResource=Marshal.GetDelegateForFunctionPointer<ConstructionGoldResource>(context.ModuleHandle+0xB58B0);
                goldCallback=ForwardGoldRefund;
                ulong module=unchecked((ulong)context.ModuleHandle.ToInt64());
                goldPatches=ConstructionGoldCallPatch.Prepare(new[]{module+(uint)GoldCallRvas[0],module+(uint)GoldCallRvas[1]},
                    GoldCallBytes,goldCallback);
                // A foreign patch on either CALL refuses both while they are still unpublished.
                goldPatches.RequireUnpatched(GoldCallsForeignPatchFeature);
                // Root before the first possible write. A second-write failure must retain the first
                // relay and callback forever; ready remains false and the bridge simply forwards.
                goldPublication.Begin(null,null,goldPatches);
                try { goldPatches.Publish(); goldPublication.Complete(); }
                catch(Exception error) { goldPublication.Fail(error); throw; }
                goldPatches.Watch(GoldCallsForeignPatchFeature);
                NativeApiLog.Info(logger,"CONSTRUCTION_GOLD_CALLS: E8/5 at 0xC4793,0xC496C; near FF25 relays; process lifetime.");
            }
            catch
            {
                goldPublication.RollbackUnpublished(null,()=>{
                    goldPatches=null; goldCallback=null; nativeAddResource=null;
                    ApiSharedNativeSites.Release(ownership,GoldCallsSite);
                });
                throw;
            }
        }
        private static long ForwardGoldRefund(IntPtr manager,int player,eGoods good,int amount) =>
            InvokeGoldRefundCall(nativeAddResource,manager,player,good,amount);

        internal static long InvokeGoldRefundCall(ConstructionGoldResource original,IntPtr manager,int player,eGoods good,int amount)
        {
            if (!ready || refund==null) return original(manager,player,good,amount);
            var previous=goldCall;
            goldCall=new GoldCallToken{Frame=refund,ExpectedDepth=resourceDepth+1};
            try { return original(manager,player,good,amount); }
            finally { goldCall=previous; }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate long ConstructionGoldResource(IntPtr manager,int player,eGoods good,int amount);

    // A dedicated CALL-site patch, not a function-entry detour. E8 retains the original +5 return
    // address and Win64 call frame; the FF25 tail relay does not touch any register or stack slot.
    // All machine writes are startup publication only. Published instances are never disposed.
    internal sealed class ConstructionGoldCallPatch
    {
        private readonly ulong[] sites;
        private readonly IntPtr[] relays;
        private readonly byte[][] calls;
        private readonly byte[] relayBytes;
        private readonly ConstructionGoldResource callback;
        private ConstructionGoldCallPatch(ulong[] sites,IntPtr[] relays,byte[][] calls,byte[] relayBytes,ConstructionGoldResource callback)
        { this.sites=sites;this.relays=relays;this.calls=calls;this.relayBytes=relayBytes;this.callback=callback; }

        internal static ConstructionGoldCallPatch Prepare(ulong[] sites,byte[][] expected,ConstructionGoldResource callback,
            Func<ulong,int,IntPtr> allocate=null)
        {
            if(sites==null || expected==null || sites.Length!=2 || expected.Length!=2 || callback==null)
                throw new ArgumentException("Two complete construction gold callsites are required.");
            if(allocate==null) allocate=NativeMemoryManager.AllocateStub;
            byte[] relay=CodePatch.BuildAbsoluteJump(unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64()));
            var relays=new IntPtr[2];var calls=new byte[2][];
            // Validate every original span before allocating/preparing; Publish validates again.
            for(int i=0;i<2;i++)
                if(expected[i].Length!=5 || expected[i][0]!=0xE8 || !CodePatch.Read(sites[i],5).AsSpan().SequenceEqual(expected[i]))
                    throw new InvalidOperationException("Construction gold live CALL overlaps another patch.");
            for(int i=0;i<2;i++)
            {
                relays[i]=allocate(sites[i],relay.Length);
                if(relays[i]==IntPtr.Zero) throw new InvalidOperationException("No nearby construction gold relay available.");
                calls[i]=EncodeCall(sites[i],unchecked((ulong)relays[i].ToInt64()));
                if(!NativeMemoryManager.WriteStub(relays[i],relay) || !CodePatch.Read(unchecked((ulong)relays[i].ToInt64()),14).AsSpan().SequenceEqual(relay))
                    throw new InvalidOperationException("Construction gold relay preparation failed.");
            }
            return new ConstructionGoldCallPatch((ulong[])sites.Clone(),relays,calls,relay,callback) {
                original=new[]{(byte[])expected[0].Clone(),(byte[])expected[1].Clone()} };
        }
        private byte[][] original;
        // Foreign-patch guard over both 5-byte CALL spans: before publication, and once published.
        internal void RequireUnpatched(string feature)
        {
            for(int i=0;i<2;i++) NativeForeignPatchGuard.RequireUnpatched(sites[i],5,"gold CALL "+i,feature);
        }
        internal void Watch(string feature)
        {
            for(int i=0;i<2;i++) NativeForeignPatchGuard.Watch(sites[i],5,"gold CALL "+i,feature);
        }
        internal static byte[] EncodeCall(ulong site,ulong target)
        {
            long distance=checked((long)target-(long)site-5);
            if(distance<int.MinValue || distance>int.MaxValue) throw new InvalidOperationException("Gold relay is outside E8 rel32 reach.");
            byte[] bytes=new byte[5];bytes[0]=0xE8;Buffer.BlockCopy(BitConverter.GetBytes((int)distance),0,bytes,1,4);return bytes;
        }
        internal void Publish(Action<int> beforeWrite=null)
        {
            for(int i=0;i<2;i++)
                if(!CodePatch.Read(sites[i],5).AsSpan().SequenceEqual(original[i]))
                    throw new InvalidOperationException("Construction gold CALL changed before publication.");
            for(int i=0;i<2;i++) { beforeWrite?.Invoke(i);CodePatch.Write(sites[i],calls[i]); }
            for(int i=0;i<2;i++)
                if(!CodePatch.Read(sites[i],5).AsSpan().SequenceEqual(calls[i]) ||
                    !CodePatch.Read(unchecked((ulong)relays[i].ToInt64()),14).AsSpan().SequenceEqual(relayBytes))
                    throw new InvalidOperationException("Construction gold CALL publication differs.");
            GC.KeepAlive(callback);
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.NativeSites;
using APIShared.Simulation;
using APIShared.WorldMutations;
using BepInEx.Logging;
using R3;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.X64.Hooks;
using SHCDESE.API.LowLevel;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    internal static unsafe partial class UnitRelocationRuntime
    {
        internal const string Site="Units.Relocation";
        private static CrusaderLibraryLoadContext context;
        private static ManualLogSource log;
        private static int startupThread,closed,failed,metadataAttempted,metadataReady;
        private static UnitRelocationMetadata metadata;
        private static IntPtr image,interest;
        private static object engineMonitor;
        private static readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private static NativeInlineStubGroup group;
        private static readonly HookHandle<X64InlineHook> hook=new HookHandle<X64InlineHook>();
        private static byte[] expectedPatch,expectedStub,expectedContinuation,expectedStandingCaller;
        private static UnitRelocationIntegrity integrity;
        private static IDisposable initialization,started;
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int Decision(IntPtr manager,int id,uint x,uint y,ushort height,ulong caller);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void RelocateCall(IntPtr manager,int id,uint x,uint y,ushort height);
        private static readonly Decision callback=Decide;
        private static RelocateCall relocate;
        internal static UnitRelocationPolicies Policies {get;private set;}
        [ThreadStatic] private static Frame explicitFrame;
        [ThreadStatic] private static int policyDepth;
        private sealed class Frame
        {internal string Owner;internal UnitRelocationRequest Request;internal ushort Height;internal bool Visited,Vetoed,Allowed;internal UnitRelocationStatus Failure;}
        internal static void Initialize(CrusaderLibraryLoadContext load,string hash,ManualLogSource logger)
        {
            if(Interlocked.Exchange(ref metadataAttempted,1)!=0)return;
            log=logger;
            try
            {
                if(load==null||load.ModuleHandle==IntPtr.Zero||hash!=ApiSharedRuntime.SupportedHash)throw new InvalidOperationException("Relocation image is not audited.");
                var prepared=UnitRelocationMetadata.Create(()=>Internal.EngineMonitor.Current,()=>Marshal.AllocHGlobal(65536),
                    ()=>Internal.MissionEvents.Initialization.Subscribe(_=>Volatile.Write(ref closed,1)),
                    ()=>Internal.MissionEvents.Started.Subscribe(_=>{Volatile.Write(ref closed,1);CheckIntegrity();}),
                    error=>ReportFailure("policy failed",error));
                context=load;image=load.ModuleHandle;startupThread=Thread.CurrentThread.ManagedThreadId;
                engineMonitor=prepared.Monitor;interest=prepared.Interest;Policies=prepared.Policies;
                initialization=prepared.Initialization;started=prepared.Started;metadata=prepared;
                // Final release publication. No partial preparation can admit Ensure or native code.
                Volatile.Write(ref metadataReady,1);
            }
            catch{Volatile.Write(ref failed,1);throw;}
            // Binding metadata claims/patches nothing until a consumer explicitly requests it.
        }
        internal static NativeSiteSpan[] Spans()=>new[]{
            new NativeSiteSpan(UnitRelocationNative.EntryRva,UnitRelocationNative.Length,NativeSiteMode.Exclusive),
            new NativeSiteSpan(UnitRelocationNative.EntryRva+UnitRelocationNative.Length,281-UnitRelocationNative.Length,NativeSiteMode.Observe),
            new NativeSiteSpan(UnitRelocationNative.StandingReturnRva-5,5,NativeSiteMode.Observe)};
        internal static bool Ensure(string owner,out string reason)
        {
            if(string.IsNullOrWhiteSpace(owner)){reason="A stable caller GUID is required.";return false;}
            if(publication.IsReady){bool ok=CheckIntegrity();reason=ok?string.Empty:"Published relocation integrity was lost.";return ok;}
            if(Volatile.Read(ref metadataReady)==0||metadata==null||context==null||interest==IntPtr.Zero||Policies==null||engineMonitor==null||
                Volatile.Read(ref failed)!=0||Volatile.Read(ref closed)!=0||Thread.CurrentThread.ManagedThreadId!=startupThread)
            {reason="Relocation acquisition requires the initialized LibraryLoaded startup thread before mission initialization.";return false;}
            try
            {
                ValidateLayouts();
                expectedContinuation=context.Memory.Slice(UnitRelocationNative.EntryRva+16,281-16).ToArray();
                expectedStandingCaller=context.Memory.Slice(UnitRelocationNative.StandingReturnRva-5,5).ToArray();
                UnitRelocationNative.ValidateStandingCaller(expectedStandingCaller,unchecked((ulong)image.ToInt64()));
                RequireExecutable(image+UnitRelocationNative.EntryRva,281,true);
                RequireExecutable(image+UnitRelocationNative.StandingReturnRva-5,5,true);
                NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.StandingReturnRva-5,expectedStandingCaller);
                NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.EntryRva+16,expectedContinuation);
                group=new NativeInlineStubGroup(Site,publication);group.Claim(Spans());group.Begin(context.Region);
                ulong units=Address(UnitRelocationNative.UnitsRva),gate=unchecked((ulong)interest.ToInt64()),call=unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64());
                group.Add(hook,Address(UnitRelocationNative.EntryRva),(a,instructions,back)=>UnitRelocationNative.Emit(a,instructions,back,units,gate,call),16);
                var commit=group.Commit("Relocation native owner could not prepare.");
                if(!hook.Success||hook.Hook.DisplacedByteCount!=16)throw new InvalidOperationException("Relocation backend span differs.");
                expectedStub=NativeInlineStub.ExpectedStub(Address(UnitRelocationNative.EntryRva),UnitRelocationNative.Entry,16,
                    (a,ins,back)=>UnitRelocationNative.Emit(a,ins,back,units,gate,call),unchecked((ulong)hook.Hook.StubAddress.ToInt64()),"Relocation span boundary differs.");
                RequireExecutable(hook.Hook.StubAddress,expectedStub.Length,false);
                NativeIndirectEntry.VerifyBytes(hook.Hook.StubAddress,expectedStub);
                expectedPatch=new byte[16];expectedPatch[0]=0xFF;expectedPatch[1]=0x25;
                Array.Copy(BitConverter.GetBytes(hook.Hook.StubAddress.ToInt64()),0,expectedPatch,6,8);expectedPatch[14]=expectedPatch[15]=0x90;
                relocate=Marshal.GetDelegateForFunctionPointer<RelocateCall>(image+UnitRelocationNative.EntryRva);
                integrity=new UnitRelocationIntegrity(ReadIntegrity,OnIntegrityLost);
                group.Publish(commit,typeof(UnitRelocationRuntime),()=>{
                    NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.EntryRva,expectedPatch);
                    NativeIndirectEntry.VerifyBytes(hook.Hook.StubAddress,expectedStub);
                    NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.EntryRva+16,expectedContinuation);
                    NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.StandingReturnRva-5,expectedStandingCaller);
                });
                reason=string.Empty;return true;
            }
            catch(Exception error)
            {
                Volatile.Write(ref failed,1);group?.RollbackUnpublished(()=>relocate=null);
                ReportFailure("unavailable",error);reason=SafeReason(error);return false;
            }
        }
        internal static bool IsAvailable=>publication.IsReady&&CheckIntegrity();
        private static bool CheckIntegrity()
        {
            if(Volatile.Read(ref failed)!=0||!publication.IsReady)return false;
            return integrity!=null&&integrity.Check();
        }
        private static bool ReadIntegrity()
        {
            if(!hook.Success||!hook.Hook.IsInstalled)throw new InvalidOperationException("Relocation hook owner disappeared.");
            RequireExecutable(image+UnitRelocationNative.EntryRva,281,true);
            RequireExecutable(hook.Hook.StubAddress,expectedStub.Length,false);
            RequireExecutable(image+UnitRelocationNative.StandingReturnRva-5,5,true);
            NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.EntryRva,expectedPatch);
            NativeIndirectEntry.VerifyBytes(hook.Hook.StubAddress,expectedStub);
            NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.EntryRva+16,expectedContinuation);
            NativeIndirectEntry.VerifyBytes(image+UnitRelocationNative.StandingReturnRva-5,expectedStandingCaller);
            return true;
        }
        private static void OnIntegrityLost(Exception error)
        {
            Volatile.Write(ref failed,1);
            for(int i=0;i<65536;i++)Marshal.WriteByte(interest,i,0);
            ReportFailure("integrity permanently lost",error);
        }
        private static ulong Address(int rva)=>unchecked((ulong)image.ToInt64())+(uint)rva;
        [StructLayout(LayoutKind.Sequential)] private struct MemoryRegion
        {internal IntPtr BaseAddress,AllocationBase;internal uint AllocationProtect;internal UIntPtr RegionSize;internal uint State,Protect,Type;}
        [DllImport("kernel32",SetLastError=true)] private static extern UIntPtr VirtualQuery(IntPtr address,out MemoryRegion region,UIntPtr size);
        private static void RequireExecutable(IntPtr address,int length,bool module)
        {
            ulong cursor=unchecked((ulong)address.ToInt64()),end=checked(cursor+(uint)length);
            while(cursor<end)
            {
                if(VirtualQuery(new IntPtr(unchecked((long)cursor)),out MemoryRegion region,(UIntPtr)Marshal.SizeOf(typeof(MemoryRegion)))==UIntPtr.Zero||
                    region.State!=0x1000||(region.Protect&0x100)!=0||(region.Protect&0xE0)==0||
                    module&&(region.Type!=0x1000000||region.AllocationBase!=image))
                    throw new InvalidOperationException("Relocation executable memory span changed.");
                ulong next=checked(unchecked((ulong)region.BaseAddress.ToInt64())+region.RegionSize.ToUInt64());
                if(next<=cursor)throw new InvalidOperationException("Relocation executable region extent differs.");
                cursor=next;
            }
        }
        internal static void ReportFailure(string operation,Exception error)
        {try{NativeApiLog.Error(log,"UNIT_RELOCATION: "+operation+": "+error);}catch{}}
        internal static string SafeReason(Exception error)
        {try{return error?.Message??"Relocation failed.";}catch{return "Relocation failure details are unavailable.";}}
        private static int Decide(IntPtr manager,int id,uint x,uint y,ushort height,ulong caller)
        {
            Frame frame=explicitFrame;
            bool own=frame!=null&&manager==image+UnitRelocationNative.UnitsRva&&id==frame.Request.UnitId&&x==(uint)frame.Request.TargetX&&y==(uint)frame.Request.TargetY&&height==frame.Height;
            try
            {
                if(Volatile.Read(ref failed)!=0)return own?1:0;
                if(own&&frame.Visited){frame.Failure=UnitRelocationStatus.Rejected;return 1;}
                if(own)frame.Visited=true;
                // Ordinary native observations retain the native Life2 predicate. The stronger
                // death-marker exclusion belongs to explicit mutation admission, not this observation.
                if(manager!=image+UnitRelocationNative.UnitsRva||!TryCanonical(id,out GameUnit* unit)||unit->r_AliveState!=AliveState.IsAlive)return own?1:0;
                var origin=UnitRelocationValidation.Origin(own,caller,unchecked((ulong)image.ToInt64()));
                var observation=new UnitRelocationContext(id,unit->r_GlobalId,unit->r_UnitChimp,unit->r_ControllableForPlayerId,(int)x,(int)y,height,origin,own?frame.Owner:null);
                bool veto;
                policyDepth++;try{veto=Policies.Cancel(observation);}finally{policyDepth--;}
                if(own)
                {
                    if(!ValidateRequest(frame.Request,out frame.Failure,out _))return 1;
                    frame.Vetoed=veto;frame.Allowed=!veto;
                }
                return veto?1:0;
            }
            catch(Exception error){ReportFailure("callback failed",error);if(own){frame.Failure=UnitRelocationStatus.Rejected;return 1;}return 0;}
        }
    }
}

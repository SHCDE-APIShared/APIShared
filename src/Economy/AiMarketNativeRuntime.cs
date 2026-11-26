using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Logging;
using Iced.Intel;
using RedBird.Abstractions.Hooks;
using RedBird.Abstractions.Hooks.Transaction;
using RedBird.Backends.NativeX64;
using RedBird.Core.Memory;
using RedBird.X64.Hooks.Transaction;
using APIShared.NativeSites;

namespace APIShared.Economy
{
    // One permanent shared owner. Initialization is LibraryLoaded-only; no late patch retry.
    internal static unsafe class AiMarketNativeRuntime
    {
        internal const string AuditedHash="FBCB93195FC7EFCA9BDAC5204852EFDD76F9818F59A6711750D77C9CEF2831E2";
        internal const string SiteName="Economy.AiMarketEvents";
        internal static readonly int[] Rvas={0x3EC90,0x40740,0x29650,0x29700};
        internal static readonly byte[][] Entries={
            new byte[]{0x40,0x56,0x41,0x54,0x41,0x55},
            new byte[]{0x40,0x55,0x56,0x41,0x56,0x48,0x81,0xEC,0x60,0x01,0,0},
            new byte[]{0x48,0x89,0x5C,0x24,8,0x48,0x89,0x6C,0x24,0x10},
            new byte[]{0x45,0x85,0xC9,0x0F,0x88,0x96,0,0,0}};
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PairCall(IntPtr root,int player);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int BuyCall(IntPtr root,int player,int good,int amount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SellCall(IntPtr root,int player,int good,int amount);
        internal static readonly NativeDetourBackend Backend=new NativeDetourBackend(new NativeDetourOptions{AllowedSchemes=DetourScheme.Indirect,FollowJumps=false});
        private static readonly NativeHookPublicationState publication=new NativeHookPublicationState();
        private static readonly DetourHandle<PairCall> decision=new DetourHandle<PairCall>(),recruit=new DetourHandle<PairCall>();
        private static readonly DetourHandle<BuyCall> buy=new DetourHandle<BuyCall>();
        private static readonly DetourHandle<SellCall> sell=new DetourHandle<SellCall>();
        private static HookTransaction transaction;
        private static bool initialized;
        private static IntPtr image;
        private static ManualLogSource log;
        private static int integrityFailures;
        private static string diagnostic="AI market hooks have not initialized.";
        internal static bool IsAvailable=>publication.IsReady&&Volatile.Read(ref integrityFailures)==0;
        internal static string Diagnostic=>IsAvailable?string.Empty:diagnostic;
        internal static NativeSiteSpan[] Spans()
        {
            var result=new NativeSiteSpan[Rvas.Length];
            for(int i=0;i<result.Length;i++)result[i]=new NativeSiteSpan(Rvas[i],Entries[i].Length,NativeSiteMode.Exclusive);
            return result;
        }
        internal static void Initialize(IntPtr module,ScanRegion region,string hash,ManualLogSource logger)
        {
            if(initialized){publication.ThrowIfUnavailable();return;}
            initialized=true;image=module;log=logger;
            HookTransaction pending=null;
            var sites=ApiSharedRuntime.ProcessInstance.NativeOwnership;
            bool claimed=false;
            try
            {
                if(!string.Equals(hash,AuditedHash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("AI market entries require the audited native hash.");
                for(int i=0;i<Rvas.Length;i++)
                {
                    NativeForeignPatchGuard.RequireUnpatched(unchecked((ulong)(module+Rvas[i]).ToInt64()),Entries[i].Length,"AI market entry",SiteName);
                    NativeIndirectEntry.VerifyBytes(module+Rvas[i],Entries[i]);
                }
                ApiSharedNativeSites.Claim(sites,SiteName,Spans(),reason=>new InvalidOperationException(reason));claimed=true;
                pending=new HookTransaction(region,SHCDESE.BepInEx.Bootstrap.Plugin.Instance.LoggerFactory,
                    new HookTransactionOptions{FailureMode=TransactionFailureMode.RollbackAndThrow,OwnsHooks=true,
                        InstallOnCommit=false,Backend=NativeStartupPatchWindow.Backend(Backend)});
                pending.AddDetour(decision,HookTarget.FromAddress(unchecked((ulong)(module+Rvas[0]).ToInt64())),(PairCall)Decision);
                pending.AddDetour(recruit,HookTarget.FromAddress(unchecked((ulong)(module+Rvas[1]).ToInt64())),(PairCall)Recruit);
                pending.AddDetour(buy,HookTarget.FromAddress(unchecked((ulong)(module+Rvas[2]).ToInt64())),(BuyCall)Buy);
                pending.AddDetour(sell,HookTarget.FromAddress(unchecked((ulong)(module+Rvas[3]).ToInt64())),(SellCall)Sell);
                var result=pending.Commit();
                if(!result.IsCompleteSuccess||!decision.Success||!recruit.Success||!buy.Success||!sell.Success)
                    throw new InvalidOperationException("All four AI market entries must prepare successfully.");
                Validate(decision.Hook,module+Rvas[0],Entries[0],false);
                Validate(recruit.Hook,module+Rvas[1],Entries[1],false);
                Validate(buy.Hook,module+Rvas[2],Entries[2],false);
                Validate(sell.Hook,module+Rvas[3],Entries[3],false);
                transaction=pending;
                NativeHookPublication.Enable(pending,result,publication,typeof(AiMarketNativeRuntime),()=>{
                    Validate(decision.Hook,module+Rvas[0],Entries[0],true);
                    Validate(recruit.Hook,module+Rvas[1],Entries[1],true);
                    Validate(buy.Hook,module+Rvas[2],Entries[2],true);
                    Validate(sell.Hook,module+Rvas[3],Entries[3],true);
                });
                NativeApiLog.Info(logger,"AI market shared entry observations ready: 3EC90/6,40740/12,29650/10,29700/9; process lifetime.");
            }
            catch(Exception error)
            {
                diagnostic=error.Message;
                publication.RollbackUnpublished(pending,()=>{transaction=null;if(claimed)ApiSharedNativeSites.Release(sites,SiteName);});
                throw;
            }
        }
        private static void Decision(IntPtr root,int player)=>Pair(AiMarketOperation.PurchaseDecision,root,player,decision.Original);
        private static void Recruit(IntPtr root,int player)=>Pair(AiMarketOperation.Recruitment,root,player,recruit.Original);
        internal static void Pair(AiMarketOperation operation,IntPtr root,int player,PairCall original)
        {
            var observers=publication.IsReady?AiMarketEvents.Snapshot():Array.Empty<AiMarketRegistration>();
            DispatchPair(observers,operation,root,player,original);
        }
        internal static void DispatchPair(AiMarketRegistration[] observers,AiMarketOperation operation,IntPtr root,int player,PairCall original)
        {
            long* tokens=stackalloc long[observers.Length];
            var context=new AiMarketContext(operation,root,player,-1,0);
            uint entered=AiMarketEvents.Begin(observers,context,tokens);bool completed=false;
            try{original(root,player);completed=true;}
            finally{AiMarketEvents.End(observers,context,tokens,entered,completed,0);}
        }
        private static int Buy(IntPtr root,int player,int good,int amount)=>DispatchBuy(
            publication.IsReady?AiMarketEvents.Snapshot():Array.Empty<AiMarketRegistration>(),root,player,good,amount,buy.Original);
        internal static int DispatchBuy(AiMarketRegistration[] observers,IntPtr root,int player,int good,int amount,BuyCall original)
        {
            long* tokens=stackalloc long[observers.Length];
            var context=new AiMarketContext(AiMarketOperation.Buy,root,player,good,amount);
            uint entered=AiMarketEvents.Begin(observers,context,tokens);bool completed=false;int result=0;
            try{result=original(root,player,good,amount);completed=true;return result;}
            finally{AiMarketEvents.End(observers,context,tokens,entered,completed,result);}
        }
        private static void Sell(IntPtr root,int player,int good,int amount)=>DispatchSell(
            publication.IsReady?AiMarketEvents.Snapshot():Array.Empty<AiMarketRegistration>(),root,player,good,amount,sell.Original);
        internal static void DispatchSell(AiMarketRegistration[] observers,IntPtr root,int player,int good,int amount,SellCall original)
        {
            long* tokens=stackalloc long[observers.Length];
            var context=new AiMarketContext(AiMarketOperation.Sell,root,player,good,amount);
            uint entered=AiMarketEvents.Begin(observers,context,tokens);bool completed=false;
            try{original(root,player,good,amount);completed=true;}
            finally{AiMarketEvents.End(observers,context,tokens,entered,completed,0);}
        }
        internal static bool CheckIntegrity()
        {
            if(!IsAvailable)return false;
            bool valid=Live(decision.Hook,0)&&Live(recruit.Hook,1)&&Live(buy.Hook,2)&&Live(sell.Hook,3);
            if(!valid)
            {
                diagnostic="AI market shared entries were overwritten or changed after publication.";
                if(FailureThrottle.ShouldReport(ref integrityFailures))
                    try{NativeApiLog.Error(log,diagnostic);}catch{}
            }
            return valid;
        }
        private static bool Live<T>(IDetour<T> hook,int index) where T:Delegate
        {
            var native=hook as NativeDetour<T>;IntPtr target=image+Rvas[index];
            if(native==null||!native.IsInstalled||native.Scheme!=DetourScheme.Indirect||native.DisplacedByteCount!=Entries[index].Length||
                native.TargetAddress!=unchecked((ulong)target.ToInt64())||native.ChainDepth!=1||native.PointerSlot==IntPtr.Zero||
                Marshal.ReadByte(target)!=0xFF||Marshal.ReadByte(target,1)!=0x25||target+6+Marshal.ReadInt32(target,2)!=native.PointerSlot||
                Marshal.ReadIntPtr(native.PointerSlot)!=native.HookEntryPointAddress)return false;
            for(int i=6;i<Entries[index].Length;i++)if(Marshal.ReadByte(target,i)!=0x90)return false;
            return true;
        }
        internal static void Validate<T>(IDetour<T> hook,IntPtr target,byte[] entry,bool installed) where T:Delegate
        {
            var native=hook as NativeDetour<T>;
            if(native==null||native.IsInstalled!=installed||native.Scheme!=DetourScheme.Indirect||native.DisplacedByteCount!=entry.Length||
                native.TargetAddress!=unchecked((ulong)target.ToInt64())||native.ChainDepth!=1||native.PointerSlot==IntPtr.Zero||native.TrampolineAddress==IntPtr.Zero)
                throw new InvalidOperationException("AI market indirect detour contract differs.");
            if(!installed)NativeIndirectEntry.VerifyBytes(target,entry);
            else
            {
                if(Marshal.ReadByte(target)!=0xFF||Marshal.ReadByte(target,1)!=0x25||target+6+Marshal.ReadInt32(target,2)!=native.PointerSlot||
                    Marshal.ReadIntPtr(native.PointerSlot)!=native.HookEntryPointAddress)
                    throw new InvalidOperationException("AI market installed pointer slot differs.");
                for(int i=6;i<entry.Length;i++)if(Marshal.ReadByte(target,i)!=0x90)throw new InvalidOperationException("AI market padding differs.");
            }
            // RedBird re-encodes redundant REX prefixes (40 56 becomes 56), so compare
            // decoded instructions and branch targets, never fixed trampoline byte offsets.
            byte[] replay=APIShared.Internal.NativeCodeReader.ReadBounded(native.TrampolineAddress,Math.Min(native.TrampolineSize,entry.Length+32));
            var expected=Decoder.Create(64,new ByteArrayCodeReader(entry),unchecked((ulong)target.ToInt64()));
            var actual=Decoder.Create(64,new ByteArrayCodeReader(replay),unchecked((ulong)native.TrampolineAddress.ToInt64()));
            ulong end=unchecked((ulong)(target+entry.Length).ToInt64());
            while(expected.IP<end)
            {
                Instruction before=expected.Decode(),after=actual.Decode();
                if(expected.LastError!=DecoderError.None||actual.LastError!=DecoderError.None||before.IsInvalid||after.IsInvalid)
                    throw new InvalidOperationException("AI market displaced instructions do not decode.");
                if(before.FlowControl==FlowControl.Next&&!before.IsIPRelativeMemoryOperand)
                {
                    if(after.FlowControl!=FlowControl.Next||after.IsIPRelativeMemoryOperand||before.Code!=after.Code||Format(before)!=Format(after))
                        throw new InvalidOperationException("AI market trampoline changes an instruction.");
                }
                else if(before.FlowControl==FlowControl.ConditionalBranch&&before.Op0Kind==OpKind.NearBranch64&&
                    (before.NearBranchTarget<unchecked((ulong)target.ToInt64())||before.NearBranchTarget>=end))
                {
                    if(after.FlowControl!=FlowControl.ConditionalBranch||after.ConditionCode!=before.ConditionCode||
                        after.Op0Kind!=OpKind.NearBranch64||after.NearBranchTarget!=before.NearBranchTarget)
                        throw new InvalidOperationException("AI market relocated condition or destination differs.");
                }
                else throw new InvalidOperationException("Unsupported audited AI market instruction.");
            }
            if(expected.IP!=end)throw new InvalidOperationException("AI market entry ends inside an instruction.");
            Instruction tail=actual.Decode();
            if(actual.LastError!=DecoderError.None||tail.IsInvalid||tail.FlowControl!=FlowControl.UnconditionalBranch||
                tail.Op0Kind!=OpKind.NearBranch64||tail.NearBranchTarget!=end)
                throw new InvalidOperationException("AI market trampoline continuation differs.");
        }
        private static string Format(Instruction instruction)
        {
            var output=new StringOutput();new NasmFormatter().Format(instruction,output);return output.ToStringAndReset();
        }
    }
}

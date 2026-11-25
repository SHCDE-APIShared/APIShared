using System;
using BepInEx.Logging;
using APIShared.Units.AiMilitary.Orders;
using APIShared.Units.AiMilitary.Native;
using RedBird.Abstractions.Hooks;
using RedBird.Backends.NativeX64;
using SHCDESE.API.LowLevel;
using SHCDESE.Detours;

namespace APIShared.Units
{
    internal static class UnitMoveRuntime
    {
        private static ManualLogSource log;
        private static ulong nativeTarget;
        private static UnitMoveNativeOwner<BulkUnitDetours.c_game_unit_issueorder_movehere_delegate> nativeOwner;
        // The single process instance has immutable injected world/log boundaries. Tests exercise
        // the same broker with an independent world; no test hook or native game access is needed.
        private static UnitMoveBroker broker=new UnitMoveBroker(ReadWorld,Report,SourceAvailable);
        internal static UnitMoveBroker Broker=>broker;
        private static UnitMoveWorld ReadWorld()=>WorldMutations.WorldMutationEvents.TryGetCurrentWorld(out long session,out long generation)
            ?new UnitMoveWorld(session,generation):default;
        private static void Report(Exception error)
        {try{NativeApiLog.Error(log,"UNIT_MOVE_OBSERVER_FAILED: "+error);}catch{}}
        private static bool SourceAvailable()
        {
            if(!AiMilitaryOrderBroker.IsUnitMoveApplied)return false;
            try
            {
                var handle=BulkUnitDetours.c_game_unit_issueorder_movehere_hook;
                return nativeOwner!=null && nativeOwner.IsCurrent(handle!=null && handle.Success,handle!=null && handle.Success?handle.Hook:null);
            }
            catch{nativeOwner?.IsCurrent(false,null);return false;}
        }
        internal static bool NativeOwnerReady<T>(bool succeeded,IDetour<T> hook,ulong target) where T:Delegate
            =>succeeded && target!=0 && hook is NativeDetour<T> native && native.IsInstalled && native.TargetAddress==target;
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource logger)
        {
            log=logger;
            if(hash!=ApiSharedRuntime.SupportedHash || !AiMilitaryOrderBroker.IsUnitMoveApplied || !WorldMutations.WorldMutationRuntime.IsWorldLifecycleReady)
                throw new InvalidOperationException("Effective unit moves require the audited Original owner and identified-world lifecycle.");
            nativeTarget=unchecked((ulong)context.ModuleHandle.ToInt64())+AiMilitaryNativeBindings.MoveRva;
            var handle=BulkUnitDetours.c_game_unit_issueorder_movehere_hook;
            bool succeeded=handle!=null && handle.Success;
            if(!UnitMoveNativeOwner<BulkUnitDetours.c_game_unit_issueorder_movehere_delegate>.TryCreate(succeeded,
                succeeded?handle.Hook:null,nativeTarget,NativeOwnerLost,out nativeOwner,out string reason))
                throw new InvalidOperationException(reason);
            broker.SetAvailable(true);
            NativeApiLog.Info(log,"UNIT_MOVE_OBSERVATIONS_READY: final Original arguments/results; guarded outer-wrapper identities; no additional native hook.");
        }
        private static void NativeOwnerLost(string reason)
        {try{NativeApiLog.Error(log,"UNIT_MOVE_NATIVE_OWNER_CHANGED: "+reason);}catch{}}
        internal static UnitMoveBroker.Frame Enter()=>broker.EnterWrapper();
        internal static void Exit(UnitMoveBroker.Frame frame)=>broker.ExitWrapper(frame);
        internal static void OwnerRejected(int unit,int x,int y,int extra)=>broker.OwnerRejected(unit,x,y,extra);
        internal static bool Before(int unit,int x,int y,int extra,out UnitMoveBroker.Invocation invocation)=>broker.BeforeOriginal(unit,x,y,extra,out invocation);
        internal static void Complete(UnitMoveBroker.Invocation invocation,bool invoked,bool returned,long result,Exception failure)
        {try{broker.Complete(invocation,invoked,returned,result,failure);}catch(Exception error){Report(error);}}
    }
}

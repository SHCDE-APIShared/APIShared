using System;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using APIShared.Internal;
using APIShared.Simulation;
using APIShared.WorldMutations;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    internal static unsafe partial class UnitRelocationRuntime
    {
        private static void ValidateLayouts()
        {
            if(Marshal.SizeOf(typeof(GameUnit))!=0x490||GameUnitManager.UnitRecordByteSize!=0x490||GameUnitManager.FirstLiveUnitId!=1||
                GameUnitManager.LastLiveUnitId!=9999||GameUnitManager.NativeUnitSlotCount!=10000||
                (long)UnitRelocationNative.UnitsRva+10000L*0x490+0x65C>context.Memory.Length)
                throw new InvalidOperationException("Relocation unit pool layout differs.");
            if(Enum.GetUnderlyingType(typeof(eChimps))!=typeof(ushort)||Enum.GetUnderlyingType(typeof(AliveState))!=typeof(short))throw new InvalidOperationException("Relocation native enum widths differ.");
            Offset<uint>(nameof(GameUnit.r_GlobalId),0x94);Offset<eChimps>(nameof(GameUnit.r_UnitChimp),0x8A);Offset<ushort>(nameof(GameUnit.r_ControllableForPlayerId),0x92);
            Offset<AliveState>(nameof(GameUnit.r_AliveState),0x88);Offset<ushort>(nameof(GameUnit.r_IsKilledByProjectile),0x29C);
            Offset<ushort>(nameof(GameUnit.r_CurrentTilePositionX),0xC0);Offset<ushort>(nameof(GameUnit.r_CurrentTilePositionY),0xC2);
            Offset<uint>(nameof(GameUnit.r_CurrentPositionTileId),0xD0);Offset<ushort>(nameof(GameUnit.r_NextTilePositionX2),0xDC);Offset<ushort>(nameof(GameUnit.r_NextTilePositionY2),0xDE);
            Offset<ushort>(nameof(GameUnit.r_CurrentWorldPositionX),0xB2);Offset<ushort>(nameof(GameUnit.r_CurrentWorldPositionY),0xB4);Offset<ushort>(nameof(GameUnit.r_HeightElevation),0xB6);
            Offset<ushort>(nameof(GameUnit.r_MovementSubstep),0xF4);
            if(!NativeInlineStub.SnapshotMatches(context.Memory,UnitRelocationNative.EntryRva,UnitRelocationNative.Entry))throw new InvalidOperationException("Relocation original bytes differ.");
        }
        private static void Offset<T>(string name,int expected)
        {
            FieldInfo field=typeof(GameUnit).GetField(name,BindingFlags.Public|BindingFlags.Instance);
            if(field==null||field.FieldType!=typeof(T)||Marshal.OffsetOf(typeof(GameUnit),name).ToInt32()!=expected)
                throw new InvalidOperationException("Relocation field layout/type differs: "+name);
        }
        private static bool TryLive(int id,out GameUnit* unit)
        {return TryCanonical(id,out unit)&&UnitAccess.IsReallyAlive(unit);}
        private static bool TryCanonical(int id,out GameUnit* unit)
        {
            unit=null;
            return id>=GameUnitManager.FirstLiveUnitId&&id<=GameUnitManager.LastLiveUnitId&&UnitAccess.TryGetById(id,out unit,out _)&&
                unit==(GameUnit*)((byte*)image+UnitRelocationNative.UnitsRva+(long)id*0x490+0x65C);
        }
        private static int Tile(int x,int y)
        {
            if((uint)x>=800||(uint)y>=800||!GameTileManagerAPI.Instance.IsTileInsideMapBounds(x,y))return -1;
            int tile=GameTileManagerAPI.Instance.GetTileId(x,y);return (uint)tile<320800?tile:-1;
        }
        // Bounded traversal rejects cycles without allocating a visited set. Neighbor Current caches
        // are deliberately not compared: legitimate in-flight occupancy can use accepted Next.
        private static bool Chain(int tile,int actor,int expectedCount)
        {
            if((uint)tile>=320800)return false;
            return UnitRelocationValidation.Chain(((short*)(image+0x4C559B0))[tile],actor,expectedCount,ReadChainNode);
        }
        private static readonly Func<int,UnitRelocationValidation.Node?> ReadChainNode=ReadNode;
        private static UnitRelocationValidation.Node? ReadNode(int id)=>TryCanonical(id,out GameUnit* node)?
            (UnitRelocationValidation.Node?)new UnitRelocationValidation.Node(node->r_GlobalId,*(short*)((byte*)node+0x8FA-0x65C)):null;
        private static bool Chains(int source,int destination,int actor,bool after)
        {
            if((uint)source>=320800||(uint)destination>=320800)return false;
            int sourceCount=after&&source!=destination?0:1,destinationCount=after||source==destination?1:0;
            return UnitRelocationValidation.Pair(((short*)(image+0x4C559B0))[source],((short*)(image+0x4C559B0))[destination],
                actor,sourceCount,destinationCount,source==destination,ReadChainNode);
        }
        private static bool ValidateRequest(UnitRelocationRequest request,out UnitRelocationStatus status,out string reason)
        {
            status=UnitRelocationStatus.Rejected;
            bool world=WorldMutationEvents.TryGetCurrentWorld(out long session,out long generation);
            if(!UnitRelocationValidation.Scope(SimulationMutationScope.IsTickActive,engineMonitor!=null&&Monitor.IsEntered(engineMonitor),
                WorldMutationRuntime.Registry.HasOpenBatch,world&&MissionLifecycleService.HasStartedMission,session,generation,request))
            {reason="Relocation requires the current identified simulation tick, engine monitor and completed source batch.";return false;}
            if(!TryLive(request.UnitId,out GameUnit* unit)||!UnitRelocationValidation.Identity(request,request.UnitId,unit->r_GlobalId,unit->r_UnitChimp,unit->r_ControllableForPlayerId,true))
            {status=UnitRelocationStatus.IdentityChanged;reason="The requested unit lifetime/type/controller changed.";return false;}
            int source=Tile(unit->r_CurrentTilePositionX,unit->r_CurrentTilePositionY),destination=Tile(request.TargetX,request.TargetY);
            if(source<0||destination<0||unit->r_CurrentPositionTileId!=(uint)source||
                *(ushort*)((byte*)unit+0x9B0-0x65C)!=0||*(ushort*)((byte*)unit+0x8EC-0x65C)!=0||
                *(ushort*)((byte*)unit+0x8F6-0x65C)!=0||*(uint*)((byte*)unit+0x6B8-0x65C)!=0||*(ushort*)((byte*)unit+0x708-0x65C)!=0||
                !Chains(source,destination,request.UnitId,false))
            {reason="Relocation source/destination occupancy or ordinary unmounted state is unsupported.";return false;}
            reason=string.Empty;return true;
        }
        internal static bool TryRelocate(string owner,UnitRelocationRequest request,out UnitRelocationResult result,out string reason)
        {
            result=new UnitRelocationResult(UnitRelocationStatus.Unavailable);
            if(string.IsNullOrWhiteSpace(owner)||!IsAvailable){reason="The shared relocation owner is unavailable.";return false;}
            if(policyDepth!=0||explicitFrame!=null){result=new UnitRelocationResult(UnitRelocationStatus.Rejected);reason="Recursive explicit relocation is forbidden.";return false;}
            if(!ValidateRequest(request,out UnitRelocationStatus status,out reason)){result=new UnitRelocationResult(status);return false;}
            int destination=Tile(request.TargetX,request.TargetY);
            TryLive(request.UnitId,out GameUnit* original);
            int source=Tile(original->r_CurrentTilePositionX,original->r_CurrentTilePositionY);
            var frame=new Frame{Owner=owner,Request=request,Height=((byte*)image)[0x4DDD350+destination],Failure=UnitRelocationStatus.Rejected};
            explicitFrame=frame;
            try
            {
                relocate(image+UnitRelocationNative.UnitsRva,request.UnitId,(uint)request.TargetX,(uint)request.TargetY,frame.Height);
                if(frame.Visited&&!frame.Allowed)
                {status=frame.Vetoed?UnitRelocationStatus.Vetoed:frame.Failure;result=new UnitRelocationResult(status);reason="The guarded relocation Original was vetoed or lost admission.";return false;}
                bool world=WorldMutationEvents.TryGetCurrentWorld(out long session,out long generation)&&session==request.SessionId&&generation==request.Generation;
                GameUnit* unit=null;
                bool same=world&&TryLive(request.UnitId,out unit)&&unit->r_GlobalId==request.GlobalId&&unit->r_UnitChimp==request.UnitType&&unit->r_ControllableForPlayerId==request.ControllingPlayerId;
                bool pose=same&&unit->r_CurrentTilePositionX==request.TargetX&&unit->r_CurrentTilePositionY==request.TargetY&&
                    unit->r_CurrentPositionTileId==(uint)destination&&unit->r_NextTilePositionX2==request.TargetX&&unit->r_NextTilePositionY2==request.TargetY&&
                    unit->r_CurrentWorldPositionX==request.TargetX*8+4&&unit->r_CurrentWorldPositionY==request.TargetY*8+4&&unit->r_HeightElevation==frame.Height&&
                    unit->r_MovementSubstep==8&&*(uint*)((byte*)unit+0x740-0x65C)==0&&Chains(source,destination,request.UnitId,true);
                status=pose?UnitRelocationStatus.VerifiedRelocated:UnitRelocationStatus.PostconditionFailed;
                result=new UnitRelocationResult(status,true,true);reason=pose?string.Empty:"Native relocation returned without the qualified final identity/pose/occupancy.";return pose;
            }
            catch(Exception error){result=new UnitRelocationResult(UnitRelocationStatus.PostconditionFailed,true,false);reason=SafeReason(error);return false;}
            finally{explicitFrame=null;}
        }
    }
}

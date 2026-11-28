using System;
using SHCDESE.Interop;

namespace APIShared.Units
{
    // Pure production admission/chain kernel, shared by live reads and isolated boundary tests.
    internal static class UnitRelocationValidation
    {
        internal readonly struct Node
        {internal readonly uint Global;internal readonly int Next;internal Node(uint global,int next){Global=global;Next=next;}}
        internal static bool Scope(bool tick,bool monitor,bool batch,bool ready,long session,long generation,UnitRelocationRequest request)=>
            tick&&monitor&&!batch&&ready&&session>0&&generation>0&&session==request.SessionId&&generation==request.Generation;
        internal static bool Identity(UnitRelocationRequest request,int id,uint global,eChimps type,int owner,bool alive)=>
            alive&&id==request.UnitId&&global!=0&&global==request.GlobalId&&type==request.UnitType&&owner==request.ControllingPlayerId;
        internal static bool Chain(int head,int actor,int expectedCount,Func<int,Node?> read)
        {
            int count=0,visits=0,current=head;
            while(current!=0)
            {
                if(++visits>9999||current<1||current>9999)return false;
                Node? node=read(current);if(!node.HasValue||node.Value.Global==0)return false;
                if(current==actor)count++;
                current=node.Value.Next;
            }
            return count==expectedCount;
        }
        // Two tile lists must be disjoint, not merely independently acyclic: unlinking a
        // shared suffix mutates the other tile too. A fixed 1252-byte stack bitset covers
        // the complete native ID domain and is isolated from nested synchronous calls.
        internal static unsafe bool Pair(int source,int destination,int actor,int sourceCount,int destinationCount,bool sameTile,Func<int,Node?> read)
        {
            uint* visited=stackalloc uint[313];for(int i=0;i<313;i++)visited[i]=0;
            if(!Walk(source,actor,sourceCount,read,visited))return false;
            return sameTile?source==destination&&sourceCount==destinationCount:Walk(destination,actor,destinationCount,read,visited);
        }
        private static unsafe bool Walk(int current,int actor,int expectedCount,Func<int,Node?> read,uint* visited)
        {
            int count=0,steps=0;
            while(current!=0)
            {
                if(++steps>9999||current<1||current>9999)return false;
                uint bit=1u<<(current&31);int word=current>>5;
                if((visited[word]&bit)!=0)return false;visited[word]|=bit;
                Node? node=read(current);if(!node.HasValue||node.Value.Global==0)return false;
                if(current==actor)count++;
                current=node.Value.Next;
            }
            return count==expectedCount;
        }
        internal static UnitRelocationOrigin Origin(bool own,ulong caller,ulong image)=>own?UnitRelocationOrigin.ExplicitApiRequest:
            caller==image+UnitRelocationNative.StandingReturnRva?UnitRelocationOrigin.InvalidStandingTerrainRecovery:UnitRelocationOrigin.Native;
    }
}

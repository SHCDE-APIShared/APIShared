using System;
using System.Threading;
namespace APIShared
{
    /// <summary>Changes only the additional gate access policy; never overrides native physical checks.</summary>
    public enum GateAccessDecision
    {
        /// <summary>Keep the preceding contribution.</summary>
        Unchanged,
        /// <summary>Remove only the additional gate restriction.</summary>
        Allow,
        /// <summary>Add a gate passage restriction.</summary>
        Deny
    }
    /// <summary>Immutable, verified snapshot input. Game IDs are one-based. Not a retained native pointer.</summary>
    public readonly struct GateAccessQuery
    {
        /// <summary>Creates a copied gate access query.</summary>
        public GateAccessQuery(int playerId,int buildingId,uint globalId,int owner,int capturer,bool allowed)
        { PlayerId=playerId;BuildingId=buildingId;GlobalId=globalId;Owner=owner;Capturer=capturer;Allowed=allowed; }
        /// <summary>Querying player, from 1 through 8.</summary>
        public int PlayerId { get; }
        /// <summary>One-based gatehouse game ID.</summary>
        public int BuildingId { get; }
        /// <summary>Gatehouse generation identity, checked alongside its game ID.</summary>
        public uint GlobalId { get; }
        /// <summary>Original gatehouse owner, from 1 through 8.</summary>
        public int Owner { get; }
        /// <summary>Current capturer, or zero if uncaptured.</summary>
        public int Capturer { get; }
        /// <summary>The current policy result, including earlier ordered contributions.</summary>
        public bool Allowed { get; }
    }
    /// <summary>Passive, process-lived rules. Register once; invalidate after changing synchronized rule data.
    /// Callbacks run on the snapshot publisher thread, outside registry locks, and must be pure and deterministic.
    /// No game commands, native searches or world writes. Consumers synchronize their own multiplayer settings.
    /// Order is ordinal owner GUID then registration ID; last valid explicit decision wins. Exceptions preserve
    /// the preceding decision and are reported to the publisher. Registration does not install hooks or scan gates.</summary>
    public static class GateAccessRules
    {
        private sealed class Entry { internal string Owner,Id;internal Func<GateAccessQuery,GateAccessDecision> Rule; }
        private static readonly object Sync=new object();
        private sealed class Registry
        {
            internal Registry(Entry[] entries,long version) { Entries=entries; Version=version; }
            internal readonly Entry[] Entries;
            internal readonly long Version;
        }
        private static Registry registry=new Registry(Array.Empty<Entry>(),0);
        /// <summary>Whether any process-lived contributions are registered.</summary>
        public static bool HasRegistrations => Volatile.Read(ref registry).Entries.Length!=0;
        /// <summary>Monotone publication revision; registration and invalidation change it atomically.</summary>
        public static long Version => Volatile.Read(ref registry).Version;
        /// <summary>Registers one process-lived contribution per owner GUID and registration ID.</summary>
        public static bool TryRegister(string ownerGuid,string registrationId,Func<GateAccessQuery,GateAccessDecision> rule,out string reason)
        {
            if(string.IsNullOrWhiteSpace(ownerGuid)||string.IsNullOrWhiteSpace(registrationId)||rule==null) { reason="Owner, registration ID and rule required";return false; }
            lock(Sync) {
                var state=registry; var old=state.Entries;
                foreach(var e in old)if(e.Owner==ownerGuid&&e.Id==registrationId) { reason="Registration already exists";return false; }
                var next=new Entry[old.Length+1];Array.Copy(old,next,old.Length);next[old.Length]=new Entry{Owner=ownerGuid,Id=registrationId,Rule=rule};
                Array.Sort(next,(a,b)=> {int c=StringComparer.Ordinal.Compare(a.Owner,b.Owner);return c!=0?c:StringComparer.Ordinal.Compare(a.Id,b.Id);});
                Volatile.Write(ref registry,new Registry(next,checked(state.Version+1)));reason=null;return true;
            }
        }
        /// <summary>Invalidates policy/cache publications. Only an existing registration can request this.</summary>
        public static bool Invalidate(string ownerGuid,string registrationId)
        {
            lock(Sync)
            {
                var state=registry;
                foreach(var e in state.Entries)
                    if(e.Owner==ownerGuid&&e.Id==registrationId)
                    { Volatile.Write(ref registry,new Registry(state.Entries,checked(state.Version+1)));return true; }
                return false;
            }
        }
        /// <summary>Snapshot publisher only. Unknown identities preserve the input without invoking rules.
        /// Report failures once per cause in deferred publisher diagnostics; this method never logs globally.</summary>
        public static bool Evaluate(GateAccessQuery query,Action<string,string,Exception> reportFailure=null)
        {
            bool allowed=query.Allowed;
            if(query.PlayerId<1||query.PlayerId>8||query.BuildingId<1||query.GlobalId==0||query.Owner<1||query.Owner>8||query.Capturer<0||query.Capturer>8)return allowed;
            foreach(var e in Volatile.Read(ref registry).Entries) {
                try {
                    var result=e.Rule(new GateAccessQuery(query.PlayerId,query.BuildingId,query.GlobalId,query.Owner,query.Capturer,allowed));
                    if(result==GateAccessDecision.Allow)allowed=true;
                    else if(result==GateAccessDecision.Deny)allowed=false;
                    else if(result!=GateAccessDecision.Unchanged)throw new InvalidOperationException("Unknown gate access decision");
                } catch(Exception ex) { try{reportFailure?.Invoke(e.Owner,e.Id,ex);}catch{} }
            }
            return allowed;
        }
    }
}

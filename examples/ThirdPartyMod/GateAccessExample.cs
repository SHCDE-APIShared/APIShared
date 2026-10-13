using System;
using System.Threading;
using APIShared;
namespace ThirdPartyMod.Examples
{
    // Call Register once. Publish only verified, synchronized state; no world calls in rules.
    internal static class GateAccessExample
    {
        private const string Owner = "example.independent.gate-access";
        private sealed class State
        {
            internal readonly bool Enabled;
            internal readonly int AllowedGate, RestrictedGate;
            internal readonly uint AllowedGlobal, RestrictedGlobal;
            internal State(bool enabled,int allow,uint allowGlobal,int deny,uint denyGlobal)
            { Enabled=enabled;AllowedGate=allow;AllowedGlobal=allowGlobal;RestrictedGate=deny;RestrictedGlobal=denyGlobal; }
        }
        private static State state=new State(false,0,0,0,0);
        internal static void Register()
        {
            if(!GateAccessRules.TryRegister(Owner,"access",query=>
            {
                State current=Volatile.Read(ref state);
                if(!current.Enabled)return GateAccessDecision.Unchanged;
                if(query.BuildingId==current.RestrictedGate&&query.GlobalId==current.RestrictedGlobal)return GateAccessDecision.Deny;
                if(query.BuildingId==current.AllowedGate&&query.GlobalId==current.AllowedGlobal)return GateAccessDecision.Allow;
                return GateAccessDecision.Unchanged;
            },out string reason))throw new InvalidOperationException(reason);
        }
        internal static void PublishSynchronized(bool enabled,int allow,uint allowGlobal,int deny,uint denyGlobal)
        {
            Volatile.Write(ref state,new State(enabled,allow,allowGlobal,deny,denyGlobal));
            if(!GateAccessRules.Invalidate(Owner,"access"))throw new InvalidOperationException("Register before publishing rule state");
        }
    }
}

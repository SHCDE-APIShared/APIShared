using System;
using System.Threading;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Units
{
    internal sealed unsafe class UnitRelocationPolicies
    {
        internal sealed class Policy
        {internal string Owner,Id;internal int Order,Enabled,Reported;internal int? Type;internal Func<UnitRelocationContext,bool> Cancel;}
        private readonly object sync=new object();
        private Policy[] policies=Array.Empty<Policy>();
        private readonly byte* interest;
        private readonly Action<Exception> report;
        internal UnitRelocationPolicies(IntPtr interest,Action<Exception> report){this.interest=(byte*)interest;this.report=report;}
        internal static bool ValidArguments(string owner,string id,eChimps? type,Func<UnitRelocationContext,bool> cancel)=>
            !string.IsNullOrWhiteSpace(owner)&&!string.IsNullOrWhiteSpace(id)&&cancel!=null&&(!type.HasValue||(int)type.Value>=0&&(int)type.Value<=ushort.MaxValue);
        internal bool Register(string owner,string id,eChimps? type,Func<UnitRelocationContext,bool> cancel,int order,
            out UnitRelocationPolicyRegistration handle,out string reason)
        {
            handle=null;
            if(!ValidArguments(owner,id,type,cancel))
            {reason="A stable owner/ID, veto and UInt16 type are required.";return false;}
            lock(sync)
            {
                foreach(var p in policies)if(p.Owner==owner&&p.Id==id){reason="Duplicate relocation policy owner/ID.";return false;}
                var added=new Policy{Owner=owner,Id=id,Order=order,Type=type.HasValue?(int?)type.Value:null,Cancel=cancel};
                var next=new Policy[policies.Length+1];Array.Copy(policies,next,policies.Length);next[next.Length-1]=added;
                Array.Sort(next,(a,b)=>{int n=a.Order.CompareTo(b.Order);if(n==0)n=StringComparer.Ordinal.Compare(a.Owner,b.Owner);return n!=0?n:StringComparer.Ordinal.Compare(a.Id,b.Id);});
                Volatile.Write(ref policies,next);handle=new UnitRelocationPolicyRegistration(this,added);
            }
            reason=string.Empty;return true;
        }
        internal void SetEnabled(Policy policy,bool value)
        {
            lock(sync)
            {
                if((Volatile.Read(ref policy.Enabled)!=0)==value)return;
                // Enable publishes native demand first; callbacks may conservatively enter before
                // the policy's linearization point, but no active rule can be skipped. Disable does
                // the reverse. Only an all-types policy requires rebuilding the whole bitmap.
                if(!value)Volatile.Write(ref policy.Enabled,0);
                int first=policy.Type??0,last=policy.Type??ushort.MaxValue;
                for(int type=first;type<=last;type++)
                {
                    byte enabled=0;foreach(var p in policies)if((ReferenceEquals(p,policy)?value:Volatile.Read(ref p.Enabled)!=0)&&(!p.Type.HasValue||p.Type.Value==type)){enabled=1;break;}
                    Volatile.Write(ref interest[type],enabled);
                }
                if(value)Volatile.Write(ref policy.Enabled,1);
            }
        }
        internal bool Cancel(UnitRelocationContext context)
        {
            bool cancel=false;
            foreach(var p in Volatile.Read(ref policies))
            {
                if(Volatile.Read(ref p.Enabled)==0||p.Type.HasValue&&p.Type.Value!=(int)context.UnitType)continue;
                try{cancel|=p.Cancel(context);}catch(Exception error){if(Interlocked.Exchange(ref p.Reported,1)==0){try{report?.Invoke(error);}catch{}}}
            }
            return cancel;
        }
    }
}

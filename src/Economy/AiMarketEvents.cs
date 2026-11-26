using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace APIShared.Economy
{
    /// <summary>The four independently observable native AI market operations.</summary>
    public enum AiMarketOperation
    {
        /// <summary>Selection and execution of at most one successful pending purchase.</summary>
        PurchaseDecision,
        /// <summary>AI recruitment and its native equipment-demand producers.</summary>
        Recruitment,
        /// <summary>Actual native purchase attempt.</summary>
        Buy,
        /// <summary>Actual native sale attempt.</summary>
        Sell
    }

    /// <summary>Copied native arguments. No pointer is retained beyond this synchronous operation.</summary>
    public readonly struct AiMarketContext
    {
        internal AiMarketContext(AiMarketOperation operation, IntPtr root, int player, int good, int amount)
        { InvocationId=Interlocked.Increment(ref nextInvocation); Operation=operation; ProfileRoot=root; PlayerId=player; GoodIndex=good; SignedAmount=amount; }
        private static long nextInvocation;
        /// <summary>Process-local observation identity, shared by this callback pair. Never synchronized or persisted.</summary>
        public long InvocationId { get; }
        /// <summary>Native operation; recruitment also generates equipment purchase wishes.</summary>
        public AiMarketOperation Operation { get; }
        /// <summary>Opaque native profile root; only valid during this callback pair.</summary>
        public IntPtr ProfileRoot { get; }
        /// <summary>Unmodified native player argument; consumers validate its domain before access.</summary>
        public int PlayerId { get; }
        /// <summary>Unmodified goods index for Buy/Sell, otherwise -1.</summary>
        public int GoodIndex { get; }
        /// <summary>Unmodified signed quantity for Buy/Sell, otherwise zero.</summary>
        public int SignedAmount { get; }
    }
    /// <summary>Returns private observer state represented by a token, including zero.</summary>
    public delegate long AiMarketBegin(AiMarketContext context);
    /// <summary>Matches one successful Begin. Completed means the native function returned, not that a trade succeeded.</summary>
    /// <param name="context">The same copied arguments as Begin.</param>
    /// <param name="token">That observer's private Begin return value.</param>
    /// <param name="completed">False when the original threw; the exception remains unmodified.</param>
    /// <param name="rawResult">Unmodified native Buy result; zero for void operations or an incomplete call.</param>
    public delegate void AiMarketEnd(AiMarketContext context,long token,bool completed,int rawResult);

    /// <summary>A process-lifetime registration. Disabling affects future calls, not outstanding callback pairs.</summary>
    public sealed class AiMarketRegistration
    {
        internal readonly string Owner,Id;
        internal readonly int Order;
        internal readonly AiMarketBegin Begin;
        internal readonly AiMarketEnd End;
        internal readonly Action<Exception> Failure;
        internal bool Enabled;
        internal AiMarketRegistration(string owner,string id,int order,AiMarketBegin begin,AiMarketEnd end,Action<Exception> failure)
        {Owner=owner;Id=id;Order=order;Begin=begin;End=end;Failure=failure;}
        /// <summary>Changes logical demand. The consumer owns synchronized mode/session activation.</summary>
        public void SetEnabled(bool enabled) => AiMarketEvents.SetEnabled(this,enabled);
    }

    /// <summary>Shared paired observations at AI purchase selection, recruitment, purchase and sale entries.</summary>
    /// <remarks>Register once with a stable foreign owner GUID and owner-local ID; initially disabled, no replay.
    /// Begin and End run synchronously on the native caller thread, ordered by order/GUID/ID, outside registry locks.
    /// Each call snapshots registrations; disabling or adding observers during Begin cannot unpair an outstanding End.
    /// Reentry has independent tokens and no shared depth capacity. The ordinary dispatch path allocates no managed memory.
    /// Observer exceptions are isolated; the optional failure callback runs synchronously and is itself isolated.
    /// Critical consumers must use it to stop an unsafe world. Diagnostic failure never suppresses other observers.
    /// Vanilla runs exactly once, with unchanged arguments and result; callbacks cannot cancel or replace it.
    /// No callbacks imply trade success. Sell has no native result. Consumers own validation, gameplay policy,
    /// multiplayer agreement and mission admission; IsAvailable only describes technical hook readiness.</remarks>
    public static class AiMarketEvents
    {
        private static readonly object Sync=new object();
        private static readonly List<AiMarketRegistration> registrations=new List<AiMarketRegistration>();
        private static AiMarketRegistration[] active=Array.Empty<AiMarketRegistration>();
        /// <summary>Whether all four permanently installed native entries passed their contracts.</summary>
        public static bool IsAvailable => AiMarketNativeRuntime.IsAvailable;
        /// <summary>Technical initialization failure or an empty string when ready.</summary>
        public static string Diagnostic => AiMarketNativeRuntime.Diagnostic;
        /// <summary>Checks current entry jumps, slots and padding. Call at startup/admission, never on every tick.</summary>
        /// <remarks>A foreign overwrite reports an error and returns false; this never restores executable bytes.</remarks>
        public static bool CheckIntegrity() => AiMarketNativeRuntime.CheckIntegrity();
        /// <summary>Registers a disabled callback pair. At most 32 registrations are accepted process-wide.</summary>
        public static bool TryRegister(string ownerGuid,string registrationId,AiMarketBegin begin,AiMarketEnd end,
            out AiMarketRegistration registration,out string diagnostic,int order=0,Action<Exception> onFailure=null)
        {
            registration=null;
            if(string.IsNullOrWhiteSpace(ownerGuid)||string.IsNullOrWhiteSpace(registrationId)||begin==null||end==null)
            {diagnostic="A nonempty owner, registration ID and callback pair are required.";return false;}
            lock(Sync)
            {
                if(registrations.Count>=32 || registrations.Any(r=>r.Owner==ownerGuid&&r.Id==registrationId))
                {diagnostic="AI market registration capacity or unique identity contract refused.";return false;}
                registration=new AiMarketRegistration(ownerGuid,registrationId,order,begin,end,onFailure);
                registrations.Add(registration);diagnostic=string.Empty;return true;
            }
        }
        internal static void SetEnabled(AiMarketRegistration registration,bool enabled)
        {
            lock(Sync)
            {
                if(registration.Enabled==enabled)return;
                registration.Enabled=enabled;
                Volatile.Write(ref active,registrations.Where(r=>r.Enabled).OrderBy(r=>r.Order)
                    .ThenBy(r=>r.Owner,StringComparer.Ordinal).ThenBy(r=>r.Id,StringComparer.Ordinal).ToArray());
            }
        }
        internal static AiMarketRegistration[] Snapshot() => Volatile.Read(ref active);
        internal static unsafe uint Begin(AiMarketRegistration[] observers,AiMarketContext context,long* tokens)
        {
            uint entered=0;
            for(int i=0;i<observers.Length;i++)
                try {tokens[i]=observers[i].Begin(context);entered|=1u<<i;}
                catch(Exception error){Failed(observers[i],error);}
            return entered;
        }
        internal static unsafe void End(AiMarketRegistration[] observers,AiMarketContext context,long* tokens,uint entered,bool completed,int result)
        {
            for(int i=0;i<observers.Length;i++)
                if((entered&(1u<<i))!=0)
                    try {observers[i].End(context,tokens[i],completed,result);}
                    catch(Exception error){Failed(observers[i],error);}
        }
        private static void Failed(AiMarketRegistration observer,Exception error)
        {try{observer.Failure?.Invoke(error);}catch{/* Never replace Vanilla or another observer's outcome. */}}
    }
}

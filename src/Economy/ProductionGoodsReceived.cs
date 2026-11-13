using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using APIShared.Internal;
using APIShared.GameModes;
using BepInEx.Logging;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    /// <summary>An immutable, proven credit from one completed production delivery to player storage.</summary>
    /// <remarks>IDs are one-based. Amount is the actual positive credit, already including output modifiers.
    /// ProductionRole is Quarry Grunt for both stone transport paths; TransportUnit identifies the real carrier.
    /// This historical receipt does not keep either native object alive.</remarks>
    public sealed class ProductionGoodsReceipt
    {
        internal ProductionGoodsReceipt(ProductionGoodsSource source, ProductionGoodsStock stock, eGoods good, int amount)
        { PlayerId=stock.PlayerId; Good=good; Amount=amount; StorageBuildingId=stock.BuildingId;
          StorageBuildingGlobalId=stock.GlobalId; TransportUnitId=source.UnitId;
          TransportUnitGlobalId=source.GlobalId; ProductionRole=source.Role; }
        /// <summary>Owner of the storage receiving this credit.</summary>
        public int PlayerId { get; }
        /// <summary>Effective completed good after recipe and Extender Pre changes.</summary>
        public eGoods Good { get; }
        /// <summary>Confirmed positive quantity added by the original native primitive.</summary>
        public int Amount { get; }
        /// <summary>Receiving storage's one-based building ID.</summary>
        public int StorageBuildingId { get; }
        /// <summary>Receiving storage's identity at mutation.</summary>
        public uint StorageBuildingGlobalId { get; }
        /// <summary>Original physical carrier's one-based unit ID.</summary>
        public int TransportUnitId { get; }
        /// <summary>Original carrier's identity captured before foreign callbacks.</summary>
        public uint TransportUnitGlobalId { get; }
        /// <summary>Economic worker role, distinct from an Ox carrier.</summary>
        public eChimps ProductionRole { get; }
    }

    /// <summary>A process-held observer. Logical demand may change; registration and native code remain rooted.</summary>
    public sealed class ProductionGoodsReceivedRegistration
    {
        internal readonly string Owner, Id;
        internal readonly int Order;
        internal readonly Action<ProductionGoodsReceipt> Callback;
        internal readonly Action<long,GameModeSnapshot> OnMissionAdmitted;
        internal readonly Action<long> OnMissionEnded;
        internal long AdmittedSession;
        internal bool Enabled;
        internal bool Required;
        internal GameplayModActivationProfile? Activation;
        internal ProductionGoodsReceivedRegistration(string owner,string id,int order,Action<ProductionGoodsReceipt> callback,
            Action<long,GameModeSnapshot> onMissionAdmitted,Action<long> onMissionEnded)
        { Owner=owner;Id=id;Order=order;Callback=callback;OnMissionAdmitted=onMissionAdmitted;OnMissionEnded=onMissionEnded; }
        /// <summary>Changes observer demand. Consumers own synchronized mission/mode activation; this is not a network protocol.</summary>
        public void SetEnabled(bool enabled) => ProductionGoodsReceived.SetEnabled(this,enabled);
        /// <summary>Declares that the next mission requires complete receipt support in the permitted modes.</summary>
        /// <remarks>Set from synchronized desired settings before mission initialization, independently of live observer demand.
        /// The shared production world gate enforces this for local saves and the editor as well as lobby starts.
        /// Null activation permits every recognized mode. Changing this does not enable callback delivery.</remarks>
        public void SetRequiredForMission(bool required,GameplayModActivationProfile? activation=null) =>
            ProductionGoodsReceived.SetRequired(this,required,activation);
    }

    /// <summary>Post-only observation of genuine production received in final stockpile, granary or armoury storage.</summary>
    /// <remarks>Callbacks run synchronously on the simulation caller, ordered by priority/GUID/ID, without registry locks.
    /// Exceptions are isolated. There is no replay, cancellation or mutation of receipt arguments. Start goods,
    /// purchases, gifts, refunds, ingredient movement, local buffers and failed additions are excluded.
    /// Never infer this event from a generic stockpile event or a stale current-unit global.</remarks>
    public static class ProductionGoodsReceived
    {
        private static readonly object Sync=new object();
        private static readonly List<ProductionGoodsReceivedRegistration> Registrations=new List<ProductionGoodsReceivedRegistration>();
        private static ProductionGoodsReceivedRegistration[] active=Array.Empty<ProductionGoodsReceivedRegistration>();
        private static readonly IntPtr demand=AllocateDemand();
        private static ManualLogSource logger;
        private static int observerFailures;
        private static IntPtr AllocateDemand() { var p=Marshal.AllocHGlobal(4);Marshal.WriteInt32(p,0);return p; }
        internal static ulong DemandAddress => unchecked((ulong)demand.ToInt64());
        internal static ulong DeliveryCallbackAddress => ProductionGoodsReceivedRuntime.CallbackAddress;
        /// <summary>Whether managed capture and every required final-delivery source are ready.</summary>
        public static bool IsAvailable => ProductionGoodsReceivedRuntime.IsAvailable && ProductionGoodsReceivedNativeRuntime.IsAvailable;
        /// <summary>Technical unavailability detail; consumers should present a localized blocking message.</summary>
        public static string Diagnostic => !ProductionGoodsReceivedRuntime.IsAvailable ? ProductionGoodsReceivedRuntime.Diagnostic : ProductionGoodsReceivedNativeRuntime.Diagnostic;
        /// <summary>Registers an initially disabled observer with a unique owner-local ID. Registration does not require native readiness.</summary>
        /// <remarks>Optional mission callbacks run synchronously under the production engine gate, once per admitted session,
        /// before its first advancing simulation run and on retirement. Snapshot authoritative prepared settings there;
        /// managed MissionStarted is too late to guarantee the first deliveries. Never call UI synchronously here.
        /// A throwing admission callback blocks the world when that registration requires support in the actual mode.</remarks>
        public static bool TryRegister(string ownerGuid,string registrationId,Action<ProductionGoodsReceipt> observer,
            out ProductionGoodsReceivedRegistration registration,out string diagnostic,int order=0,
            Action<long,GameModeSnapshot> onMissionAdmitted=null,Action<long> onMissionEnded=null)
        {
            registration=null;
            if(string.IsNullOrWhiteSpace(ownerGuid)||string.IsNullOrWhiteSpace(registrationId)||observer==null)
            {diagnostic="A nonempty owner, registration ID and callback are required.";return false;}
            lock(Sync)
            {
                if(Registrations.Any(r=>r.Owner==ownerGuid&&r.Id==registrationId))
                {diagnostic="The production receipt observer identity is already registered.";return false;}
                registration=new ProductionGoodsReceivedRegistration(ownerGuid,registrationId,order,observer,onMissionAdmitted,onMissionEnded);
                Registrations.Add(registration);diagnostic=string.Empty;return true;
            }
        }
        internal static void SetEnabled(ProductionGoodsReceivedRegistration registration,bool enabled)
        {
            lock(Sync)
            {
                if(registration.Enabled==enabled)return;
                registration.Enabled=enabled;
                var next=Registrations.Where(r=>r.Enabled).OrderBy(r=>r.Order).ThenBy(r=>r.Owner,StringComparer.Ordinal)
                    .ThenBy(r=>r.Id,StringComparer.Ordinal).ToArray();
                Volatile.Write(ref active,next);Marshal.WriteInt32(demand,next.Length==0?0:1);
            }
        }
        internal static bool HasDemand => Volatile.Read(ref active).Length!=0;
        internal static void SetRequired(ProductionGoodsReceivedRegistration registration,bool required,GameplayModActivationProfile? activation)
        {lock(Sync){registration.Required=required;registration.Activation=activation;}}
        internal static bool ValidateAdmission(GameModeSnapshot mode,out string diagnostic)
        {
            diagnostic=null;
            if(IsAvailable)return true;
            lock(Sync)
                foreach(var registration in Registrations)
                    if(registration.Required&&(!registration.Activation.HasValue||
                        GameplayModModePolicy.IsAllowed(registration.Activation.Value,mode,out _)))
                    {diagnostic="Production receipts required by "+registration.Owner+" are unavailable: "+Diagnostic;return false;}
            return true;
        }
        private static ProductionGoodsReceivedRegistration[] OrderedRegistrations()
        {lock(Sync)return Registrations.OrderBy(r=>r.Order).ThenBy(r=>r.Owner,StringComparer.Ordinal).ThenBy(r=>r.Id,StringComparer.Ordinal).ToArray();}
        internal static bool Admit(long session,GameModeSnapshot mode,out string diagnostic)
        {
            diagnostic=null;
            if(!ValidateAdmission(mode,out diagnostic))return false;
            foreach(var registration in OrderedRegistrations())
            {
                if(registration.AdmittedSession==session)continue;
                try
                {
                    registration.OnMissionAdmitted?.Invoke(session,mode);
                    registration.AdmittedSession=session;
                }
                catch(Exception error)
                {
                    registration.SetEnabled(false);
                    bool required;GameplayModActivationProfile? activation;
                    lock(Sync){required=registration.Required;activation=registration.Activation;}
                    if(required&&(!activation.HasValue||GameplayModModePolicy.IsAllowed(activation.Value,mode,out _)))
                    {diagnostic="Production receipt admission failed for "+registration.Owner+": "+error.Message;return false;}
                    ReportObserverFailure(error);
                }
            }
            return true;
        }
        internal static void EndMission(long session)
        {
            foreach(var registration in OrderedRegistrations())
            {
                if(registration.AdmittedSession!=session)continue;
                registration.AdmittedSession=0;
                try{registration.OnMissionEnded?.Invoke(session);}catch(Exception error){ReportObserverFailure(error);}
            }
        }
        internal static void Publish(ProductionGoodsReceipt receipt)
        {
            foreach(var observer in Volatile.Read(ref active))
                try {observer.Callback(receipt);}catch(Exception error)
                {ReportObserverFailure(error);}
        }
        private static void ReportObserverFailure(Exception error)
        {try{if(FailureThrottle.ShouldReport(ref observerFailures))NativeApiLog.Error(logger,"Production receipt observer failed: "+error);}catch{/* Observers cannot break native admission/delivery. */}}
        /// <summary>Gets the permanent native delivery adapter for an already audited external final-production call site.</summary>
        /// <remarks>LibraryLoaded only. ABI is Int64(buildingManager, Int32 buildingId, Int32 globalId, eGoods,
        /// Int32 amount, Int32 capacity, Int32 add), matching the SE stockpile primitive. Call only at a genuine
        /// final production-delivery site with the real current carrier. This is not an arbitrary goods-add API.
        /// Inactive demand forwards entirely natively. The entry remains valid for process lifetime.</remarks>
        public static bool TryGetNativeDeliveryAdapter(out ulong address,out string diagnostic)
        {
            address=ProductionGoodsReceivedNativeRuntime.AdapterAddress;
            diagnostic=address==0?"The native production delivery adapter has not initialized.":string.Empty;
            return address!=0;
        }
        /// <summary>Confirms ImprovedHunters' permanently published Tanner delivery source uses the shared adapter in both routes.</summary>
        /// <remarks>Integration handshake, not a general availability override. Call only after all owned Tanner stubs were verified.</remarks>
        public static void ConfirmExternalTannerAdapter() => ProductionGoodsReceivedNativeRuntime.ConfirmExternalTannerAdapter();
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {logger=log;ProductionGoodsReceivedRuntime.Initialize(context,hash,log);ProductionGoodsReceivedNativeRuntime.Initialize(context,hash,log);}
    }
}

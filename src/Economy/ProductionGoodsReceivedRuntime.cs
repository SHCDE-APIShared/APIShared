using System;
using System.Linq;
using System.Runtime.InteropServices;
using APIShared.Internal;
using BepInEx.Logging;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.API;
using SHCDESE.API.LowLevel;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal static unsafe class ProductionGoodsReceivedRuntime
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate long Delivery(IntPtr manager,int building,int global,eGoods good,int amount,int capacity,int add);
        private static readonly Delivery callback=Deliver;
        private static readonly ulong callbackAddress=unchecked((ulong)Marshal.GetFunctionPointerForDelegate(callback).ToInt64());
        private static ProductionGoodsReceivedSeBridge bridge;
        private static ProductionGoodsEntry entry;
        private static Patching.SafeIlHook hook;
        private static ManualLogSource logger;
        private static bool attempted,ready;
        private static int failures;
        internal static ulong CallbackAddress=>callbackAddress;
        internal static bool IsAvailable=>ready;
        internal static string Diagnostic {get;private set;}="Production receipt capture has not initialized.";
        internal static void Initialize(CrusaderLibraryLoadContext context,string hash,ManualLogSource log)
        {
            if(attempted)return;attempted=true;logger=log;
            try
            {
                if(!string.Equals(hash,WorkerProductionBonusNativeDefinition.ReferenceHash,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Production receipts require the audited native image.");
                var method=ProductionGoodsReceivedSeContract.Resolve();ProductionGoodsReceivedSeContract.Validate(method);
                ProductionGoodsReceivedSeContract.ValidateNativeOwner(method,context.ModuleHandle+0xB6100);
                entry=(ProductionGoodsEntry)Delegate.CreateDelegate(typeof(ProductionGoodsEntry),method);
                var candidate=new ProductionGoodsReceivedSeBridge(entry,
                    ProductionGoodsReceivedSeContract.CreateForwarder(ProductionGoodsReceivedSeContract.OriginalDelegateType(method)),
                    ReadStock,ProductionGoodsReceived.Publish,Report,ValidateSource);
                using(var definition=new DynamicMethodDefinition(method))using(var il=new ILContext(definition.Definition))
                {il.ReferenceBag=RuntimeILReferenceBag.Instance;il.Invoke(code=>ProductionGoodsReceivedSeContract.Apply(code,candidate.Invoke,candidate.EnterWrapper,candidate.LeaveWrapper));definition.Generate();}
                bridge=candidate;
                if(!Patching.SafeIlHook.TryInstall(APISharedPlugin.PluginGuid,"APIShared.ProductionGoodsReceived.SEOriginal",method,
                    code=>ProductionGoodsReceivedSeContract.Apply(code,candidate.Invoke,candidate.EnterWrapper,candidate.LeaveWrapper),log,out var installed,out string reason))
                    throw new InvalidOperationException(reason);
                hook=installed;ready=true;Diagnostic=string.Empty;
                NativeApiLog.Info(log,"Production receipts prepared around the existing SE stockpile Original invocation.");
            }
            catch(Exception error){Diagnostic=error.Message;Report(error);throw;}
        }
        private static long Deliver(IntPtr nativeManager,int building,int global,eGoods good,int amount,int capacity,int add)
        {
            // Only audited final-call owners enter this function; ordinary stockpile calls
            // never reach it. Capture physical identity before any SE subscriber runs.
            try
            {
                var manager=new NativePointer<GameBuildingManager>((GameBuildingManager*)nativeManager.ToPointer());
                if(!ready||!ProductionGoodsReceived.HasDemand||!ProductionGoodsReceived.IsAvailable)
                    return entry(manager,building,global,good,amount,capacity,add);
                ProductionGoodsSource source=default;bool valid=false;
                try{valid=TryReadSource(out source);}catch(Exception error){Report(error);}
                return valid?bridge.Execute(source,manager,building,global,good,amount,capacity,add):
                    entry(manager,building,global,good,amount,capacity,add);
            }
            catch(Exception error){Report(error);return 0;}
        }
        private static bool TryReadSource(out ProductionGoodsSource source)
        {
            source=default;int id=GameUnitManagerAPI.Instance.GetCurrentContextUnitId();
            if(!UnitAccess.TryGetById(GameUnitManagerAPI.Instance,id,out GameUnit* unit,out _)||!UnitAccess.IsReallyAlive(unit)||unit->r_GlobalId==0)return false;
            eChimps role=unit->r_UnitChimp==eChimps.CHIMP_TYPE_QUARRY_OX?eChimps.CHIMP_TYPE_QUARRY_GRUNT:unit->r_UnitChimp;
            var supported=WorkerProductionModifiers.SupportedWorkers;
            for(int i=0;i<supported.Count;i++)if(supported[i]==role)
            {source=new ProductionGoodsSource(id,unit->r_GlobalId,role);return true;}
            return false;
        }
        private static bool ReadStock(NativePointer<GameBuildingManager> manager,int id,int global,eGoods good,out ProductionGoodsStock stock)
        {
            stock=default;
            if(manager.Pointer==null||manager.Pointer!=GameBuildingManagerAPI.Instance.GetBuildingManager().Pointer||
                !BuildingAccess.TryGetById(id,out GameBuilding* building,out _)||building->r_AliveState!=AliveState.IsAlive||
                building->r_GlobalId!=unchecked((uint)global)||building->r_PlayerIdOwner<1||building->r_PlayerIdOwner>8)return false;
            if(!TryGetFinalStorageType(good,out eStructs expected)||building->r_BuildingType!=expected)return false;
            int amount=((int*)&building->r_NullAmount)[(int)good];
            if(amount<0)return false;
            stock=new ProductionGoodsStock(id,building->r_GlobalId,building->r_PlayerIdOwner,amount);return true;
        }
        internal static bool TryGetFinalStorageType(eGoods good,out eStructs expected)
        {
            expected=default;
            // Explicit final-output domain: native mapping alone also accepts raw logs,
            // which are NOT usable stockpile wood and caused the original 48-log defect.
            switch(good)
            {
                case eGoods.STORED_WOOD_PLANKS:case eGoods.STORED_RAW_HOPS:case eGoods.STORED_STONE_BLOCKS:
                case eGoods.STORED_IRON_INGOTS:case eGoods.STORED_PITCH_RAW:case eGoods.STORED_RAW_WHEAT:
                case eGoods.STORED_FOOD_ALE:case eGoods.STORED_FLOUR:expected=eStructs.STRUCT_GOODS_YARD;break;
                case eGoods.STORED_FOOD_BREAD:case eGoods.STORED_FOOD_CHEESE:case eGoods.STORED_FOOD_MEAT:
                case eGoods.STORED_FOOD_FRUIT:expected=eStructs.STRUCT_GRANARY;break;
                case eGoods.STORED_BOWS:case eGoods.STORED_CROSSBOWS:case eGoods.STORED_SPEARS:case eGoods.STORED_PIKES:
                case eGoods.STORED_MACES:case eGoods.STORED_SWORDS:case eGoods.STORED_LEATHER_ARMOUR:
                case eGoods.STORED_METAL_ARMOUR:expected=eStructs.STRUCT_ARMOURY;break;
                default:return false;
            }
            return true;
        }
        private static bool ValidateSource(ProductionGoodsSource source)
        {
            if(!UnitAccess.TryGetById(GameUnitManagerAPI.Instance,source.UnitId,out GameUnit* unit,out _)||
                !UnitAccess.IsReallyAlive(unit)||unit->r_GlobalId!=source.GlobalId)return false;
            eChimps role=unit->r_UnitChimp==eChimps.CHIMP_TYPE_QUARRY_OX?eChimps.CHIMP_TYPE_QUARRY_GRUNT:unit->r_UnitChimp;
            return role==source.Role;
        }
        private static void Report(Exception error)
        {try{if(FailureThrottle.ShouldReport(ref failures))NativeApiLog.Error(logger,"Production receipt capture failed; no retry: "+error);}catch{/* Never unwind across a native callback. */}}
    }
}

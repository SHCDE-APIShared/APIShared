using System;
using RedBird.Core.Memory;
using SHCDESE.Interop;

namespace APIShared.Economy
{
    internal readonly struct ProductionGoodsSource
    {
        internal readonly int UnitId;
        internal readonly uint GlobalId;
        internal readonly eChimps Role;
        internal ProductionGoodsSource(int id,uint globalId,eChimps role){UnitId=id;GlobalId=globalId;Role=role;}
    }
    internal readonly struct ProductionGoodsStock
    {
        internal readonly int BuildingId,PlayerId,Amount;
        internal readonly uint GlobalId;
        internal ProductionGoodsStock(int id,uint globalId,int player,int amount)
        {BuildingId=id;GlobalId=globalId;PlayerId=player;Amount=amount;}
    }
    internal delegate long ProductionGoodsEntry(NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,int amount,int capacity,int add);
    internal delegate long ProductionGoodsForward(Delegate original,NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,int amount,int capacity,int add);
    internal delegate bool ProductionGoodsRead(NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,out ProductionGoodsStock stock);

    // The source frame is created only by audited final-call owners. Wrapper entry depth claims it
    // before Pre, so a nested add from any foreign callback cannot steal this provenance.
    internal sealed class ProductionGoodsReceivedSeBridge
    {
        private readonly ProductionGoodsEntry entry;
        private readonly ProductionGoodsForward forward;
        private readonly ProductionGoodsRead read;
        private readonly Action<ProductionGoodsReceipt> publish;
        private readonly Action<Exception> report;
        private readonly Func<ProductionGoodsSource,bool> validateSource;
        [ThreadStatic] private static Frame current;
        [ThreadStatic] private static int depth;
        private sealed class Frame
        {
            internal ProductionGoodsReceivedSeBridge Owner;
            internal ProductionGoodsSource Source;
            internal int Depth,Invocations;
            internal ProductionGoodsReceipt Receipt;
            internal long Result;
        }
        internal ProductionGoodsReceivedSeBridge(ProductionGoodsEntry entry,ProductionGoodsForward forward,ProductionGoodsRead read,
            Action<ProductionGoodsReceipt> publish,Action<Exception> report,Func<ProductionGoodsSource,bool> validateSource=null)
        {this.entry=entry;this.forward=forward;this.read=read;this.publish=publish;this.report=report;this.validateSource=validateSource;}
        internal void EnterWrapper()=>depth++;
        internal void LeaveWrapper()=>depth--;
        internal long Execute(ProductionGoodsSource source,NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,int amount,int capacity,int add)
        {
            var frame=new Frame{Owner=this,Source=source,Depth=depth+1};var previous=current;current=frame;
            try {return entry(manager,building,global,good,amount,capacity,add);}
            catch(Exception error){Report(error);return frame.Result;}
            finally
            {
                // Clear provenance before consumer callbacks, including on an SE Post exception.
                current=previous;
                if(frame.Receipt!=null)try{publish(frame.Receipt);}catch(Exception error){Report(error);}
            }
        }
        internal long Invoke(Delegate original,NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,int amount,int capacity,int add)
        {
            Frame frame=current;
            if(frame==null||frame.Owner!=this||frame.Depth!=depth)
                return forward(original,manager,building,global,good,amount,capacity,add);
            frame.Invocations++;
            if(frame.Invocations!=1)frame.Receipt=null;
            // Readback failures never suppress the original mutation.
            ProductionGoodsStock before=default;bool captured=false;
            try{captured=frame.Invocations==1&&add==1&&amount>0&&capacity>0&&
                (validateSource==null||validateSource(frame.Source))&&read(manager,building,global,good,out before);}
            catch(Exception error){Report(error);}
            long result=forward(original,manager,building,global,good,amount,capacity,add);
            frame.Result=result;
            try
            {
                if(captured&&result==1&&read(manager,building,global,good,out var after)&&
                    before.BuildingId==after.BuildingId&&before.GlobalId==after.GlobalId&&before.PlayerId==after.PlayerId&&
                    (long)after.Amount-before.Amount==amount)
                    frame.Receipt=new ProductionGoodsReceipt(frame.Source,after,good,amount);
            }
            catch(Exception error){Report(error);}
            return result;
        }
        private void Report(Exception error){try{report(error);}catch{/* Diagnosis must not cross the native ABI. */}}
    }
}

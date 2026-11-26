using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Economy;
using APIShared.GameModes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using RedBird.Core.Memory;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.Buildings;
using SHCDESE.Interop;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class ProductionGoodsReceivedSeTests
    {
        private static Fixture active;
        private static void Raise(AddGoodToGoodsyardEventArgs args)
        { if(args.Phase==EventHookPhase.Pre)active.Pre?.Invoke(args);else active.Post?.Invoke(args); }

        // Execute the installed SE body, including its real mutable Pre and stale Post tuple.
        // Only publisher and native-delegate boundaries are replaced; the production transform
        // and receipt bridge remain the actual implementation under test.
        private sealed class Fixture : IDisposable
        {
            internal readonly ProductionGoodsReceivedSeBridge Bridge;
            internal readonly List<ProductionGoodsReceipt> Receipts=new List<ProductionGoodsReceipt>();
            internal readonly List<Exception> Errors=new List<Exception>();
            internal readonly Dictionary<string,int> Stock=new Dictionary<string,int>();
            internal Action<AddGoodToGoodsyardEventArgs> Pre,Post;
            internal Action BeforeNative,AfterNative;
            internal bool ThrowBeforeRead,ThrowAfterRead,ReadUnavailable,SkipNativeMutation;
            internal int Calls,Reads,ObservedGlobal=55,ObservedPlayer=1;
            internal long Result=1;
            internal ProductionGoodsEntry Run;
            internal Action<ProductionGoodsReceipt> Observer;
            internal Func<ProductionGoodsSource,bool> SourceValidation=source=>true;
            private ILContext copiedContext;
            internal Fixture()
            {
                Assert.IsNull(active);active=this;
                Bridge=new ProductionGoodsReceivedSeBridge((m,b,g,k,a,c,d)=>Run(m,b,g,k,a,c,d),Forward,Read,
                    receipt=>{Receipts.Add(receipt);Observer?.Invoke(receipt);},error=>Errors.Add(error),source=>SourceValidation(source));
                Run=Compile();
            }
            private ProductionGoodsEntry Compile()
            {
                using(var definition=new DynamicMethodDefinition(ProductionGoodsReceivedSeContract.Resolve()))
                {
                    // EmitDelegate references belong to ILContext; retain it for the same
                    // lifetime as the generated body, matching a real permanent ILHook.
                    var il=copiedContext=new ILContext(definition.Definition);
                    il.ReferenceBag=RuntimeILReferenceBag.Instance;
                    ProductionGoodsReceivedSeContract.Apply(il,Bridge.Invoke,Bridge.EnterWrapper,Bridge.LeaveWrapper);
                    int handles=0,publishers=0,raises=0;
                    foreach(var instruction in il.Body.Instructions)
                    {
                        if(instruction.Operand is FieldReference field&&field.Name=="c_game_add_good_to_goodsyard_hook")
                        {
                            Assert.AreEqual(OpCodes.Ldsfld,instruction.OpCode);
                            Instruction getter=instruction.Next;
                            Assert.AreEqual("get_Original",((MethodReference)getter.Operand).Name);
                            instruction.OpCode=OpCodes.Ldnull;instruction.Operand=null;
                            getter.OpCode=OpCodes.Nop;getter.Operand=null;handles++;
                        }
                        else if(instruction.Operand is FieldReference publisher&&publisher.Name=="OnGoodsyardAddGood")
                        {instruction.OpCode=OpCodes.Nop;instruction.Operand=null;publishers++;}
                        else if(instruction.Operand is MethodReference method&&method.Name=="Raise"&&
                            method.DeclaringType is GenericInstanceType generic&&generic.GenericArguments.Count==1&&
                            generic.GenericArguments[0].FullName==typeof(AddGoodToGoodsyardEventArgs).FullName)
                        {
                            instruction.OpCode=OpCodes.Call;
                            instruction.Operand=il.Import(typeof(ProductionGoodsReceivedSeTests).GetMethod(nameof(Raise),BindingFlags.NonPublic|BindingFlags.Static));
                            raises++;
                        }
                    }
                    Assert.AreEqual(1,handles);Assert.AreEqual(2,publishers);Assert.AreEqual(2,raises);
                    return (ProductionGoodsEntry)definition.Generate().CreateDelegate(typeof(ProductionGoodsEntry));
                }
            }
            private static string Key(int building,eGoods good)=>building+":"+(int)good;
            internal int Amount(int building,eGoods good)=>Stock.TryGetValue(Key(building,good),out int amount)?amount:0;
            private bool Read(NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,out ProductionGoodsStock stock)
            {
                Reads++;
                if((ThrowBeforeRead&&Calls==0)||(ThrowAfterRead&&Calls>0))throw new InvalidOperationException("Read failure");
                stock=new ProductionGoodsStock(building,(uint)ObservedGlobal,ObservedPlayer,Amount(building,good));
                return !ReadUnavailable;
            }
            private long Forward(Delegate original,NativePointer<GameBuildingManager> manager,int building,int global,eGoods good,int amount,int capacity,int add)
            {
                Calls++;BeforeNative?.Invoke();
                if(!SkipNativeMutation&&Result==1&&add==1&&capacity>0&&amount>0)Stock[Key(building,good)]=Amount(building,good)+amount;
                AfterNative?.Invoke();return Result;
            }
            internal long Deliver(int unit=7,int building=2,eGoods good=eGoods.STORED_WOOD_PLANKS,int amount=1,int capacity=48,int add=1)=>
                Bridge.Execute(new ProductionGoodsSource(unit,(uint)(700+unit),eChimps.CHIMP_TYPE_WOODCUTTER),default,building,55,good,amount,capacity,add);
            internal long Ordinary(int building=2,eGoods good=eGoods.STORED_WOOD_PLANKS,int amount=1,int capacity=48,int add=1)=>
                Run(default,building,55,good,amount,capacity,add);
            public void Dispose(){Run=null;copiedContext?.Dispose();active=null;}
        }

        [TestMethod]
        public void EffectivePreTupleIsCreditedInsteadOfLegacyPostArguments()
        {
            using(var f=new Fixture())
            {
                f.Pre=args=>{args.BuildingId=3;args.Good=eGoods.STORED_FOOD_MEAT;args.AddAmount=4;args.Capacity=250;};
                f.Post=args=>{Assert.AreEqual(2,args.BuildingId);Assert.AreEqual(eGoods.STORED_WOOD_PLANKS,args.Good);Assert.AreEqual(1,args.AddAmount);};
                Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(1,f.Calls);Assert.AreEqual(1,f.Receipts.Count);
                var receipt=f.Receipts[0];
                Assert.AreEqual(3,receipt.StorageBuildingId);Assert.AreEqual(eGoods.STORED_FOOD_MEAT,receipt.Good);
                Assert.AreEqual(4,receipt.Amount);Assert.AreEqual(7,receipt.TransportUnitId);Assert.AreEqual(707U,receipt.TransportUnitGlobalId);
            }
        }

        [TestMethod]
        public void OrdinaryPreOriginalAndPostNestedAddsCannotInheritProductionSource()
        {
            using(var f=new Fixture())
            {
                bool pre=false,native=false,post=false;
                f.Pre=args=>{if(pre)return;pre=true;Assert.AreEqual(1L,f.Ordinary(8));};
                f.BeforeNative=()=>{if(native)return;native=true;Assert.AreEqual(1L,f.Ordinary(9));};
                f.Post=args=>{if(post)return;post=true;Assert.AreEqual(1L,f.Ordinary(10));};
                Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(4,f.Calls);Assert.AreEqual(1,f.Receipts.Count);
                Assert.AreEqual(2,f.Receipts[0].StorageBuildingId);
                f.Pre=null;f.BeforeNative=null;f.Post=null;
                f.Ordinary();Assert.AreEqual(1,f.Receipts.Count,"Completed source frame must not leak to later additions.");
            }
        }

        [TestMethod]
        public void NestedGenuineDeliveryRetainsItsOwnSourceAndOuterSourceReturns()
        {
            using(var f=new Fixture())
            {
                bool nested=false;
                f.Pre=args=>{if(nested)return;nested=true;f.Deliver(9,3);};
                f.Deliver();Assert.AreEqual(2,f.Calls);Assert.AreEqual(2,f.Receipts.Count);
                Assert.AreEqual(9,f.Receipts[0].TransportUnitId);Assert.AreEqual(7,f.Receipts[1].TransportUnitId);
                f.Pre=null;f.Ordinary();Assert.AreEqual(2,f.Receipts.Count);
            }
        }

        [TestMethod]
        public void OpaqueFailureDryRunAndResetNeverPublishButOriginalRunsOnce()
        {
            foreach(long result in new[]{0L,2L,0x76543210L})
            using(var f=new Fixture())
            {f.Result=result;Assert.AreEqual(result,f.Deliver());Assert.AreEqual(1,f.Calls);Assert.AreEqual(0,f.Receipts.Count);}
            using(var f=new Fixture())
            {
                f.Deliver(add:0);f.Deliver(capacity:0);f.Deliver(amount:0);f.Deliver(amount:-1);
                Assert.AreEqual(4,f.Calls);Assert.AreEqual(0,f.Receipts.Count);
            }
            using(var f=new Fixture())
            {f.SkipNativeMutation=true;Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(1,f.Calls);Assert.AreEqual(0,f.Receipts.Count);}
        }

        [TestMethod]
        public void ArbitraryStockChangesHaveNoSourceAndRequireNoReceiptReadbacks()
        {
            using(var f=new Fixture())
            {
                f.Ordinary();f.Ordinary(3,eGoods.STORED_FOOD_MEAT,7,250);
                Assert.AreEqual(2,f.Calls);Assert.AreEqual(0,f.Reads);Assert.AreEqual(0,f.Receipts.Count);
            }
        }

        [TestMethod]
        public void ReadFailureAndReaderUnavailabilityPreserveOriginalMutation()
        {
            for(int mode=0;mode<3;mode++)
            using(var f=new Fixture())
            {
                f.ThrowBeforeRead=mode==0;f.ThrowAfterRead=mode==1;f.ReadUnavailable=mode==2;
                Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(1,f.Calls);
                Assert.AreEqual(1,f.Amount(2,eGoods.STORED_WOOD_PLANKS));Assert.AreEqual(0,f.Receipts.Count);
            }
        }

        [TestMethod]
        public void ChangedStorageIdentityOrAmbiguousMutationDoesNotProduceReceipt()
        {
            using(var f=new Fixture())
            {f.AfterNative=()=>f.ObservedGlobal++;f.Deliver();Assert.AreEqual(1,f.Calls);Assert.AreEqual(0,f.Receipts.Count);}
            using(var f=new Fixture())
            {f.AfterNative=()=>f.ObservedPlayer++;f.Deliver();Assert.AreEqual(1,f.Calls);Assert.AreEqual(0,f.Receipts.Count);}
            using(var f=new Fixture())
            {
                bool nested=false;f.AfterNative=()=>{if(nested)return;nested=true;f.Ordinary();};
                f.Deliver();Assert.AreEqual(2,f.Calls);Assert.AreEqual(0,f.Receipts.Count,"A net delta including foreign nested stock changes is ambiguous.");
            }
        }

        [TestMethod]
        public void ExceptionalPreNativeAndPostExitNeverRetryAndRestoreSource()
        {
            for(int stage=0;stage<3;stage++)
            using(var f=new Fixture())
            {
                if(stage==0)f.Pre=args=>throw new InvalidOperationException("Pre");
                if(stage==1)f.BeforeNative=()=>throw new InvalidOperationException("Native");
                if(stage==2)f.Post=args=>throw new InvalidOperationException("Post");
                long result=f.Deliver();Assert.AreEqual(stage==0?0:1,f.Calls);
                Assert.AreEqual(stage==2?1L:0L,result);Assert.AreEqual(stage==2?1:0,f.Receipts.Count);
                Assert.AreEqual(1,f.Errors.Count);f.Pre=null;f.Post=null;f.BeforeNative=null;
                f.Ordinary();Assert.AreEqual(stage==2?1:0,f.Receipts.Count);
            }
        }

        [TestMethod]
        public void ObserverFailureCannotChangeReturnOrExtendProvenance()
        {
            using(var f=new Fixture())
            {
                f.Observer=receipt=>{f.Ordinary(8);throw new InvalidOperationException("Observer");};
                Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(2,f.Calls);Assert.AreEqual(1,f.Receipts.Count);Assert.AreEqual(1,f.Errors.Count);
            }
        }

        [TestMethod]
        public void RetiredSourceOrSourceReaderFailureCannotSuppressOriginal()
        {
            for(int failure=0;failure<2;failure++)
            using(var f=new Fixture())
            {
                f.Pre=args=>f.SourceValidation=source=>failure==0?false:throw new InvalidOperationException("Source read");
                Assert.AreEqual(1L,f.Deliver());Assert.AreEqual(1,f.Calls);Assert.AreEqual(0,f.Receipts.Count);
                Assert.AreEqual(1,f.Amount(2,eGoods.STORED_WOOD_PLANKS));
            }
        }

        [TestMethod]
        public void RequiredReceiptSupportBlocksAdmissionIndependentlyOfLiveObserverDemand()
        {
            Assert.IsFalse(ProductionGoodsReceived.IsAvailable,"This isolated fixture does not initialize native game services.");
            Assert.IsTrue(ProductionGoodsReceived.TryRegister("test.production-admission","required",_=>{},out var registration,out _));
            var customGame=new GameModeSnapshot(false,true,true,false,false,false,true,false,true,
                false,false,0,0,0,0,0,0,0,0,0,GameModeKind.CustomGame,default,0,0,0,0,0,false);
            try
            {
                Assert.IsTrue(ProductionGoodsReceived.ValidateAdmission(customGame,out _));
                registration.SetRequiredForMission(true);
                Assert.IsFalse(ProductionGoodsReceived.ValidateAdmission(customGame,out string reason));
                StringAssert.Contains(reason,"test.production-admission");
                registration.SetEnabled(false);
                Assert.IsFalse(ProductionGoodsReceived.ValidateAdmission(customGame,out _),"Dormant callbacks do not remove the next mission's requirement.");
                registration.SetRequiredForMission(true,new GameplayModActivationProfile("test.production-admission","Test",GameplayModAllowedContext.None));
                Assert.IsTrue(ProductionGoodsReceived.ValidateAdmission(customGame,out _),"A recognized excluded mode needs no receipt backend.");
                registration.SetRequiredForMission(true,new GameplayModActivationProfile("test.production-admission","Test",GameplayModAllowedContext.CustomGame));
                Assert.IsFalse(ProductionGoodsReceived.ValidateAdmission(customGame,out _));
                registration.SetRequiredForMission(false);
                Assert.IsTrue(ProductionGoodsReceived.ValidateAdmission(customGame,out _));
            }
            finally {registration.SetEnabled(false);registration.SetRequiredForMission(false);}
        }

        [TestMethod]
        public void PublicObserversStartDormantAndKeepOrderedSnapshotAndFailureIsolation()
        {
            var order=new List<string>();
            ProductionGoodsReceivedRegistration last=null;
            Assert.IsTrue(ProductionGoodsReceived.TryRegister("test.production-publish.a","first",_=>{order.Add("a");last.SetEnabled(false);throw new InvalidOperationException("Observer");},out var first,out _));
            Assert.IsTrue(ProductionGoodsReceived.TryRegister("test.production-publish.b","last",_=>order.Add("b"),out last,out _));
            Assert.IsFalse(ProductionGoodsReceived.TryRegister("test.production-publish.a","first",_=>{},out _,out _));
            var receipt=new ProductionGoodsReceipt(new ProductionGoodsSource(1,11,eChimps.CHIMP_TYPE_WOODCUTTER),new ProductionGoodsStock(2,22,1,3),eGoods.STORED_WOOD_PLANKS,1);
            try
            {
                ProductionGoodsReceived.Publish(receipt);Assert.AreEqual(0,order.Count,"Registration is inactive and has no replay.");
                last.SetEnabled(true);first.SetEnabled(true);ProductionGoodsReceived.Publish(receipt);
                CollectionAssert.AreEqual(new[]{"a","b"},order,"A failing observer and a registration update cannot interrupt the captured ordered snapshot.");
                order.Clear();ProductionGoodsReceived.Publish(receipt);
                CollectionAssert.AreEqual(new[]{"a"},order,"Logical demand changes apply to the next receipt.");
            }
            finally {first.SetEnabled(false);last.SetEnabled(false);first.SetRequiredForMission(false);last.SetRequiredForMission(false);}
        }

        private static GameModeSnapshot CustomGame()=>new GameModeSnapshot(false,true,true,false,false,false,true,false,true,
            false,false,0,0,0,0,0,0,0,0,0,GameModeKind.CustomGame,default,0,0,0,0,0,false);

        [TestMethod]
        public void NativeAdmissionPreparesOnceAndOldRetirementCannotDisableNewSession()
        {
            bool observeSession=true;
            int admitted=0;var ended=new List<long>();
            ProductionGoodsReceivedRegistration registration=null;
            Assert.IsTrue(ProductionGoodsReceived.TryRegister("test.production-session","timing",_=>{},out registration,out _,
                onMissionAdmitted:(session,mode)=>{if(!observeSession)return;admitted++;Assert.AreEqual(GameModeKind.CustomGame,mode.Kind);registration.SetEnabled(true);},
                onMissionEnded:session=>{if(!observeSession)return;ended.Add(session);registration.SetEnabled(false);}));
            try
            {
                Assert.IsTrue(ProductionGoodsReceived.Admit(731,CustomGame(),out _));
                Assert.IsTrue(ProductionGoodsReceived.Admit(731,CustomGame(),out _));Assert.AreEqual(1,admitted);
                Assert.IsTrue(registration.Enabled,"Admission prepares callbacks before advancing simulation resumes.");
                Assert.IsTrue(ProductionGoodsReceived.Admit(732,CustomGame(),out _));Assert.AreEqual(2,admitted);
                ProductionGoodsReceived.EndMission(731);Assert.AreEqual(0,ended.Count);Assert.IsTrue(registration.Enabled);
                ProductionGoodsReceived.EndMission(732);CollectionAssert.AreEqual(new[]{732L},ended);Assert.IsFalse(registration.Enabled);
                ProductionGoodsReceived.EndMission(732);Assert.AreEqual(1,ended.Count);
            }
            finally {observeSession=false;registration.SetEnabled(false);registration.SetRequiredForMission(false);ProductionGoodsReceived.EndMission(732);}
        }

        [TestMethod]
        public void ThrowingRequiredAdmissionCallbackCannotReleaseTheWorld()
        {
            bool failAdmission=true;
            ProductionGoodsReceivedRegistration registration=null;
            Assert.IsTrue(ProductionGoodsReceived.TryRegister("test.production-session","failure",_=>{},out registration,out _,
                onMissionAdmitted:(session,mode)=>{
                    if(!failAdmission)return;
                    // Backend is deliberately absent. Declaring the requirement here lets
                    // this isolated test reach the required-callback exception boundary.
                    registration.SetRequiredForMission(true);registration.SetEnabled(true);
                    throw new InvalidOperationException("Authoritative settings rejected");}));
            try
            {
                Assert.IsFalse(ProductionGoodsReceived.Admit(741,CustomGame(),out string diagnostic));
                StringAssert.Contains(diagnostic,"test.production-session");StringAssert.Contains(diagnostic,"Authoritative settings rejected");
                Assert.IsFalse(registration.Enabled);
            }
            finally {failAdmission=false;registration.SetEnabled(false);registration.SetRequiredForMission(false);}
        }

        [TestMethod]
        public void FinalStorageDomainRejectsRawLogsAndOtherIntermediates()
        {
            foreach(var good in new[]{eGoods.STORED_NULL,eGoods.STORED_WOOD_LOGS,eGoods.STORED_COW_HIDES,
                eGoods.STORED_PITCH_REFINED,eGoods.STORED_GOLD,eGoods.Count,eGoods._SE_REQUIRE_HORSE})
                Assert.IsFalse(ProductionGoodsReceivedRuntime.TryGetFinalStorageType(good,out _),"Unsupported final output: "+good);
            foreach(var good in new[]{eGoods.STORED_WOOD_PLANKS,eGoods.STORED_RAW_HOPS,eGoods.STORED_STONE_BLOCKS,
                eGoods.STORED_IRON_INGOTS,eGoods.STORED_PITCH_RAW,eGoods.STORED_RAW_WHEAT,eGoods.STORED_FOOD_ALE,eGoods.STORED_FLOUR})
            {Assert.IsTrue(ProductionGoodsReceivedRuntime.TryGetFinalStorageType(good,out var storage));Assert.AreEqual(eStructs.STRUCT_GOODS_YARD,storage);}
            foreach(var good in new[]{eGoods.STORED_FOOD_BREAD,eGoods.STORED_FOOD_CHEESE,eGoods.STORED_FOOD_MEAT,eGoods.STORED_FOOD_FRUIT})
            {Assert.IsTrue(ProductionGoodsReceivedRuntime.TryGetFinalStorageType(good,out var storage));Assert.AreEqual(eStructs.STRUCT_GRANARY,storage);}
            foreach(var good in new[]{eGoods.STORED_BOWS,eGoods.STORED_CROSSBOWS,eGoods.STORED_SPEARS,eGoods.STORED_PIKES,
                eGoods.STORED_MACES,eGoods.STORED_SWORDS,eGoods.STORED_LEATHER_ARMOUR,eGoods.STORED_METAL_ARMOUR})
            {Assert.IsTrue(ProductionGoodsReceivedRuntime.TryGetFinalStorageType(good,out var storage));Assert.AreEqual(eStructs.STRUCT_ARMOURY,storage);}
        }
    }
}

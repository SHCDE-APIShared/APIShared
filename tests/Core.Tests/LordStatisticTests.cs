#nullable enable
using APIShared.Statistics;
using APIShared.GameModes;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Core.Tests {
 [TestClass]
 public sealed class LordStatisticTests {
  private static LordStatisticColumnDefinition Definition(string id,Func<LordStatisticRequest,IReadOnlyDictionary<int,decimal?>> read)=>
   new(id,()=>"Distance",()=>"Distance in metres",()=>null,read,LordStatisticTheme.Military,v=>v.ToString("0.00",System.Globalization.CultureInfo.InvariantCulture)+" m");
  [TestMethod] public void RegistryIsDormantAndCopiesOnlyAuthorizedIdsWithDecimalPrecision(){
   int calls=0;LordStatisticRequest? retained=null;var values=new Dictionary<int,decimal?>{{1,-1.25m},{2,8.625m},{8,999m}};
   var registry=new StatisticRegistry(()=>true,(_,_)=>{});
   Assert.IsTrue(registry.Register("foreign/owner",Definition("range",r=>{calls++;retained=r;return values;}),out var column,out _));
   var catalog=registry.Snapshot();Assert.AreEqual(0,calls);Assert.AreEqual("foreign%2Fowner/range",column.Key);
   Assert.IsFalse(registry.Register("foreign/owner",Definition("range",_=>values),out _,out var reason));Assert.IsTrue(reason.Contains("Duplicate"));
   column.Read(0,new[]{1});column.Read(10,Array.Empty<int>());Assert.AreEqual(0,calls);
   var result=column.Read(10,new[]{2,1,2,0,9});Assert.AreEqual(1,calls);CollectionAssert.AreEqual(new[]{1,2},retained!.PlayerIds.ToArray());
   Assert.AreEqual(10L,retained.SessionId);Assert.AreEqual(2,result.Count);Assert.AreEqual(-1.25m,result[1]);Assert.AreEqual(8.625m,result[2]);
   values[1]=0;Assert.AreEqual(-1.25m,result[1]);Assert.AreEqual("8.63 m",column.Format(8.625m));
   column.SetActive(false);Assert.AreEqual(0,column.Read(10,new[]{1}).Count);Assert.AreEqual(1,calls);
   column.SetActive(true);Assert.AreEqual(0m,column.Read(10,new[]{1})[1]);Assert.AreSame(column,catalog.Columns[0]);
  }
  [TestMethod] public void ProviderFailureRecursionAndMetadataErrorsAreIsolatedAndRecover(){
   int failures=0;bool fail=true;var registry=new StatisticRegistry(()=>true,(_,_)=>failures++);LordStatisticColumn? handle=null;
   var definition=new LordStatisticColumnDefinition("v",()=>throw new Exception(),()=>"help",()=>null,r=>{
    Assert.AreEqual(0,handle!.Read(r.SessionId,r.PlayerIds).Count);
    if(fail)throw new Exception();return new Dictionary<int,decimal?>{{1,1.5m}};
   },format:_=>throw new Exception());
   Assert.IsTrue(registry.Register("other",definition,out handle,out _));
   for(int i=0;i<4;i++){Assert.AreEqual("other/v",handle.GetName());Assert.AreEqual(0,handle.Read(3,new[]{1}).Count);Assert.AreEqual("1.5",handle.Format(1.5m));}
   Assert.AreEqual(3,failures);fail=false;Assert.AreEqual(1.5m,handle.Read(3,new[]{1})[1]);
  }
  [TestMethod] public void NotificationsAreOrderedReentrantAndObserverFailuresDoNotBlockOtherConsumers(){
   int failures=0;var registry=new StatisticRegistry(()=>true,(_,_)=>failures++);var revisions=new List<long>();bool nested=false;
   registry.Changed+=_=>throw new Exception();
   registry.Changed+=notification=>{if(nested)return;nested=true;registry.Register("b",Definition("v",_=>new Dictionary<int,decimal?>()),out _,out _);};
   registry.Changed+=c=>revisions.Add(c.Revision);
   registry.Register("a",Definition("v",_=>new Dictionary<int,decimal?>()),out var column,out _);
   CollectionAssert.AreEqual(new long[]{1,2},revisions);Assert.AreEqual(2,failures);
   column.SetActive(true);Assert.AreEqual(2,revisions.Count);column.SetActive(false);Assert.AreEqual(3,revisions.Count);
   column.RefreshMetadata();Assert.AreEqual(4,revisions.Count);
  }
  [TestMethod] public void ColumnsDecideIndependentlyAndOnlyOptInGetsSortOnlyValues(){
   var registry=new StatisticRegistry(()=>true,(_,_)=>{});
   var received=new List<int[]>();
   IReadOnlyDictionary<int,decimal?> Read(LordStatisticRequest request){
    received.Add(request.PlayerIds.ToArray());
    return new Dictionary<int,decimal?>{{1,11m},{2,22m},{3,33m},{8,88m}};
   }
   var legacy=Definition("legacy",Read);
   var custom=new LordStatisticColumnDefinition("custom",()=>"",()=>"",()=>null,Read,
    LordStatisticTheme.OtherMods,null,view=>view.PlayerId==2,true);
   Assert.IsTrue(registry.Register("test",legacy,out var defaultColumn,out _));
   Assert.IsTrue(registry.Register("test",custom,out var customColumn,out _));
   Assert.AreEqual(0,customColumn.Read(3,new[]{1,2}).Count);
   Assert.AreEqual(0,received.Count);
   var roster=new[]{View(2),View(1),View(2),View(3,ally:true),View(8,session:9),View(9)};
   var normal=defaultColumn.ReadForView(3,roster);
   var projected=customColumn.ReadForView(3,roster);
   CollectionAssert.AreEqual(new[]{1,3},received[0]);
   CollectionAssert.AreEqual(new[]{1,2,3},received[1]);
   Assert.IsFalse(normal.ContainsKey(2));
   Assert.AreEqual(22m,projected[2].DisplayValue);
   Assert.IsNull(projected[3].DisplayValue);
   Assert.AreEqual(33m,projected[3].SortValue);
   Assert.IsFalse(projected.ContainsKey(8));

   foreach(var view in new[]{View(1),View(2,ally:true),View(2,spectator:true),
       View(2,eliminated:true),View(2,mode:GameModeKind.MapEditor),View(2,permission:true)})
    Assert.IsTrue(view.CanShowDetails);
   Assert.IsFalse(View(2).CanShowDetails);
  }
  [TestMethod] public void VisibilityFailureAndCatalogChangesDiscardUnavailableValues(){
   int failures=0;
   bool throwForTwo=true, refreshInVisibility=false, refreshInRead=false;
   int reads=0;
   LordStatisticColumn handle=null!;
   var registry=new StatisticRegistry(()=>true,(_,_)=>failures++);
   var definition=new LordStatisticColumnDefinition("value",()=>"",()=>"",()=>null,request=>{
    reads++;
    if(throwForTwo)CollectionAssert.AreEqual(new[]{1},request.PlayerIds.ToArray());
    if(refreshInRead)handle.RefreshMetadata();
    return new Dictionary<int,decimal?>{{1,8m},{2,9m}};
   },LordStatisticTheme.OtherMods,null,view=>{
    Assert.AreEqual(0,handle.ReadForView(3,new[]{View(1)}).Count);
    if(refreshInVisibility)handle.RefreshMetadata();
    if(throwForTwo && view.PlayerId==2)throw new InvalidOperationException();
    return view.IsOwn;
   },true);
   Assert.IsTrue(registry.Register("test",definition,out handle,out _));
   var views=new[]{View(1),View(2)};
   for(int i=0;i<2;i++){
    var result=handle.ReadForView(3,views);
    Assert.AreEqual(8m,result[1].DisplayValue);
    Assert.IsFalse(result.ContainsKey(2));
   }
   Assert.AreEqual(1,failures);
   throwForTwo=false;
   Assert.AreEqual(9m,handle.ReadForView(3,views)[2].SortValue);
   refreshInVisibility=true;
   Assert.AreEqual(0,handle.ReadForView(3,views).Count);
   Assert.AreEqual(3,reads);
   refreshInVisibility=false;
   refreshInRead=true;
   Assert.AreEqual(0,handle.ReadForView(3,views).Count);
   handle.SetActive(false);
   Assert.AreEqual(0,handle.ReadForView(3,views).Count);
   Assert.AreEqual(4,reads);
  }
  [TestMethod] public void AChangeToAnotherColumnDoesNotDiscardAnIndependentSample(){
   var registry=new StatisticRegistry(()=>true,(_,_)=>{});
   Assert.IsTrue(registry.Register("test",Definition("other",_=>null!),out var other,out _));
   var definition=Definition("value",_=>{
    other.RefreshMetadata();
    return new Dictionary<int,decimal?>{{1,8m}};
   });
   Assert.IsTrue(registry.Register("test",definition,out var handle,out _));
   long revision=handle.Revision;
   long otherRevision=other.Revision;
   Assert.AreEqual(8m,handle.ReadForView(3,new[]{View(1)})[1].DisplayValue);
   Assert.AreEqual(revision,handle.Revision);
   Assert.AreEqual(otherRevision+1,other.Revision);
  }
  private static LordStatisticView View(int player,bool ally=false,bool spectator=false,bool eliminated=false,
   GameModeKind mode=GameModeKind.CustomGame,bool permission=false,long session=3)=>
   new(session,1,player,mode,true,spectator,eliminated,ally,permission);
  [TestMethod] public void WrongThreadAndIncompleteContractsDoNotRegister(){
   bool onThread=false;var registry=new StatisticRegistry(()=>onThread,(_,_)=>{});
   Assert.IsFalse(registry.Register("a",Definition("v",_=>null),out _,out _));
   Assert.ThrowsExactly<InvalidOperationException>(()=>registry.Snapshot());onThread=true;
   Assert.IsFalse(registry.Register("a",new LordStatisticColumnDefinition("v",null,()=>"",()=>null,_=>null),out _,out _));
   Assert.AreEqual(0,registry.Snapshot().Columns.Count);
  }
 }
}

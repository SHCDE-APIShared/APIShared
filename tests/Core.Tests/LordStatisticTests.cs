#nullable enable
using APIShared.Statistics;
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
  [TestMethod] public void WrongThreadAndIncompleteContractsDoNotRegister(){
   bool onThread=false;var registry=new StatisticRegistry(()=>onThread,(_,_)=>{});
   Assert.IsFalse(registry.Register("a",Definition("v",_=>null),out _,out _));
   Assert.ThrowsExactly<InvalidOperationException>(()=>registry.Snapshot());onThread=true;
   Assert.IsFalse(registry.Register("a",new LordStatisticColumnDefinition("v",null,()=>"",()=>null,_=>null),out _,out _));
   Assert.AreEqual(0,registry.Snapshot().Columns.Count);
  }
 }
}

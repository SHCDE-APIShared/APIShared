using APIShared;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Core.Tests
{
    [TestClass]
    public class GateAccessRulesTests
    {
        [TestMethod]
        public void OrderedContributionsArePassiveAtomicAndFailIndividually()
        {
            Assert.IsFalse(GateAccessRules.HasRegistrations);
            long revision=GateAccessRules.Version;
            int calls=0, errors=0; bool deny=true;
            Assert.IsFalse(GateAccessRules.TryRegister("", "x", q=>GateAccessDecision.Allow,out _));
            Assert.IsFalse(GateAccessRules.Invalidate("missing","x"));
            Assert.AreEqual(revision,GateAccessRules.Version);
            Assert.IsTrue(GateAccessRules.TryRegister("z.mod","last",q=>{calls++;return deny?GateAccessDecision.Deny:GateAccessDecision.Unchanged;},out _));
            Assert.IsTrue(GateAccessRules.TryRegister("a.mod","first",q=>{calls++;return GateAccessDecision.Allow;},out _));
            Assert.IsTrue(GateAccessRules.TryRegister("m.mod","error",q=>{calls++;throw new System.InvalidOperationException();},out _));
            Assert.AreEqual(0,calls,"registration never invokes contributions");
            Assert.IsFalse(GateAccessRules.TryRegister("z.mod","last",q=>GateAccessDecision.Allow,out _));
            var query=new GateAccessQuery(2,134,45,1,0,false);
            Assert.IsFalse(GateAccessRules.Evaluate(query,(o,i,e)=>errors++));
            Assert.AreEqual(3,calls);Assert.AreEqual(1,errors);
            Assert.AreEqual(revision+3,GateAccessRules.Version);
            deny=false;Assert.IsTrue(GateAccessRules.Invalidate("z.mod","last"));
            Assert.IsTrue(GateAccessRules.Evaluate(query,(o,i,e)=>{throw e;}),"reporter failure cannot revoke an earlier valid decision");
            Assert.AreEqual(revision+4,GateAccessRules.Version);
            int before=calls;
            Assert.IsFalse(GateAccessRules.Evaluate(new GateAccessQuery(0,134,45,1,0,false)));
            Assert.IsFalse(GateAccessRules.Evaluate(new GateAccessQuery(2,134,0,1,0,false)));
            Assert.AreEqual(before,calls,"invalid identities do not reach contributors");
            for(int n=0;n<1000;n++) Assert.IsTrue(GateAccessRules.Evaluate(query));
            Assert.AreEqual(before+3000,calls);
        }
    }
}

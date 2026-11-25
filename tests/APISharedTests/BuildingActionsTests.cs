using System;
using System.Collections.Generic;
using APIShared.Buildings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;

namespace APISharedTests
{
    // Registration and dispatch rules of building actions; the live building validation of TryExecute is an in-game check.
    [TestClass]
    public sealed class BuildingActionsTests
    {
        private static readonly eStructs[] Gates = { eStructs.STRUCT_GATE_MAIN, eStructs.STRUCT_DRAWBRIDGE };

        [TestMethod]
        public void ActionsRejectInvalidOrDuplicateIdentitiesAndKnowTheirTypes()
        {
            var registry = new BuildingActionRegistry((_, __) => { });
            BuildingActionExecutor ok = (BuildingActionRequest request, out string reason) => { reason = null; return true; };
            Assert.IsFalse(registry.TryRegister(null, out _));
            Assert.IsFalse(registry.TryRegister(new BuildingActionOptions("", "switch", Gates, ok), out _));
            Assert.IsFalse(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", Gates, null), out _));
            Assert.IsFalse(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", new eStructs[0], ok), out _));
            Assert.IsTrue(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", Gates, ok), out _));
            Assert.IsFalse(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", Gates, ok), out string duplicate));
            StringAssert.Contains(duplicate, "Duplicate");
            Assert.IsTrue(registry.Find("gate.mod", "switch").Types.Contains(eStructs.STRUCT_DRAWBRIDGE));
            Assert.IsFalse(registry.Find("gate.mod", "switch").Types.Contains(eStructs.STRUCT_TANNERS_WORKSHOP));
            Assert.IsNull(registry.Find("other.mod", "switch"));
        }

        // The exact value is the contract: peers of different processes and APIShared versions must agree on it.
        [TestMethod]
        public void ActionKeysAreStableAndResolveOnlyUniqueActions()
        {
            Assert.AreEqual(1598242251, BuildingActionRegistry.KeyOf("ExtraFeatures_Serp", "gate-automatic"));
            Assert.AreNotEqual(BuildingActionRegistry.KeyOf("a", "bc"), BuildingActionRegistry.KeyOf("ab", "c"), "the separator keeps owner and ID apart");
            var registry = new BuildingActionRegistry((_, __) => { });
            BuildingActionExecutor ok = (BuildingActionRequest request, out string reason) => { reason = null; return true; };
            Assert.IsTrue(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", Gates, ok), out _));
            Assert.AreEqual("switch", registry.FindByKey(BuildingActionRegistry.KeyOf("gate.mod", "switch")).Id);
            Assert.IsNull(registry.FindByKey(BuildingActionRegistry.KeyOf("gate.mod", "other")));
        }

        [TestMethod]
        public void ExecutorsReceiveTheRequestAndFailuresBecomeLoggedRefusals()
        {
            var reports = new List<string>();
            var registry = new BuildingActionRegistry((name, _) => reports.Add(name));
            var seen = new List<BuildingActionRequest>();
            Assert.IsTrue(registry.TryRegister(new BuildingActionOptions("gate.mod", "switch", Gates,
                (BuildingActionRequest request, out string reason) => { seen.Add(request); reason = request.Argument == 1 ? null : "bad"; return request.Argument == 1; }), out _));
            Assert.IsTrue(registry.TryRegister(new BuildingActionOptions("gate.mod", "broken", Gates,
                (BuildingActionRequest request, out string reason) => throw new InvalidOperationException()), out _));
            var request1 = new BuildingActionRequest("gate.mod", "switch", 2, 7, 100, 1);
            Assert.IsTrue(registry.TryExecute(registry.Find("gate.mod", "switch"), request1, out _));
            Assert.AreSame(request1, seen[0]);
            Assert.IsFalse(registry.TryExecute(registry.Find("gate.mod", "switch"), new BuildingActionRequest("gate.mod", "switch", 2, 7, 100, 5), out string refused));
            Assert.AreEqual("bad", refused);
            Assert.IsFalse(registry.TryExecute(registry.Find("gate.mod", "broken"), request1, out _));
            CollectionAssert.AreEqual(new[] { "gate.mod/broken building action" }, reports);
        }
    }
}

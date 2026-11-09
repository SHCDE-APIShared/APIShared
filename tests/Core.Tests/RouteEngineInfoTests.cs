using APIShared.Pathfinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Core.Tests
{
    [TestClass]
    public class RouteEngineInfoTests
    {
        [TestMethod]
        public void OneOwnerReportsUntilItClears()
        {
            RouteEngineInfo.Clear("tests.engine.a");
            Assert.IsFalse(RouteEngineInfo.IsActive);
            Assert.AreEqual(RouteEngineFeatures.None, RouteEngineInfo.Features);
            Assert.IsFalse(RouteEngineInfo.TryReport(" ", RouteEngineFeatures.TileRules, out string empty));
            Assert.IsNotNull(empty);

            Assert.IsTrue(RouteEngineInfo.TryReport("tests.engine.a", RouteEngineFeatures.TileRules, out _));
            Assert.IsFalse(RouteEngineInfo.TryReport("tests.engine.b", RouteEngineFeatures.RouteCosts, out string taken));
            StringAssert.Contains(taken, "tests.engine.a");
            Assert.AreEqual(RouteEngineFeatures.TileRules, RouteEngineInfo.Features, "a refused report changes nothing");

            Assert.IsTrue(RouteEngineInfo.TryReport("tests.engine.a", RouteEngineFeatures.TileRules | RouteEngineFeatures.ReplanPolicies, out _));
            Assert.AreEqual(RouteEngineFeatures.TileRules | RouteEngineFeatures.ReplanPolicies, RouteEngineInfo.Features);
            Assert.AreEqual("tests.engine.a", RouteEngineInfo.OwnerGuid);

            RouteEngineInfo.Clear("tests.engine.b");
            Assert.IsTrue(RouteEngineInfo.IsActive, "another owner cannot clear the report");
            RouteEngineInfo.Clear("tests.engine.a");
            Assert.IsFalse(RouteEngineInfo.IsActive);
            Assert.IsNull(RouteEngineInfo.OwnerGuid);
            Assert.IsTrue(RouteEngineInfo.TryReport("tests.engine.b", RouteEngineFeatures.RouteCosts, out _));
            RouteEngineInfo.Clear("tests.engine.b");
        }
    }
}

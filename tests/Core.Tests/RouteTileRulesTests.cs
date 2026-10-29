using APIShared.Pathfinding;
using Microsoft.VisualStudio.TestTools.UnitTesting;
namespace Core.Tests
{
    [TestClass]
    public class RouteTileRulesTests
    {
        // Process-wide registry: identities are unique to this test and every effect is cleared again.
        [TestMethod]
        public void CombinedEffectsJournalAndValidation()
        {
            Assert.IsFalse(RouteTileRules.TryRegister("", "x", out _, out _));
            Assert.IsTrue(RouteTileRules.TryRegister("tests.tile.a", "fire", out RouteTileRuleSet fire, out _));
            Assert.IsTrue(RouteTileRules.TryRegister("tests.tile.b", "zone", out RouteTileRuleSet zone, out _));
            Assert.IsFalse(RouteTileRules.TryRegister("tests.tile.a", "fire", out _, out _), "a second registration of one identity");
            Assert.AreEqual(RouteTileEffect.None, RouteTileRules.GetEffect(10, 10, 1, out _), "no effect without rules");

            long cursor = RouteTileRules.Version;
            Assert.IsTrue(fire.Set(10, 10, RouteTileEffect.Penalty, 20));
            Assert.IsTrue(zone.Set(10, 10, RouteTileEffect.Penalty, 40, 1U << 2));
            Assert.IsTrue(zone.Set(11, 10, RouteTileEffect.Forbidden, 0, 1U << 1));
            Assert.IsTrue(fire.Set(11, 10, RouteTileEffect.Penalty, 200));
            Assert.IsTrue(RouteTileRules.HasRules);

            // Forbidden for a player wins; otherwise the largest penalty of the sets that include the player.
            Assert.AreEqual(RouteTileEffect.Penalty, RouteTileRules.GetEffect(10, 10, 1, out int p1)); Assert.AreEqual(20, p1);
            Assert.AreEqual(RouteTileEffect.Penalty, RouteTileRules.GetEffect(10, 10, 2, out int p2)); Assert.AreEqual(40, p2);
            Assert.AreEqual(RouteTileEffect.Forbidden, RouteTileRules.GetEffect(11, 10, 1, out int p3)); Assert.AreEqual(0, p3);
            Assert.AreEqual(RouteTileEffect.Penalty, RouteTileRules.GetEffect(11, 10, 2, out int p4)); Assert.AreEqual(200, p4);
            Assert.AreEqual(RouteTileEffect.None, RouteTileRules.GetEffect(12, 10, 1, out _));

            // Invalid arguments change nothing; an unchanged Set is not journaled.
            long before = RouteTileRules.Version;
            Assert.IsFalse(fire.Set(-1, 0, RouteTileEffect.Forbidden));
            Assert.IsFalse(fire.Set(0, RouteTileRules.MapSize, RouteTileEffect.Forbidden));
            Assert.IsFalse(fire.Set(5, 5, RouteTileEffect.Penalty, 0));
            Assert.IsFalse(fire.Set(5, 5, RouteTileEffect.Penalty, RouteTileRules.MaximumPenalty + 1));
            Assert.IsFalse(fire.Set(5, 5, RouteTileEffect.Forbidden, 0, 1U));
            Assert.IsTrue(fire.Set(10, 10, RouteTileEffect.Penalty, 20));
            Assert.AreEqual(before, RouteTileRules.Version);
            Assert.AreEqual(RouteTileEffect.None, RouteTileRules.GetEffect(10, 10, 0, out _), "player IDs are one-based");

            // The journal lists every effective change in order, packed as y * MapSize + x.
            var changes = new int[16];
            Assert.AreEqual(4, RouteTileRules.ReadChanges(ref cursor, changes));
            CollectionAssert.AreEqual(new[] { 10 * 800 + 10, 10 * 800 + 10, 10 * 800 + 11, 10 * 800 + 11 }, changes[..4]);
            Assert.AreEqual(RouteTileRules.Version, cursor);
            Assert.AreEqual(0, RouteTileRules.ReadChanges(ref cursor, changes));
            long behind = -5;
            Assert.AreEqual(-1, RouteTileRules.ReadChanges(ref behind, changes), "an invalid cursor reports lost changes");
            Assert.AreEqual(RouteTileRules.Version, behind);

            // Clearing removes effects and journals each cleared tile.
            Assert.IsTrue(zone.Clear(11, 10));
            Assert.AreEqual(RouteTileEffect.Penalty, RouteTileRules.GetEffect(11, 10, 1, out int p5)); Assert.AreEqual(200, p5);
            fire.ClearAll(); zone.ClearAll();
            Assert.AreEqual(4, RouteTileRules.ReadChanges(ref cursor, changes));
            Assert.IsFalse(RouteTileRules.HasRules);
            Assert.AreEqual(RouteTileEffect.None, RouteTileRules.GetEffect(10, 10, 2, out _));
            var small = new int[1]; long pending = cursor;
            Assert.IsTrue(fire.Set(1, 1, RouteTileEffect.Forbidden)); Assert.IsTrue(fire.Set(2, 1, RouteTileEffect.Forbidden));
            Assert.AreEqual(-1, RouteTileRules.ReadChanges(ref pending, small), "a buffer smaller than the pending changes reports loss");
            fire.ClearAll();
        }
    }
}

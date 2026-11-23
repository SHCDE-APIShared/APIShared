using System;
using System.Linq;
using System.Reflection;
using APIShared.Presentation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil.Cil;
using MonoMod.Utils;

namespace APISharedTests
{
    [TestClass]
    public sealed class AllyGoodsFooterTests
    {
        [TestMethod]
        public void DormantFooterReturnDoesNotAllocateFactoryClosures()
        {
            // Inspect the actual production boundary: capturing the context inside
            // its dirty branch used to hoist a display-class allocation before demand.
            using (var definition = new DynamicMethodDefinition(typeof(AllyGoodsPanelExtensions)
                .GetMethod("UpdateFooter", BindingFlags.NonPublic | BindingFlags.Static)))
            {
                bool returned = false;
                foreach (var instruction in definition.Definition.Body.Instructions)
                {
                    Assert.IsFalse(instruction.OpCode == OpCodes.Newobj || instruction.OpCode == OpCodes.Newarr ||
                        instruction.OpCode == OpCodes.Box, "Allocation before the dormant footer return: " + instruction);
                    if (instruction.OpCode == OpCodes.Ret) { returned = true; break; }
                }
                Assert.IsTrue(returned);
            }
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void IndependentFooterOwnersKeepOrderAndDoNotShareSectionIds()
        {
            var footer = new AllyGoodsSectionRegistry();
            var sections = new AllyGoodsSectionRegistry();
            Assert.IsTrue(footer.TryRegister("z", "action", _ => null, 0, out var first, out _));
            var before = footer.Entries;
            Assert.IsTrue(footer.TryRegister("a", "action", _ => null, 0, out _, out _));
            Assert.IsTrue(footer.TryRegister("z", "priority", _ => null, -1, out _, out _));
            Assert.IsFalse(footer.TryRegister("z", "action", _ => null, 0, out _, out _));
            Assert.IsTrue(sections.TryRegister("z", "action", _ => null, 0, out _, out _));
            CollectionAssert.AreEqual(new[] { "z/priority", "a/action", "z/action" },
                footer.Entries.Select(entry => entry.Owner + "/" + entry.Id).ToArray());
            Assert.AreEqual(1, before.Length); Assert.AreSame(first, before[0]);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FooterCacheRetainsViewsUntilExplicitContentRefreshOrHostReplacement()
        {
            var registry = new AllyGoodsSectionRegistry();
            registry.TryRegister("a", "action", _ => null, 0, out var entry, out _);
            var cache = new AllyGoodsFooterCache<object>();
            int created = 0, released = 0;
            Func<object> create = () => { created++; return new object(); };
            Action<object> release = _ => released++;
            Action<Exception> fail = _ => Assert.Fail();
            object first = cache.GetOrCreate(entry, create, fail, release);
            entry.Visible = false; entry.Enabled = false;
            Assert.AreSame(first, cache.GetOrCreate(entry, create, fail, release));
            entry.Visible = true;
            Assert.AreSame(first, cache.GetOrCreate(entry, create, fail, release));
            Assert.AreEqual(1, created); Assert.AreEqual(0, released);
            entry.Revision++;
            Assert.AreNotSame(first, cache.GetOrCreate(entry, create, fail, release));
            Assert.AreEqual(2, created); Assert.AreEqual(1, released);
            cache.Clear(release);
            Assert.AreEqual(2, released);
            cache.GetOrCreate(entry, create, fail, release);
            Assert.AreEqual(3, created);
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FooterFactoryFailuresAreIndependentAndOnlyExplicitlyRetried()
        {
            var registry = new AllyGoodsSectionRegistry();
            registry.TryRegister("a", "bad", _ => null, 0, out var bad, out _);
            registry.TryRegister("b", "good", _ => null, 0, out var good, out _);
            var cache = new AllyGoodsFooterCache<object>();
            int attempts = 0, failures = 0;
            Func<object> fail = () => { attempts++; throw new InvalidOperationException(); };
            Action<Exception> report = _ => failures++;
            Assert.IsNull(cache.GetOrCreate(bad, fail, report, _ => Assert.Fail()));
            object healthy = cache.GetOrCreate(good, () => new object(), report, _ => { });
            Assert.IsNull(cache.GetOrCreate(bad, fail, report, _ => Assert.Fail()));
            Assert.AreEqual(1, attempts); Assert.AreEqual(1, failures);
            bad.Revision++;
            Assert.IsNull(cache.GetOrCreate(bad, fail, report, _ => Assert.Fail()));
            Assert.AreEqual(2, attempts); Assert.AreEqual(2, failures);
            Assert.AreSame(healthy, cache.GetOrCreate(good, () => new object(), report, _ => { }));
        }

        [TestMethod]
        [TestCategory("Presentation")]
        public void FooterStripDoesNotCoverVanillaControlsOrUnknownForeignContent()
        {
            Assert.IsTrue(AllyGoodsFooterPolicy.HasStrip(660, 80));
            // Native 35-high goods icon/confirm buttons are vertically centered.
            Assert.IsFalse(AllyGoodsFooterPolicy.Overlaps(30, 22.5f, 35, 35, 660, 80));
            Assert.IsFalse(AllyGoodsFooterPolicy.Overlaps(75, 26, 400, 28, 660, 80));
            Assert.IsTrue(AllyGoodsFooterPolicy.Overlaps(200, 60, 100, 16, 660, 80));
            Assert.IsTrue(AllyGoodsFooterPolicy.Overlaps(float.NaN, 0, 1, 1, 660, 80));
            Assert.IsFalse(AllyGoodsFooterPolicy.HasStrip(32, 80));
            Assert.IsFalse(AllyGoodsFooterPolicy.HasStrip(660, 21));
            Assert.IsFalse(AllyGoodsFooterPolicy.HasStrip(float.PositiveInfinity, 80));
            Assert.IsTrue(AllyGoodsFooterPolicy.Fits(628, 660 - 32));
            Assert.IsFalse(AllyGoodsFooterPolicy.Fits(629, 660 - 32));
            Assert.IsFalse(AllyGoodsFooterPolicy.Fits(float.NaN, 628));
            // Geometry/CommandParameter cannot establish that a Button is decoration:
            // a foreign Click handler may use the same size and a null parameter.
            object markedFrame = new object(), foreignButton = new object();
            Assert.IsTrue(AllyGoodsFooterPolicy.IsMarkedBackground(markedFrame, markedFrame));
            Assert.IsFalse(AllyGoodsFooterPolicy.IsMarkedBackground(foreignButton, markedFrame));
            Assert.IsFalse(AllyGoodsFooterPolicy.IsMarkedBackground(markedFrame, null));
        }
    }
}
